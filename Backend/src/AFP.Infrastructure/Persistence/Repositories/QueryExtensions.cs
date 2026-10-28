using AFP.Application.Common;
using Microsoft.EntityFrameworkCore;

namespace AFP.Infrastructure.Persistence.Repositories;

internal static class QueryExtensions
{
    public static async Task<PagedResult<T>> ToPagedAsync<T>(this IQueryable<T> query, PageRequest page, CancellationToken ct)
    {
        var total = await query.CountAsync(ct);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
        return new PagedResult<T>(items, page.Page, page.PageSize, total);
    }

    /// <summary>Case-insensitive contains that translates on PostgreSQL and the in-memory provider.</summary>
    public static string Pattern(string search) => search.Trim().ToLowerInvariant();
}
