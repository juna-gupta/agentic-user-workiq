# Work IQ notes

How this sample uses Work IQ as an agentic user: access, the tools it calls, reading a deck, and cost.

## Access

- **Grant.** Delegated `WorkIQAgent.Ask` on the Work IQ app (`fdcc1f02-fc51-4226-8753-f668596af7f7`):
  - client: the **agent identity**;
  - principal: the **agent user**.

  No Graph permissions and no application permissions. [scripts](../scripts/README.md) creates the grant. The
  optional file-transfer APIs need two more Work IQ scopes; see
  [File transfers without model context](#file-transfers-without-model-context).
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
The existing Iris workflows use only these:

| Tool | Used for | Where |
| --- | --- | --- |
| `fetch` | Chat and channel messages, calendar view, shared-link lookups, folder listings | `ConversationReader`, `CalendarReader`, `DeckFinder` |
| `fetch_blob` | The runbook in her OneDrive and last week's .pptx deck. Returns base64, up to 4 MB. | `RunbookStore`, `IncidentAgentService` |
| `search_paths` | Wrapped, not used at run time. Handy while building, to find which operations a path supports. | `WorkIqClient` |

The optional file-transfer client APIs additionally use `call_function` to prepare a download and `do_action` to
prepare an upload. They are callable by trusted application code, not by the model.

The paths are hard-coded in `WorkIqPaths` for now. The recommended approach is to discover them at run time with
`search_paths` and `get_schema`.
The model doesn't choose Work IQ tools; see "How Work IQ is used" in the [README](../README.md#how-work-iq-is-used).

## File transfers without model context

`IWorkIq.DownloadFileAsync` and `UploadFileAsync` exchange metadata over MCP, then stream the actual file over
authenticated Work IQ HTTP. No file bytes, base64, or local file handles appear in MCP arguments or returned receipts.
The existing `fetch_blob` implementation and Iris workflows are unchanged.

Use the existing factory and agent-user access-token callback:

```csharp
await using var workIq = factory.Create(accessToken);

var upload = await workIq.UploadFileAsync(
    new UploadFileRequest(@"C:\Transfers\report.pptx", documentLibraryId)
    {
        ParentFolderId = "root",
        ConflictBehavior = UploadConflictBehavior.Fail,
    }, cancellationToken);

var download = await workIq.DownloadFileAsync(
    new DownloadFileRequest(documentLibraryId, remoteDriveItemId, @"C:\Transfers\download.pptx")
    {
        Overwrite = false,
    }, cancellationToken);
```

The receipt contains the absolute local path, prepared file name/type, byte count, and opaque Work IQ `TransferId`.
That ID is **not** a remote driveItem ID: uploads do not return the completed driveItem or its final renamed name.
Resolve the remote item separately before using it as `remoteDriveItemId`.

**Preparation and authorization.** The client prepares each transfer through these Work IQ provider paths:

| MCP tool | Provider path | Structured `jsonBody` |
| --- | --- | --- |
| `call_function` | `/sharepoint/getDownloadFileDetails` | `documentLibraryId`, `fileId` |
| `do_action` | `/sharepoint/getUploadFileDetails` | `documentLibraryId`, `fileName`, `fileSize`, `parentFolderId`, `mimeType`, `conflictBehavior` |

These are provider paths, not direct calls to the underlying SharePoint MCP tools. They were checked by calling them
directly as an agent user; confirm them under your own agent identity and endpoint before deployment.

Each path needs a delegated Work IQ scope in addition to `WorkIQAgent.Ask`, on the same grant (client: the agent
identity, principal: the agent user):

| Operation | Scope |
| --- | --- |
| Download (`getDownloadFileDetails`) | `WorkIQ.Read` |
| Upload (`getUploadFileDetails`) | `WorkIQ.Act` |

Without it, preparation fails with 403. The Iris workflows don't use these APIs, so [scripts](../scripts/README.md)
grants only `WorkIQAgent.Ask`. Add the scopes your application needs to that grant; tokens issued after the change
carry them. Provider policy and feature gates still apply, and the client doesn't grant any access itself.

The authoritative handoff is in the returned business data's `_meta["com.microsoft.workiq/clientAction"]`, not MCP
transport metadata or prose instructions. The client validates its version, direction, expiry, method, sizes, and
constraints. Missing, native, or unsupported handoffs fail explicitly; there is no fallback to `fetch_blob` or native
SharePoint URLs.

Content and status requests each acquire the existing **Work IQ** bearer token. Only same-origin, Work IQ-issued
URLs are accepted; root-relative URLs resolve at the MCP endpoint's origin, not under `/mcp`. HTTPS is required
unless the configured endpoint is explicitly HTTP loopback for development. Redirects, cookies, generic retries,
provider-supplied headers, and default URI-bearing HTTP client logging are disabled for the dedicated transfer
client. Treat tokens, ticket URLs, and opaque transfer IDs as sensitive; do not log them or raw response bodies.

**Local files and conflicts.** Paths must be explicit, absolute paths supplied by trusted application code; these
APIs do not provide a filesystem sandbox or accept paths chosen by the model. The download's parent must already
exist. Downloads default to refusing overwrite, stream to a unique temporary sibling, validate the full byte count,
and publish only after success. Failure or cancellation removes that temporary file and preserves any old
destination. A late destination collision is checked again when publishing. Remote names and `Content-Disposition`
never choose the local destination. Empty downloads are supported.

Uploads open the source read-only and derive its size from the file. Empty uploads are rejected. The default remote
name is the source basename, folder is `root`, MIME type is `application/octet-stream`, and conflict behavior is
explicitly `fail`; callers can select `replace` or `rename`.

**Progress and failures.** Uploads send sequential, bounded file fragments with exact `Content-Length` and
`Content-Range`. Returned chunk recommendations/alignment are honored, with every PUT strictly below Work IQ's
60 MiB ingress ceiling. The final fragment may be unaligned. A 202 acknowledgement must confirm the exact next
offset; final success requires a completed Work IQ status with matching size/direction and no remaining ranges.
Each status response can extend the upload session, and the client checks later fragments against the latest
`expiresAt` it reports.

An ambiguous PUT is reconciled through the separately issued status URL. The client proceeds only after exact
confirmed advancement or final completion. Unchanged, partial, excessive, malformed, missing, expired, or
indeterminate status stops the upload without replaying bytes or creating a replacement session.
`WorkIqFileTransferException` retains the HTTP failure classification/status, transfer ID when available, and a
bounded `RecoveryReason` such as `upload_outcome_unknown` or `upload_session_unavailable`. A preparation error that
Work IQ returns inside a 200 envelope is classified by the provider's own status, so an unknown file ID is
`NotFound` (404). Opening files and writing/publishing downloads preserve local I/O errors.

`WorkIq:FileTransferTimeoutSeconds` defaults to **240 seconds per HTTP request**, including body streaming; it is
independent of the MCP timeout. Caller cancellation remains `OperationCanceledException` and stops local work,
but does not prove a remote upload was rolled back. Download resume and cross-process upload resume are not
implemented.

Tests use scripted MCP/HTTP and isolated local files, including large binary transfers and recovery failures.
They do not establish tenant feature availability. A live run as an agent user against its own OneDrive covered
downloads, a small and a 25 MiB multi-chunk upload with matching hashes, `replace`, local overwrite handling, an
unknown file ID, and cleanup of temporary files. No live test is part of the automated suite.

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
