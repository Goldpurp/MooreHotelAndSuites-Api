using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Application.Exceptions;

namespace MooreHotels.Application.Services;

public class RoomService : IRoomService
{
    private readonly IRoomRepository _roomRepo;
    private readonly IBookingRepository _bookingRepo;
    private readonly IImageService _imageService;

    private const int CHECK_IN_HOUR = 14; // 2:00 PM
    private const int CHECK_OUT_HOUR = 12; // 12:00 PM

    public RoomService(IRoomRepository roomRepo, IBookingRepository bookingRepo, IImageService imageService)
    {
        _roomRepo = roomRepo;
        _bookingRepo = bookingRepo;
        _imageService = imageService;
    }

    public async Task<IEnumerable<RoomDto>> GetAllRoomsAsync(RoomCategory? category = null, bool includeOffline = false)
    {
        var rooms = await _roomRepo.GetAllAsync(onlyOnline: !includeOffline);
        if (category.HasValue) rooms = rooms.Where(r => r.Category == category);

        return rooms.Select(MapToDto);
    }

    public async Task<IEnumerable<RoomDto>> SearchRoomsAsync(RoomSearchRequest request)
    {
        var checkIn = request.CheckIn?.Date.AddHours(CHECK_IN_HOUR);
        var checkOut = request.CheckOut?.Date.AddHours(CHECK_OUT_HOUR);

        var rooms = await _roomRepo.SearchAsync(
            checkIn,
            checkOut,
            request.Category,
            request.Capacity,
            request.RoomNumber,
            request.Amenity);

        return rooms.Select(MapToDto);
    }

    public async Task<RoomDto?> GetRoomByIdAsync(Guid id)
    {
        var room = await _roomRepo.GetByIdWithImagesAsync(id);
        return room != null ? MapToDto(room) : null;
    }

    public async Task<RoomAvailabilityResponse> CheckAvailabilityAsync(Guid roomId, DateTime checkIn, DateTime checkOut)
    {
        var room = await _roomRepo.GetByIdAsync(roomId);
        if (room == null) return new RoomAvailabilityResponse(false, "Asset not found in registry.");

        if (!room.IsOnline)
            return new RoomAvailabilityResponse(false, "Asset is currently offline or under maintenance.");

        var start = checkIn.Date.AddHours(CHECK_IN_HOUR);
        var end = checkOut.Date.AddHours(CHECK_OUT_HOUR);

        if (start >= end)
            return new RoomAvailabilityResponse(false, $"Invalid range. Standard policy: Check-out by {CHECK_OUT_HOUR}:00 PM.");

        var isBooked = await _bookingRepo.IsRoomBookedAsync(roomId, start, end);

        if (isBooked)
            return new RoomAvailabilityResponse(false, "Asset is already secured for these dates.");

        return new RoomAvailabilityResponse(true, $"Available (Check-in {start:HH:mm}, Check-out {end:HH:mm}).");
    }

    public async Task<RoomDto> CreateRoomAsync(CreateRoomRequest request)
    {
        var existingRoom = await _roomRepo.GetByRoomNumberAsync(request.RoomNumber);
        if (existingRoom != null) throw new BadRequestException("That room number is already registered.");

        var room = new Room
        {
            Id = Guid.NewGuid(),
            RoomNumber = request.RoomNumber,
            Name = request.Name,
            Category = request.Category,
            Floor = request.Floor,
            PricePerNight = request.PricePerNight,
            Capacity = request.Capacity,
            Size = request.Size,
            Description = request.Description,
            Amenities = NormalizeAmenities(request.Amenities),
            Images = new List<RoomImage>(),
            Status = request.Status,
            IsOnline = request.Status != RoomStatus.Maintenance && (request.IsOnline ?? true),
            CreatedAt = DateTime.UtcNow
        };

        await _roomRepo.AddAsync(room);
        return MapToDto(room);
    }


    public async Task UpdateRoomAsync(Guid id, UpdateRoomRequest request)
    {
        var room = await _roomRepo.GetByIdWithImagesAsync(id);
        if (room == null) throw new NotFoundException("Room not found.");

        if (request.Name != null) room.Name = request.Name.Trim();
        if (request.Category != null) room.Category = request.Category.Value;
        if (request.Floor != null) room.Floor = request.Floor.Value;
        if (request.Status != null)
        {
            var wasInMaintenance = room.Status == RoomStatus.Maintenance;
            room.Status = request.Status.Value;
            if (room.Status == RoomStatus.Maintenance)
            {
                room.IsOnline = false;
            }
            else if (request.IsOnline.HasValue)
            {
                room.IsOnline = request.IsOnline.Value;
            }
            else if (wasInMaintenance)
            {
                // Preserve the existing maintenance toggle behaviour for older clients.
                room.IsOnline = true;
            }
        }
        else if (request.IsOnline.HasValue)
        {
            // A room under maintenance must never be published accidentally.
            room.IsOnline = room.Status != RoomStatus.Maintenance && request.IsOnline.Value;
        }
        if (request.PricePerNight != null) room.PricePerNight = request.PricePerNight.Value;
        if (request.Capacity != null) room.Capacity = request.Capacity.Value;
        if (request.Size != null) room.Size = request.Size.Trim();
        if (request.Description != null) room.Description = request.Description;
        if (request.ReplaceAmenities == true)
        {
            room.Amenities = NormalizeAmenities(request.Amenities);
        }
        else if (request.Amenities != null)
        {
            room.Amenities = NormalizeAmenities(request.Amenities);
        }

        await _roomRepo.UpdateAsync(room);
    }


   public async Task<List<string>> DeleteRoomAsync(Guid id)
{
    var room = await _roomRepo.GetByIdWithImagesAsync(id);
    if (room == null) return new List<string>();

    var publicIds = room.Images
        .Select(img => img.PublicId)
        .Where(id => !string.IsNullOrEmpty(id))
        .ToList();

    await _roomRepo.DeleteAsync(room);

    return publicIds;
}


    private static RoomDto MapToDto(Room r) => new(
        r.Id, r.RoomNumber, r.Name, r.Category, r.Floor, r.Status,
        r.PricePerNight, r.Capacity, r.Size, r.IsOnline, r.Description,
        r.Amenities, r.Images.Select(i => i.Url).ToList(), r.CreatedAt);

    private static List<string> NormalizeAmenities(IEnumerable<string>? amenities) =>
        amenities?
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .ToList() ?? [];
}
