# Iris tenant setup scripts

These scripts create the agent identity for the `agentic-user-workiq` sample and put its runbook in its OneDrive. They do not contain tenant IDs, object IDs, UPNs, tunnel URLs, tokens, or passwords. Runtime state and generated credentials stay in gitignored files beside the scripts.

## Install

Use Python 3.10 or later.

```powershell
cd scripts
python -m venv .venv
.\.venv\Scripts\Activate.ps1
pip install -r requirements.txt
```

## Run order

1. Sign in with a tenant admin:
   `python graph_auth.py login --tenant-id <tenant-id>`
2. Create Iris:
   `python create_au.py --domain <verified-domain>`
3. Verify token issuance:
   `python au_token.py`
4. Verify Work IQ access:
   `python workiq_probe.py tools`
5. Point Teams at your host (or use the Teams Developer Portal, see [SETUP.md](../docs/SETUP.md)), and optionally
   turn on all-message delivery:
   `python comm_config.py set endpoint=https://<host>/api/messages`
   `python comm_config.py set group=all channel=all meeting=all`
6. Upload the runbook to Iris's OneDrive:
   `python seed_onedrive.py`

All commands support `--help`, and each one is safe to re-run. `create_au.py` reuses the objects listed in
`au-state.json`, and `seed_onedrive.py` replaces the file.

## Scripts

| Script | Purpose | Writes |
| --- | --- | --- |
| `graph_auth.py` | Device-code sign-in with the Microsoft Graph Command Line Tools public client. Shared Graph helper. | `.msal-cache.json` |
| `create_au.py` | Creates the agent identity blueprint, blueprint service principal, blueprint secret, agent identity, agentic user, Teams grant, Work IQ grant, profile, manager, licenses, and avatar. The avatar is `iris-avatar.png` unless you pass `--avatar`. | `au-state.json`, `.blueprint-secret.json` |
| `au_token.py` | Mints an agentic-user token and prints safe claims. | Nothing |
| `workiq_probe.py` | Calls Work IQ MCP as Iris. `tools` lists tools. `fetch` fetches entity URLs. | Nothing |
| `comm_config.py` | Gets, sets, or restores the blueprint's Teams endpoint and delivery modes. | `comm-config-backup.json` on the first `set` that finds a previous configuration |
| `seed_onedrive.py` | Temporarily grants Iris Graph `Files.ReadWrite`, uploads the runbook to Iris's OneDrive at `/Runbooks/`, then removes the grant. | Nothing |

## Required roles and consent

Use a tenant admin account that can create applications, service principals, users, delegated permission grants, and license assignments. Global Administrator works for a test tenant. In locked-down tenants, combine the equivalent Entra, User and License roles.

The default sign-in asks for these delegated Graph scopes because the scripts need them:

- Agent Identity: `AgentIdentityBlueprint.Create`, `AgentIdentityBlueprint.AddRemoveCreds.All`, `AgentIdentityBlueprint.UpdateAuthProperties.All`, `AgentIdentityBlueprintPrincipal.Create`, `AgentIdUser.ReadWrite.All`, `AgentCommunicationConfiguration.ReadWrite`
- Consent and app lookup: `DelegatedPermissionGrant.ReadWrite.All`, `Application.Read.All`
- Users and licenses: `User.ReadWrite.All`, `LicenseAssignment.ReadWrite.All`

To ask for an extra scope for your own scripts, add `--scope <scope>` to `graph_auth.py login`.

If a script can't get a token from the cached sign-in, it prints the `error` and `error_description` from Entra (for
example an `AADSTS` code) and the `graph_auth.py login` command to run again.

`create_au.py` also grants the first-party Teams Messaging Bot API `AgentData.ReadWrite` scope and Work IQ `WorkIQAgent.Ask` for Iris.

## Configuration and safety

Pass tenant-specific values as command-line arguments or environment variables. Useful variables are `AZURE_TENANT_ID`, `GRAPH_TENANT_ID`, `GRAPH_LOGIN_ACCOUNT`, `AGENTIC_USER_LICENSE_SKUS`, and `WORKIQ_MCP_ENDPOINT`.

Do not commit generated state. The sample `.gitignore` excludes token caches, generated secrets, `au-state.json`, backups, and Python caches.

`comm_config.py set` is a full Graph `PUT`. It reads the current configuration, changes only what you pass, and sends
the rest back, including the endpoint. If there was a previous configuration, the first `set` saves it to
`comm-config-backup.json`. Keep that file until you are done. On a new blueprint there is nothing to back up, and
`comm_config.py get` says there is no configuration yet.

Saving the blueprint's Configuration page in the Teams Developer Portal sets group, channel and meeting delivery to
`atMentionedMessagesOnly`. Run `comm_config.py get` after saving, and `comm_config.py set group=all channel=all meeting=all`
again if needed. To change only the URL, use `comm_config.py set endpoint=https://<host>/api/messages`.
