using System.Collections.Concurrent;

namespace IncidentAgent.Agent;

/// <summary>
/// What the agent remembers between turns: per conversation, its last incident summary and any draft waiting for
/// confirmation; across conversations, its notes per incident ID. In memory, so a restart forgets; a real deployment
/// would keep this in a store.
/// </summary>
public sealed class AgentMemory
{
    private readonly ConcurrentDictionary<string, ConversationMemory> _conversations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, IncidentNote> _incidents = new(StringComparer.OrdinalIgnoreCase);

    public ConversationMemory For(string conversationKey) => _conversations.GetOrAdd(conversationKey, _ => new ConversationMemory());

    public void Remember(string conversationKey, IncidentNote note)
    {
        For(conversationKey).Summary = note;
        if (note.Id is { } id)
        {
            _incidents[id] = note;
        }
    }

    /// <summary>The notes for an incident, or the most recent notes when no ID is given.</summary>
    public IncidentNote? Incident(string? id) =>
        id is not null ? _incidents.GetValueOrDefault(id) : _incidents.Values.MaxBy(n => n.At);
}

public sealed class ConversationMemory
{
    public IncidentNote? Summary { get; set; }

    public ShiproomDraft? Draft { get; set; }

    /// <summary>One piece of work at a time per conversation, whether someone asked or the agent noticed it.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);
}
