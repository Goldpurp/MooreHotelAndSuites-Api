using MooreHotels.Application.DTOs;

namespace MooreHotels.Application.Interfaces.Services;

public interface IClientGuestReconciliationService
{
    Task<PagedResult<ClientGuestLinkIssueDto>> GetIssuesAsync(
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<ClientGuestReconciliationResult> ReconcileAsync(
        Guid userId,
        ReconcileClientGuestRequest request,
        Guid actingUserId,
        string requestId,
        CancellationToken cancellationToken = default);
}
