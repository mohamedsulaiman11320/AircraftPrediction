using System.Threading.Channels;
using AFP.Application.Abstractions;
using AFP.Application.Telemetry;
using AFP.Domain.Enums;

namespace AFP.Api.Infrastructure;

public sealed class TelemetryImportQueue : ITelemetryImportQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<Guid> Reader => _channel.Reader;

    public ValueTask EnqueueAsync(Guid importId, CancellationToken ct) => _channel.Writer.WriteAsync(importId, ct);
}

/// <summary>
/// Processes CSV imports off the request thread (the upload endpoint returns 202 + a status URL).
/// Imports left Queued or Processing by a previous shutdown are resumed at startup; their transaction
/// was never committed, so re-processing is safe.
/// </summary>
public sealed class TelemetryImportWorker(
    TelemetryImportQueue queue, IServiceScopeFactory scopeFactory, ILogger<TelemetryImportWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeuePendingAsync(stoppingToken);
        await foreach (var importId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ITelemetryImportProcessor>().ProcessAsync(importId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Telemetry import worker failed for {ImportId}", importId);
            }
        }
    }

    private async Task RequeuePendingAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<ITelemetryRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            foreach (var interrupted in await repo.ListImportsByStatusAsync(TelemetryImportStatus.Processing, ct))
            {
                var tracked = await repo.GetImportAsync(interrupted.Id, ct);
                if (tracked is not null) tracked.Status = TelemetryImportStatus.Queued;
            }
            await unitOfWork.SaveChangesAsync(ct);
            foreach (var pending in await repo.ListImportsByStatusAsync(TelemetryImportStatus.Queued, ct))
                await queue.EnqueueAsync(pending.Id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The API stays available (e.g. while the database is down); imports resume on next start.
            logger.LogWarning(ex, "Could not resume pending telemetry imports at startup");
        }
    }
}
