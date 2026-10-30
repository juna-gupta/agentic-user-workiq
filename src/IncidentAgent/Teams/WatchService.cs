using System.Collections.Concurrent;
using IncidentAgent.Agent;
using IncidentAgent.Conversations;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.Options;

namespace IncidentAgent.Teams;

/// <summary>A conversation the agent is reading along in, and how far it has read.</summary>
public sealed class Watch(TeamsDelivery delivery, DateTimeOffset since, DateTimeOffset until)
{
    private int _busy;

    public TeamsDelivery Delivery { get; set; } = delivery;

    /// <summary>Only messages after this count: the agent's last post.</summary>
    public DateTimeOffset Since { get; set; } = since;

    public DateTimeOffset Until { get; set; } = until;

    /// <summary>Messages already looked at. Only the poll that holds <see cref="TryStart"/> touches it.</summary>
    public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);

    private long _heardAtTicks;

    /// <summary>When Teams last delivered a message here that wasn't for the agent (only with all-message delivery).</summary>
    public DateTimeOffset? HeardAt => Interlocked.Read(ref _heardAtTicks) is var t and > 0 ? new DateTimeOffset(t, TimeSpan.Zero) : null;

    public void Heard(DateTimeOffset at) => Interlocked.Exchange(ref _heardAtTicks, at.UtcTicks);

    /// <summary>Marks a delivered message as handled, unless another one arrived since <paramref name="handled"/> was read.</summary>
    public void ClearHeard(DateTimeOffset? handled)
    {
        if (handled is { } h)
        {
            Interlocked.CompareExchange(ref _heardAtTicks, 0, h.UtcTicks);
        }
    }

    public bool TryStart() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void Finish() => Volatile.Write(ref _busy, 0);
}

public sealed class WatchRegistry(IOptions<WatchOptions> options, TimeProvider clock, ILogger<WatchRegistry> logger)
{
    private readonly ConcurrentDictionary<string, Watch> _watches = new(StringComparer.Ordinal);

    /// <summary>Starts (or extends) reading along after the agent posted at <paramref name="postedAt"/>.</summary>
    public void Arm(TeamsDelivery delivery, DateTimeOffset? postedAt)
    {
        var o = options.Value;
        if (!o.Enabled || delivery.Turn.Target is not (ConversationTarget.GroupChat or ConversationTarget.ChannelThread))
        {
            return;
        }

        var since = postedAt ?? clock.GetUtcNow();
        var until = clock.GetUtcNow().AddMinutes(o.WindowMinutes);
        _watches.AddOrUpdate(
            delivery.Turn.Key,
            _ => new Watch(delivery, since, until),
            (_, w) =>
            {
                w.Delivery = delivery;
                w.Since = since > w.Since ? since : w.Since;
                w.Until = until;
                return w;
            });
        logger.LogInformation("Reading along in {Scope} until {Until:t} ({Trigger})", delivery.Turn.Scope, until.ToLocalTime(),
            o.ReceivesAllMessages ? "when messages arrive" : $"every {o.PollSeconds}s");
    }

    /// <summary>
    /// Teams delivered a message that wasn't for the agent, which happens only when the blueprint delivers all messages.
    /// Returns true when the agent is reading along in that conversation.
    /// </summary>
    public bool Heard(TeamsDelivery delivery)
    {
        var now = clock.GetUtcNow();
        if (!_watches.TryGetValue(delivery.Turn.Key, out var watch) || watch.Until <= now)
        {
            return false;
        }

        watch.Heard(now);
        return true;
    }

    public IReadOnlyList<Watch> Active()
    {
        var now = clock.GetUtcNow();
        foreach (var (key, watch) in _watches)
        {
            if (watch.Until <= now && _watches.TryRemove(key, out _))
            {
                logger.LogInformation("Stopped reading along in {Scope}", watch.Delivery.Turn.Scope);
            }
        }

        return [.. _watches.Values];
    }
}

/// <summary>
/// Reads the conversations in <see cref="WatchRegistry"/> through Work IQ, as the agent user, and hands new messages from
/// people to <see cref="IncidentAgentService.HandleObservedAsync"/> once the exchange settles. With mention-only delivery
/// it polls; when Teams delivers all messages it reads only after a message arrives.
/// </summary>
public sealed class WatchService(
    WatchRegistry registry,
    TeamsSender sender,
    IncidentAgentService agent,
    ConversationReader reader,
    IWorkIqFactory workIqFactory,
    AgentTokens tokens,
    IOptions<WatchOptions> options,
    TimeProvider clock,
    ILogger<WatchService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            logger.LogInformation("Reading along is off (Watch:Enabled = false); the agent only answers @mentions");
            return;
        }

        // With all-message delivery a tick only checks in-memory state, so it can run often; a read happens only when due.
        var tick = o.ReceivesAllMessages ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(Math.Max(2, o.PollSeconds));
        var settle = TimeSpan.FromSeconds(o.SettleSeconds);
        logger.LogInformation("Reading along {Trigger}", o.ReceivesAllMessages ? "when Teams delivers a message" : $"by polling every {tick.TotalSeconds:0}s");
        using var timer = new PeriodicTimer(tick);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                foreach (var watch in registry.Active())
                {
                    if (WatchLogic.DueToRead(o.ReceivesAllMessages, watch.HeardAt, clock.GetUtcNow(), settle) && watch.TryStart())
                    {
                        _ = PollAsync(watch, stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task PollAsync(Watch watch, CancellationToken stoppingToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var ct = timeout.Token;
            var delivery = watch.Delivery;
            var turn = delivery.Turn;
            var heard = watch.HeardAt;

            await using var workIq = workIqFactory.Create(tokens.For(delivery.Identity));
            var lines = await reader.ReadRecentAsync(workIq, turn.Target, options.Value.MessagesPerPoll, turn.SelfId, ct);
            var fresh = WatchLogic.NewLines(lines, watch.Since, watch.Seen);
            if (fresh.Count == 0)
            {
                watch.ClearHeard(heard);
                return;
            }

            if (!WatchLogic.Settled(fresh, clock.GetUtcNow(), TimeSpan.FromSeconds(options.Value.SettleSeconds)))
            {
                if (options.Value.ReceivesAllMessages)
                {
                    // Look again after another settle period rather than on every tick.
                    watch.Heard(clock.GetUtcNow());
                }

                return;
            }

            foreach (var line in fresh)
            {
                watch.Seen.Add(line.Id);
            }

            watch.ClearHeard(heard);
            await agent.HandleObservedAsync(turn, lines, fresh, workIq, sender.For(delivery), ct);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Reading along failed; trying again next poll");
        }
        finally
        {
            watch.Finish();
        }
    }
}
