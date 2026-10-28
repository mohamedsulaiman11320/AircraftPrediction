using AFP.Application.Abstractions;
using AFP.Application.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AFP.Infrastructure.Persistence;

public sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    public async Task<int> SaveChangesAsync(CancellationToken ct)
    {
        try
        {
            return await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("The record was modified by someone else. Reload and try again.", "concurrency_conflict");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            throw new ConflictException($"A record with the same unique value already exists ({pg.ConstraintName}).", "unique_violation");
        }
    }

    public async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        // Non-relational providers (unit/integration tests) and nested calls run without a new transaction.
        if (!db.Database.IsRelational() || db.Database.CurrentTransaction is not null) return await work(ct);

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async innerCt =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(innerCt);
            var result = await work(innerCt);
            await tx.CommitAsync(innerCt);
            return result;
        }, ct);
    }
}
