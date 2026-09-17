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
    private readonly IHotelTimeService _hotelTime;
    private readonly IAuditService _auditService;

    public RoomService(
        IRoomRepository roomRepo,
        IBookingRepository bookingRepo,
        IHotelTimeService hotelTime,
        IAuditService auditService)
    {
        _roomRepo = roomRepo;
        _bookingRepo = bookingRepo;
        _hotelTime = hotelTime;
        _auditService = auditService;
    }

    public async Task<IEnumerable<RoomDto>> GetAllRoomsAsync(RoomCategory? category = null, bool includeOffline = false)
    {
        if (category.HasValue && !Enum.IsDefined(category.Value))
            throw new BadRequestException("Room category is invalid.");
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
        if (request.Category.HasValue && !Enum.IsDefined(request.Category.Value))
            throw new BadRequestException("Room category is invalid.");
        if (IsInvalidSearchTerm(request.RoomNumber, 30) || IsInvalidSearchTerm(request.Amenity, 120))
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
        if (roomId == Guid.Empty)
            return new RoomAvailabilityResponse(false, "Asset not found in registry.");
        var room = await _roomRepo.GetByIdAsync(roomId);
        if (room == null) return new RoomAvailabilityResponse(false, "Asset not found in registry.");

        if (!room.IsOnline || room.RoomType is not { IsActive: true } ||
            room.Status is RoomStatus.Maintenance or RoomStatus.OutOfOrder)
            return new RoomAvailabilityResponse(false, "Asset is currently offline or under maintenance.");

        var start = _hotelTime.GetCheckInUtc(checkIn);
        var end = _hotelTime.GetCheckOutUtc(checkOut);

        if (DateOnly.FromDateTime(checkIn) < _hotelTime.Today)
            return new RoomAvailabilityResponse(false, "Check-in cannot be in the past.");
        if (start >= end)
            return new RoomAvailabilityResponse(false, $"Invalid range. Standard check-out is {_hotelTime.CheckOutTime:HH\\:mm}.");
        if (DateOnly.FromDateTime(checkOut).DayNumber - DateOnly.FromDateTime(checkIn).DayNumber > 90)
            return new RoomAvailabilityResponse(false, "An availability check cannot exceed 90 nights.");

        var isBooked = await _bookingRepo.IsRoomBookedAsync(roomId, start, end);

        if (isBooked)
            return new RoomAvailabilityResponse(false, "Asset is already secured for these dates.");

        return new RoomAvailabilityResponse(
            true,
            $"Available (Check-in {_hotelTime.CheckInTime:HH\\:mm}, Check-out {_hotelTime.CheckOutTime:HH\\:mm} hotel time).");
    }

    public async Task<RoomDto> CreateRoomAsync(CreateRoomRequest request, Guid actorId)
    {
        RequireActor(actorId);
        var roomNumber = RequireText(request.RoomNumber, "Room number", 30);
        var name = RequireText(request.Name, "Room name", 120);
        var size = RequireText(request.Size, "Room size", 50);
        var description = RequireText(request.Description, "Room description", 4000);
        ValidateRoomValues(request.Category, request.Floor, request.Status,
            request.PricePerNight, request.Capacity, request.RoomTypeId);
        var amenities = NormalizeAmenities(request.Amenities);
        var existingRoom = await _roomRepo.GetByRoomNumberAsync(roomNumber);
        if (existingRoom != null) throw new BadRequestException("That room number is already registered.");

        var roomType = request.RoomTypeId.HasValue
            ? await _roomRepo.GetRoomTypeByIdAsync(request.RoomTypeId.Value)
            : await ResolveOrCreateInitialRoomTypeAsync(
                request.Category,
                request.Capacity,
                request.PricePerNight,
                description,
                amenities,
                actorId);
        if (roomType is null || !roomType.IsActive)
            throw new BadRequestException("Select an active room type.");
        if (roomType.Category != request.Category)
            throw new BadRequestException("The physical room category must match its room type.");
        if (request.Capacity > roomType.MaxOccupancy)
            throw new BadRequestException("Physical-room capacity cannot exceed the room-type maximum occupancy.");

        var room = new Room
        {
            Id = Guid.NewGuid(),
            RoomTypeId = roomType.Id,
            RoomType = roomType,
            RoomNumber = roomNumber,
            Name = name,
            Category = request.Category,
            Floor = request.Floor,
            PricePerNight = request.PricePerNight,
            Capacity = request.Capacity,
            Size = size,
            Description = description,
            Amenities = amenities,
            Images = new List<RoomImage>(),
            Status = request.Status,
            IsOnline = request.Status is not (RoomStatus.Maintenance or RoomStatus.OutOfOrder) &&
                       (request.IsOnline ?? true),
            CreatedAt = DateTime.UtcNow
        };
        if (room.IsOnline)
        {
            room.InventoryPeriods.Add(new RoomInventoryPeriod
            {
                Id = Guid.NewGuid(),
                RoomId = room.Id,
                StartDate = _hotelTime.Today,
                RecordedAtUtc = room.CreatedAt
            });
        }

        await _roomRepo.AddAsync(room);
        await _auditService.LogActionAsync(actorId, "CREATE_ROOM", "Room", room.Id.ToString(),
            newData: new
            {
                room.RoomNumber,
                room.Name,
                room.RoomTypeId,
                room.Category,
                room.Floor,
                room.Status,
                room.PricePerNight,
                room.Capacity,
                room.IsOnline
            });
        return MapToDto(room);
    }

    private async Task<RoomType?> ResolveOrCreateInitialRoomTypeAsync(
        RoomCategory category,
        int capacity,
        decimal pricePerNight,
        string description,
        List<string> amenities,
        Guid actorId)
    {
        var activeType = await _roomRepo.GetDefaultRoomTypeForCategoryAsync(category);
        if (activeType is not null) return activeType;

        // An inactive type is an intentional inventory configuration. Require an
        // administrator to reactivate or replace it instead of silently bypassing it.
        if (await _roomRepo.GetAnyRoomTypeForCategoryAsync(category) is not null)
            return null;

        var now = DateTime.UtcNow;
        var roomType = new RoomType
        {
            Id = Guid.NewGuid(),
            Code = GetInitialRoomTypeCode(category),
            Name = GetInitialRoomTypeName(category),
            Category = category,
            BaseOccupancy = Math.Min(2, capacity),
            MaxOccupancy = capacity,
            BasePricePerNight = pricePerNight,
            Description = description,
            Amenities = [.. amenities],
            IsActive = true,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        await _roomRepo.AddRoomTypeAsync(roomType);
        await _auditService.LogActionAsync(
            actorId,
            "ROOM_TYPE_CREATED",
            "RoomType",
            roomType.Id.ToString(),
            newData: new
            {
                roomType.Code,
                roomType.Name,
                roomType.Category,
                roomType.BaseOccupancy,
                roomType.MaxOccupancy,
                roomType.BasePricePerNight,
                roomType.IsActive,
                Source = "FIRST_PHYSICAL_ROOM"
            });
        return roomType;
    }

    private static string GetInitialRoomTypeCode(RoomCategory category) => category switch
    {
        RoomCategory.Standard => "STANDARD",
        RoomCategory.Deluxe => "DELUXE",
        RoomCategory.Executive => "EXECUTIVE",
        RoomCategory.PresidentialSuite => "PRESIDENTIAL-SUITE",
        _ => throw new BadRequestException("Room category is invalid.")
    };

    private static string GetInitialRoomTypeName(RoomCategory category) => category switch
    {
        RoomCategory.Standard => "Standard",
        RoomCategory.Deluxe => "Deluxe",
        RoomCategory.Executive => "Executive",
        RoomCategory.PresidentialSuite => "Presidential Suite",
        _ => throw new BadRequestException("Room category is invalid.")
    };

    public async Task UpdateRoomAsync(Guid id, UpdateRoomRequest request, Guid actorId)
    {
        RequireActor(actorId);
        if (id == Guid.Empty) throw new NotFoundException("Room not found.");
        var room = await _roomRepo.GetByIdWithImagesAsync(id);
        if (room == null) throw new NotFoundException("Room not found.");
        var wasOnline = room.IsOnline;
        var oldState = new
        {
            room.Name,
            room.RoomTypeId,
            room.Category,
            room.Floor,
            room.Status,
            room.PricePerNight,
            room.Capacity,
            room.Size,
            room.IsOnline
        };

        if (request.Name != null) room.Name = RequireText(request.Name, "Room name", 120);
        if (request.Category.HasValue && !Enum.IsDefined(request.Category.Value))
            throw new BadRequestException("Room category is invalid.");
        if (request.Floor.HasValue && !Enum.IsDefined(request.Floor.Value))
            throw new BadRequestException("Property floor is invalid.");
        if (request.Status.HasValue && !Enum.IsDefined(request.Status.Value))
            throw new BadRequestException("Room status is invalid.");
        if (request.PricePerNight is <= 0 or > 100000000m)
            throw new BadRequestException("Price per night is invalid.");
        if (request.Capacity is < 1 or > 50)
            throw new BadRequestException("Room capacity must be between 1 and 50.");
        if (request.RoomTypeId == Guid.Empty)
            throw new BadRequestException("Room type is invalid.");
        if (request.Size != null) _ = RequireText(request.Size, "Room size", 50);
        if (request.Description != null) _ = RequireOptionalText(request.Description, "Room description", 4000);
        if (request.Amenities != null) _ = NormalizeAmenities(request.Amenities);
        var targetCategory = request.Category ?? room.Category;
        RoomType? targetType = room.RoomType;
        if (request.RoomTypeId.HasValue)
            targetType = await _roomRepo.GetRoomTypeByIdAsync(request.RoomTypeId.Value);
        else if (request.Category.HasValue && request.Category != room.Category)
            targetType = await _roomRepo.GetDefaultRoomTypeForCategoryAsync(request.Category.Value);
        if (targetType is null || !targetType.IsActive)
            throw new BadRequestException("Select an active room type.");
        if (targetType.Category != targetCategory)
            throw new BadRequestException("The physical room category must match its room type.");
        var targetCapacity = request.Capacity ?? room.Capacity;
        if (targetCapacity > targetType.MaxOccupancy)
            throw new BadRequestException("Physical-room capacity cannot exceed the room-type maximum occupancy.");
        room.RoomTypeId = targetType.Id;
        room.RoomType = targetType;
        room.Category = targetCategory;
        if (request.Floor != null) room.Floor = request.Floor.Value;
        if (request.Status != null)
        {
            var wasInMaintenance = room.Status is RoomStatus.Maintenance or RoomStatus.OutOfOrder;
            room.Status = request.Status.Value;
            if (room.Status is RoomStatus.Maintenance or RoomStatus.OutOfOrder)
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
            room.IsOnline = room.Status is not (RoomStatus.Maintenance or RoomStatus.OutOfOrder) &&
                            request.IsOnline.Value;
        }
        if (wasOnline && !room.IsOnline &&
            await _bookingRepo.HasActiveOrFutureRoomReservationAsync(room.Id, DateTime.UtcNow))
        {
            throw new ConflictException(
                "This room has an active or future reservation. Reassign the reservation before taking the room offline.");
        }
        if (wasOnline != room.IsOnline)
        {
            var openPeriod = room.InventoryPeriods.SingleOrDefault(period => !period.EndDate.HasValue);
            if (room.IsOnline)
            {
                if (openPeriod is null)
                {
                    room.InventoryPeriods.Add(new RoomInventoryPeriod
                    {
                        Id = Guid.NewGuid(),
                        RoomId = room.Id,
                        StartDate = _hotelTime.Today,
                        RecordedAtUtc = DateTime.UtcNow
                    });
                }
            }
            else if (openPeriod is not null)
            {
                if (openPeriod.StartDate < _hotelTime.Today)
                    openPeriod.EndDate = _hotelTime.Today;
                else
                    _roomRepo.RemoveInventoryPeriod(openPeriod);
            }
        }
        if (request.PricePerNight != null) room.PricePerNight = request.PricePerNight.Value;
        if (request.Capacity != null) room.Capacity = request.Capacity.Value;
        if (request.Size != null) room.Size = RequireText(request.Size, "Room size", 50);
        if (request.Description != null) room.Description = RequireOptionalText(request.Description, "Room description", 4000);
        if (request.ReplaceAmenities == true)
        {
            room.Amenities = NormalizeAmenities(request.Amenities);
        }
        else if (request.Amenities != null)
        {
            room.Amenities = NormalizeAmenities(request.Amenities);
        }

        await _roomRepo.UpdateAsync(room);
        await _auditService.LogActionAsync(actorId, "UPDATE_ROOM", "Room", room.Id.ToString(),
            oldData: oldState,
            newData: new
            {
                room.Name,
                room.RoomTypeId,
                room.Category,
                room.Floor,
                room.Status,
                room.PricePerNight,
                room.Capacity,
                room.Size,
                room.IsOnline
            });
    }

    public async Task<List<string>> DeleteRoomAsync(Guid id, Guid actorId)
    {
        RequireActor(actorId);
        var room = await _roomRepo.GetByIdWithImagesAsync(id);
        if (room == null) return new List<string>();
        if (room.InventoryPeriods.Count > 0)
        {
            throw new ConflictException(
                "A room that has contributed sellable inventory cannot be deleted. Mark it offline instead.");
        }

        var publicIds = room.Images
            .Select(img => img.PublicId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        await _roomRepo.DeleteAsync(room);
        await _auditService.LogActionAsync(actorId, "DELETE_ROOM", "Room", room.Id.ToString(),
            oldData: new { room.RoomNumber, room.Name, room.RoomTypeId, room.Category });

        return publicIds;
    }

    private static RoomDto MapToDto(Room r) => new(
        r.Id, r.RoomNumber, r.Name, r.Category, r.Floor, r.Status,
        r.PricePerNight, r.Capacity, r.Size, r.IsOnline, r.Description,
        r.Amenities, r.Images.Select(i => i.Url).ToList(), r.CreatedAt,
        r.RoomTypeId, r.RoomType?.Code, r.RoomType?.Name);

    private static List<string> NormalizeAmenities(IEnumerable<string>? amenities)
    {
        var values = amenities?.ToList() ?? [];
        if (values.Count > 50 || values.Any(value =>
                value is null || value.Length > 120 || value.Any(char.IsControl)))
            throw new BadRequestException("Room amenities are invalid or too long.");
        return values
            .Select(value => value.Trim())
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void ValidateRoomValues(
        RoomCategory category,
        PropertyFloor floor,
        RoomStatus status,
        decimal pricePerNight,
        int capacity,
        Guid? roomTypeId)
    {
        if (!Enum.IsDefined(category)) throw new BadRequestException("Room category is invalid.");
        if (!Enum.IsDefined(floor)) throw new BadRequestException("Property floor is invalid.");
        if (!Enum.IsDefined(status)) throw new BadRequestException("Room status is invalid.");
        if (pricePerNight is <= 0 or > 100000000m) throw new BadRequestException("Price per night is invalid.");
        if (capacity is < 1 or > 50) throw new BadRequestException("Room capacity must be between 1 and 50.");
        if (roomTypeId == Guid.Empty) throw new BadRequestException("Room type is invalid.");
    }

    private static string RequireText(string? value, string field, int maximumLength)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length == 0 || cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is invalid or too long.");
        return cleaned;
    }

    private static string RequireOptionalText(string value, string field, int maximumLength)
    {
        var cleaned = value.Trim();
        if (cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is invalid or too long.");
        return cleaned;
    }

    private static bool IsInvalidSearchTerm(string? value, int maximumLength) =>
        value is not null && (value.Length > maximumLength || value.Any(char.IsControl));

    private static void RequireActor(Guid actorId)
    {
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("The authenticated actor is invalid.");
    }
}
