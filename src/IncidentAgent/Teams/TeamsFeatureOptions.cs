namespace IncidentAgent.Teams;

public enum StreamingScope
{
    Off,

    /// <summary>1:1 chats only. Recommended: stream in 1:1 chats and send a single reply in group chats and channels.</summary>
    Personal,

    /// <summary>Also group chats and channels.</summary>
    All,
}

/// <summary>
/// Switches for the Teams message features the agent uses. The ones that are off by default are preview features you
/// can turn on to try.
/// </summary>
public sealed class TeamsFeatureOptions
{
    public const string Section = "Features";

    /// <summary>Where answers are shown while they're written.</summary>
    public StreamingScope Streaming { get; set; } = StreamingScope.Personal;

    /// <summary>
    /// In group chats and channels, send failures and other replies meant for one person as targeted messages that
    /// only that person sees. Falls back to a normal post when Teams doesn't accept them.
    /// </summary>
    public bool TargetedMessages { get; set; }

    /// <summary>A sensitivity label shown on answers, summaries and drafts, like "Confidential". Empty for none.</summary>
    public string? SensitivityLabel { get; set; }

    public string? SensitivityLabelDescription { get; set; }

    /// <summary>Post meeting drafts as an Adaptive Card with Approve, Edit and Discard buttons, instead of text.</summary>
    public bool DraftCard { get; set; } = true;

    /// <summary>Add a live status tile (an HTML widget) under incident summaries.</summary>
    public bool HtmlWidget { get; set; }

    /// <summary>The https origin the widget says it's from, usually the agent's public URL. Required with <see cref="HtmlWidget"/>.</summary>
    public string? WidgetDomain { get; set; }

    /// <summary>In group chats and channels, react to a request with eyes while working on it, then a check mark.</summary>
    public bool ReactionAck { get; set; } = true;

    public bool StreamsIn(Conversations.ConversationScope scope) =>
        Streaming == StreamingScope.All || (Streaming == StreamingScope.Personal && scope == Conversations.ConversationScope.Personal);
}
