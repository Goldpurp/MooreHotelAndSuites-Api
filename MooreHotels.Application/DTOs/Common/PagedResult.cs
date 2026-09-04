namespace MooreHotels.Application.DTOs;

public record PagedRequest(
    int PageNumber = 1,
    int PageSize = 20,
    string? Search = null,
    string? SortBy = null,
    bool IsAscending = false)
{
    public int NormalizedPageNumber => Math.Max(1, PageNumber);
    public int NormalizedPageSize => Math.Clamp(PageSize, 1, 100);
}

public record PagedResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int PageNumber,
    int PageSize,
    int TotalPages)
{
    public bool HasPreviousPage => PageNumber > 1;
    public bool HasNextPage => PageNumber < TotalPages;

    public static PagedResult<T> Create(
        IReadOnlyList<T> items,
        int totalCount,
        int pageNumber,
        int pageSize)
    {
        var totalPages = pageSize > 0
            ? (int)Math.Ceiling(totalCount / (double)pageSize)
            : 0;

        return new PagedResult<T>(
            items,
            totalCount,
            pageNumber,
            pageSize,
            totalPages);
    }
}
