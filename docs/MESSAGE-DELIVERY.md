# Message delivery: @mentions only vs all messages

Which group chat, channel and meeting messages Teams sends to Iris is set on the **agent blueprint**, not in this
app. This page covers:
- how to change the setting,
- how the sample behaves in each mode,
- what each mode delivers,
- guidance for admins.

## The setting

The Graph beta resource
[`agentCommunicationConfiguration`](https://learn.microsoft.com/graph/api/resources/agentcommunicationconfiguration?view=graph-rest-beta)
holds two things:
- the **endpoint**: Teams posts activities to the callback URL, or to a bot ID;
- a `messageNotificationMode` per context.

| Context | Property | Default |
| --- | --- | --- |
| 1:1 chat | `oneOnOneChatConfiguration` | `allMessages` |
| Group chat | `groupChatConfiguration` | `atMentionedMessagesOnly` |
| Channel | `channelConfiguration` | `atMentionedMessagesOnly` |
| Meeting chat | `meetingChatConfiguration` | `atMentionedMessagesOnly` |

```http
PUT https://graph.microsoft.com/beta/applications/{blueprint-object-id}/microsoft.graph.agentIdentityBlueprint/communicationConfiguration
{
  "isOverridableAtAgentIdLevel": false,
  "endpointConfiguration": { "configurationType": "apiBased",
    "apiBased": { "callbackUri": "https://<your-host>/api/messages" } },
  "teamworkConfiguration": {
    "groupChatConfiguration":    { "messageNotificationMode": "allMessages" },
    "channelConfiguration":      { "messageNotificationMode": "allMessages" },
    "oneOnOneChatConfiguration": { "messageNotificationMode": "allMessages" },
    "meetingChatConfiguration":  { "messageNotificationMode": "allMessages" } } }
```

- **Permissions.** Delegated `AgentCommunicationConfiguration.ReadWrite`, or application `.ReadWrite.All`, both
  admin-consented. Read-only `.Read` and `.Read.All` exist too.
- **PUT replaces the whole object.** Always send `endpointConfiguration` back, or Iris stops receiving anything.
  `scripts` has a helper that reads the current value, changes only the modes, and keeps a backup to restore from
  (see [scripts/README.md](../scripts/README.md)).
- **Takes effect in about 40 seconds.** With `isOverridableAtAgentIdLevel: false`, the agent identity picks up the
  blueprint's value immediately.
- **Set the modes again after saving in the Teams Developer Portal.** The blueprint's Configuration page there sets
  the endpoint, with @mention delivery for group, channel and meeting chats. If you change the URL there, set the
  modes again. To change only the URL, use `comm_config.py set endpoint=https://<host>/api/messages`.

## How the sample behaves

Tell the app which mode the blueprint uses with `Watch:ReceivesAllMessages`:

| | `@mentions only` (default) | `allMessages` |
| --- | --- | --- |
| Blueprint | `atMentionedMessagesOnly` for group/channel | `allMessages` for group/channel (and meeting) |
| `Watch:ReceivesAllMessages` | `false` | `true` |
| A message that @mentions Iris | A request: she answers | Same |
| A message that doesn't | Never delivered | Delivered, but never treated as a request. It only tells the read-along that something new arrived. |
| Read-along after Iris posts | Polls the conversation through Work IQ every `PollSeconds` for `WindowMinutes`: up to 60 calls per window | Reads once, when a delivered message has settled for `SettleSeconds`. No polling. |
| What the read-along sees | The single thread or chat she posted in | Same. New top-level channel posts and other threads still need an @mention. |

**If the two settings don't match:**
- `ReceivesAllMessages=true` with a mention-only blueprint: proactive behaviour quietly stops, because nothing
  unmentioned arrives to trigger a read.
- `false` with an `allMessages` blueprint: still works, but polls for nothing.

**How the sample decides a message mentions Iris:** a mention entity whose `mentioned.id` equals the activity's
`recipient.id` (`8:orgid:<agent user id>`). It doesn't check for her name in the text.

**Handle each activity once.** The same activity can be delivered more than once, and Teams redelivers an activity
when handling fails. `SeenActivities` remembers each conversation and activity ID for 10 minutes and skips a repeat.
If handling fails, the ID is forgotten so the retry is processed.

**Mail arrives too.** An agent user has a mailbox, and her email notifications reach the same URL as message
activities on the `agents` channel, with an `emailNotification` entity. The sample handles only the `msteams` channel
and logs the shape of anything else, never its content.

## What each mode delivers

| Scenario | @mentions only | `allMessages` |
| --- | --- | --- |
| Group chat, no mention | Not delivered | Delivered |
| Group chat, @Iris | Delivered | Delivered |
| Group chat, @someone else | Not delivered | Delivered |
| Channel thread reply, no mention | Not delivered | Delivered |
| Channel thread reply, @Iris | Delivered | Delivered |
| New channel post, no mention | Not delivered | Delivered |
| New channel post, @Iris | Delivered | Delivered |
| Meeting chat, no mention | Not delivered | Delivered |
| Meeting chat, @Iris | Delivered | Delivered |
| 1:1 | All messages by default | Same |
| Iris's own posts | Not delivered | Not delivered |

To see what arrives in your tenant, log each `POST /api/messages` on the host.

## Guidance for admins

| Goal | How |
| --- | --- |
| Stop unmentioned messages reaching the agent | Set the modes back to `atMentionedMessagesOnly` with the Graph API. It takes effect in about 40 seconds |
| Stop message delivery to the agent | Remove the endpoint from the blueprint's communication configuration |
| Stop the agent acting | Disable the agent identity or the agent user's account in Entra. Token requests then fail, so the agent can't read or post. Delivery is set by the communication configuration, so change that too |
| Manage the agent | Use Entra and the Graph API, where the blueprint, agent identity and agent user are |

- **Set and check the mode with the Graph API.** `comm_config.py get` shows the current modes. Keep your own record
  of changes, such as the backup file `comm_config.py set` writes.
- **Tell people about all-message delivery.** It's set on the blueprint and applies to every chat the agent user is
  in. Let the people in those chats know the agent receives all of their messages, for example in its welcome.
- **Make the welcome idempotent.** Teams can send "member added" again, plus an `event` activity, when the agent
  user's account is re-enabled. The sample posts its welcome each time. Remember where you've greeted to post it once.

Review these before turning on `allMessages` in a production tenant.
