# Helpdesk bot runbook

Troubleshooting cases for the Helpdesk Teams bot and other Teams apps. Iris (the incident agent) reads this file from its own OneDrive and matches cases against incident conversations. When adding a case, copy an existing block, use the next free TB ID, and keep the four labels in the same order so the parser can read it.

## Bots and messaging

### TB-001: Bot doesn't respond when @mentioned in a channel
**Symptoms:** Engineers say "the bot is installed but ignores @mentions" or users see no reply after tagging the bot in a team channel.
**Keywords:** @mention, channel, no response, bot ignored, Teams channel
**Cause:** The bot only receives channel messages when it is installed in that team and explicitly mentioned, and the Teams channel must be enabled on the Azure Bot. A mismatched bot ID in the manifest or a stale app package can also route the mention to no bot.
**Fix:**
1. Confirm the Teams app is installed in the target team and the bot is included in the `team` scope in the manifest.
2. Verify the manifest bot ID matches the Azure Bot Microsoft App ID exactly.
3. Ensure the Microsoft Teams channel is enabled on the Azure Bot resource.
4. Reinstall or update the app in the team, then test a fresh @mention in a new channel thread.

### TB-002: Bot doesn't respond in personal chat after installation
**Symptoms:** A user opens a 1:1 chat with the bot and sends a command, but the chat remains silent or only shows "Something went wrong."
**Keywords:** personal chat, 1:1, no reply, personal scope, bot install
**Cause:** The app package may not declare the `personal` bot scope, or the bot may not handle personal conversation activities. In personal scope, Teams sends different conversation metadata than channel scope and the bot must not require team or channel IDs.
**Fix:**
1. Add `personal` to the bot scopes in the Teams manifest if personal chat is supported.
2. Update the bot handler so personal activities do not require `teamId` or `channelId`.
3. Re-upload or publish the updated app package and reinstall it for the test user.

### TB-003: Bot replies twice or sends duplicate activities
**Symptoms:** Users report duplicate answers, duplicate Adaptive Cards, or two bot messages for one command.
**Keywords:** duplicate reply, double response, retried activity, repeated card
**Cause:** Teams or Bot Connector can retry delivery when the bot endpoint times out or returns a transient failure, and multiple running bot instances may process the same activity. The bot may also both return an invoke response and send a separate message for the same action.
**Fix:**
1. Log the incoming `activity.id`, `conversation.id`, and operation path to identify retries versus duplicate code paths.
2. Make command processing idempotent by storing processed activity IDs for a short retention window.
3. Return success quickly for invoke activities and avoid also sending a duplicate proactive message unless intentionally needed.
4. Check deployment slots, webhooks, and bot registration so only one live endpoint handles production traffic.

### TB-004: Bot gets 401 or 403 from Bot Connector when replying
**Symptoms:** The bot receives the user activity but reply calls fail with "Unauthorized", "Forbidden", or HTTP 401/403 from the connector service.
**Keywords:** 401, 403, Bot Connector, Unauthorized, Forbidden, MicrosoftAppId
**Cause:** The bot credentials used by the SDK do not match the Azure Bot registration, app registration, tenant type, or cloud environment. Expired client secrets and incorrect managed identity configuration are common causes.
**Fix:**
1. Verify `MicrosoftAppId`, credential type, tenant ID, and app type match the Azure Bot and app registration.
2. Replace expired client secrets or validate the managed identity assignment and federated credential configuration.
3. Confirm the bot is using the correct Bot Framework authority and channel service for the target cloud.
4. Restart the host after configuration changes and test a simple reply activity.

### TB-005: Messaging endpoint is wrong or not reachable
**Symptoms:** Teams shows "Unable to reach app" or the Azure Bot test pane fails after a tunnel, route, or deployment change.
**Keywords:** messaging endpoint, /api/messages, unable to reach app, 404, 502, tunnel
**Cause:** Azure Bot sends activities to the configured messaging endpoint, and that endpoint must be public HTTPS and route to the bot adapter. A changed dev tunnel or App Service route can leave Azure Bot pointing at a stale URL.
**Fix:**
1. Confirm the Azure Bot messaging endpoint uses a publicly reachable HTTPS URL with the correct bot route.
2. Test the endpoint health separately from Teams and verify the app receives POST requests.
3. Update the Azure Bot messaging endpoint after any tunnel or hostname change.
4. Redeploy or restart the host if routing middleware was changed.

### TB-006: Proactive messages are not delivered
**Symptoms:** A scheduled or background notification says it sent successfully in logs, but no Teams user or channel receives a message.
**Keywords:** proactive message, conversation reference, not delivered, continue conversation, app not installed
**Cause:** Proactive sends require a valid conversation reference for the target scope and the app must be installed for that user, chat, or team. Old service URLs, deleted chats, or using a channel conversation reference for a personal message can fail silently in poorly logged code.
**Fix:**
1. Capture and persist the conversation reference from a recent valid Teams activity in the same target scope.
2. Confirm the app is installed for the recipient or install it using an approved Teams app installation flow.
3. Use the latest `serviceUrl`, `conversation.id`, tenant ID, and bot app ID when calling continue conversation.
4. Log connector responses and remove stale conversation references when Teams returns not found or forbidden.

### TB-007: conversationUpdate is not received after install or update
**Symptoms:** Setup code waiting for `conversationUpdate` never runs, or engineers say "the bot never gets membersAdded."
**Keywords:** conversationUpdate, membersAdded, install event, app update, roster
**Cause:** Teams sends conversation update activities for specific install and roster events, not for every manifest upload or catalog update. Existing installations may not receive a new install event after an app package is updated.
**Fix:**
1. Test by installing the app into a fresh personal scope, chat, or team rather than only updating the package.
2. Handle both `conversationUpdate` and Teams-specific install update events if the bot depends on installation state.
3. Do not require `membersAdded` for normal message handling; lazily initialize state on the first user activity as well.

### TB-008: Mention text is included in the command
**Symptoms:** The command parser sees strings like "<at>SupportBot</at> help" and fails to match the intended command.
**Keywords:** mention text, RemoveRecipientMention, command parsing, <at>, entities
**Cause:** In channel and group chat scopes, Teams includes the bot mention in the message text and also provides mention entities. The bot must remove its own mention before parsing the command.
**Fix:**
1. Use the Bot Framework helper to remove the recipient mention before command parsing.
2. Fall back to inspecting `activity.entities` if the helper is not available in the SDK version.
3. Trim whitespace and HTML entity artifacts before matching commands.

### TB-009: User-specific reply appears to everyone or is not visible
**Symptoms:** Engineers expect a private answer in a channel thread, but the bot response is visible to the whole conversation or the intended user never sees it.
**Keywords:** private reply, targeted message, channel visibility, personal chat, ephemeral
**Cause:** Normal bot messages sent to a channel or group chat are visible to that conversation. To send user-specific content, the bot usually needs a personal chat conversation with the user or a supported invoke response surface, not a standard channel reply.
**Fix:**
1. Decide whether the response is safe for the whole channel; if not, do not send it as a channel message.
2. Use or create the user's personal bot conversation after confirming the app is installed for that user.
3. Send sensitive results only in personal scope and post a non-sensitive acknowledgement in the channel if needed.

### TB-010: Bot is throttled with HTTP 429
**Symptoms:** Bulk sends or notification jobs fail with 429, "Too Many Requests", or intermittent connector send failures.
**Keywords:** 429, throttling, Retry-After, rate limit, bulk notification
**Cause:** Teams and Bot Connector enforce rate limits to protect users and the service. Fan-out jobs that send many messages at once can exceed per-bot, per-conversation, or service limits.
**Fix:**
1. Honor `Retry-After` headers exactly and use exponential backoff with jitter for retryable sends.
2. Queue proactive notifications and limit concurrency per tenant and conversation.
3. Batch non-urgent notifications into fewer messages when the product experience allows it.
4. Track 429 rates in telemetry so notification jobs can be tuned before they fail broadly.

### TB-011: Adaptive Card actions do not reach the bot
**Symptoms:** Users click a card button and nothing happens, or the bot never receives the expected invoke activity.
**Keywords:** Adaptive Card, Action.Submit, Action.Execute, invoke, button click
**Cause:** Teams only routes card actions correctly when the card action type, schema, and bot response contract match the Teams capability being used. Cards posted by connectors, webhooks, or Graph are not equivalent to bot-sent cards for bot invoke handling.
**Fix:**
1. Confirm the card was sent by the bot in the same conversation where the action is clicked.
2. Use `Action.Submit` for classic bot card submits or implement Universal Actions correctly for `Action.Execute`.
3. Handle the corresponding invoke name and return the Teams-specific invoke response shape.
4. Test the card on desktop, web, and mobile if the action is business critical.

### TB-012: Typing indicator never appears during a long operation
**Symptoms:** The bot takes several seconds to answer and users think it is frozen because no typing indicator appears.
**Keywords:** typing indicator, long running, delay, user feedback, activity type typing
**Cause:** Teams does not automatically show bot typing status while server work is running. The bot must send typing activities before or during longer work, and the final response must still meet Teams timeout expectations for the activity type.
**Fix:**
1. Send a `typing` activity as soon as long-running work starts.
2. For operations that may exceed the invoke timeout, acknowledge quickly and continue work asynchronously.
3. Send the final answer as a normal message or proactive update when processing completes.

### TB-013: Updating or deleting a bot message fails
**Symptoms:** Calls to update or delete an activity return not found, forbidden, or no visible change in Teams.
**Keywords:** update activity, delete activity, activity id, not found, forbidden
**Cause:** Bots can update or delete only messages they sent, and they must use the activity ID returned by Teams for that specific conversation. Using an incoming user activity ID, a reply-to ID, or an ID from a different scope will fail.
**Fix:**
1. Store the activity ID returned from the original send operation.
2. Update or delete the activity in the same conversation reference and tenant where it was sent.
3. If the original message was not sent by the bot, post a corrective follow-up instead of attempting to edit it.

### TB-014: Markdown or rich text renders incorrectly
**Symptoms:** Lists, links, code blocks, or bold text look different in Teams than they did in local tests or another channel.
**Keywords:** markdown, rich text, formatting, HTML, rendering
**Cause:** Teams bot messages support a Teams-specific subset of Markdown and HTML behavior, and rendering differs between plain text messages and Adaptive Cards. Mention rendering also requires mention entities, not just visible text.
**Fix:**
1. Keep bot message Markdown simple and test it in the Teams clients that users rely on.
2. Use Adaptive Cards for structured layouts instead of complex Markdown or HTML.
3. For mentions, include proper Teams mention entities and IDs rather than hand-written `<at>` text.

### TB-015: Attachments or files in bot chat do not open
**Symptoms:** Users see a broken attachment, a card with no file, or a link that requires unexpected authentication.
**Keywords:** attachment, file consent, broken file, HTTPS, download
**Cause:** Bot attachments must be reachable by Teams clients, and local paths or private URLs cannot be fetched by users. File upload patterns differ between personal bot chats, channel files, and Graph-backed file storage.
**Fix:**
1. Use public HTTPS content URLs or authenticated links that the recipient can access.
2. For personal bot file uploads, use the Teams file consent card flow where appropriate.
3. For team or channel files, upload through SharePoint or Graph with correct permissions and send a link or card.
4. Avoid embedding secrets or short-lived internal URLs in card content.

### TB-016: Bot cannot read channel messages without an @mention
**Symptoms:** Engineers expect the bot to monitor all channel messages, but it only receives messages where users mention the bot.
**Keywords:** read channel messages, no @mention, RSC, ChannelMessage.Read.Group, background monitoring
**Cause:** Teams bots are not general channel listeners by default; they receive messages addressed to them and certain conversation events. Reading all channel messages requires Microsoft Graph permissions, and Teams resource-specific consent can be used for team-scoped scenarios when configured and consented.
**Fix:**
1. Keep bot conversation handling based on @mentions unless full channel monitoring is truly required.
2. Add the appropriate Graph or RSC permission such as `ChannelMessage.Read.Group` only for the scenario that needs it.
3. Reinstall or re-consent the app so the new RSC permission is granted.
4. Read messages through Graph and keep the bot reply path separate from the Graph ingestion path.

### TB-017: Bot invoke times out after about 15 seconds
**Symptoms:** Card actions, task modules, or message extension invokes show a timeout even though backend work eventually finishes.
**Keywords:** invoke timeout, 15 seconds, timeout, task module, long running
**Cause:** Teams expects invoke handlers to return quickly, and long-running LLM calls, Graph calls, or database work can exceed the client timeout. A late successful HTTP response may still be treated as a failed invoke by Teams.
**Fix:**
1. Return the required invoke acknowledgement or initial response within the Teams timeout.
2. Move long-running work to a background queue or worker.
3. Update the original card or send a follow-up message when the work completes.
4. Add timeout telemetry around external dependencies to find the slow segment.

### TB-018: Teams channel is not enabled on the Azure Bot
**Symptoms:** The bot works in Emulator or Web Chat but does not receive any Teams activities.
**Keywords:** Teams channel, Azure Bot channel, Emulator works, Web Chat works
**Cause:** Bot Framework Emulator and Web Chat can work even when the Microsoft Teams channel is not enabled on the Azure Bot. Teams traffic will not reach the bot until the Teams channel is configured.
**Fix:**
1. Open the Azure Bot resource and verify the Microsoft Teams channel is enabled.
2. Accept or update the Teams channel terms and configuration if prompted.
3. Wait for configuration propagation and test from a newly installed Teams app package.

### TB-019: Bot posts in the wrong thread or conversation
**Symptoms:** A reply appears as a new channel post, in a different thread, or in personal chat instead of the original conversation.
**Keywords:** wrong thread, replyToId, conversation id, channel thread, proactive
**Cause:** Teams channel threads, group chats, and personal chats use different conversation IDs and reply semantics. Using a saved personal conversation reference or omitting the correct reply target can move the response to the wrong place.
**Fix:**
1. For direct replies, use the current incoming activity and let the adapter create the reply activity.
2. For proactive thread replies, save the conversation reference and the root or parent activity ID that should receive the reply.
3. Do not reuse conversation references across personal, group chat, and channel scopes.

### TB-020: Bot command menu is missing or commands are ignored
**Symptoms:** Users do not see suggested bot commands, or selecting a command inserts text that the bot does not recognize.
**Keywords:** command menu, commandLists, bot commands, menu missing
**Cause:** Bot command menus come from `commandLists` in the Teams manifest and are scoped by `personal`, `team`, or `groupChat`. The bot still receives plain text and must parse the command exactly as declared.
**Fix:**
1. Add command entries under the correct scope in the manifest `commandLists`.
2. Keep command titles short and align the declared command text with the bot parser.
3. Increment the manifest version and reinstall or update the app to refresh the command menu.

## Sign-in and SSO

### TB-021: Token exchange returns 412 consent required
**Symptoms:** The bot receives a token exchange invoke and responds with or logs HTTP 412, "consent required", or "precondition failed."
**Keywords:** 412, consent required, token exchange, SSO, OAuthPrompt
**Cause:** Teams attempted silent SSO, but the user or tenant has not consented to the exposed API or downstream scopes. A 412 is expected when the bot must fall back to an interactive sign-in card.
**Fix:**
1. Handle the token exchange invoke and return the correct 412 response when consent is required.
2. Start the OAuth prompt or sign-in card flow so the user can consent interactively.
3. Request tenant admin consent for scopes that require admin approval.
4. Retest with a fresh user session after consent is granted.

### TB-022: Teams SSO resource is not api://botid-{appId}
**Symptoms:** Teams SSO fails with invalid resource, token exchange failure, or a token whose audience does not match the bot API.
**Keywords:** webApplicationInfo, resource, api://botid, invalid resource, audience
**Cause:** For bot SSO, the manifest `webApplicationInfo.resource` must match the Application ID URI exposed by the bot's Microsoft Entra app registration, commonly `api://botid-{appId}`. If the manifest, app registration, and OAuth connection use different resource values, token exchange cannot succeed.
**Fix:**
1. Set the app registration Application ID URI to the intended Teams SSO resource.
2. Set manifest `webApplicationInfo.id` to the Entra app ID and `webApplicationInfo.resource` to the exact same Application ID URI.
3. Update the Azure Bot OAuth connection token exchange URL to the same resource value.
4. Repackage and reinstall the Teams app before testing again.

### TB-023: Teams client IDs are not pre-authorized
**Symptoms:** SSO works only after manual consent or fails silently with consent errors for Teams desktop, mobile, or web.
**Keywords:** pre-authorized, 1fec8e78-bce4-4aaf-ab1b-5451cc387264, 5e3ce6c0-2b1f-4285-8d4b-75ee78787346, access_as_user
**Cause:** The exposed API scope must pre-authorize the Teams clients so Teams can request the token on the user's behalf. Missing the Teams desktop/mobile client ID or the Teams web client ID breaks silent SSO on those clients.
**Fix:**
1. In the Entra app registration, expose the bot API scope such as `access_as_user`.
2. Add pre-authorized client `1fec8e78-bce4-4aaf-ab1b-5451cc387264` for Teams desktop and mobile.
3. Add pre-authorized client `5e3ce6c0-2b1f-4285-8d4b-75ee78787346` for Teams web.
4. Save the app registration and retest SSO after the updated app package is installed.

### TB-024: OAuth connection token exchange URL is wrong
**Symptoms:** The OAuth prompt appears even though SSO is configured, or token exchange invokes are rejected by the bot framework token service.
**Keywords:** OAuth connection, token exchange URL, Azure Bot, SSO, connection name
**Cause:** The Azure Bot OAuth connection must use the same token exchange URL as the exposed API Application ID URI. A typo, old app ID, or missing `api://botid-` prefix prevents token exchange from mapping Teams SSO tokens to the bot connection.
**Fix:**
1. Open the Azure Bot OAuth connection used by the bot code.
2. Set the token exchange URL to the exact Application ID URI used in the manifest resource.
3. Confirm the bot code references the same OAuth connection name.
4. Restart the bot host and run a fresh sign-in test.

### TB-025: Bot sign-in redirect URI is missing
**Symptoms:** Interactive sign-in fails after credentials are entered, or Entra reports that the reply URL does not match.
**Keywords:** redirect URI, reply URL, token.botframework.com, .auth/web/redirect, AADSTS50011
**Cause:** Bot Framework OAuth sign-in uses the redirect URI `https://token.botframework.com/.auth/web/redirect`. If that URI is not registered on the Entra app, the authorization response is rejected.
**Fix:**
1. Add `https://token.botframework.com/.auth/web/redirect` as a web redirect URI on the Entra app registration used by the OAuth connection.
2. Save the app registration and allow configuration propagation.
3. Retry the sign-in flow in Teams with a new sign-in card.

### TB-026: token.botframework.com is missing from validDomains
**Symptoms:** The sign-in popup is blocked, the auth card does not open, or Teams says the domain is not allowed.
**Keywords:** validDomains, token.botframework.com, sign-in popup, domain not allowed
**Cause:** Teams app packages restrict web content and auth surfaces to domains listed in `validDomains`. Bot Framework OAuth cards require `token.botframework.com` to be trusted by the manifest.
**Fix:**
1. Add `token.botframework.com` to the manifest `validDomains` array without a protocol or path.
2. Include the app's own auth and tab host domains if they are used in the sign-in flow.
3. Increment the manifest version, upload the package, and reinstall or update the app.

### TB-027: OBO call fails with AADSTS65001
**Symptoms:** The bot gets a Teams SSO token but the downstream Microsoft Graph or API call fails with AADSTS65001 or "user or administrator has not consented."
**Keywords:** AADSTS65001, OBO, admin consent, Graph scopes, consent
**Cause:** The token for the bot API does not automatically grant downstream API permissions. The On-Behalf-Of flow still requires user or admin consent for the downstream delegated scopes being requested.
**Fix:**
1. Add the required downstream delegated permissions to the Entra app registration.
2. Grant tenant admin consent for admin-restricted scopes.
3. Request only the scopes the feature needs and retry OBO with the Teams SSO token as the user assertion.
4. Clear cached failed tokens before retesting.

### TB-028: OBO token has invalid audience or AADSTS50013
**Symptoms:** OBO fails with AADSTS50013, "invalid assertion", or an API rejects the token because the audience is wrong.
**Keywords:** AADSTS50013, invalid assertion, invalid audience, aud, OBO
**Cause:** The user assertion passed to OBO must be a token issued for the bot API, not a Graph token or a token for another resource. Audience mismatches often come from an incorrect `webApplicationInfo.resource` or from using the wrong token cache entry.
**Fix:**
1. Decode the incoming token without logging secrets and confirm the `aud` claim matches the bot API Application ID URI or app ID expected by the OBO configuration.
2. Align the manifest resource, exposed API identifier, and OAuth token exchange URL.
3. Make sure the OBO request asks for the downstream resource scopes, not the bot API scope again.

### TB-029: AADSTS700016 app not found in tenant
**Symptoms:** Sign-in fails with AADSTS700016 or says the application identifier was not found in the directory.
**Keywords:** AADSTS700016, app not found, tenant, single tenant, multi tenant
**Cause:** The app registration may be single-tenant in a different tenant, or the manifest may reference an app ID that does not exist in the user's tenant. Multi-tenant apps also need the publisher and consent configuration to allow external tenant use.
**Fix:**
1. Verify the app ID in the Teams manifest and Azure Bot configuration is the correct Entra application client ID.
2. If users are outside the home tenant, configure the app registration for the intended supported account types.
3. Have an admin in the target tenant consent to the app if cross-tenant use is supported.
4. Reinstall the Teams app after correcting the identifiers.

### TB-030: AADSTS7000215 invalid or expired client secret
**Symptoms:** OBO or token acquisition fails with AADSTS7000215, "invalid client secret", or "expired secret."
**Keywords:** AADSTS7000215, invalid client secret, expired secret, client credential
**Cause:** The secret used by the bot or OAuth connection is wrong, expired, or copied as the secret ID instead of the secret value. Secret rotation can also leave one deployment slot with old settings.
**Fix:**
1. Create a new client secret or certificate credential on the Entra app registration.
2. Store the secret value, not the secret ID, in the bot configuration or Key Vault.
3. Update every deployment slot, worker, and OAuth connection that uses the credential.
4. Restart the bot and remove the expired credential after validation.

### TB-031: Sign-in card keeps looping
**Symptoms:** Users complete sign-in, return to Teams, and immediately get another sign-in card for the same action.
**Keywords:** sign-in loop, OAuthPrompt, repeated login, token not found, magic code
**Cause:** The bot is not retrieving the token from the same OAuth connection and user context that created the sign-in card. Connection name mismatches, blocked cookies, stale cards, and missing token exchange handling can all restart the prompt.
**Fix:**
1. Confirm the OAuth connection name in code exactly matches the Azure Bot connection.
2. Handle token response events, token exchange invokes, and magic-code fallback paths in the dialog.
3. Delete or ignore stale sign-in cards after a successful token is stored.
4. Test in a private browser session to separate code issues from cached client state.

### TB-032: Sign-in in group chat or channel is visible to everyone
**Symptoms:** A sign-in card or auth prompt appears in a channel thread where other members can see it.
**Keywords:** group chat sign-in, channel sign-in, visible to everyone, auth card, privacy
**Cause:** Messages sent to group chat or channel scope are visible to members of that conversation. Authentication prompts can reveal that a user is signing in, even though credentials are not shown in the chat.
**Fix:**
1. Avoid posting user-specific sign-in cards directly into public channel threads.
2. Send an instruction in the shared scope and continue authentication in the user's personal bot chat when possible.
3. Keep shared acknowledgements generic and never include tokens, claims, or sensitive user data.

### TB-033: MFA or Conditional Access returns interaction_required
**Symptoms:** Silent SSO or OBO fails with AADSTS50076, AADSTS50079, or `interaction_required`.
**Keywords:** AADSTS50076, MFA, Conditional Access, interaction_required, claims challenge
**Cause:** Conditional Access or MFA policy requires user interaction that cannot be completed silently inside the current token exchange. The app must surface an interactive sign-in or claims challenge path.
**Fix:**
1. Detect `interaction_required` and MFA-related errors instead of retrying silently.
2. Start an interactive OAuth sign-in flow so the user can satisfy the policy.
3. If an API returns a claims challenge, pass the claims parameter through the auth request where supported.
4. Document tenant policy requirements for admins testing the app.

### TB-034: Guest users cannot complete SSO
**Symptoms:** Guest users see consent errors, app-not-found errors, or tokens from an unexpected tenant.
**Keywords:** guest user, B2B, tenant, SSO, external user
**Cause:** Teams guests operate across home and resource tenants, and not every bot SSO or downstream permission model supports guests the same way as members. Single-tenant registrations and tenant-specific consent assumptions commonly break guest sign-in.
**Fix:**
1. Decide whether guest access is supported for the app scenario and document any limitations.
2. Configure supported account types and consent in the tenant where the Teams app is installed.
3. Validate issuer and tenant claims before using the token for tenant-scoped data.
4. Provide a graceful error message when a guest user cannot be authorized.

### TB-035: Sign out does not clear the cached token
**Symptoms:** After a user signs out, the bot still calls Graph successfully or immediately signs the user back in.
**Keywords:** sign out, token cache, cached token, signout, OAuthPrompt
**Cause:** Signing out of the app UI does not automatically clear Bot Framework token service state or custom server-side token caches. Multiple cache layers may continue returning valid tokens until explicitly cleared or expired.
**Fix:**
1. Call the Bot Framework sign-out method for the exact OAuth connection and user.
2. Clear any app-owned token, OBO, or session cache entries for that user and tenant.
3. Confirm the next protected action requires a new token acquisition.

### TB-036: Token cache returns old scopes or stale claims
**Symptoms:** The app still lacks a newly granted Graph scope, or tokens contain old roles after an admin changed consent.
**Keywords:** stale token, token cache, old scopes, claims, consent update
**Cause:** Access tokens and server-side caches can remain valid until expiration, and refresh tokens may continue to mint tokens based on previous consent state for a short time. App code may also key the cache too broadly across tenants or users.
**Fix:**
1. Key token caches by user, tenant, client ID, and scope set.
2. Force a new token acquisition or clear the affected cache after permission changes.
3. Ask the user to retry after signing out and back in if client-side state is involved.
4. Verify the new token contains the expected `scp` or role claims before calling the API.

### TB-037: Regional or sovereign token endpoints are mismatched
**Symptoms:** Authentication works in commercial tenants but fails in GCC, GCC High, DoD, or another cloud with authority or issuer errors.
**Keywords:** sovereign cloud, authority, login endpoint, issuer, regional endpoint
**Cause:** Bot Framework, Entra ID, Graph, and Teams endpoints differ by cloud. Using commercial authority or Graph endpoints for a sovereign tenant can produce invalid issuer, audience, or connectivity failures.
**Fix:**
1. Identify the tenant cloud and configure the matching Entra authority, Graph endpoint, and Bot Framework channel service.
2. Avoid hard-coded `login.microsoftonline.com` or commercial Graph URLs when the app is intended for sovereign clouds.
3. Validate issuer and audience against the configured cloud metadata.

### TB-038: Authentication popup closes or shows a blank page
**Symptoms:** The Teams auth window opens briefly, closes without completing sign-in, or displays a blank page after redirect.
**Keywords:** auth popup, blank sign-in, redirect, popup closes, validDomains
**Cause:** The auth page, redirect URI, or post-login page may not be allowed by Teams, may be blocked by frame or popup settings, or may fail to notify the Teams client that authentication completed. Missing valid domains and redirect URI mismatches are common.
**Fix:**
1. Add every auth host used in the flow to manifest `validDomains`.
2. Verify the Entra redirect URI exactly matches the platform and path used by the auth flow.
3. Ensure the completion page calls the Teams JavaScript authentication success or failure callback when using a custom auth page.
4. Test the flow in Teams web and desktop because popup behavior can differ.

### TB-039: Magic code appears instead of silent SSO
**Symptoms:** Users see a six-digit code or manual verification prompt even though the app was designed for Teams SSO.
**Keywords:** magic code, verification code, silent SSO, token exchange, OAuthPrompt
**Cause:** The bot fell back to the generic OAuth card flow because silent token exchange did not complete. This usually means token exchange invoke handling, pre-authorization, or the OAuth connection's token exchange URL is incomplete.
**Fix:**
1. Confirm the bot adapter and dialog handle token exchange invoke activities.
2. Validate the manifest resource, pre-authorized Teams clients, and OAuth token exchange URL.
3. Keep magic-code handling as a fallback, but do not treat it as proof that SSO is working.

### TB-040: Consent prompt requests unexpected scopes
**Symptoms:** Users or admins see a consent dialog asking for broader permissions than the feature should need.
**Keywords:** unexpected scopes, consent prompt, permissions, least privilege, admin consent
**Cause:** The app registration may include delegated permissions from old features, or the OAuth request may ask for `.default` and include all configured permissions. Overbroad scopes increase consent friction and security review effort.
**Fix:**
1. Review the app registration API permissions and remove scopes that are no longer required.
2. Request explicit delegated scopes for the current feature instead of broad `.default` where practical.
3. Re-run admin consent after reducing permissions and verify the token contains only expected scopes.

## App package and manifest

### TB-041: Manifest validation fails in Developer Portal
**Symptoms:** Uploading the app package fails with a schema or validation error before the app can be installed.
**Keywords:** manifest validation, Developer Portal, schema error, upload failed
**Cause:** The manifest JSON does not match the declared schema version or contains invalid field names, types, IDs, or URLs. Small packaging errors can produce generic validation failures.
**Fix:**
1. Validate `manifest.json` against the Teams app manifest schema version declared in `$schema` or `manifestVersion`.
2. Check that required fields such as `id`, `version`, `developer`, `name`, `description`, and icons are present.
3. Remove unsupported properties for the selected schema version.
4. Recreate the ZIP with `manifest.json` and icons at the package root.

### TB-042: App icons are rejected
**Symptoms:** The package upload reports invalid icon dimensions, transparency, or file format.
**Keywords:** icon, 192x192, 32x32, outline, color, transparent
**Cause:** Teams requires a color icon and an outline icon with specific dimensions and PNG formatting. The outline icon must be 32x32 with a transparent background, and the color icon must be 192x192.
**Fix:**
1. Export the color icon as a 192x192 PNG.
2. Export the outline icon as a 32x32 PNG with transparency.
3. Reference the exact icon file names in the manifest.
4. Put both icon files at the root of the app package ZIP.

### TB-043: Duplicate app IDs cause install or update conflicts
**Symptoms:** Teams says the app already exists, updates the wrong app, or refuses to install a package with an ID conflict.
**Keywords:** duplicate app ID, manifest id, app already exists, conflict
**Cause:** The manifest `id` is the Teams app ID and must uniquely identify the app in the catalog. Reusing the same ID for unrelated dev, test, and production apps causes catalog and client conflicts.
**Fix:**
1. Use a stable unique manifest `id` for each app identity and environment that should install side by side.
2. Keep the same manifest `id` only when intentionally updating the same Teams app.
3. Remove old sideloaded packages from the client when testing a corrected ID.

### TB-044: App update is ignored because the version was not bumped
**Symptoms:** Engineers upload a new package but Teams keeps showing the old icon, commands, tabs, or permissions.
**Keywords:** version not bumped, app update ignored, cache, package update
**Cause:** Teams uses the manifest version to identify updates. If the package contents change but `version` does not increase, clients and catalogs may continue using the previous package.
**Fix:**
1. Increment the manifest `version` for every package update.
2. Upload the new package to the same distribution location.
3. Reinstall or update the app in the target scope and restart the client if cached metadata remains.

### TB-045: validDomains contains wildcards or protocols
**Symptoms:** Package validation fails with an invalid domain error, or tabs and auth pages are blocked even though the URL looks correct.
**Keywords:** validDomains, wildcard, https, protocol, domain validation
**Cause:** `validDomains` entries must be host names, not full URLs, paths, or broad wildcards. Teams uses this list to decide which domains app content can load.
**Fix:**
1. Replace entries like `https://example.com/path` with `example.com`.
2. Avoid broad wildcard domains; list the specific hosts used by tabs, auth, cards, and task modules.
3. Include `token.botframework.com` when using Bot Framework OAuth.
4. Upload a bumped package version after editing the manifest.

### TB-046: Manifest schema version is not supported
**Symptoms:** The app package validates in one tool but fails in another tenant or older client with "unsupported manifest version."
**Keywords:** manifestVersion, schema version, unsupported, client compatibility
**Cause:** New manifest schema versions can include fields not recognized by all tenants, tools, or clients. Declaring a newer schema than the features require can reduce compatibility.
**Fix:**
1. Use the lowest Teams app manifest version that supports the features in the package.
2. Remove fields that belong only to newer schemas if the app targets older clients or tenant tooling.
3. Validate the package in the same Developer Portal or admin center path used for distribution.

### TB-047: Upload is blocked by custom app policy or sideloading disabled
**Symptoms:** Users cannot upload a custom app, or Teams says the organization does not allow custom apps.
**Keywords:** sideloading disabled, custom app policy, upload blocked, app permission policy
**Cause:** Teams admins can disable custom app uploads or restrict app availability with setup, permission, and custom app policies. This is a tenant policy issue, not a package format problem.
**Fix:**
1. Ask a Teams admin to confirm custom app upload is allowed for the test user.
2. Check Teams app permission and setup policies for the target user or group.
3. Use the organization app catalog or approved app distribution path if sideloading is disabled.

### TB-048: App does not appear after upload to the org catalog
**Symptoms:** The package uploads successfully, but users cannot find the app in Teams search or the app catalog.
**Keywords:** app catalog, propagation, not appearing, Teams store, admin center
**Cause:** Organization catalog publishing and Teams client cache can take time to propagate. App permission policies can also hide an uploaded app from specific users.
**Fix:**
1. Confirm the app is published and allowed in Teams admin center.
2. Check that the target user is covered by a policy that allows the app.
3. Wait for catalog propagation and restart Teams or test in Teams web.
4. Verify the app name and short name used for search.

### TB-049: RSC permissions are not granted until reinstall
**Symptoms:** Graph calls that depend on resource-specific consent still fail after the manifest was updated with new RSC permissions.
**Keywords:** RSC, resource-specific consent, reinstall, permissions, ChannelMessage.Read.Group
**Cause:** Adding RSC permissions to the manifest does not automatically grant them to existing installations. The app must be reinstalled or re-consented in the relevant team or chat.
**Fix:**
1. Add the required RSC permissions to `authorization.permissions.resourceSpecific`.
2. Increment the manifest version and publish the updated package.
3. Reinstall the app or have an owner/admin grant the new resource-specific consent.
4. Retry the Graph call and verify the token includes the expected permission.

### TB-050: Bot ID mismatch between manifest and Azure Bot
**Symptoms:** The app installs but messages never reach the bot, or Teams reports the bot cannot be found.
**Keywords:** botId mismatch, bot id, Azure Bot, Microsoft App ID, manifest bot
**Cause:** The bot ID in the Teams manifest must be the Microsoft App ID of the Azure Bot registration. Confusing the Teams app ID, Entra object ID, and bot app ID is a common package error.
**Fix:**
1. Copy the Microsoft App ID from the Azure Bot resource or its linked app registration.
2. Set the manifest bot `botId` to that exact GUID.
3. Keep the manifest `id` as the Teams app ID; do not replace it with the bot ID unless they are intentionally the same.
4. Repackage, upload, and reinstall the app.

### TB-051: Bot command lists do not show
**Symptoms:** The compose box does not show bot commands, or only commands for another scope appear.
**Keywords:** commandLists, commands not showing, bot menu, scope
**Cause:** Commands are declared per scope in `commandLists`, and Teams only shows the list that matches the current personal, team, or group chat context. Client cache can also keep old command metadata.
**Fix:**
1. Put each command under the correct `scopes` entry in the manifest.
2. Keep command titles and descriptions within Teams manifest limits.
3. Bump the app version and update the installed app.
4. Restart Teams or test in web if cached commands remain.

### TB-052: Tab scope is wrong in the manifest
**Symptoms:** A tab can be added in personal scope but not a channel, or the channel tab configuration page never appears.
**Keywords:** staticTabs, configurableTabs, tab scope, personal tab, channel tab
**Cause:** Personal tabs use `staticTabs`, while channel, group chat, and meeting tabs usually use `configurableTabs` with allowed scopes. Declaring the wrong tab type or scope hides the tab from the intended surface.
**Fix:**
1. Use `staticTabs` for personal tabs and `configurableTabs` for tabs users configure in teams, group chats, or meetings.
2. Set the tab `scopes` to the exact contexts the app supports.
3. Verify the tab content and configuration URLs are on domains listed in `validDomains`.

### TB-053: App package ZIP has the wrong layout
**Symptoms:** Teams rejects the package even though `manifest.json` looks correct when opened locally.
**Keywords:** zip layout, manifest root, package, icons missing
**Cause:** Teams expects `manifest.json` and referenced icon files at the root of the ZIP package. Zipping the parent folder or including generated directory layers prevents Teams from finding the required files.
**Fix:**
1. Open the ZIP and confirm `manifest.json`, the color icon, and the outline icon are at the root.
2. Remove extra top-level folders and unrelated build artifacts.
3. Recreate the ZIP from the files themselves, not from their parent directory.

### TB-054: Localization files fail validation
**Symptoms:** The package fails only after adding localization, or localized names and descriptions do not appear.
**Keywords:** localization, languages, manifest validation, locale, strings
**Cause:** Localization files must follow the Teams manifest localization schema and be referenced with valid locale tags. Missing required localized strings or malformed JSON can invalidate the package.
**Fix:**
1. Validate every localization JSON file for syntax and required fields.
2. Use supported locale tags that match the manifest references.
3. Keep localized app names and descriptions within Teams length limits.
4. Upload a bumped package and test with a client language that matches the locale.

### TB-055: Changing the manifest app ID creates a second app
**Symptoms:** Users see two similar apps, or an update installs beside the old app instead of replacing it.
**Keywords:** manifest id changed, duplicate app, update created new app, app identity
**Cause:** The manifest `id` is the Teams app identity. Changing it tells Teams this is a different app, even if the name, bot ID, and icons are the same.
**Fix:**
1. Restore the original manifest `id` when the goal is to update an existing app.
2. Use a new manifest `id` only when intentionally creating a separate app or environment.
3. Remove the accidental duplicate app from the catalog or test tenant after confirming no users depend on it.

## Message extensions and cards

### TB-056: Search message extension returns no results
**Symptoms:** Users type in the message extension search box and see an empty list even though backend data exists.
**Keywords:** message extension search, no results, composeExtension/query, search command
**Cause:** The bot may not handle the `composeExtension/query` invoke correctly, or the manifest command parameters may not match the backend query parser. Search message extensions must return a valid compose extension result with preview cards.
**Fix:**
1. Confirm the manifest command type is `query` and parameter names match the bot handler.
2. Log incoming `composeExtension/query` invokes and the search text received from Teams.
3. Return a valid `composeExtension` result with at least one attachment when matches exist.
4. Add a clear no-results response when the query is valid but no data matches.

### TB-057: Search message extension is too slow
**Symptoms:** The message extension spinner runs for several seconds and then Teams shows a timeout or "Unable to reach app."
**Keywords:** message extension timeout, slow search, composeExtension, query latency
**Cause:** Search message extensions need fast responses, and slow database, Graph, or LLM calls can exceed the Teams invoke deadline. Teams may retry or abandon the request before backend work finishes.
**Fix:**
1. Optimize the query path and avoid long LLM calls in the initial search response.
2. Return the top lightweight results quickly and defer expensive enrichment until selection.
3. Add server-side timeouts and telemetry for dependency latency.
4. Cache common search results when data freshness requirements allow it.

### TB-058: Action command task module does not open
**Symptoms:** Selecting a message extension action command shows an error or no task module appears.
**Keywords:** action command, task/fetch, task module, composeExtension/fetchTask, dialog
**Cause:** Action commands require the bot to handle the task fetch invoke and return a valid task module or adaptive card response. Invalid URLs, missing valid domains, or unsupported response shapes prevent the dialog from opening.
**Fix:**
1. Confirm the manifest action command is declared with the correct fetch task behavior.
2. Handle `composeExtension/fetchTask` or the relevant task module invoke in the bot.
3. Return a valid task module response with an HTTPS URL or Adaptive Card payload.
4. Ensure the task module domain is listed in `validDomains`.

### TB-059: Link unfurling does not trigger
**Symptoms:** Pasting a supported link into Teams does not show the expected preview card from the app.
**Keywords:** link unfurling, messageHandlers, domains, URL preview, paste link
**Cause:** Teams link unfurling only invokes apps whose manifest declares a matching message handler domain pattern and whose app is installed in the conversation scope. Unsupported wildcard patterns or missing installation prevent the invoke.
**Fix:**
1. Add the target host to the manifest message handler domains without protocol or path mistakes.
2. Install the app in the chat or team where the link is pasted.
3. Handle the link query invoke and return a valid card response.
4. Test with a full HTTPS URL on the declared domain.

### TB-060: Adaptive Card version is unsupported
**Symptoms:** A card renders blank, drops elements, or Teams says the card could not be displayed.
**Keywords:** Adaptive Card version, unsupported, card blank, Teams supports 1.5
**Cause:** Teams clients support Adaptive Cards up to version 1.5 on most current surfaces, but not every element or feature is supported everywhere. A card authored for a newer schema can be partially ignored or rejected.
**Fix:**
1. Set the card `version` to 1.5 or lower unless the target Teams surface explicitly supports more.
2. Remove elements and actions not supported by Teams clients in the target surface.
3. Validate the card in Teams, not only in the generic Adaptive Card designer.
4. Provide fallback text for critical content.

### TB-061: Action.Execute or Universal Actions refresh is not invoked
**Symptoms:** A Universal Actions card does not refresh, or `Action.Execute` buttons do nothing for some users.
**Keywords:** Action.Execute, Universal Actions, refresh, adaptiveCard/action, userIds
**Cause:** Universal Actions require the bot to handle `adaptiveCard/action` invokes and return the correct adaptive card invoke response. Refresh behavior can also be limited by the card's `refresh` user IDs and client support.
**Fix:**
1. Implement handling for `adaptiveCard/action` invokes and parse the action verb and data.
2. Return the expected invoke response status and updated card payload.
3. Include the correct users in the card `refresh.userIds` list when targeted refresh is needed.
4. Test fallback behavior for clients that do not support the full Universal Actions experience.

### TB-062: Adaptive Card is too large
**Symptoms:** Sending a card fails, or Teams displays an error after a card with many facts, images, or choices is posted.
**Keywords:** card too large, 28 KB, Adaptive Card size, payload limit
**Cause:** Teams enforces payload size limits for cards, and Adaptive Card content should stay within the documented card size limit, commonly treated as about 28 KB for bot cards. Large choice sets, embedded data, and repeated text can exceed the limit.
**Fix:**
1. Measure the serialized card JSON size before sending.
2. Remove hidden data, long descriptions, and unnecessary repeated fields.
3. Use paging, task modules, or links to show large details outside the card.
4. Send a smaller summary card and fetch details on demand.

### TB-063: Card images do not load
**Symptoms:** Cards show broken image placeholders or images appear in one client but not another.
**Keywords:** card image, broken image, HTTPS, image size, CDN
**Cause:** Teams clients must be able to fetch card images over HTTPS from a reachable domain. Private network URLs, expired SAS links, redirects requiring auth, or very large images can fail.
**Fix:**
1. Host images on HTTPS endpoints reachable by Teams clients without interactive authentication.
2. Keep image file sizes and dimensions reasonable for chat rendering.
3. Add the image host to `validDomains` when it is part of app content.
4. Avoid short-lived URLs that expire before users open the message.

### TB-064: Invoke response misses the Teams deadline
**Symptoms:** Users see "Something went wrong" after a message extension action, card submit, or task module submit even though logs show later success.
**Keywords:** invoke response, 5 seconds, 15 seconds, timeout, Teams deadline
**Cause:** Some Teams invoke flows require a very fast response, and all invoke handlers must complete within the client timeout window. Slow downstream calls make Teams report failure even when the server eventually finishes.
**Fix:**
1. Return the required invoke response immediately for operations that can be acknowledged.
2. Move long-running work to a queue and update the card or send a follow-up message later.
3. Put strict timeouts around Graph, database, and LLM calls in invoke handlers.
4. Record latency by invoke name to find the flows breaching the deadline.

### TB-065: Teams shows Unable to reach app for a message extension
**Symptoms:** Opening or using the message extension shows "Unable to reach app" with no useful client-side detail.
**Keywords:** Unable to reach app, message extension, 500, 502, endpoint
**Cause:** Teams could not get a valid response from the bot messaging endpoint for the extension invoke. Common causes are endpoint outages, invalid auth credentials, unhandled invoke names, and malformed response JSON.
**Fix:**
1. Check server logs for the exact invoke name and HTTP status returned to Teams.
2. Verify the Azure Bot messaging endpoint is healthy and using valid bot credentials.
3. Add explicit handlers for every message extension invoke declared in the manifest.
4. Return a valid Teams invoke response instead of a generic error page or empty body.

### TB-066: Message extension result has an invalid shape
**Symptoms:** Backend logs show results were found, but Teams displays an error or no cards in the message extension result list.
**Keywords:** composeExtension result, invalid response, attachments, preview card, resultType
**Cause:** Message extension responses require a specific `composeExtension` object with result type, attachment layout, attachments, and preview cards. Returning normal bot messages or incomplete attachment metadata is rejected by Teams.
**Fix:**
1. Return a `composeExtension` response with the correct `type`, `attachmentLayout`, and `attachments`.
2. Include preview cards for result list rendering.
3. Keep attachment content types and card payloads valid for Teams.
4. Validate the serialized response against a known working sample.

### TB-067: Task module submit payload is not received
**Symptoms:** Users submit a dialog, but the bot receives no data or handles it as an unknown invoke.
**Keywords:** task module submit, task/submit, dialog submit, missing payload
**Cause:** Task module submissions arrive as invoke activities with task module-specific names and payloads, not as normal chat messages. If the submit button or card action omits data, the backend has nothing to process.
**Fix:**
1. Handle the task submit invoke name used by the Teams surface.
2. Include required form fields in the Adaptive Card action data or inputs.
3. Validate submitted data server-side and return a task module response or message as appropriate.
4. Log sanitized payload shapes to catch client-specific differences.

### TB-068: Card refresh shows stale data for other users
**Symptoms:** One user sees an updated card, while other users in the same chat still see old status or disabled buttons.
**Keywords:** card refresh, stale card, Universal Actions, per-user view, update card
**Cause:** Updating a card for one action does not automatically refresh every user's rendered card in all clients. Universal Actions refresh and bot message updates have different semantics and client support.
**Fix:**
1. Use Universal Actions refresh when each user needs a personalized card view.
2. Use bot message update when the shared card state should change for everyone.
3. Include server-side state checks when processing actions so stale buttons cannot perform invalid operations.
4. Provide a manual refresh action if client support is inconsistent.

### TB-069: Mentions or Markdown inside cards do not render
**Symptoms:** A card shows literal `<at>User</at>` text or Markdown symbols instead of formatted content.
**Keywords:** card mention, markdown in card, literal at tag, Adaptive Card TextBlock
**Cause:** Adaptive Card text rendering is not the same as normal Teams message Markdown. User mentions in cards require Teams-specific mention entities in the card metadata, and not all Markdown is supported in every card element.
**Fix:**
1. Use supported Markdown only in card text fields that Teams renders as Markdown.
2. Add Teams mention entities when mentioning users in Adaptive Cards.
3. Test the card in Teams clients rather than relying only on the Adaptive Card designer.

### TB-070: Card action works on desktop but fails on mobile
**Symptoms:** A button or input flow works in Teams desktop but does nothing or renders differently on mobile.
**Keywords:** mobile, Adaptive Card action, client difference, unsupported element
**Cause:** Teams mobile clients support a different set of card features, input layouts and task module behaviors than desktop. Cards with dense layouts or unsupported elements may degrade differently on mobile.
**Fix:**
1. Check whether the card elements and actions are supported on Teams mobile for the target surface.
2. Simplify the card layout and avoid desktop-only assumptions.
3. Provide a link or task module fallback for flows that are hard to complete on mobile.
4. Include mobile Teams in release validation for action-heavy cards.

## Tabs and meeting apps

### TB-071: Tab is blank or refused to connect
**Symptoms:** Teams shows a blank tab, "refused to connect", or the browser console reports frame blocking.
**Keywords:** tab blank, refused to connect, X-Frame-Options, CSP, frame-ancestors
**Cause:** Teams loads tab content in a frame, so the site must allow framing by Teams. `X-Frame-Options: DENY` or restrictive Content Security Policy `frame-ancestors` directives block the tab.
**Fix:**
1. Remove `X-Frame-Options` values that block Teams framing for the tab route.
2. Configure CSP `frame-ancestors` to allow the appropriate Teams hosts.
3. Ensure the tab URL is HTTPS and listed in manifest `validDomains`.
4. Test in Teams desktop and web after clearing cached headers.

### TB-072: TeamsJS initialization is not called
**Symptoms:** The tab loads but Teams context, theme, or authentication APIs fail with initialization errors.
**Keywords:** TeamsJS, app.initialize, context undefined, SDK initialization
**Cause:** Teams JavaScript SDK APIs require the app to call and await initialization before using context, authentication, or meeting APIs. Calling SDK APIs too early creates intermittent failures.
**Fix:**
1. Call `app.initialize()` from the Teams JavaScript SDK as the tab starts.
2. Await initialization before calling `app.getContext()`, authentication, or meeting APIs.
3. Handle initialization errors and show a useful in-Teams error page.

### TB-073: Deep link opens the wrong scope or nothing
**Symptoms:** A Teams deep link opens the app home page, the wrong chat, or an error page instead of the intended tab or entity.
**Keywords:** deep link, entityId, subEntityId, context, wrong scope
**Cause:** Teams deep links are sensitive to app ID, page ID, entity ID, context, and URL encoding. Links built for one scope do not automatically work in personal, channel, and meeting contexts.
**Fix:**
1. Generate deep links with the correct Teams app ID and tab entity or page ID.
2. URL-encode context and nested URLs exactly once.
3. Include scope-specific identifiers such as channel, chat, meeting, `entityId`, or `subEntityId` where required.
4. Test links from Teams desktop, web, and mobile.

### TB-074: Tab SSO getAuthToken fails
**Symptoms:** `getAuthToken` rejects, returns an empty token, or reports that the resource is not configured.
**Keywords:** getAuthToken, tab SSO, webApplicationInfo, resource, TeamsJS authentication
**Cause:** Tab SSO requires the Teams manifest `webApplicationInfo`, exposed API scope, pre-authorized Teams clients, and valid domain configuration to align. The tab must also run inside Teams after SDK initialization.
**Fix:**
1. Verify `webApplicationInfo.id` and `resource` match the Entra app registration exposed API.
2. Pre-authorize the Teams client IDs for the tab API scope.
3. Ensure the tab domain is listed in `validDomains` and the TeamsJS SDK is initialized before calling `getAuthToken`.
4. Fall back to interactive authentication when policy or consent requires it.

### TB-075: Tab context is undefined or incomplete
**Symptoms:** The tab cannot find the team, channel, chat, or user context it expects.
**Keywords:** tab context, getContext, teamId, channelId, chatId, undefined
**Cause:** Teams context fields vary by scope and client. A personal tab will not have channel context, and meeting or chat tabs expose different IDs from team channel tabs.
**Fix:**
1. Check the tab scope before reading scope-specific context fields.
2. Use `app.getContext()` after TeamsJS initialization and handle missing optional fields.
3. Store app-specific entity state during tab configuration instead of relying only on runtime context.

### TB-076: Meeting side panel does not appear
**Symptoms:** The app is installed for a meeting, but users cannot open the side panel during the meeting.
**Keywords:** meeting side panel, meeting app, sidePanel, meeting scope, app not showing
**Cause:** Meeting apps require the correct manifest scopes, meeting extension context, and installation in the meeting. Apps installed only in personal or team scope do not automatically appear in the meeting side panel.
**Fix:**
1. Add the required meeting or group chat scopes and meeting-capable tab configuration to the manifest.
2. Install or add the app to the specific meeting before or during the meeting.
3. Use TeamsJS meeting APIs only after confirming the app is running in meeting context.
4. Check tenant app policies if the meeting app is hidden for some users.

### TB-077: Stage view cannot be shared
**Symptoms:** A user clicks share-to-stage and nothing appears on the meeting stage, or Teams reports the content cannot be shared.
**Keywords:** stage view, share to stage, meeting stage, meeting tab
**Cause:** Stage sharing works only from supported meeting app contexts and requires valid deep links or content URLs that Teams can frame. The meeting organizer or tenant policy may also restrict stage interactions.
**Fix:**
1. Confirm the app is running in a meeting context that supports stage sharing.
2. Build the stage content URL from a Teams-valid HTTPS tab URL on an allowed domain.
3. Handle the TeamsJS share-to-stage result and show errors when policy blocks sharing.
4. Test with organizer and attendee roles.

### TB-078: Tab renders poorly on mobile
**Symptoms:** The tab is clipped, hard to use, or missing controls in Teams mobile.
**Keywords:** mobile tab, responsive layout, viewport, touch, Teams mobile
**Cause:** Teams mobile uses constrained webview dimensions and touch input, and desktop-oriented layouts can overflow or hide critical controls. Some tab capabilities also differ on mobile.
**Fix:**
1. Use responsive layouts and test with narrow viewport widths.
2. Avoid fixed pixel heights that assume desktop Teams.
3. Make primary actions touch-friendly and visible without horizontal scrolling.
4. Provide a simplified mobile path if a desktop feature is unsupported.

### TB-079: Configurable tab save button stays disabled
**Symptoms:** During tab setup, the Save button never enables or saving the configuration does nothing.
**Keywords:** configurable tab, save button, settings.setValidityState, configuration
**Cause:** Configurable tabs must notify Teams when the configuration is valid and provide settings before save. If the page never calls the TeamsJS pages configuration APIs, Teams keeps the Save button disabled.
**Fix:**
1. Initialize TeamsJS on the configuration page.
2. Call the pages configuration validity API when required settings are complete.
3. Register the save handler and provide `contentUrl`, `websiteUrl`, and `entityId` values.
4. Keep all configured URLs on domains listed in `validDomains`.

### TB-080: Meeting APIs fail outside a meeting
**Symptoms:** Calls to meeting-specific TeamsJS APIs fail from a personal tab, channel tab, or browser test page.
**Keywords:** meeting API, not in meeting, TeamsJS, meeting context
**Cause:** Meeting APIs are available only when the app runs inside a Teams meeting context that supports them. Local browser tests and non-meeting tabs do not expose meeting context.
**Fix:**
1. Check the Teams context before calling meeting-specific APIs.
2. Hide or disable meeting-only features outside meeting scope.
3. Test meeting features from an actual Teams meeting with the app installed.

## Graph and permissions

### TB-081: Graph returns 403 when reading channel messages
**Symptoms:** Graph calls to read channel messages fail with 403 even though the user can see the channel in Teams.
**Keywords:** 403, channel messages, ChannelMessage.Read.All, Graph permissions, admin consent
**Cause:** Reading channel messages with Microsoft Graph requires the correct delegated or application permissions and consent. Application permissions for Teams messages can be restricted and may require admin consent or protected API approval depending on the API and scenario.
**Fix:**
1. Verify whether the code is using delegated, application, or RSC permissions for the channel message API.
2. Add the least-privileged permission that matches the scenario, such as `ChannelMessage.Read.All` or an applicable RSC permission.
3. Grant admin or resource-specific consent as required.
4. Confirm the access token contains the expected `scp` or `roles` claim before retrying.

### TB-082: Protected Teams message APIs are not approved
**Symptoms:** Application-permission Graph calls for chats or channel messages fail despite admin consent.
**Keywords:** protected API, Teams messages, application permission, approval, 403
**Cause:** Some Microsoft Graph APIs for Teams messages are protected when used with application permissions. Admin consent alone may not be sufficient unless the tenant and app are approved for the protected API scenario.
**Fix:**
1. Confirm the API and permission are supported for the intended production scenario.
2. Check whether Microsoft requires protected API approval for that application permission.
3. Use delegated permissions, RSC, or bot interactions instead if they fit the product requirement.
4. Handle 403 responses with a clear admin-facing configuration message.

### TB-083: Chat messages query fails with filter or orderby
**Symptoms:** Graph returns "unsupported query", "invalid filter", or inconsistent results when filtering chat messages.
**Keywords:** chat messages, $filter, $orderby, unsupported query, Graph
**Cause:** Microsoft Graph Teams message APIs support only specific query parameters and filtering patterns. Assuming arbitrary OData filtering works on chat messages leads to failed requests.
**Fix:**
1. Check the specific Graph API documentation for supported `$filter`, `$top`, `$orderby`, and paging behavior.
2. Use supported time-range or paging patterns instead of unsupported filters.
3. Filter remaining results in application code when the dataset size and compliance requirements allow it.
4. Log the full Graph error code and request ID for troubleshooting.

### TB-084: Graph throttles requests with 429
**Symptoms:** Graph calls fail intermittently with 429, "TooManyRequests", or a `Retry-After` header during sync or fan-out jobs.
**Keywords:** Graph 429, throttling, Retry-After, TooManyRequests, backoff
**Cause:** Microsoft Graph enforces workload-specific throttling, and Teams message APIs can throttle quickly under high concurrency. Retrying immediately makes the outage worse.
**Fix:**
1. Honor the `Retry-After` header before retrying a throttled request.
2. Use exponential backoff with jitter when `Retry-After` is absent.
3. Reduce concurrency and avoid polling endpoints that support change notifications or delta queries.
4. Track throttling by tenant, user, and API route.

### TB-085: Delta query misses changes or returns invalid token
**Symptoms:** A sync job misses Teams or directory changes, or Graph returns an invalid or expired delta token error.
**Keywords:** delta query, delta token, sync, invalid token, missed changes
**Cause:** Delta tokens are opaque state for a specific query shape and can expire or become invalid. Changing query parameters, permissions, or resource scope while reusing an old token can break the sync.
**Fix:**
1. Store delta links exactly as returned by Graph for each tenant and query shape.
2. Do not edit delta URLs or reuse them across different users, teams, or permission sets.
3. When Graph says the token is invalid, restart with a full sync for that resource.
4. Make the sync idempotent so replayed changes do not corrupt state.

### TB-086: Graph IDs are confused with Bot Framework conversation IDs
**Symptoms:** Code passes a Teams chat or channel ID from Graph into Bot Framework send APIs and gets not found or forbidden.
**Keywords:** Graph ID, conversation ID, Bot Framework, chat id, channel id
**Cause:** Microsoft Graph resource IDs and Bot Framework conversation IDs are not interchangeable. Bot proactive messaging requires Bot Framework conversation references captured from bot activities or created through supported install/chat flows.
**Fix:**
1. Store Graph IDs and Bot Framework conversation references in separate fields.
2. Use Graph IDs only with Graph APIs and bot conversation IDs only with Bot Framework connector APIs.
3. Capture a bot conversation reference from an activity in the target scope before sending proactive bot messages.

### TB-087: Team aadGroupId is confused with the team thread ID
**Symptoms:** Graph calls using a team ID fail, or bot code cannot match Teams context to Graph team resources.
**Keywords:** aadGroupId, team id, thread id, groupId, Teams context
**Cause:** Teams exposes multiple identifiers: the Microsoft 365 group object ID, the Teams thread ID, and channel IDs. Graph team APIs usually use the group ID, while bot and Teams context can include thread-style IDs.
**Fix:**
1. Inspect Teams context for both `teamId` and `team.aadGroupId` where available.
2. Use `aadGroupId` for Graph team resource paths that expect the group ID.
3. Keep thread IDs for Teams context, deep links, or bot conversation mapping only.
4. Add telemetry that records which ID type is being used without logging sensitive content.

### TB-088: Other users' profiles cannot be read
**Symptoms:** The signed-in user's profile works, but looking up teammates fails with 403 or returns limited data.
**Keywords:** User.Read.All, profile, other users, 403, directory
**Cause:** `User.Read` is generally enough for the signed-in user's own profile, not arbitrary users in the tenant. Reading other users usually requires broader delegated or application permissions such as `User.Read.All` and admin consent.
**Fix:**
1. Identify exactly which user profile fields are required.
2. Add the least-privileged Graph permission that allows reading those users and fields.
3. Obtain admin consent when required.
4. Handle users who are hidden, guests, or outside the tenant gracefully.

### TB-089: directReports returns non-user objects
**Symptoms:** Code assumes every direct report is a user and crashes when Graph returns another directory object type.
**Keywords:** directReports, directoryObject, non-user, cast, manager
**Cause:** The Graph `directReports` relationship can return a collection of directory objects, not only users. Applications must inspect the object type before reading user-specific properties.
**Fix:**
1. Check the `@odata.type` or typed SDK model for each returned object.
2. Process user objects and skip or separately handle groups, contacts, or other directory object types.
3. Add tests with mixed directory object responses.
4. Avoid assuming optional user properties are always present.

### TB-090: Graph send message is used as a bot replacement
**Symptoms:** Engineers try to send proactive app notifications with Graph chatMessage APIs and hit permission, policy, or unsupported-scenario errors.
**Keywords:** Graph send message, proactive notification, ChatMessage.Send, bot replacement, application permission
**Cause:** Graph chat message send APIs are not a general app-only replacement for Teams bots. Many send scenarios require delegated user context or are intended for specific migration or user-driven flows.
**Fix:**
1. Use Bot Framework proactive messaging for app-as-bot notifications.
2. Use Graph send APIs only for supported delegated or documented scenarios.
3. Avoid using migration-only permissions for live user notifications.
4. Document which identity appears as the sender for each notification path.

## Hosting and operations

### TB-091: Dev tunnel URL changed
**Symptoms:** A bot or message extension worked yesterday, but today Teams cannot reach the local developer service.
**Keywords:** dev tunnel, ngrok, tunnel URL changed, local debug, messaging endpoint
**Cause:** Dev tunnels often create new hostnames unless configured as persistent. Azure Bot messaging endpoints, manifest domains, and auth redirect settings can still point to the old URL.
**Fix:**
1. Start a persistent tunnel or capture the new HTTPS tunnel URL.
2. Update the Azure Bot messaging endpoint to the current tunnel URL.
3. Update manifest `validDomains`, tab URLs, and auth redirect URIs if they depend on the tunnel host.
4. Reinstall the updated app package before testing in Teams.

### TB-092: App Service cold start causes bot timeouts
**Symptoms:** The first user message after idle time fails or takes long enough for Teams to report a timeout.
**Keywords:** cold start, App Service, timeout, first request, idle
**Cause:** App Service instances can unload or scale from zero depending on plan and configuration, and cold startup can exceed Teams bot and invoke timeout windows. Heavy dependency initialization makes the first request slower.
**Fix:**
1. Enable Always On for supported App Service plans.
2. Move expensive initialization out of the request path or warm it during startup.
3. Configure health checks and minimum instances for production workloads.
4. Monitor first-request latency after deployments and scale events.

### TB-093: Health checks or Always On are not configured
**Symptoms:** The app restarts unexpectedly, traffic routes to unhealthy instances, or Teams sees intermittent 502 and 503 errors.
**Keywords:** health check, Always On, 502, 503, App Service, readiness
**Cause:** Without a reliable health endpoint and warm instances, the hosting platform may send traffic to an app that is starting, unhealthy, or unloaded. Teams bot traffic is sensitive to these transient failures.
**Fix:**
1. Add a lightweight health endpoint that verifies critical startup dependencies.
2. Configure the hosting platform health check path to that endpoint.
3. Enable Always On or minimum replicas for production bot hosts.
4. Alert on failed health checks and connector-facing 5xx responses.

### TB-094: Managed identity is not assigned or the wrong identity is used
**Symptoms:** Calls to Azure resources fail with 401 or 403 even though code uses managed identity.
**Keywords:** managed identity, user-assigned identity, DefaultAzureCredential, 401, 403
**Cause:** The host may not have a system-assigned or user-assigned managed identity enabled, or `DefaultAzureCredential` may select a different identity than expected. The selected identity also needs roles on the target resource.
**Fix:**
1. Confirm the managed identity is enabled on the hosting resource.
2. If using a user-assigned identity, configure the client ID explicitly.
3. Grant the selected identity the required role on the target resource.
4. Log the authenticated tenant and client ID in non-sensitive diagnostics.

### TB-095: Azure OpenAI returns 401 or 403
**Symptoms:** LLM calls fail with unauthorized or forbidden errors after deployment, even though the endpoint and keyless auth code look correct.
**Keywords:** Azure OpenAI, 401, 403, Cognitive Services OpenAI User, managed identity
**Cause:** Microsoft Entra authentication to Azure OpenAI requires the calling identity to have a data-plane role such as `Cognitive Services OpenAI User` on the Azure OpenAI resource. Network restrictions or using the wrong resource can also produce 403.
**Fix:**
1. Identify the managed identity or app registration used by the bot at runtime.
2. Assign `Cognitive Services OpenAI User` or the appropriate least-privileged role on the Azure OpenAI resource.
3. Verify the endpoint points to the intended Azure OpenAI resource and network access allows the host.
4. Retry after role assignment propagation.

### TB-096: Azure OpenAI is throttled or out of quota
**Symptoms:** LLM requests fail with 429, quota exceeded, rate limit, or tokens-per-minute errors during busy conversations.
**Keywords:** Azure OpenAI 429, quota, rate limit, TPM, RPM
**Cause:** Azure OpenAI enforces deployment-level and resource-level rate limits based on request and token usage. Conversation fan-out, large prompts, and retries can quickly consume quota.
**Fix:**
1. Honor retry headers and use backoff instead of immediate retries.
2. Reduce prompt size, max tokens, and concurrent requests where possible.
3. Add request queuing or per-tenant rate limits for bursty workloads.
4. Request or allocate more quota to the deployment when sustained load requires it.

### TB-097: Azure OpenAI deployment name returns 404
**Symptoms:** Requests to Azure OpenAI fail with 404 or "deployment not found" even though the model exists in the resource.
**Keywords:** Azure OpenAI 404, deployment not found, deployment name, model name
**Cause:** Azure OpenAI API calls use the deployment name, not the underlying model name. Using `gpt-4o`, `gpt-4.1`, or another model name when the deployment has a custom name returns not found.
**Fix:**
1. Check the deployment name in Azure AI Foundry or the Azure portal.
2. Configure the app to use the deployment name exactly, including case and punctuation.
3. Confirm the API version supports the model and feature being called.
4. Deploy the model in the target region if the deployment does not exist.

### TB-098: Key Vault access is denied
**Symptoms:** Startup fails or secret loading returns 403 from Key Vault after moving to managed identity.
**Keywords:** Key Vault, access denied, 403, secret, managed identity, RBAC
**Cause:** The runtime identity does not have permission to read the secret, or Key Vault is using a different authorization model than the deployment assumes. Firewall and private endpoint settings can also block the host.
**Fix:**
1. Confirm whether the vault uses Azure RBAC or access policies.
2. Grant the runtime identity secret get/list permissions or an appropriate Key Vault role.
3. Verify network rules allow the hosting environment to reach the vault.
4. Restart the app after updating secret references or identity settings.

### TB-099: Logs or Application Insights do not show bot failures
**Symptoms:** Teams users report errors, but engineers cannot find correlated request, dependency, or exception logs.
**Keywords:** Application Insights, logs missing, correlation, telemetry, diagnostics
**Cause:** Telemetry may not be configured for all hosts, adaptive sampling may drop important traces, or correlation IDs from Teams, Bot Framework, Graph, and Azure OpenAI may not be logged together.
**Fix:**
1. Enable request, dependency, exception, and trace telemetry for the bot host and workers.
2. Log sanitized activity IDs, conversation IDs, tenant IDs, operation IDs, and external request IDs.
3. Tune sampling so errors and low-volume troubleshooting events are retained.
4. Add dashboards or queries for connector errors, invoke timeouts, and dependency failures.

### TB-100: Clock skew or certificate rotation breaks authentication
**Symptoms:** Token validation suddenly fails with not-yet-valid, expired, invalid signature, or certificate thumbprint errors.
**Keywords:** clock skew, certificate rotation, expired certificate, invalid signature, token validation
**Cause:** Authentication depends on accurate system time and current signing or client certificates. Hosts with skewed clocks, stale metadata caches, or rotated certificates not deployed to all instances can reject otherwise valid tokens.
**Fix:**
1. Verify host time synchronization and allow only small clock skew in token validation.
2. Refresh OpenID metadata and signing keys when validation errors indicate key rollover.
3. Deploy renewed client certificates or secrets to every slot, worker, and configuration store before old credentials expire.
4. Alert on certificate expiration and token validation failure spikes.
