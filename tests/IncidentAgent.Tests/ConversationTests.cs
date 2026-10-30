using IncidentAgent.Agent;
using IncidentAgent.Conversations;

namespace IncidentAgent.Tests;

public class ConversationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 10, 17, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Flags_the_agents_own_posts_and_mentions_of_it()
    {
        Assert.True(ChatMessageParser.TryRead(Graph.Message("1", "Iris (Incident Agent)", T0, "<p>Hi all</p>", authorId: Graph.Self), Graph.Self, out var own));
        Assert.True(own.FromSelf);
        Assert.False(own.MentionsSelf);

        Assert.True(ChatMessageParser.TryRead(Graph.Message("2", "Maya", T0, "<at id=\"0\">Iris</at> catch us up", mentionId: Graph.Self), Graph.Self, out var asked));
        Assert.False(asked.FromSelf);
        Assert.True(asked.MentionsSelf);
        Assert.Equal("@Iris catch us up", asked.Text);
    }

    [Fact]
    public void Transcripts_leave_out_the_agent_and_requests_to_it()
    {
        Assert.False(ChatMessageParser.TryParse(Graph.Message("1", "Iris", T0, "summary", authorId: Graph.Self), null, Graph.Self, out _));
        Assert.False(ChatMessageParser.TryParse(Graph.Message("2", "Maya", T0, "@Iris hi", mentionId: Graph.Self), null, Graph.Self, out _));
        Assert.False(ChatMessageParser.TryParse(Graph.Message("3", "Maya", T0, "skip me"), "3", Graph.Self, out _));
        Assert.True(ChatMessageParser.TryParse(Graph.Message("4", "Daniel", T0, "Opening INC-2304"), null, Graph.Self, out var line));
        Assert.Equal("Daniel", line.Author);
    }

    [Fact]
    public void Drops_system_events()
    {
        Assert.False(ChatMessageParser.TryRead(Graph.Message("1", "Maya", T0, "<systemEventMessage/>", type: "systemEventMessage"), Graph.Self, out _));
    }

    [Fact]
    public void Read_along_only_looks_at_new_unaddressed_messages_from_people()
    {
        var lines = new List<ChatLine>
        {
            new("old", "Maya", T0.AddMinutes(-1), "before the agent posted"),
            new("own", "Iris", T0.AddSeconds(5), "agent post") { FromSelf = true },
            new("asked", "Maya", T0.AddSeconds(6), "@Iris hi") { MentionsSelf = true },
            new("seen", "Daniel", T0.AddSeconds(7), "already looked at"),
            new("new", "Daniel", T0.AddSeconds(8), "Nice, this is solid."),
        };

        var fresh = WatchLogic.NewLines(lines, T0, new HashSet<string> { "seen" });

        Assert.Equal(["new"], fresh.Select(l => l.Id));
    }

    [Fact]
    public void Waits_for_the_exchange_to_settle()
    {
        var lines = new List<ChatLine> { new("1", "Maya", T0, "Agreed. We need an update for tomorrow's shiproom.") };

        Assert.False(WatchLogic.Settled(lines, T0.AddSeconds(2), TimeSpan.FromSeconds(5)));
        Assert.True(WatchLogic.Settled(lines, T0.AddSeconds(6), TimeSpan.FromSeconds(5)));
        Assert.False(WatchLogic.Settled([], T0.AddMinutes(1), TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void Delivered_messages_trigger_a_read_once_the_exchange_settles()
    {
        var settle = TimeSpan.FromSeconds(5);

        Assert.True(WatchLogic.DueToRead(receivesAllMessages: false, heardAt: null, T0, settle));
        Assert.False(WatchLogic.DueToRead(receivesAllMessages: true, heardAt: null, T0, settle));
        Assert.False(WatchLogic.DueToRead(receivesAllMessages: true, heardAt: T0, T0.AddSeconds(2), settle));
        Assert.True(WatchLogic.DueToRead(receivesAllMessages: true, heardAt: T0, T0.AddSeconds(5), settle));
    }
}
