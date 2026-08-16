using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using ShoppingList.Api.Data;
using ShoppingList.Api.Data.Entities;
using ShoppingList.Api.Telemetry;

namespace ShoppingList.Api.Infrastructure.Embeddings;

public interface IEmbeddingQueue
{
    ValueTask EnqueueAsync(Guid itemId, CancellationToken ct);
}

/// <summary>
/// In-process work queue for embedding generation.
/// <para>
/// The point of this class is that embedding is <b>off the request path</b>. A model inference
/// takes tens to hundreds of milliseconds, and putting it inline would mean every item creation
/// pays for it, and every embedder outage becomes a write outage. Queueing makes item creation
/// a database insert and nothing more.
/// </para>
/// <para>
/// A bounded channel with <c>DropOldest</c>, not an unbounded one: an unbounded queue under load
/// grows until the process runs out of memory, which converts a slow dependency into a crash.
/// Dropping is acceptable here because the database — not the channel — is the source of truth
/// for what still needs embedding, and the reconciliation sweep re-queues anything missed.
/// </para>
/// <para>
/// Known limitation, stated in the README: this is in-process, so N replicas would each run
/// their own worker over the same rows. A durable queue or an outbox is the production answer.
/// </para>
/// </summary>
internal sealed class EmbeddingQueue : IEmbeddingQueue
{
    private readonly Channel<Guid> _channel;
    private readonly ApiMetrics _metrics;

    public EmbeddingQueue(ApiMetrics metrics)
    {
        _metrics = metrics;
        _channel = Channel.CreateBounded<Guid>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public async ValueTask EnqueueAsync(Guid itemId, CancellationToken ct)
    {
        await _channel.Writer.WriteAsync(itemId, ct);
        _metrics.EmbeddingQueued();
    }

    internal IAsyncEnumerable<Guid> ReadAllAsync(CancellationToken ct) =>
        _channel.Reader.ReadAllAsync(ct);
}

/// <summary>
/// Drains the queue and periodically sweeps the database for anything the queue missed.
/// <para>
/// The sweep is what makes the dropped-message tradeoff safe. Rows carry
/// <c>embedding_status = 'Pending'</c> until they succeed, and a partial index makes finding
/// them cheap regardless of table size — so a restart, a dropped queue entry, or an embedder
/// outage self-heals rather than leaving items permanently unembedded.
/// </para>
/// </summary>
internal sealed class EmbeddingBackgroundService(
    EmbeddingQueue queue,
    IServiceScopeFactory scopeFactory,
    ApiMetrics metrics,
    ILogger<EmbeddingBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Embedding worker started.");

        var sweeper = Task.Run(() => SweepLoopAsync(stoppingToken), stoppingToken);

        try
        {
            await foreach (var itemId in queue.ReadAllAsync(stoppingToken))
            {
                metrics.EmbeddingDequeued();
                await ProcessAsync(itemId, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        await sweeper;
        logger.LogInformation("Embedding worker stopped.");
    }

    private async Task SweepLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(SweepInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // IgnoreQueryFilters: this runs with no HTTP request and therefore no current
                // user, so the ownership filter would exclude every row. Background work
                // legitimately operates across all users, and doing so explicitly — rather than
                // by weakening the filter — keeps the filter's guarantee intact for request paths.
                var pending = await db.Items
                    .IgnoreQueryFilters()
                    .AsTracking()
                    .Where(i => i.EmbeddingStatus == EmbeddingStatus.Pending)
                    .OrderBy(i => i.CreatedAt)
                    .Take(50)
                    .ToListAsync(ct);

                if (pending.Count == 0)
                {
                    continue;
                }

                logger.LogInformation("Sweep found {Count} item(s) awaiting embedding.", pending.Count);

                var generator = scope.ServiceProvider.GetRequiredService<IEmbeddingGenerator>();

                foreach (var item in pending)
                {
                    await EmbedAsync(item, generator, ct);
                }

                await db.SaveChangesAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            // A crashed sweeper would leave items unembedded forever with nothing to say why.
            logger.LogError(ex, "Embedding sweep failed; it will resume on the next interval.");
        }
    }

    private async Task ProcessAsync(Guid itemId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var generator = scope.ServiceProvider.GetRequiredService<IEmbeddingGenerator>();

            var item = await db.Items
                .IgnoreQueryFilters()
                .AsTracking()
                .FirstOrDefaultAsync(i => i.Id == itemId, ct);

            if (item is null || item.EmbeddingStatus == EmbeddingStatus.Ready)
            {
                return;
            }

            await EmbedAsync(item, generator, ct);
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One failed item must not take the worker down — the next queued item would never
            // be processed. Left Pending, the sweep retries it.
            logger.LogError(ex, "Failed to embed item {ItemId}; the sweep will retry it.", itemId);
        }
    }

    private async Task EmbedAsync(ShoppingItem item, IEmbeddingGenerator generator, CancellationToken ct)
    {
        var embedding = await generator.GenerateAsync(item.ToEmbeddingText(), ct);

        if (embedding is null)
        {
            // Marked Failed rather than left Pending, so a permanently unembeddable item does
            // not cycle through every sweep forever. The sweep only picks up Pending rows.
            item.MarkEmbeddingFailed();
            logger.LogWarning("Embedding unavailable for item {ItemId}; it remains searchable by keyword.", item.Id);
            return;
        }

        item.SetEmbedding(embedding, generator.ModelName);
        logger.LogDebug("Embedded item {ItemId} with {Model}.", item.Id, generator.ModelName);
    }
}
