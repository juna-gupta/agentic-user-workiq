using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace IncidentAgent.Teams;

public sealed class WorkQueueOptions
{
    public const string Section = "WorkQueue";

    /// <summary>Requests handled at once. Each one holds a Work IQ session and model calls.</summary>
    public int MaxConcurrency { get; set; } = 8;

    /// <summary>Requests waiting beyond this are turned away with a "busy" reply.</summary>
    public int Capacity { get; set; } = 100;

    public int TimeoutSeconds { get; set; } = 300;
}

/// <summary>
/// Runs requests after the incoming activity has been acknowledged. Teams expects a quick answer to each activity,
/// and a meeting update can take a minute, so the agent replies proactively when it's done.
/// </summary>
public sealed class WorkQueue(IOptions<WorkQueueOptions> options, ILogger<WorkQueue> logger) : BackgroundService
{
    private readonly Channel<WorkItem> _items = Channel.CreateBounded<WorkItem>(
        new BoundedChannelOptions(Math.Max(1, options.Value.Capacity)) { FullMode = BoundedChannelFullMode.Wait });

    /// <summary>False when the queue is full.</summary>
    public bool TryEnqueue(string name, Func<CancellationToken, Task> work) => _items.Writer.TryWrite(new WorkItem(name, work));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, o.MaxConcurrency), CancellationToken = stoppingToken };
        try
        {
            await Parallel.ForEachAsync(_items.Reader.ReadAllAsync(stoppingToken), parallel, async (item, ct) =>
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(o.TimeoutSeconds));
                try
                {
                    await item.Work(timeout.Token);
                }
                catch (OperationCanceledException) when (timeout.IsCancellationRequested)
                {
                    logger.LogWarning("{Work} was cancelled after {Seconds}s or at shutdown", item.Name, o.TimeoutSeconds);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "{Work} failed", item.Name);
                }
            });
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private sealed record WorkItem(string Name, Func<CancellationToken, Task> Work);
}
