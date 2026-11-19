namespace BuildingBlocks.Common;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, long TotalCount)
{
    public static PagedResult<T> Empty(int page = 1, int pageSize = 20) => new([], page, pageSize, 0);
}
