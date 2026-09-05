
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;

namespace MooreHotels.Application.Services;

public class VisitRecordService : IVisitRecordService
{
    private readonly IVisitRecordRepository _visitRepo;
    private readonly IBookingRepository _bookingRepo;

    public VisitRecordService(IVisitRecordRepository visitRepo, IBookingRepository bookingRepo)
    {
        _visitRepo = visitRepo;
        _bookingRepo = bookingRepo;
    }

    public async Task<IEnumerable<VisitRecordDto>> GetAllRecordsAsync()
    {
        var records = await _visitRepo.GetAllAsync();
        return records.Select(v => new VisitRecordDto(
            v.Id, v.GuestId, v.GuestName, v.RoomNumber, v.BookingCode,
            v.Action, v.Timestamp, v.AuthorizedBy));
    }

    public async Task<PagedResult<VisitRecordDto>> GetPagedRecordsAsync(int pageNumber = 1, int pageSize = 20, string? search = null)
    {
        var paged = await _visitRepo.GetPagedRecordsAsync(pageNumber, pageSize, search);
        var mapped = paged.Items.Select(v => new VisitRecordDto(
            v.Id, v.GuestId, v.GuestName, v.RoomNumber, v.BookingCode,
            v.Action, v.Timestamp, v.AuthorizedBy)).ToList();
        return PagedResult<VisitRecordDto>.Create(mapped, paged.TotalCount, paged.PageNumber, paged.PageSize);
    }

    public async Task CreateRecordAsync(string bookingCode, string action, string authorizedBy)
    {
        var booking = await _bookingRepo.GetByCodeAsync(bookingCode);
        if (booking == null) throw new NotFoundException("Invalid booking code.");
        var assignedUnit = booking.ReservationRooms
            .OrderBy(item => item.Sequence)
            .FirstOrDefault(item => item.AssignedRoomId.HasValue);
        var roomId = assignedUnit?.AssignedRoomId ?? booking.RoomId;
        if (!roomId.HasValue)
            throw new BadRequestException("Assign a physical room before recording a stay event.");

        var record = new VisitRecord
        {
            Id = Guid.NewGuid(),
            BookingCode = bookingCode,
            GuestId = booking.GuestId,
            GuestName = $"{booking.Guest?.FirstName} {booking.Guest?.LastName}",
            RoomId = roomId.Value,
            RoomNumber = assignedUnit?.AssignedRoom?.RoomNumber ?? booking.Room?.RoomNumber ?? "N/A",
            Action = action,
            Timestamp = DateTime.UtcNow,
            AuthorizedBy = authorizedBy
        };

        await _visitRepo.AddAsync(record);
    }
}
