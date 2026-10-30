# Scenario: an on-call incident, with Iris as a teammate

A walk-through you can run in your own tenant with a colleague. You are on call, and your colleague is the
engineering lead. [SETUP.md](SETUP.md) creates Iris and puts the sample runbook in her OneDrive.

## Before you start

- **The incident.** The scenes use an example incident, INC-2304: Helpdesk bot 2.3 staging replies fail with 401
  from Bot Connector after last night's secret rotation. The staging slot still has the old secret. It starts at
  Sev 2. You can use a real incident instead. Keep its ID in your messages, because Iris keeps her notes per
  incident ID.
- **A group chat** with you and your colleague, where you've talked the incident through in a few messages: what
  broke, what you tried, and the fix. Iris isn't in it yet.
- **A shiproom meeting tomorrow** (for scenes 3 and 4), with Iris invited and the decks linked in the invite, as in
  [SETUP.md](SETUP.md#7-try-it-with-your-own-team). Last week's deck has an "Incident updates" slide about another
  incident. Iris copies its format.
- **A team with a channel** (for scene 5), with a post that asks for the status of INC-2304. Iris isn't a member yet.
- **The host is running**, with `Watch:Enabled=true` (the default). The scenes work with @mention-only delivery,
  where Iris polls while she reads along, and with all-message delivery and `Watch:ReceivesAllMessages=true`
  ([MESSAGE-DELIVERY.md](MESSAGE-DELIVERY.md)).

Incident notes and drafts are kept in memory, so keep the host running from scene 2 to scene 5. The shiproom update
and the channel status build on the summary.

**Teams features.** With the defaults, answers in a 1:1 stream in, drafts are cards, and Iris reacts to requests in
group chats and channels. To see the rest, start the host with:

```powershell
dotnet run --project src/IncidentAgent -- --Features:Streaming=All --Features:HtmlWidget=true `
  --Features:WidgetDomain=https://<your tunnel host> `
  --Features:SensitivityLabel=General --Features:SensitivityLabelDescription="OK to share inside the team"
```

[FEATURES.md](FEATURES.md) has notes for each feature, and the [Teams features walk-through](#teams-features-walk-through)
at the end shows them in order.

## Scene 1: 1:1 chat

1. Find Iris in Teams search, open a chat with her and ask:
   > Hey Iris, what does our runbook say about AADSTS7000215 on a bot right after a secret rotation?

   Iris reads the runbook from her OneDrive and answers with the matching cases (TB-004, TB-030) and their steps.
   The answer streams in after a "Checking the runbook" line with a Stop button. The case numbers are citation pills
   that open the case, and thumbs up and down under the answer open Teams' feedback dialog.
2. Ask:
   > Are you on the invite for tomorrow's shiproom?

   Iris looks on her own calendar. She gives the meeting, its time and organizer, and confirms she's invited.

## Scene 2: add Iris to the group chat

1. Add Iris to the group chat, **including the chat history**. Iris introduces herself.
2. Post `@Iris catch us up on this incident`.
3. Iris reacts to your message with eyes, then posts an incident summary: impact, cause, fix, and next steps with
   owners, citing the runbook cases. The eyes turn into a check mark. With `HtmlWidget` on, a status tile with the
   severity, status and latest update sits under the summary.

## Scene 3: Iris acts without an @mention

You and your colleague talk to each other. Neither of you mentions Iris. She reads along for `Watch:WindowMinutes`
(10 by default) after she posts, so keep this within that time.

1. You:
   > Nice, this is solid. 401s have been flat for 30 minutes, so I've dropped it to Sev 3.

   Iris reads it and **stays quiet**: it changes a fact but doesn't ask for anything.
2. Your colleague:
   > Agreed. We'll need an update on this for tomorrow's shiproom.

   Iris picks this up on her own once the chat has been quiet for `Watch:SettleSeconds` (with @mention-only
   delivery, on her next poll). She:
   - posts "On it" with the meeting she found;
   - opens last week's deck from the invite and reads the "Incident updates" slide;
   - posts a draft in that slide's format, as a card with Approve, Edit and Discard. The draft names the meeting,
     organizer and attendees, and lists what changed since her summary: **now Sev 3**.

## Scene 4: approve the draft

1. Optional: Edit on the card opens a dialog with one field per line. Saving closes it, and the card shows the
   change and who made it. Discard deletes the card.
2. Approve the draft in one of three ways:
   - say, still without a mention, "Looks good, can you add it to the deck?";
   - click Approve on the card;
   - react to the card with a like.
3. The card shows who approved it, with an Open deck button when the invite links this week's deck. Iris then posts
   the slide text ready to paste into this week's deck, and names and links the deck.

## Scene 5: channels

1. Add Iris to the team, like any new teammate. Iris introduces herself in General.
2. In the channel, reply to the post that asks for the status: `@Iris please post the current status here`.
3. Iris posts the current status in the thread. It carries what she learned in the group chat, including **Sev 3**,
   so the thread has the latest in one place.

## Teams features walk-through

This shows the Teams features for agent users, in order. Use the same colleague, meeting and team, with Iris already
a member of the team, and start the host with the feature flags above. Each step notes what Iris reads through
Work IQ.

1. **Streaming, with progress steps.** Start a new 1:1 chat with Iris and ask about AADSTS7000215. "Reading our
   chat" and "Checking the runbook" show with a Stop button while Iris reads both through Work IQ. Then the answer
   streams in with citations, the AI agent badge, the sensitivity label and feedback buttons.
2. **Notifications in 1:1 chats.** Ask whether Iris is on the shiproom invite, then move to another chat while she
   checks her calendar through Work IQ. Her answer arrives as a Teams notification.
3. **Streaming in group chats.** Add Iris to another group chat about the incident, with its history, and ask her
   to catch the chat up. Iris shows her steps as she reads the chat and the runbook cases, then posts the summary
   with the status tile.
4. **Adaptive Cards in notifications.** Go back to your 1:1 with Iris. In the group chat, your colleague asks Iris
   to prepare the shiproom update. The draft card arrives as a notification with the meeting and the slide heading,
   not "Sent a card".
5. **Card dialogs.** Open the notification. The card has the shiproom from Iris's calendar and the slide format from
   last week's deck, both read through Work IQ. Open Edit on the card, change the Ask line and save. The card
   updates in place for everyone and shows who edited it.
6. **Threaded chat in channels.** In the channel, reply in the thread that asks for the status and ask Iris to post
   it. Iris streams it into the same thread, steps first, with the status tile.

## What to check if a scene goes wrong

| Symptom | Likely cause |
| --- | --- |
| Iris never answers | The blueprint's notification URL doesn't point at your tunnel or host, or the tunnel isn't anonymous. Check `POST /api/messages` in the host log. |
| "I can't reach Microsoft 365 right now: Work IQ didn't accept my sign-in" | The Work IQ grant is missing for the agent user, or Work IQ is turned off for the tenant |
| "I couldn't find my runbook at ... in my OneDrive" | Run `python seed_onedrive.py` ([SETUP.md](SETUP.md#7-try-it-with-your-own-team)) |
| No reaction to your colleague's message | Iris hasn't posted in that chat in the last `Watch:WindowMinutes`, or `Watch:ReceivesAllMessages=true` while the blueprint is still mention-only |
| Iris can't find the meeting | She isn't invited, or the title doesn't contain `Shiproom:MeetingKeyword` |
| The draft has no template | The invite has no deck links, the decks aren't shared with Iris, or the earlier deck has no `Shiproom:SlideTitle` slide |
| Iris looks for a meeting a year away | A month and day that has already passed, such as "Oct 11", means next year. Give the full date (2026-10-11) |
| A like on the draft card does nothing | The draft was already approved or discarded, or the host restarted and forgot it |
| The widget says "Couldn't refresh" | Refresh is optional. The tile shows the status from when it was sent, and the next summary posts a new tile ([FEATURES.md](FEATURES.md)) |
| The severity is stale in the channel status | The host restarted between scenes, so the incident notes were lost, or the channel post doesn't name the incident ID |
| No notification for Iris's reply | Teams doesn't notify for the chat you have open, so move to another chat first. Also check your Teams notification settings for chats and mentions |
