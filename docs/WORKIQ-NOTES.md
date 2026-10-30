# Work IQ notes

How this sample uses Work IQ as an agentic user: access, the tools it calls, reading a deck, and cost.

## Access

- **Grant.** Delegated `WorkIQAgent.Ask` on the Work IQ app (`fdcc1f02-fc51-4226-8753-f668596af7f7`):
  - client: the **agent identity**;
  - principal: the **agent user**.

  No Graph permissions and no application permissions. [scripts](../scripts/README.md) creates the grant.
- **Token.** The Teams SDK's `GetAgenticUserTokenAsync(identity, "api://workiq.svc.cloud.microsoft/.default")` runs
  the agent user flow: blueprint, then agent identity, then agent user. The token's `oid` is Iris, `scp` is
  `WorkIQAgent.Ask`, and `azp` is the agent identity. Nobody signs in.
- **Reach.** Whatever Iris can open:
  - chats and teams she's a member of;
  - her own calendar and OneDrive;
  - files shared with her, such as the deck links in a meeting invite.

  She can't see the shiproom unless she's invited, or a deck unless it's shared with her.

## Tools

To see the tools the agent user gets, run `python workiq_probe.py tools` in `scripts/`. They include `fetch`,
`fetch_blob`, `search_paths`, `get_schema`, `ask` and `retrieve`, plus tools that create, update and act on items.
This sample uses only these:

| Tool | Used for | Where |
| --- | --- | --- |
| `fetch` | Chat and channel messages, calendar view, shared-link lookups, folder listings | `ConversationReader`, `CalendarReader`, `DeckFinder` |
| `fetch_blob` | The runbook in her OneDrive and last week's .pptx deck. Returns base64, up to 4 MB. | `RunbookStore`, `IncidentAgentService` |
| `search_paths` | Wrapped, not used at run time. Handy while building, to find which operations a path supports. | `WorkIqClient` |

The paths are hard-coded in `WorkIqPaths` for now. The recommended approach is to discover them at run time with
`search_paths` and `get_schema`.
The model doesn't choose Work IQ tools; see "How Work IQ is used" in the [README](../README.md#how-work-iq-is-used).

## Reading a deck

For a shiproom update, Iris reads the decks the meeting invite links to:
1. `fetch` on `/me/calendarView` finds the meeting on her calendar.
2. `fetch` on `/shares/{encoded link}/driveItem` resolves each SharePoint or OneDrive link in the invite. A linked
   .pptx is a deck, and a linked folder's children are listed with another `fetch`.
3. The deck whose file name has the meeting date is this week's deck. The most recent earlier one is last week's.
4. `fetch_blob` downloads last week's deck, and the sample reads the slide titled `Shiproom:SlideTitle`. The model
   copies that slide's format, not its facts.

Iris then posts the draft for the team to approve. Once it's approved, she posts the slide text ready to paste into
this week's deck, with a link to it. Everything in the scenario is a read, so the agent needs no write actions.

## Paging chat messages

The sample reads a chat newest first with `$orderby=lastModifiedDateTime desc`, and reads older pages with
`$filter=lastModifiedDateTime lt ...`.

## Cost

Work IQ is billed per call in Copilot Credits to the tenant, not per Copilot licence. A tool call such as `fetch`
costs 0.1 credit (about $0.001). `ask` costs more and varies with the question.

What drives the number of calls here:
- **Each request** reads the conversation, sometimes the runbook (cached for `Runbook:CacheMinutes`), and for a
  shiproom update the calendar, the invite's links and the prior deck.
- **Reading along with @mention-only delivery** polls each active conversation every `Watch:PollSeconds`. That's up
  to 60 `fetch` calls per 10-minute window, even if nobody says anything.
- **With all-message delivery**, a read happens only after a message arrives. The polling cost goes away, but the
  agent receives every message in every chat and channel it's in.

Admins see usage and set limits and alerts in the Microsoft 365 admin center's Copilot cost management page
(`https://admin.cloud.microsoft/#/copilot/costmanagement`). They turn Work IQ on or off and scope it under Agents >
Tools (`https://admin.cloud.microsoft/#/agents/tools`).

Iris is billed like a user. Cost management > Consumption > Users lists her as her own row, with her credits and the
same per-user monthly limit as everyone else, and she doesn't need a Copilot licence. The Work IQ MCP server's
Policies tab sets which data areas agents can reach, paging and collection limits, and whether writes are allowed.
