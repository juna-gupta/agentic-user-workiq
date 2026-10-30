using IncidentAgent.Agent;

namespace IncidentAgent.Tests;

public class AgentModelTests
{
    private static readonly TimeZoneInfo Pacific = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");

    // Saturday Oct 10, 2026, 10:00 PDT.
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 17, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("tomorrow", "2026-10-11")]
    [InlineData("tomorrow's", "2026-10-11")]
    [InlineData("today", "2026-10-10")]
    [InlineData("Monday", "2026-10-12")]
    [InlineData("next saturday", "2026-10-17")]
    [InlineData("this saturday", "2026-10-10")]
    [InlineData("2026-10-15", "2026-10-15")]
    [InlineData("Oct 14", "2026-10-14")]
    [InlineData("on October 20", "2026-10-20")]
    public void Resolves_dates_in_the_team_time_zone(string when, string expected)
    {
        Assert.Equal(DateOnly.Parse(expected), DateResolver.Resolve(when, Now, Pacific));
    }

    [Fact]
    public void Tidy_summary_puts_each_section_on_its_own_paragraph()
    {
        var raw = "**INC-2304 · Staging bot down (Sev 3) — Mitigated** **Impact:** Pilot only. **Cause:** Old secret.\n" +
                  "**Next:**\n- Maya Chen: add the alert. **Open questions:**\n- Other bots affected?";

        var tidy = Messages.TidySummary(raw);

        Assert.Equal(
            "**INC-2304 · Staging bot down (Sev 3) — Mitigated**\n\n**Impact:** Pilot only.\n\n**Cause:** Old secret.\n" +
            "**Next:**\n- Maya Chen: add the alert.\n\n**Open questions:**\n- Other bots affected?",
            tidy);
        Assert.Equal(tidy, Messages.TidySummary(tidy));
    }

    [Fact]
    public void Tomorrow_follows_the_local_date_not_utc()
    {
        // 23:30 PDT on Oct 10 is already Oct 11 in UTC.
        var lateEvening = new DateTimeOffset(2026, 10, 11, 6, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 10, 11), DateResolver.Resolve("tomorrow", lateEvening, Pacific));
    }

    [Fact]
    public void A_message_delivered_twice_is_handled_once()
    {
        var seen = new SeenActivities();
        const string thread = "19:abc@thread.tacv2;messageid=1";

        Assert.True(seen.FirstTime(thread, "100", Now));
        Assert.False(seen.FirstTime(thread, "100", Now.AddSeconds(1)));
        Assert.True(seen.FirstTime(thread, "101", Now.AddSeconds(1)));
        Assert.True(seen.FirstTime("19:other@thread.v2", "100", Now.AddSeconds(1)));
        Assert.True(seen.FirstTime(thread, null, Now));
        Assert.True(seen.FirstTime(thread, null, Now));
        Assert.True(seen.FirstTime(thread, "100", Now + SeenActivities.Memory + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void A_redelivery_after_a_failed_attempt_is_handled()
    {
        var seen = new SeenActivities();
        const string chat = "19:abc@thread.v2";

        Assert.True(seen.FirstTime(chat, "200", Now));
        seen.Forget(chat, "200");

        Assert.True(seen.FirstTime(chat, "200", Now.AddSeconds(1)));
        Assert.False(seen.FirstTime(chat, "200", Now.AddSeconds(2)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    public void Unknown_dates_resolve_to_null(string? when)
    {
        Assert.Null(DateResolver.Resolve(when, Now, Pacific));
    }

    [Fact]
    public void Parses_the_intent_json()
    {
        var intent = AgentIntent.Parse("""{"intent":"shiproom_update","confidence":0.92,"when":"tomorrow","meeting":"shiproom","incident":"inc-2304","reason":"Maya asked"}""");

        Assert.Equal(AgentIntentKind.ShiproomUpdate, intent.Kind);
        Assert.Equal(0.92, intent.Confidence, 3);
        Assert.Equal("tomorrow", intent.When);
        Assert.Equal("INC-2304", intent.Incident);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"intent":"launch_rockets","confidence":1}""")]
    public void Anything_unexpected_is_no_intent(string reply)
    {
        Assert.Equal(AgentIntentKind.None, AgentIntent.Parse(reply).Kind);
    }

    [Fact]
    public void Clamps_confidence_and_ignores_non_ids()
    {
        var intent = AgentIntent.Parse("""{"intent":"add_to_deck","confidence":7,"incident":"the outage"}""");
        Assert.Equal(1, intent.Confidence);
        Assert.Null(intent.Incident);
    }

    [Fact]
    public void Incident_notes_carry_later_facts()
    {
        var note = new IncidentNote("INC-2304", "Staging 401s", "**INC-2304 (Sev 2)**", Now);

        Assert.Same(note, note.WithUpdates([], Now.AddMinutes(5)));
        var updated = note.WithUpdates(["Sev 2 to Sev 3"], Now.AddMinutes(5)).WithUpdates(["clean for 30 minutes"], Now.AddMinutes(6));
        Assert.Equal("**INC-2304 (Sev 2)**", updated.Summary);
        Assert.Equal(["Sev 2 to Sev 3", "clean for 30 minutes"], updated.Updates);
        Assert.Equal(Now.AddMinutes(6), updated.At);
    }

    [Fact]
    public void Summary_gets_its_header_line_when_the_model_leaves_it_out()
    {
        const string body = "**Impact:** Pilot only.\n\n**Cause:** Old secret.";

        Assert.Equal("**INC-2304 · Staging 401s (Sev 2) — Mitigated**\n\n" + body, Messages.WithHeader(body, "INC-2304", "Staging 401s", "Sev 2", "Mitigated"));
        Assert.Equal("**Staging 401s**\n\n" + body, Messages.WithHeader(body, null, "Staging 401s", null, null));
        Assert.Equal(body, Messages.WithHeader(body, null, null, "Sev 2", null));

        const string full = "**INC-2304 · Staging 401s (Sev 2) — Mitigated**\n\n" + body;
        Assert.Equal(full, Messages.WithHeader(full, "INC-2304", "Other title", "Sev 3", "Resolved"));
    }

    [Fact]
    public void Prompts_list_later_facts_after_the_notes()
    {
        var note = new IncidentNote("INC-2304", "Staging 401s", "**Next:**\n- Dan: keep Sev 2 until clean", Now).WithUpdates(["Sev 2 to Sev 3"], Now);
        var transcript = new IncidentAgent.Conversations.Transcript([], "a thread", false);

        var user = IncidentAgent.Llm.Prompts.SummaryUser(transcript, null, note, [], 0, Pacific);

        Assert.Matches(@"keep Sev 2 until clean\nDATA>>>\n\nChanges after those notes[^\n]*:\n<<<DATA\n- Sev 2 to Sev 3\nDATA>>>", user);
    }

    [Fact]
    public void Memory_finds_notes_across_conversations()
    {
        var memory = new AgentMemory();
        memory.Remember("chat-a", new IncidentNote("INC-1", "one", "s1", Now));
        memory.Remember("chat-b", new IncidentNote("INC-2", "two", "s2", Now.AddMinutes(1)));

        Assert.Equal("s1", memory.Incident("inc-1")?.Summary);
        Assert.Equal("s2", memory.Incident(null)?.Summary);
        Assert.Equal("s1", memory.For("chat-a").Summary?.Summary);
        Assert.Null(memory.For("chat-c").Summary);
    }
}
