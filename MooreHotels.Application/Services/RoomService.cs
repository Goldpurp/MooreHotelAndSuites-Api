using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Services;

public class RoomService : IRoomService
{
    private readonly IRoomRepository _roomRepo;
    private readonly IBookingRepository _bookingRepo;
    private readonly IImageService _imageService;
    private readonly IHotelTimeService _hotelTime;

    public RoomService(
        IRoomRepository roomRepo,
        IBookingRepository bookingRepo,
        IImageService imageService,
        IHotelTimeService hotelTime)
    {
        _roomRepo = roomRepo;
        _bookingRepo = bookingRepo;
        _imageService = imageService;
        _hotelTime = hotelTime;
    }

    public async Task<IEnumerable<RoomDto>> GetAllRoomsAsync(RoomCategory? category = null, bool includeOffline = false)
    {
        if (includeOffline)
        {
            var adminRooms = await _roomRepo.GetAllAsync(onlyOnline: false);
            if (category.HasValue) adminRooms = adminRooms.Where(r => r.Category == category);
            return adminRooms.Select(MapToDto);
        }

        var rooms = await _roomRepo.GetAllAsync(onlyOnline: true);
        if (category.HasValue) rooms = rooms.Where(r => r.Category == category);

        return rooms.Select(MapToDto).ToList();
    }

    public async Task<IEnumerable<RoomDto>> SearchRoomsAsync(RoomSearchRequest request)
    {
        if (request.CheckIn.HasValue != request.CheckOut.HasValue)
        {
            throw new BadRequestException("Both check-in and check-out dates are required for a date search.");
        }
        if (request.Capacity is < 1 or > 50)
        {
            throw new BadRequestException("Guest count must be between 1 and 50.");
        }
        if (request.RoomNumber?.Length > 30 || request.Amenity?.Length > 120)
        {
            throw new BadRequestException("A search term is too long.");
        }
        if (request.CheckIn.HasValue && request.CheckOut.HasValue)
        {
            var checkInDate = DateOnly.FromDateTime(request.CheckIn.Value);
            var checkOutDate = DateOnly.FromDateTime(request.CheckOut.Value);
            if (checkInDate < _hotelTime.Today)
            {
                throw new BadRequestException("Check-in cannot be in the past.");
            }
            if (checkOutDate <= checkInDate)
            {
                throw new BadRequestException("Check-out must be after check-in.");
            }
            if (checkOutDate.DayNumber - checkInDate.DayNumber > 90)
            {
                throw new BadRequestException("A search cannot exceed 90 nights.");
            }
        }

        DateTime? checkIn = request.CheckIn.HasValue
            ? _hotelTime.GetCheckInUtc(request.CheckIn.Value)
            : null;
        DateTime? checkOut = request.CheckOut.HasValue
            ? _hotelTime.GetCheckOutUtc(request.CheckOut.Value)
            : null;

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
        if (room == null) return null;
        return MapToDto(room);
    }

    public async Task<RoomAvailabilityResponse> CheckAvailabilityAsync(Guid roomId, DateTime checkIn, DateTime checkOut)
    {
        var room = await _roomRepo.GetByIdAsync(roomId);
        if (room == null) return new RoomAvailabilityResponse(false, "Asset not found in registry.");

        if (!room.IsOnline)
            return new RoomAvailabilityResponse(false, "Asset is currently offline or under maintenance.");

        var start = _hotelTime.GetCheckInUtc(checkIn);
        var end = _hotelTime.GetCheckOutUtc(checkOut);

        if (start >= end)
            return new RoomAvailabilityResponse(false, $"Invalid range. Standard check-out is {_hotelTime.CheckOutTime:HH\\:mm}.");

        var isBooked = await _bookingRepo.IsRoomBookedAsync(roomId, start, end);

        if (isBooked)
            return new RoomAvailabilityResponse(false, "Asset is already secured for these dates.");

        return new RoomAvailabilityResponse(
            true,
            $"Available (Check-in {_hotelTime.CheckInTime:HH\\:mm}, Check-out {_hotelTime.CheckOutTime:HH\\:mm} hotel time).");
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
