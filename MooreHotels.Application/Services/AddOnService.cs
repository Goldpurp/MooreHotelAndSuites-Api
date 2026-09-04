using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using AddOnEntity = MooreHotels.Domain.Entities.AddOnService;

namespace MooreHotels.Application.Services;

public class AddOnService : IAddOnService
{
    private readonly IAddOnRepository _addOnRepo;
    private readonly IBookingRepository _bookingRepo;

    public AddOnService(IAddOnRepository addOnRepo, IBookingRepository bookingRepo)
    {
        _addOnRepo = addOnRepo;
        _bookingRepo = bookingRepo;
    }

    public async Task<IEnumerable<AddOnServiceDto>> GetAllServicesAsync(
        bool onlyActive = true,
        AddOnCategory? category = null,
        CancellationToken cancellationToken = default)
    {
        var services = await _addOnRepo.GetAllServicesAsync(onlyActive, category, cancellationToken);
        return services.Select(MapToDto);
    }

    public async Task<AddOnServiceDto?> GetServiceByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var service = await _addOnRepo.GetServiceByIdAsync(id, cancellationToken);
        return service != null ? MapToDto(service) : null;
    }

    public async Task<AddOnServiceDto> CreateServiceAsync(CreateAddOnServiceRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new BadRequestException("Service name is required.");
        if (request.Price <= 0)
            throw new BadRequestException("Service price must be greater than zero.");

        var entity = new AddOnEntity
        {
            Id = Guid.NewGuid(),
            Name = request.Name.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Category = request.Category,
            Price = request.Price,
            IsActive = request.IsActive,
            CreatedAt = DateTime.UtcNow
        };

        await _addOnRepo.AddServiceAsync(entity, cancellationToken);
        return MapToDto(entity);
    }

    public async Task<AddOnServiceDto> UpdateServiceAsync(Guid id, UpdateAddOnServiceRequest request, CancellationToken cancellationToken = default)
    {
        var entity = await _addOnRepo.GetServiceByIdAsync(id, cancellationToken);
        if (entity == null) throw new NotFoundException("Add-on service not found.");

        if (request.Name != null) entity.Name = request.Name.Trim();
        if (request.Description != null) entity.Description = request.Description.Trim();
        if (request.Category.HasValue) entity.Category = request.Category.Value;
        if (request.Price.HasValue)
        {
            if (request.Price.Value <= 0) throw new BadRequestException("Service price must be greater than zero.");
            entity.Price = request.Price.Value;
        }
        if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;

        await _addOnRepo.UpdateServiceAsync(entity, cancellationToken);
        return MapToDto(entity);
    }

    public async Task DeleteServiceAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _addOnRepo.GetServiceByIdAsync(id, cancellationToken);
        if (entity == null) return;
        await _addOnRepo.DeleteServiceAsync(entity, cancellationToken);
    }

    public async Task<IEnumerable<BookingAddOnDto>> GetBookingAddOnsAsync(string bookingCode, CancellationToken cancellationToken = default)
    {
        var booking = await _bookingRepo.GetByCodeAsync(bookingCode);
        if (booking == null) throw new NotFoundException("Booking not found.");

        var items = await _addOnRepo.GetBookingAddOnsAsync(booking.Id, cancellationToken);
        return items.Select(MapToBookingDto);
    }

    public async Task<BookingAddOnDto> AddServiceToBookingAsync(
        string bookingCode,
        AddServiceToBookingRequest request,
        CancellationToken cancellationToken = default)
    {
        var entity = await _addOnRepo.AddBookingAddOnAndUpdateTotalAsync(
            bookingCode.Trim().ToUpperInvariant(),
            request.AddOnServiceId,
            request.Quantity,
            request.Notes?.Trim(),
            DateTime.UtcNow,
            cancellationToken);

        return MapToBookingDto(entity);
    }

    private static AddOnServiceDto MapToDto(AddOnEntity a) =>
        new(a.Id, a.Name, a.Description, a.Category, a.Price, a.IsActive, a.CreatedAt);

    private static BookingAddOnDto MapToBookingDto(BookingAddOn b) =>
        new(
            b.Id,
            b.BookingId,
            b.AddOnServiceId,
            b.AddOnService?.Name ?? "Custom Service",
            b.AddOnService?.Category ?? AddOnCategory.Other,
            b.Quantity,
            b.UnitPrice,
            b.TotalPrice,
            b.Notes,
            b.AddedAtUtc);
}
