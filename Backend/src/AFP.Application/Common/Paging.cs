namespace AFP.Application.Common;

public record PageRequest(int Page = 1, int PageSize = 25)
{
    public const int DefaultMaxPageSize = 200;

    public PageRequest Normalize(int maxPageSize = DefaultMaxPageSize) =>
        new(Math.Max(1, Page), Math.Clamp(PageSize <= 0 ? 25 : PageSize, 1, maxPageSize));

    public int Skip => (Page - 1) * PageSize;
}

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public PagedResult<TOut> Map<TOut>(Func<T, TOut> map) => new(Items.Select(map).ToList(), Page, PageSize, TotalCount);
}
