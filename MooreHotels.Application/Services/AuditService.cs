
using MooreHotels.Application.DTOs;
using MooreHotels.Application.Interfaces.Repositories;
using MooreHotels.Application.Interfaces.Services;
using MooreHotels.Domain.Entities;
using System.Text.Json;
using MooreHotels.Application.Exceptions;

namespace MooreHotels.Application.Services;

public class AuditService : IAuditService
{
    private readonly IAuditLogRepository _auditRepo;
    public AuditService(IAuditLogRepository auditRepo) => _auditRepo = auditRepo;

    public async Task<IEnumerable<AuditLogDto>> GetAllLogsAsync()
    {
        var logs = await _auditRepo.GetAllAsync();
        return logs.Select(l => new AuditLogDto(
            l.Id, l.ProfileId, l.Action, l.EntityType, l.EntityId,
            l.OldDataJson, l.NewDataJson, l.CreatedAt));
    }

    public async Task<PagedResult<AuditLogDto>> GetPagedLogsAsync(int pageNumber = 1, int pageSize = 20, string? entityType = null, string? search = null)
    {
        if (entityType?.Length > 100 || entityType?.Any(char.IsControl) == true ||
            search?.Length > 120 || search?.Any(char.IsControl) == true)
        {
            throw new BadRequestException("Audit filters are invalid or too long.");
        }
        var paged = await _auditRepo.GetPagedLogsAsync(pageNumber, pageSize, entityType, search);
        var mapped = paged.Items.Select(l => new AuditLogDto(
            l.Id, l.ProfileId, l.Action, l.EntityType, l.EntityId,
            l.OldDataJson, l.NewDataJson, l.CreatedAt)).ToList();
        return PagedResult<AuditLogDto>.Create(mapped, paged.TotalCount, paged.PageNumber, paged.PageSize);
    }

    public async Task LogActionAsync(Guid userId, string action, string entityType, string entityId, object? oldData = null, object? newData = null)
    {
        if (userId == Guid.Empty)
            throw new UnauthorizedAccessException("The audit actor is invalid.");
        ValidateAuditLabel(action, "Audit action", 100);
        ValidateAuditLabel(entityType, "Audit entity type", 100);
        ValidateAuditLabel(entityId, "Audit entity identifier", 160);
        var log = new AuditLog
        {
            Id = Guid.NewGuid(),
            ProfileId = userId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            OldDataJson = oldData != null ? JsonSerializer.Serialize(oldData) : null,
            NewDataJson = newData != null ? JsonSerializer.Serialize(newData) : null,
            CreatedAt = DateTime.UtcNow
        };
        await _auditRepo.AddAsync(log);
    }

    private static void ValidateAuditLabel(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            throw new BadRequestException($"{field} is invalid or too long.");
        }
    }
}
