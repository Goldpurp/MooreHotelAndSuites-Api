using MooreHotels.Domain.Enums;

namespace MooreHotels.Application.Interfaces.Repositories;

public sealed record ManualTransferConfirmationActor(
    Guid UserId,
    string Name,
    UserRole Role,
    string RequestId);
