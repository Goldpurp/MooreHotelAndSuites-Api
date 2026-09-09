using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using AddOnEntity = MooreHotels.Domain.Entities.AddOnService;
using MooreHotels.Application.Interfaces;

namespace MooreHotels.Application.Services;

public class AddOnService : IAddOnService
{
    private readonly IAddOnRepository _addOnRepo;
    private readonly IBookingRepository _bookingRepo;
    private readonly IAuditService _auditService;
    private readonly IApplicationTransaction _transaction;

    public AddOnService(
        IAddOnRepository addOnRepo,
        IBookingRepository bookingRepo,
        IAuditService auditService,
        IApplicationTransaction transaction)
    {
        _addOnRepo = addOnRepo;
        _bookingRepo = bookingRepo;
        _auditService = auditService;
        _transaction = transaction;
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

    public async Task<AddOnServiceDto> CreateServiceAsync(
        CreateAddOnServiceRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var name = RequireText(request.Name, "Service name", 120);
        var description = Clean(request.Description, "Service description", 500) ?? string.Empty;
        ValidateCategory(request.Category);
        ValidatePrice(request.Price);
        ValidateActor(actorId);

        var entity = new AddOnEntity
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            Category = request.Category,
            Price = request.Price,
            IsActive = request.IsActive,
            CreatedAt = DateTime.UtcNow
        };

        await _transaction.ExecuteAsync(async () =>
        {
            await _addOnRepo.AddServiceAsync(entity, cancellationToken);
            await _auditService.LogActionAsync(
                actorId,
                "ADDON_SERVICE_CREATED",
                "AddOnService",
                entity.Id.ToString(),
                newData: new { entity.Name, entity.Category, entity.Price, entity.IsActive });
        }, cancellationToken);
        return MapToDto(entity);
    }

    public async Task<AddOnServiceDto> UpdateServiceAsync(
        Guid id,
        UpdateAddOnServiceRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _addOnRepo.GetServiceByIdAsync(id, cancellationToken);
        if (entity == null) throw new NotFoundException("Add-on service not found.");
        ValidateActor(actorId);
        var oldData = new { entity.Name, entity.Category, entity.Price, entity.IsActive };

        if (request.Name != null) entity.Name = RequireText(request.Name, "Service name", 120);
        if (request.Description != null)
            entity.Description = Clean(request.Description, "Service description", 500) ?? string.Empty;
        if (request.Category.HasValue)
        {
            ValidateCategory(request.Category.Value);
            entity.Category = request.Category.Value;
        }
        if (request.Price.HasValue)
        {
            ValidatePrice(request.Price.Value);
            entity.Price = request.Price.Value;
        }
        if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;

        await _transaction.ExecuteAsync(async () =>
        {
            await _addOnRepo.UpdateServiceAsync(entity, cancellationToken);
            await _auditService.LogActionAsync(
                actorId,
                "ADDON_SERVICE_UPDATED",
                "AddOnService",
                entity.Id.ToString(),
                oldData,
                new { entity.Name, entity.Category, entity.Price, entity.IsActive });
        }, cancellationToken);
        return MapToDto(entity);
    }

    public async Task DeleteServiceAsync(
        Guid id,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var entity = await _addOnRepo.GetServiceByIdAsync(id, cancellationToken);
        if (entity == null) return;
        ValidateActor(actorId);
        if (!entity.IsActive) return;

        await _transaction.ExecuteAsync(async () =>
        {
            entity.IsActive = false;
            await _addOnRepo.UpdateServiceAsync(entity, cancellationToken);
            await _auditService.LogActionAsync(
                actorId,
                "ADDON_SERVICE_DEACTIVATED",
                "AddOnService",
                entity.Id.ToString(),
                oldData: new { IsActive = true },
                newData: new { IsActive = false });
        }, cancellationToken);
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
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(bookingCode) || bookingCode.Length > 30)
            throw new BadRequestException("Booking code is invalid.");
        if (request.AddOnServiceId == Guid.Empty)
            throw new BadRequestException("Select an add-on service.");
        if (request.Quantity is < 1 or > 1000)
            throw new BadRequestException("Quantity must be between 1 and 1000.");
        var notes = Clean(request.Notes, "Notes", 300);
        ValidateActor(actorId);
        var entity = await _addOnRepo.AddBookingAddOnAndUpdateTotalAsync(
            bookingCode.Trim().ToUpperInvariant(),
            request.AddOnServiceId,
            request.Quantity,
            notes,
            DateTime.UtcNow,
            actorId,
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

    private static void ValidateActor(Guid actorId)
    {
        if (actorId == Guid.Empty)
            throw new UnauthorizedAccessException("The authenticated actor is invalid.");
    }

    private static void ValidateCategory(AddOnCategory category)
    {
        if (!Enum.IsDefined(category))
            throw new BadRequestException("The add-on category is invalid.");
    }

    private static void ValidatePrice(decimal price)
    {
        if (price is <= 0 or > 9999999999999999m)
            throw new BadRequestException("Service price is outside the supported range.");
    }

    private static string RequireText(string? value, string field, int maximumLength) =>
        Clean(value, field, maximumLength)
        ?? throw new BadRequestException($"{field} is required.");

    private static string? Clean(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = value.Trim();
        if (cleaned.Length > maximumLength || cleaned.Any(char.IsControl))
            throw new BadRequestException($"{field} is invalid or too long.");
        return cleaned;
    }
}
