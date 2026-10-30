using System.ClientModel;
using System.ClientModel.Primitives;
using Azure.Core;
using Azure.Identity;
using IncidentAgent.Agent;
using IncidentAgent.Conversations;
using IncidentAgent.Knowledge;
using IncidentAgent.Llm;
using IncidentAgent.Shiproom;
using IncidentAgent.Teams;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace IncidentAgent.Hosting;

public static class ServiceCollectionExtensions
{
    /// <summary>Everything the agent needs apart from the Teams SDK itself.</summary>
    public static IServiceCollection AddIncidentAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<WorkIqOptions>().Bind(configuration.GetSection(WorkIqOptions.Section))
            .Validate(o => o.Scope.Length > 0, "WorkIq:Scope is required.");
        services.AddOptions<LlmOptions>().Bind(configuration.GetSection(LlmOptions.Section)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<LlmOptions>, LlmOptionsValidator>();
        services.AddOptions<AgentOptions>().Bind(configuration.GetSection(AgentOptions.Section));
        services.AddOptions<WatchOptions>().Bind(configuration.GetSection(WatchOptions.Section))
            .Validate(o => o.PollSeconds >= 2 && o.WindowMinutes >= 1, "Watch:PollSeconds must be at least 2 and Watch:WindowMinutes at least 1.");
        services.AddOptions<RunbookOptions>().Bind(configuration.GetSection(RunbookOptions.Section));
        services.AddOptions<ShiproomOptions>().Bind(configuration.GetSection(ShiproomOptions.Section));
        services.AddOptions<ConversationOptions>().Bind(configuration.GetSection(ConversationOptions.Section));
        services.AddOptions<WorkQueueOptions>().Bind(configuration.GetSection(WorkQueueOptions.Section));
        services.AddOptions<TeamsFeatureOptions>().Bind(configuration.GetSection(TeamsFeatureOptions.Section))
            .Validate(
                o => !o.HtmlWidget || (Uri.TryCreate(o.WidgetDomain, UriKind.Absolute, out var domain) && domain.Scheme == Uri.UriSchemeHttps),
                "Features:HtmlWidget needs Features:WidgetDomain set to an https URL, usually the agent's public URL. See docs/SETUP.md.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);

        services.AddHttpClient(WorkIqClientFactory.HttpClientName);
        services.AddSingleton<IWorkIqFactory, WorkIqClientFactory>();
        services.AddSingleton<AgentTokens>();

        services.AddSingleton(CreateChatClient);
        services.AddSingleton<RunbookStore>();
        services.AddSingleton<ConversationReader>();
        services.AddSingleton<CalendarReader>();
        services.AddSingleton<DeckFinder>();
        services.AddSingleton<AgentMemory>();
        services.AddSingleton<IncidentAgentService>();

        services.AddSingleton<WatchRegistry>();
        services.AddSingleton<SeenActivities>();
        services.AddSingleton<TeamsSender>();
        services.AddSingleton<WorkQueue>();
        services.AddHostedService(sp => sp.GetRequiredService<WorkQueue>());
        services.AddHostedService<WatchService>();
        services.AddSingleton<AgentHandlers>();
        return services;
    }

    /// <summary>
    /// Azure OpenAI or OpenAI through the OpenAI v1 API. Azure OpenAI uses Microsoft Entra ID (managed identity in Azure,
    /// your developer sign-in locally) unless an API key is configured; OpenAI always uses the API key.
    /// </summary>
    private static IChatClient CreateChatClient(IServiceProvider services)
    {
        var o = services.GetRequiredService<IOptions<LlmOptions>>().Value;
        var options = new OpenAIClientOptions { Endpoint = o.ApiBase() };

        ChatClient chat;
        if (!string.IsNullOrWhiteSpace(o.ApiKey))
        {
            chat = new ChatClient(o.Deployment, new ApiKeyCredential(o.ApiKey), options);
        }
        else
        {
            TokenCredential credential = string.IsNullOrWhiteSpace(o.ManagedIdentityClientId)
                ? new DefaultAzureCredential()
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(o.ManagedIdentityClientId));
#pragma warning disable OPENAI001 // Token-based auth for a custom endpoint is marked experimental.
            chat = new ChatClient(o.Deployment, new BearerTokenPolicy(credential, o.TokenScope), options);
#pragma warning restore OPENAI001
        }

        return chat.AsIChatClient()
            .AsBuilder()
            .UseLogging(services.GetRequiredService<ILoggerFactory>())
            .Build(services);
    }

    private sealed class LlmOptionsValidator : IValidateOptions<LlmOptions>
    {
        public ValidateOptionsResult Validate(string? name, LlmOptions options) =>
            options.Problem() is { } problem ? ValidateOptionsResult.Fail(problem) : ValidateOptionsResult.Success;
    }
}
