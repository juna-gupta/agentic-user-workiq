# Teams features for agent users in Iris

Iris uses Teams message features as an agent user. This page lists each one, where Iris uses it, the setting that
controls it, and notes for building your own agent.

Settings are in the `Features` section of `appsettings.json` (see [the table below](#settings)). Preview features are
off by default.

## Features Iris uses

| Feature | Where Iris uses it | Notes |
| --- | --- | --- |
| Streaming, 1:1 | Answers in a 1:1 stream in as they're written, starting with "Checking the runbook" | An informative update shows first with a Stop button, then the text streams and the final message replaces it |
| Streaming, group chats and channels | `Streaming=All` streams answers there too | Use streaming in 1:1 chats and send a single reply in group chats and channels, which is what the default `Personal` does. With `All`, a group chat shows eyes, then "Reading TB-004, TB-030, TB-100 in the runbook" with a Stop button, then the answer with citation pills and a check mark |
| Progress steps | While she works on a request made to her, Iris shows each step as the stream's status line: "Reading this chat", "Pulling the incident together from all 12 messages in this chat", "Reading TB-004, TB-030 in the runbook", "Looking for the shiproom meeting on my calendar", "Copying the slide format from ...", "Drafting the update for ..." | Each step replaces the one before, with a Stop button, and the final message (text or the draft card) replaces the last step. Steps show wherever streaming is on. Use them to show people what the agent is doing while it works |
| Threaded chat | In a channel, Iris replies in the thread she was asked in, and reads only that thread | Reply in the thread you were asked in, so the answer stays with the question. In a group chat, Iris replies in the chat |
| Notifications, 1:1 | Nothing to set. Iris's replies notify people the way a colleague's do | People get a Teams notification with the reply, as they would from a colleague |
| Adaptive Cards in notifications | Draft cards set the message's top-level `summary` (`DraftCards.Summary`) | Set `summary` on every card message. The notification shows it, for example "Iris (Incident Agent): Update for Helpdesk Shiproom: INC-2304 ...". Without it, the notification says "Sent a card" |
| Citations | Runbook cases in answers and summaries become [1] [2] pills that open the case | Name the source in the text too, as Iris does with case IDs such as TB-004, so the answer reads well on its own |
| Feedback buttons | Thumbs up and down on answers and summaries. Iris logs which one and how long the comment is | Either button opens the Teams "Submit feedback" dialog. The comment arrives as a JSON string, `{"feedbackText":"..."}`, so parse it to get the text (`AgentHandlers.FeedbackText`) |
| Sensitivity label | `SensitivityLabel` shows a label on answers, summaries and drafts | Put the label on the message's one root entity (see [Building with the Teams SDK](#building-with-the-teams-sdk)) |
| AI label | Every message is marked as AI generated | Teams shows the "AI agent" badge next to the agent user's name |
| Suggested actions | In a 1:1, an answer offers "Summarize INC-2304" and "Prepare the shiproom update" as buttons, shown to the person who asked | Treat them as shortcuts. Each button sends text the person could also type, so the conversation works the same either way |
| Markdown | All replies are markdown: bold, lists and links | |
| Reacting to messages | In a group chat or channel, Iris reacts with eyes when she picks up a request and a check mark when she's done | Use the reaction IDs Teams uses for its own emoji, such as `1f440_eyes` and `2705_whiteheavycheckmark` |
| Receiving reactions | A like or check mark on Iris's draft card approves it, the same as clicking Approve | Teams sends the reacted message's ID as `replyToId`. Iris reads it through the `KeepReplyToId` middleware (see [Building with the Teams SDK](#building-with-the-teams-sdk)) |
| Card actions | The draft card's Approve and Discard buttons (`Action.Execute`). The card updates in place to show it was approved, with an Open deck button when the invite links this week's deck | The card shows who approved it. Iris then posts the slide text ready to paste into the deck. Teams shows its usual "Your response was sent to the app" banner |
| Task modules (cards) | The draft card's Edit button opens a dialog with one field per line. Saving closes it and updates the card in place | The card shows "Edited by" and the person's name. Return an empty 200 response to close the dialog |
| Deleting messages | Discard deletes the draft card | The card is removed from the chat |
| HTML widget | `HtmlWidget=true` adds a status tile under incident summaries: severity, status, latest update, and a Refresh button | Off by default. The tile renders in group chats and channel threads. Send it with the current status so it's complete as sent, and treat Refresh as optional |
| Targeted messages | `TargetedMessages=true` sends replies meant for one person (such as "I couldn't do that") only to them, in group chats and channels | Off by default, as a preview feature. If Teams doesn't accept a targeted message, Iris posts it normally |
| Agent lifecycle events | Iris logs agent user created, enabled and disabled events | Make lifecycle handlers idempotent; the same event can be delivered more than once. Re-enabling the account can also send "member added" and a plain `event` activity (see [MESSAGE-DELIVERY.md](MESSAGE-DELIVERY.md#guidance-for-admins)) |

## Features Iris doesn't use

| Feature | Why not |
| --- | --- |
| Smart replies, dynamic commands, conversation starters | These come from a Teams app package. An agent user is added like a person, with no app package, and Iris has no commands |
| Task modules (URL) | Iris's only dialog is a card, which covers what she needs |
| Sign-in, SSO | Iris reads Microsoft 365 as herself through Work IQ, so nobody signs in |
| File attachments | Iris posts text that's ready to paste instead |
| Link unfurling, calling, meetings, presence, roster filtering, read indicator | Outside this scenario, or a Teams client feature with nothing for the agent to do |
| Live persona card, telemetry | Teams client features. They work without any code in the agent |
| Socket mode | Iris receives activities at her messaging endpoint |

## Settings

| Key | Default | What it does |
| --- | --- | --- |
| `Features:Streaming` | `Personal` | `Off`, `Personal` (1:1 only) or `All` (also group chats and channels). `Personal` streams in 1:1 chats and sends a single reply in group chats and channels, which is the pattern we recommend |
| `Features:TargetedMessages` | `false` | Replies meant for one person go only to them. Falls back to a normal post if Teams doesn't accept it |
| `Features:SensitivityLabel` | empty | A label name such as `General` or `Confidential`, shown on answers, summaries and drafts |
| `Features:SensitivityLabelDescription` | empty | The text shown when someone opens the label |
| `Features:DraftCard` | `true` | Post shiproom drafts as a card with Approve, Edit and Discard. `false` posts plain text |
| `Features:HtmlWidget` | `false` | Add the status tile under incident summaries |
| `Features:WidgetDomain` | empty | The https origin the tile says it comes from, usually the host's public URL. Required with `HtmlWidget` |
| `Features:ReactionAck` | `true` | React with eyes, then a check mark, to requests in group chats and channels |

## Building with the Teams SDK

The sample uses Teams SDK for .NET 2.1.2. These notes cover how Iris builds messages as an agent user with it. Each
one is already in the sample's code, so you only need them if you build your own.

- **One root message entity.** Teams expects one `https://schema.org/Message` entity per message. `AddAIGenerated`
  and `AddCitation` share it. `TeamsSender.Render` puts the sensitivity label on that same entity, in `usageInfo`,
  rather than calling `AddSensitivityLabel`, which adds an entity of its own. If Teams answers 400 "Multiple root
  message entities found in the bot activity", the message has more than one.
- **Entity fields.** The SDK writes each entity as its `type` plus its `Properties`. Set `@type`, `@context` and `@id`
  in `Properties`, as `JsonObject` values, as the sample does.
- **Reaction IDs.** Use the IDs Teams uses for its own emoji, such as `1f440_eyes` and `2705_whiteheavycheckmark`.
- **Streaming as an agent user.** `AgentStream` sends the stream updates through the agent user's API client, so each
  update comes from the agent user. If the final message isn't accepted, it ends the stream with the text only, so
  the reply still arrives.
- **Reading replyToId.** To know which message was reacted to or given feedback, read the incoming `replyToId`. The
  `KeepReplyToId` turn middleware runs first and keeps it in the activity's extra properties, so it's there in typed
  handlers for reactions and invokes. `TeamsTurn.RepliedToId` reads it.
- **Suggested actions recipients.** Set the person who asked as the recipient. Iris adds them to the `to` list.

## Other things worth knowing

- **Iris's mailbox shows up as messages.** An agent user has a mailbox, and Teams forwards email notifications to the
  agent as message activities on the `agents` channel, with an `emailNotification` entity. Iris ignores them; a
  production agent might want to act on them.
- **The widget runs in a sandbox.** Teams loads the tile from its own `widget-renderer.usercontent.microsoft` domain,
  inside an iframe, and passes its tool calls on to the agent. `WidgetDomain` must be an https URL; the sample uses the
  dev tunnel URL.
- **Line breaks in widget messages.** A message with a widget is sent as extended markdown, where a single line break
  joins lines into one paragraph and a list needs a blank line before it. Iris puts a blank line between the
  summary's lines (`IncidentWidget.Blocks`).
