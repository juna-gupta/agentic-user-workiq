using IncidentAgent.WorkIq;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Abstractions;
using Microsoft.Teams.Core.Hosting;
using Microsoft.Teams.Core.Schema;

namespace IncidentAgent.Teams;

/// <summary>
/// Work IQ tokens for the agent user. The agent identity (a child of the blueprint) gets a token as the agent user;
/// no person signs in. What it can reach is decided by the delegated Work IQ grant and by what's shared with the agent user.
/// </summary>
public sealed class AgentTokens(
    IAuthorizationHeaderProvider headers,
    IOptions<WorkIqOptions> workIq,
    IOptionsMonitor<ManagedIdentityOptions>? managedIdentity = null)
{
    private readonly BotTokenProvider _provider = new(headers, "AzureAd", managedIdentity);

    public Func<CancellationToken, Task<string>> For(AgenticIdentity identity) => async cancellationToken =>
        await _provider.GetAgenticUserTokenAsync(identity, workIq.Value.Scope, cancellationToken)
        ?? throw new WorkIqException(WorkIqFailure.Unauthorized, "No Work IQ token was issued for the agent user.");
}
