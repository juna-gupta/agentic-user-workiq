using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentAgent.WorkIq;

public interface IWorkIqFactory
{
    /// <summary>Opens a Work IQ session for one user. Dispose it at the end of the turn.</summary>
    WorkIqClient Create(Func<CancellationToken, Task<string>> accessToken);
}

public sealed class WorkIqClientFactory(IHttpClientFactory httpClients, IOptions<WorkIqOptions> options, ILoggerFactory loggers) : IWorkIqFactory
{
    public const string HttpClientName = "workiq";

    public WorkIqClient Create(Func<CancellationToken, Task<string>> accessToken) =>
        new(httpClients.CreateClient(HttpClientName), options.Value, accessToken, loggers.CreateLogger<WorkIqClient>());
}
