using IncidentAgent.Agent;
using IncidentAgent.Conversations;
using IncidentAgent.Knowledge;
using IncidentAgent.Llm;
using IncidentAgent.Shiproom;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IncidentAgent.Tests;

/// <summary>When the agent is reading along (nobody @mentioned it), it should act only when the team clearly wants what it can do.</summary>
public class ReadAlongTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 17, 0, 0, TimeSpan.Zero);
    private static readonly AgentTurn Turn = new(new ConversationTarget.GroupChat("19:chat@thread.v2"), ConversationScope.GroupChat, Graph.Self, TimeZoneInfo.Utc);

    private static readonly ChatLine[] Recent =
    [
        new("1", "Iris", Now.AddMinutes(-2), "INC-2304 summary…") { FromSelf = true },
        new("2", "Daniel", Now.AddSeconds(-10), "Nice, this is solid."),
    ];

    private static (IncidentAgentService Agent, FakeChatClient Chat, AgentMemory Memory) Create()
    {
        var chat = new FakeChatClient();
        var memory = new AgentMemory();
        var clock = new FixedClock(Now);
        var agent = new IncidentAgentService(
            chat,
            new RunbookStore(Options.Create(new RunbookOptions()), clock, NullLogger<RunbookStore>.Instance),
            new ConversationReader(Options.Create(new ConversationOptions()), clock, NullLogger<ConversationReader>.Instance),
            new CalendarReader(Options.Create(new ShiproomOptions()), clock, NullLogger<CalendarReader>.Instance),
            new DeckFinder(NullLogger<DeckFinder>.Instance),
            memory,
            Options.Create(new AgentOptions()),
            Options.Create(new RunbookOptions()),
            Options.Create(new ShiproomOptions()),
            Options.Create(new WatchOptions()),
            Options.Create(new LlmOptions { Endpoint = "https://example.openai.azure.com/", Deployment = "test" }),
            clock,
            NullLoggerFactory.Instance);
        return (agent, chat, memory);
    }

    [Theory]
    [InlineData("""{"intent":"none","confidence":0.95,"reason":"acknowledgement"}""")]
    [InlineData("""{"intent":"answer","confidence":0.99,"reason":"a question between people"}""")]
    [InlineData("""{"intent":"shiproom_update","confidence":0.5,"reason":"maybe"}""")]
    [InlineData("""{"intent":"add_to_deck","confidence":0.99,"reason":"no draft yet"}""")]
    [InlineData("garbage")]
    public async Task Stays_quiet_unless_the_team_clearly_wants_something_it_does(string classification)
    {
        var (agent, chat, _) = Create();
        chat.Reply(classification);
        var output = new RecordingOutput();

        var acted = await agent.HandleObservedAsync(Turn, Recent, [Recent[1]], new FakeWorkIq(), output, default);

        Assert.False(acted);
        Assert.Empty(output.Posts);
        Assert.Single(chat.Requests);
    }

    [Fact]
    public async Task Skips_a_round_while_it_is_already_working_in_the_conversation()
    {
        var (agent, chat, memory) = Create();
        var gate = memory.For(Turn.Key).Gate;
        await gate.WaitAsync();
        try
        {
            Assert.False(await agent.HandleObservedAsync(Turn, Recent, [Recent[1]], new FakeWorkIq(), new RecordingOutput(), default));
            Assert.Empty(chat.Requests);
        }
        finally
        {
            gate.Release();
        }
    }

    [Fact]
    public async Task The_classifier_sees_only_the_new_messages_as_new()
    {
        var (agent, chat, _) = Create();
        chat.Reply("""{"intent":"none","confidence":0.9,"reason":"chat"}""");

        await agent.HandleObservedAsync(Turn, Recent, [Recent[1]], new FakeWorkIq(), new RecordingOutput(), default);

        var prompt = string.Join("\n", chat.Requests[0].Select(m => m.Text));
        Assert.Contains("Nice, this is solid.", prompt);
        Assert.Contains("nobody addressed you", prompt, StringComparison.OrdinalIgnoreCase);
    }
}
