using System.Data;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Exceptions;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using MooreHotels.Domain.Enums;
using MooreHotels.Infrastructure.Persistence;

namespace MooreHotels.Infrastructure.Services;

public sealed class ChannelManagementService : IChannelManagementService
{
    private const int MaximumPayloadBytes = 128 * 1024;
    private readonly MooreHotelsDbContext _db;
    private readonly IInventoryService _inventory;

    public ChannelManagementService(MooreHotelsDbContext db, IInventoryService inventory)
    {
        _db = db;
        _inventory = inventory;
    }

    public async Task<IReadOnlyList<DistributionChannelDto>> GetChannelsAsync(
        CancellationToken cancellationToken = default) =>
        await _db.DistributionChannels.AsNoTracking().OrderBy(item => item.Code)
            .Select(item => new DistributionChannelDto(
                item.Id, item.Code, item.Name, item.IsActive, item.UpdatedAtUtc))
            .ToListAsync(cancellationToken);

    public async Task<DistributionChannelDto> SaveChannelAsync(
        Guid? id,
        SaveDistributionChannelRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var code = NormalizeCode(request.Code);
        var name = RequireText(request.Name, "Channel name", 120);
        if (await _db.DistributionChannels.AnyAsync(item => item.Code == code && item.Id != id, cancellationToken))
            throw new ConflictException("The distribution channel code already exists.");
        var now = DateTime.UtcNow;
        var channel = id.HasValue
            ? await _db.DistributionChannels.SingleOrDefaultAsync(item => item.Id == id, cancellationToken)
              ?? throw new NotFoundException("Distribution channel not found.")
            : new DistributionChannel { Id = Guid.NewGuid(), CreatedAtUtc = now };
        channel.Code = code;
        channel.Name = name;
        channel.IsActive = request.IsActive;
        channel.UpdatedAtUtc = now;
        if (!id.HasValue) _db.DistributionChannels.Add(channel);
        AddAudit(actorId, id.HasValue ? "CHANNEL_UPDATED" : "CHANNEL_CREATED", "DistributionChannel",
            channel.Id, new { channel.Code, channel.Name, channel.IsActive });
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(channel);
    }

    public async Task<ChannelEventDto> ReceiveEventAsync(
        string channelCode,
        ReceiveChannelEventRequest request,
        CancellationToken cancellationToken = default)
    {
        var code = NormalizeCode(channelCode);
        var payload = ValidateJson(request.PayloadJson);
        var key = RequireText(request.IdempotencyKey, "Idempotency key", 160);
        var eventType = RequireText(request.EventType, "Event type", 80);
        var strategy = _db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var transaction = await _db.Database.BeginTransactionAsync(
                IsolationLevel.ReadCommitted, cancellationToken);
            var channel = await _db.DistributionChannels
                .FromSqlInterpolated($"SELECT * FROM distribution_channels WHERE \"Code\" = {code} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken)
                ?? throw new NotFoundException("Distribution channel not found.");
            if (!channel.IsActive) throw new BadRequestException("The distribution channel is disabled.");
            var existing = await _db.ChannelEvents.SingleOrDefaultAsync(item =>
                item.ChannelId == channel.Id && item.IdempotencyKey == key, cancellationToken);
            if (existing is not null)
            {
                if (existing.EventType != eventType || !JsonEquivalent(existing.PayloadJson, payload) ||
                    existing.ExternalReservationId != Clean(request.ExternalReservationId))
                    throw new ConflictException("The channel idempotency key is already used for different event data.");
                await transaction.CommitAsync(cancellationToken);
                existing.Channel = channel;
                return ToDto(existing);
            }
            var now = DateTime.UtcNow;
            var channelEvent = new ChannelEvent
            {
                Id = Guid.NewGuid(),
                ChannelId = channel.Id,
                Direction = ChannelEventDirection.Inbound,
                Status = ChannelEventStatus.Pending,
                EventType = eventType,
                IdempotencyKey = key,
                ExternalReservationId = Clean(request.ExternalReservationId),
                PayloadJson = payload,
                OccurredAtUtc = request.OccurredAtUtc.HasValue
                    ? EnsureUtc(request.OccurredAtUtc.Value) : now,
                CreatedAtUtc = now
            };
            _db.ChannelEvents.Add(channelEvent);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            channelEvent.Channel = channel;
            return ToDto(channelEvent);
        });
    }

    public async Task<ChannelEventDto> QueueInventoryAsync(
        Guid channelId,
        QueueChannelInventoryRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        if (request.ToDate < request.FromDate || request.ToDate.DayNumber - request.FromDate.DayNumber > 365)
            throw new BadRequestException("Inventory export dates must be ordered and cannot exceed 366 days.");
        var channel = await RequireActiveChannelAsync(channelId, cancellationToken);
        var availability = await _inventory.GetAvailabilityAsync(
            request.RoomTypeId, request.FromDate, request.ToDate.AddDays(1), 1, cancellationToken);
        var key = RequireText(request.IdempotencyKey, "Idempotency key", 160);
        var existing = await _db.ChannelEvents.Include(item => item.Channel).SingleOrDefaultAsync(item =>
            item.ChannelId == channelId && item.IdempotencyKey == key, cancellationToken);
        var payload = JsonSerializer.Serialize(new
        {
            SchemaVersion = "inventory.v1",
            availability.RoomTypeId,
            availability.RoomTypeCode,
            FromDate = request.FromDate,
            ToDate = request.ToDate,
            Days = availability.Days.Select(day => new { day.StayDate, day.AvailableUnits })
        });
        if (existing is not null)
        {
            if (existing.Direction != ChannelEventDirection.Outbound || !JsonEquivalent(existing.PayloadJson, payload))
                throw new ConflictException("The channel idempotency key is already used for different event data.");
            return ToDto(existing);
        }
        var now = DateTime.UtcNow;
        var channelEvent = new ChannelEvent
        {
            Id = Guid.NewGuid(),
            ChannelId = channel.Id,
            Channel = channel,
            Direction = ChannelEventDirection.Outbound,
            Status = ChannelEventStatus.Pending,
            EventType = "INVENTORY_UPDATE",
            IdempotencyKey = key,
            PayloadJson = payload,
            OccurredAtUtc = now,
            CreatedAtUtc = now
        };
        _db.ChannelEvents.Add(channelEvent);
        AddAudit(actorId, "CHANNEL_INVENTORY_QUEUED", "ChannelEvent", channelEvent.Id,
            new { channel.Id, channel.Code, request.RoomTypeId, request.FromDate, request.ToDate });
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(channelEvent);
    }

    public async Task<ChannelEventDto> UpdateEventAsync(
        Guid eventId,
        UpdateChannelEventRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var channelEvent = await _db.ChannelEvents.Include(item => item.Channel)
            .SingleOrDefaultAsync(item => item.Id == eventId, cancellationToken)
            ?? throw new NotFoundException("Channel event not found.");
        if (channelEvent.Status == request.Status) return ToDto(channelEvent);
        ValidateTransition(channelEvent.Status, request.Status);
        var previous = channelEvent.Status;
        channelEvent.Status = request.Status;
        channelEvent.AttemptCount++;
        channelEvent.LastError = request.Status is ChannelEventStatus.Failed or ChannelEventStatus.DeadLetter
            ? RequireText(request.Error, "Failure reason", 1000) : null;
        channelEvent.ProcessedAtUtc = request.Status == ChannelEventStatus.Processed ? DateTime.UtcNow : null;
        AddAudit(actorId, "CHANNEL_EVENT_UPDATED", "ChannelEvent", channelEvent.Id,
            new { PreviousStatus = previous, channelEvent.Status, channelEvent.AttemptCount, channelEvent.LastError });
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(channelEvent);
    }

    public async Task<ChannelReservationMappingDto> LinkReservationAsync(
        Guid channelId,
        LinkChannelReservationRequest request,
        Guid actorId,
        CancellationToken cancellationToken = default)
    {
        var channel = await RequireActiveChannelAsync(channelId, cancellationToken);
        var externalId = RequireText(request.ExternalReservationId, "External reservation ID", 160);
        var booking = await _db.Bookings.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == request.BookingId, cancellationToken)
            ?? throw new NotFoundException("Booking not found.");
        var existing = await _db.ChannelReservationMappings.Include(item => item.Channel)
            .Include(item => item.Booking).SingleOrDefaultAsync(item =>
                item.ChannelId == channelId &&
                (item.ExternalReservationId == externalId || item.BookingId == booking.Id), cancellationToken);
        if (existing is not null)
        {
            if (existing.ExternalReservationId != externalId || existing.BookingId != booking.Id)
                throw new ConflictException("The channel reservation or booking is already linked differently.");
            return ToDto(existing);
        }
        var mapping = new ChannelReservationMapping
        {
            Id = Guid.NewGuid(),
            ChannelId = channel.Id,
            Channel = channel,
            ExternalReservationId = externalId,
            BookingId = booking.Id,
            Booking = booking,
            LinkedAtUtc = DateTime.UtcNow,
            LinkedByUserId = actorId
        };
        _db.ChannelReservationMappings.Add(mapping);
        AddAudit(actorId, "CHANNEL_RESERVATION_LINKED", "ChannelReservationMapping", mapping.Id,
            new { channel.Code, mapping.ExternalReservationId, booking.BookingCode });
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(mapping);
    }

    public async Task<ChannelReconciliationDto> GetReconciliationAsync(
        Guid channelId,
        CancellationToken cancellationToken = default)
    {
        var channel = await _db.DistributionChannels.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == channelId, cancellationToken)
            ?? throw new NotFoundException("Distribution channel not found.");
        var events = _db.ChannelEvents.AsNoTracking().Where(item => item.ChannelId == channelId);
        var mappings = _db.ChannelReservationMappings.AsNoTracking().Where(item => item.ChannelId == channelId);
        var pending = await events.CountAsync(item => item.Status == ChannelEventStatus.Pending, cancellationToken);
        var failed = await events.CountAsync(item => item.Status == ChannelEventStatus.Failed, cancellationToken);
        var deadLetter = await events.CountAsync(item => item.Status == ChannelEventStatus.DeadLetter, cancellationToken);
        var unmapped = await events.CountAsync(item =>
            item.Direction == ChannelEventDirection.Inbound && item.ExternalReservationId != null &&
            EF.Functions.ILike(item.EventType, "%RESERVATION%") &&
            !mappings.Any(mapping => mapping.ExternalReservationId == item.ExternalReservationId), cancellationToken);
        var oldest = await events.Where(item => item.Status == ChannelEventStatus.Pending)
            .OrderBy(item => item.CreatedAtUtc).Select(item => (DateTime?)item.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return new ChannelReconciliationDto(
            channel.Id, channel.Code, pending, failed, deadLetter, unmapped, oldest, DateTime.UtcNow);
    }

    private async Task<DistributionChannel> RequireActiveChannelAsync(Guid id, CancellationToken ct)
    {
        var channel = await _db.DistributionChannels.SingleOrDefaultAsync(item => item.Id == id, ct)
            ?? throw new NotFoundException("Distribution channel not found.");
        if (!channel.IsActive) throw new BadRequestException("The distribution channel is disabled.");
        return channel;
    }

    private static void ValidateTransition(ChannelEventStatus current, ChannelEventStatus requested)
    {
        if (current == requested) return;
        var valid = current switch
        {
            ChannelEventStatus.Pending => requested is ChannelEventStatus.Processed or ChannelEventStatus.Failed or ChannelEventStatus.DeadLetter,
            ChannelEventStatus.Failed => requested is ChannelEventStatus.Pending or ChannelEventStatus.Processed or ChannelEventStatus.DeadLetter,
            _ => false
        };
        if (!valid) throw new BadRequestException($"Cannot move a channel event from {current} to {requested}.");
    }

    private static string ValidateJson(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > MaximumPayloadBytes)
            throw new BadRequestException("Channel payload must be valid JSON no larger than 128 KiB.");
        try
        {
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 64 });
            return document.RootElement.GetRawText();
        }
        catch (JsonException)
        {
            throw new BadRequestException("Channel payload is not valid JSON.");
        }
    }

    private static bool JsonEquivalent(string left, string right)
    {
        try
        {
            using var leftDocument = JsonDocument.Parse(left);
            using var rightDocument = JsonDocument.Parse(right);
            return JsonElement.DeepEquals(leftDocument.RootElement, rightDocument.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private void AddAudit(Guid actorId, string action, string entityType, Guid id, object data) =>
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = id.ToString(),
            NewDataJson = JsonSerializer.Serialize(data),
            CreatedAt = DateTime.UtcNow
        });

    private static DistributionChannelDto ToDto(DistributionChannel item) =>
        new(item.Id, item.Code, item.Name, item.IsActive, item.UpdatedAtUtc);
    private static ChannelEventDto ToDto(ChannelEvent item) => new(
        item.Id, item.ChannelId, item.Channel?.Code ?? string.Empty, item.Direction, item.Status,
        item.EventType, item.IdempotencyKey, item.ExternalReservationId, item.PayloadJson,
        item.AttemptCount, item.LastError, item.OccurredAtUtc, item.CreatedAtUtc, item.ProcessedAtUtc);
    private static ChannelReservationMappingDto ToDto(ChannelReservationMapping item) => new(
        item.Id, item.ChannelId, item.Channel?.Code ?? string.Empty, item.ExternalReservationId,
        item.BookingId, item.Booking?.BookingCode ?? string.Empty, item.LinkedAtUtc);
    private static string NormalizeCode(string value) => RequireText(value, "Channel code", 40).ToUpperInvariant();
    private static string RequireText(string? value, string field, int max)
    {
        var cleaned = value?.Trim() ?? string.Empty;
        if (cleaned.Length == 0 || cleaned.Length > max)
            throw new BadRequestException($"{field} is required and cannot exceed {max} characters.");
        return cleaned;
    }
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static DateTime EnsureUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
