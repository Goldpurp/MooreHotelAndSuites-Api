
namespace MooreHotels.Application.DTOs;

public record AuditLogDto(
    Guid Id,
    Guid ProfileId,
    string Action,
    string EntityType,
    string EntityId,
    string? OldData,
    string? NewData,
    DateTime CreatedAt);
