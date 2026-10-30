namespace IncidentAgent.Agent;

/// <summary>
/// Remembers recently delivered activities so each one is handled once. The same activity can arrive more than once,
/// and Teams redelivers an activity when the endpoint fails, so a failed attempt is forgotten (<see cref="Forget"/>)
/// and the redelivery is handled.
/// </summary>
public sealed class SeenActivities
{
    /// <summary>How long an activity id is remembered.</summary>
    public static readonly TimeSpan Memory = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private DateTimeOffset _nextSweep = DateTimeOffset.MinValue;

    /// <summary>True the first time an activity arrives; false when the same one arrives again within <see cref="Memory"/>. Activities without an id always count as new.</summary>
    public bool FirstTime(string? conversationId, string? activityId, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(activityId))
        {
            return true;
        }

        var key = $"{conversationId}|{activityId}";
        lock (_gate)
        {
            if (now >= _nextSweep)
            {
                foreach (var stale in _seen.Where(e => now - e.Value >= Memory).Select(e => e.Key).ToList())
                {
                    _seen.Remove(stale);
                }

                _nextSweep = now + Memory;
            }

            if (_seen.TryGetValue(key, out var at) && now - at < Memory)
            {
                return false;
            }

            _seen[key] = now;
            return true;
        }
    }

    /// <summary>Forgets an activity whose handling failed, so a redelivery (Teams retries on errors) is handled.</summary>
    public void Forget(string? conversationId, string? activityId)
    {
        lock (_gate)
        {
            _seen.Remove($"{conversationId}|{activityId}");
        }
    }
}
