using Microsoft.Extensions.AI;

namespace IncidentAgent.Llm;

public enum LlmProvider
{
    /// <summary>An Azure OpenAI or Azure AI Foundry resource. Signs in with Microsoft Entra ID unless an API key is set.</summary>
    AzureOpenAI,

    /// <summary>OpenAI, or any server with an OpenAI-compatible chat completions API. Needs an API key.</summary>
    OpenAI,
}

/// <summary>The chat model that summarizes and investigates.</summary>
public sealed class LlmOptions
{
    public const string Section = "AzureOpenAI";

    public const string OpenAIEndpoint = "https://api.openai.com/v1/";

    public LlmProvider Provider { get; set; } = LlmProvider.AzureOpenAI;

    /// <summary>
    /// For Azure OpenAI, the resource endpoint, e.g. <c>https://my-resource.openai.azure.com/</c>; the v1 API path is added
    /// for you. For OpenAI, the API base URL; leave empty for <c>https://api.openai.com/v1/</c>.
    /// </summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>The Azure model deployment name, or the OpenAI model name, e.g. <c>gpt-4.1-mini</c>.</summary>
    public string Deployment { get; set; } = string.Empty;

    /// <summary>Required for OpenAI. For Azure OpenAI, leave empty to use Microsoft Entra ID (managed identity in Azure, your <c>az login</c> locally).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Client ID of a user-assigned managed identity, if the host uses one.</summary>
    public string? ManagedIdentityClientId { get; set; }

    /// <summary>The Entra ID scope for Azure OpenAI tokens.</summary>
    public string TokenScope { get; set; } = "https://cognitiveservices.azure.com/.default";

    /// <summary>Leave unset for reasoning models, which only accept the default.</summary>
    public float? Temperature { get; set; }

    public int MaxOutputTokens { get; set; } = 2000;

    /// <summary>The base URL the OpenAI client calls.</summary>
    public Uri ApiBase() => Provider == LlmProvider.OpenAI
        ? new Uri(string.IsNullOrWhiteSpace(Endpoint) ? OpenAIEndpoint : Endpoint.TrimEnd('/') + "/")
        : new Uri(Endpoint.TrimEnd('/') + "/openai/v1/");

    /// <summary>Why the settings can't work, or null when they can.</summary>
    public string? Problem() => Provider switch
    {
        LlmProvider.AzureOpenAI when !IsAbsolute(Endpoint) => "Set AzureOpenAI:Endpoint to your Azure OpenAI endpoint. See docs/SETUP.md.",
        LlmProvider.OpenAI when !string.IsNullOrWhiteSpace(Endpoint) && !IsAbsolute(Endpoint) => "AzureOpenAI:Endpoint must be an absolute URL, or empty for api.openai.com.",
        LlmProvider.OpenAI when string.IsNullOrWhiteSpace(ApiKey) => "Set AzureOpenAI:ApiKey when AzureOpenAI:Provider is OpenAI. See docs/SETUP.md.",
        _ when Deployment.Length == 0 => "Set AzureOpenAI:Deployment to your model deployment (or OpenAI model) name. See docs/SETUP.md.",
        _ => null,
    };

    public ChatOptions ChatOptions() => new()
    {
        Temperature = Temperature,
        MaxOutputTokens = MaxOutputTokens,
    };

    private static bool IsAbsolute(string value) => Uri.TryCreate(value, UriKind.Absolute, out _);
}
