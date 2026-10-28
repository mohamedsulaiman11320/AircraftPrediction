using AFP.Application.Abstractions;
using AFP.Domain.Entities;
using AFP.Domain.Enums;
using AFP.Infrastructure.Configuration;
using Microsoft.EntityFrameworkCore;

namespace AFP.Infrastructure.Persistence;

/// <summary>
/// The isolated development (synthetic) database, on its own <see cref="AppDbContext"/> and connection
/// (<see cref="DatabaseTarget.DevelopmentConnectionString"/>). It is never the operational context, so nothing
/// read or written here can reach operational tables. The development database has no users: user navigations
/// are never loaded and the only write (a queued <see cref="PipelineRun"/>) leaves <c>requested_by_user_id</c> null.
/// </summary>
public sealed class DevelopmentDataSource : IDevelopmentDataSource, IAsyncDisposable, IDisposable
{
    private readonly DbContextOptions<AppDbContext>? _options;
    private AppDbContext? _db;

    public DevelopmentDataSource(string? connectionString, Action<DbContextOptionsBuilder<AppDbContext>>? configure = null)
    {
        if (connectionString is null && configure is null) return;
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        if (configure is not null) configure(builder);
        else builder.UseNpgsql(connectionString).UseSnakeCaseNamingConvention();
        _options = builder.Options;
        DatabaseName = connectionString is null ? "development" : new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Database;
    }

    public bool IsConfigured => _options is not null;
    public string? DatabaseName { get; }

    private AppDbContext Db => _db ??= new AppDbContext(_options
        ?? throw new InvalidOperationException("The development environment is not configured."));

    public async Task<IReadOnlyList<ModelVersion>> ListModelsAsync(CancellationToken ct) =>
        await Db.ModelVersions.AsNoTracking().OrderByDescending(m => m.CreatedAtUtc).ToListAsync(ct);

    public Task<ModelVersion?> GetModelAsync(Guid id, CancellationToken ct) =>
        Db.ModelVersions.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<PipelineRun>> ListRunsAsync(int limit, CancellationToken ct) =>
        await Db.PipelineRuns.AsNoTracking().OrderByDescending(r => r.RequestedAtUtc).Take(limit).ToListAsync(ct);

    public Task<PipelineRun?> GetRunAsync(Guid id, CancellationToken ct) =>
        Db.PipelineRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<bool> HasPendingAsync(PipelineJobType jobType, CancellationToken ct) =>
        Db.PipelineRuns.AnyAsync(r => r.JobType == jobType
            && (r.Status == PipelineRunStatus.Queued || r.Status == PipelineRunStatus.Running), ct);

    public async Task AddRunAsync(PipelineRun run, CancellationToken ct)
    {
        if (run.RequestedByUserId is not null)
            throw new InvalidOperationException("The development database has no users; requested_by_user_id must be null.");
        await Db.PipelineRuns.AddAsync(run, ct);
        await Db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<AircraftRiskPrediction>> ListLatestModelPredictionsAsync(CancellationToken ct)
    {
        // Development data is small; grouping in memory keeps the query provider-agnostic.
        var rows = await Db.RiskPredictions.AsNoTracking()
            .Include(p => p.Aircraft).Include(p => p.ModelVersion)
            .Where(p => p.Basis == PredictionBasis.Model)
            .ToListAsync(ct);
        return rows.GroupBy(p => p.AircraftId)
            .Select(g => g.OrderByDescending(p => p.PredictionTimestampUtc).ThenByDescending(p => p.FeatureCutoffUtc).First())
            .OrderBy(p => p.Aircraft?.RegistrationNumber)
            .ToList();
    }

    public void Dispose() => _db?.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (_db is not null) await _db.DisposeAsync();
    }
}
