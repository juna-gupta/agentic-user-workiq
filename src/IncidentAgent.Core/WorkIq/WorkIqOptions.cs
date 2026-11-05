namespace IncidentAgent.WorkIq;

public sealed class WorkIqOptions
{
    public const string Section = "WorkIq";

    /// <summary>Work IQ's MCP endpoint (Streamable HTTP).</summary>
    public Uri Endpoint { get; set; } = new("https://workiq.svc.cloud.microsoft/mcp");

    /// <summary>
    /// The scope requested for the agent user. The agent identity's delegated grant on the Work IQ resource
    /// (<c>fdcc1f02-fc51-4226-8753-f668596af7f7</c>, <c>WorkIQAgent.Ask</c>) decides what the token carries.
    /// </summary>
    public string Scope { get; set; } = "api://workiq.svc.cloud.microsoft/.default";

    public int TimeoutSeconds { get; set; } = 45;

    /// <summary>Budget for each file-content/status HTTP request, including streaming its body.</summary>
    public int FileTransferTimeoutSeconds { get; set; } = 240;
}
