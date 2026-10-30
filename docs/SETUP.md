# Setup

This sets up Iris in a test tenant:
1. the agent blueprint, agent identity and agent user;
2. the notification URL;
3. the app;
4. a first run with your own team, as in [SCENARIO.md](SCENARIO.md).

Each tenant step is a Python script in [`scripts/`](../scripts/README.md). The scripts call Graph as a signed-in
admin, and each one is safe to re-run.

## 1. Prerequisites

- **A tenant with agent users enabled.** Agent users, blueprints and the Teams delivery for agent users are in
  preview (Frontier / Agent 365). Use a test tenant.
- **An admin account.** Global Administrator is simplest in a test tenant. Otherwise you need roles that can create
  applications and users, grant delegated permissions, and assign licences.
- **Licences for the agent user.** A Teams licence, plus Exchange and OneDrive for her calendar and files, for
  example Microsoft Teams Enterprise plus Office 365 E3 (no Teams). Pass your SKUs with `create_au.py --license-sku`.
- **Work IQ turned on** for the tenant: Microsoft 365 admin center > Agents > Tools
  (`https://admin.cloud.microsoft/#/agents/tools`). It's billed in Copilot Credits
  ([WORKIQ-NOTES.md](WORKIQ-NOTES.md#cost)).
- **A chat model.** Azure OpenAI with a chat model deployment (for example `gpt-5.4-mini`): give your developer identity
  (and the app's managed identity in Azure) the **Cognitive Services OpenAI User** role, or set an API key. Or use
  OpenAI, or another OpenAI-compatible server, with an API key.
- **A colleague in the tenant** to try Iris with in a group chat (step 7).
- **Tools:** .NET 10 SDK, Python 3.10 or later, and [dev tunnels](https://learn.microsoft.com/azure/developer/dev-tunnels/)
  (or any public HTTPS endpoint).

```powershell
cd scripts
python -m venv .venv; .\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

## 2. Sign in for the scripts

```powershell
python graph_auth.py login --tenant-id <tenant-id>
```

This uses the Microsoft Graph Command Line Tools client with a device code. The token cache goes to
`scripts/.msal-cache.json`, which is gitignored. The scopes it asks for are listed in
[scripts/README.md](../scripts/README.md#required-roles-and-consent). Several of them are broad, so trim the consent
when you're done (step 8).

The other scripts don't take a tenant ID. They use `AZURE_TENANT_ID` (or `GRAPH_TENANT_ID`) if it's set, and
otherwise the home tenant of the account you signed in with. If that account is a guest in the test tenant, set
`$env:AZURE_TENANT_ID = "<tenant-id>"` before you run them.

## 3. Create the agent identity and agent user

```powershell
python create_au.py --domain <verified-domain> --license-sku <sku-id> --license-sku <sku-id>
```

| Creates | Notes |
| --- | --- |
| **Agent blueprint** "Incident Teammate Blueprint" (an Entra app) | You are its owner and sponsor. The app reads this blueprint's app ID and secret as `AzureAd`. |
| Blueprint **client secret** | Written to `scripts/.blueprint-secret.json` (gitignored). A new secret can take about a minute to work. In production, use a certificate or managed identity. |
| Blueprint **principal** | |
| **Agent identity** "Iris Incident Agent" | Created with the blueprint's own token |
| **Agent user** "Iris (Incident Agent)", e.g. `iris.agent@<domain>` | A real directory account, with job title, department, office, manager (you), licences and photo (`iris-avatar.png`) |
| Grant: Messaging Bot API `AgentData.ReadWrite` | For the blueprint principal and the agent identity: Teams messaging for the agent |
| Grant: Work IQ `WorkIQAgent.Ask` | Delegated, from the agent identity, for the agent user only |

The IDs go into `scripts/au-state.json` (gitignored). Running `create_au.py` again reuses the objects listed there,
so it's safe to re-run. The photo goes into the agent user's mailbox, which takes a few minutes to be ready after the
user is created. The script retries for a few minutes. If the mailbox still isn't ready, it prints a warning, and
you can run `create_au.py` again later to set the photo.

Check that the identity works:

```powershell
python au_token.py            # prints the agent user token's claims (aud, oid, scp), not the token
python workiq_probe.py tools  # lists the Work IQ tools Iris can call
```

If it fails right after create_au.py, run it again after a minute. `workiq_probe.py` prints the error it got.

## 4. Run the app

```powershell
cd src/IncidentAgent
dotnet user-secrets set "AzureAd:TenantId" "<tenant id>"
dotnet user-secrets set "AzureAd:ClientId" "<blueprintAppId from au-state.json>"
dotnet user-secrets set "AzureAd:ClientCredentials:0:ClientSecret" "<secretText from .blueprint-secret.json>"
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://<resource>.openai.azure.com/"
dotnet user-secrets set "AzureOpenAI:Deployment" "<deployment>"
cd ../..
dotnet run --project src/IncidentAgent          # http://localhost:3980, health check at /health
```

User secrets are stored per machine under the `UserSecretsId` in `IncidentAgent.csproj`, so every checkout of this
sample on the machine shares them. To run a second copy with other settings, pass them as environment variables
(for example `$env:AzureAd__ClientId`) or arguments (`--AzureAd:ClientId=...`). Both override user secrets.

Port 3980 comes from `Properties/launchSettings.json`. To use another port, run
`dotnet run --project src/IncidentAgent -- --urls http://localhost:<port>` and use that port in the tunnel.

For OpenAI instead of Azure OpenAI, set these in place of the two `AzureOpenAI` lines above:

```powershell
dotnet user-secrets set "AzureOpenAI:Provider" "OpenAI"
dotnet user-secrets set "AzureOpenAI:ApiKey" "<api key>"
dotnet user-secrets set "AzureOpenAI:Deployment" "<model name>"
# Optional, for another OpenAI-compatible server:
dotnet user-secrets set "AzureOpenAI:Endpoint" "https://<server>/v1/"
```

To give the agent a different name or role, set `Agent:Name` and `Agent:Role`. Every setting is listed in the
[README](../README.md#configuration).

In another terminal, expose it:

```powershell
devtunnel create iris-agent --allow-anonymous
devtunnel port create iris-agent -p 3980
devtunnel host iris-agent                       # note the https://<id>-3980.<region>.devtunnels.ms URL
```

The tunnel must allow anonymous access, because Teams calls it without a dev tunnels sign-in. The Teams SDK
validates every request's token against the blueprint. `devtunnel create` also makes the new tunnel the CLI's
default, so later `devtunnel` commands without a tunnel name use it.

Preview Teams features are off by default. To try them, pass them when you
start the host, for example `--Features:Streaming=All --Features:HtmlWidget=true`. The widget also needs
`--Features:WidgetDomain=https://<your tunnel host>`. The full list is in [FEATURES.md](FEATURES.md#settings).

## 5. Set the notification URL

Teams delivers Iris's messages to the URL on her blueprint. Set it either way:

- **Script:**
  ```powershell
  python comm_config.py set endpoint=https://<your-tunnel>/api/messages
  ```
- **Teams Developer Portal:**
  1. Go to `https://dev.teams.microsoft.com` > Tools > Agent Identity Blueprint > your blueprint > Configuration.
  2. Choose **URL**, enter `https://<your-tunnel>/api/messages`, and Save.
  3. Saving here also **sets group, channel and meeting delivery to @mentions only**. If you use all-message
     delivery, run step 6 again afterwards.

Check it with `python comm_config.py get`. On a new blueprint, before the first `set`, it says there is no
configuration yet.

## 6. Optional: deliver all messages

By default, Teams delivers only the group and channel messages that @mention Iris. She still picks up work nobody
asked for by polling for a while after she posts. To have Teams push every message instead:

```powershell
python comm_config.py set group=all channel=all meeting=all
```

Then run the app with `Watch:ReceivesAllMessages=true`, for example
`dotnet run --project src/IncidentAgent -- --Watch:ReceivesAllMessages=true`. Read
[MESSAGE-DELIVERY.md](MESSAGE-DELIVERY.md) first. It covers what changes, handling repeat deliveries, and guidance
for admins. `python comm_config.py restore` puts back the configuration from before your first `set`, if there was
one.

## 7. Try it with your own team

First, put the runbook in Iris's OneDrive. She reads it at `Runbook:Path` (`/Runbooks/helpdesk-bot-runbook.md`)
when she answers, so do this before the first chat:

```powershell
python seed_onedrive.py          # the sample runbook into Iris's OneDrive at /Runbooks/
```

`seed_onedrive.py` gives itself a short-lived `Files.ReadWrite` grant to write into Iris's OneDrive, then removes it.
Iris reads the runbook through Work IQ. Re-running it replaces the file. To use your own runbook, pass its path
(`python seed_onedrive.py <file>`) and set `Runbook:Path` to match.

Then try Iris with a colleague. She shows up in Teams search by name, and you don't need a Teams app package.
1. Message Iris in a 1:1 chat, for example "what does our runbook say about AADSTS7000215?"
2. With a colleague, start a group chat and talk through an incident. Use an incident ID such as INC-2304, because
   Iris keeps her notes per incident. Add Iris by name, including the chat history, and post "@Iris catch us up".
3. Optional: invite Iris to a meeting with "shiproom" in its title, and link the decks in the invite. Then tell
   each other in the chat that you need an update for the shiproom. Iris posts a draft card. Approve it, and she
   posts the slide text ready to paste into this week's deck.

For the shiproom meeting:
- Invite Iris from Outlook or Teams like any attendee. She finds the meeting on her own calendar by the word in
  `Shiproom:MeetingKeyword`.
- Name each deck with its meeting date, for example `Shiproom 2026-05-14.pptx`, and share the decks or their folder
  with Iris.
- In the invite, link this week's deck, plus last week's deck or the folder of earlier decks.
- Give last week's deck a slide titled "Incident updates" (`Shiproom:SlideTitle`) with one incident in your team's
  format, for example a bold first line with the incident ID, title, severity and status, then Impact, Cause, Fix,
  Next and Ask lines. Iris copies that format for the new update. Without it, she still drafts the update.

[SCENARIO.md](SCENARIO.md) walks through the whole scenario, scene by scene.

## 8. Clean up

- Remove the broad consents that `graph_auth.py login` granted to Microsoft Graph Command Line Tools: Entra >
  Enterprise applications > Microsoft Graph Command Line Tools > Permissions.
- Delete `scripts/.msal-cache.json` and `.blueprint-secret.json`. Remove the blueprint secret from user secrets when
  you're done.
- To remove Iris, delete the agent user, the agent identity and the blueprint, in that order. Disabling them stops
  her from acting. Message delivery is set by the blueprint's communication configuration
  ([MESSAGE-DELIVERY.md](MESSAGE-DELIVERY.md#guidance-for-admins)).

## Troubleshooting

| Symptom | Fix |
| --- | --- |
| Nothing reaches `POST /api/messages` | Run `comm_config.py get` and check the URL. Check the tunnel is running and anonymous. Allow about 40 s after a change. |
| 401 on incoming requests | `AzureAd:ClientId` isn't the blueprint's app ID, or the tenant ID is wrong |
| `AADSTS7002207` in the log | The agent identity is disabled or doesn't exist |
| `AADSTS50057` in the log | The agent user's account is disabled |
| "Work IQ didn't accept my sign-in" | The `WorkIQAgent.Ask` grant for the agent user is missing, or Work IQ is off for the tenant |
| "I couldn't find my runbook at ... in my OneDrive" | The runbook isn't in Iris's OneDrive at `Runbook:Path`. Run `python seed_onedrive.py` (step 7). Iris still answers without it, and the log says where she looked. |
| Iris can't find the meeting | She isn't invited, or the title doesn't contain `Shiproom:MeetingKeyword`. She sees only what's shared with her, so invite her like any attendee. |
| Unmentioned messages stopped arriving | Someone saved the blueprint in the Developer Portal. Run step 6 again. |
| A script says it couldn't get a Graph token | It prints the `error` and `error_description` from Entra, such as an `AADSTS` code. Run `python graph_auth.py login --tenant-id <tenant-id>` again. |
| `AzureOpenAI:Endpoint` validation error at start | Set the user secrets in step 4 |
| `AzureOpenAI:ApiKey` validation error at start | `AzureOpenAI:Provider` is `OpenAI`, which needs an API key |
| 401 on every incoming request after an SDK upgrade | Teams SDK 2.1.2 and later accept Entra agent tokens only from the Agent 365 platform. Requests you send yourself with another app's token are refused, which is expected |
| "Multiple root message entities" (400) when Iris posts | The message has more than one root message entity, for example from `AddSensitivityLabel`. Put the label on the one root entity, as `TeamsSender.Render` does ([FEATURES.md](FEATURES.md#building-with-the-teams-sdk)) |
