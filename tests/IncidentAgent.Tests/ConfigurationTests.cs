using IncidentAgent.Agent;
using IncidentAgent.Hosting;
using IncidentAgent.Llm;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace IncidentAgent.Tests;

public class ConfigurationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Rejects_nonpositive_file_transfer_timeouts(int timeout)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkIq:FileTransferTimeoutSeconds"] = timeout.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }).Build();
        using var services = new ServiceCollection().AddLogging().AddIncidentAgent(configuration).BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptions<WorkIqOptions>>().Value);

        Assert.Contains("WorkIq:FileTransferTimeoutSeconds", error.Message);
    }

    [Fact]
    public async Task File_transfer_factory_uses_isolated_transport_without_redirects_cookies_or_uri_logging()
    {
        using var services = new ServiceCollection().AddLogging()
            .AddIncidentAgent(new ConfigurationBuilder().Build()).BuildServiceProvider();
        var clients = services.GetRequiredService<IHttpClientFactory>();
        using var http = clients.CreateClient(WorkIqClientFactory.FileTransferHttpClientName);
        Assert.Equal(Timeout.InfiniteTimeSpan, http.Timeout);
        var handler = services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(WorkIqClientFactory.FileTransferHttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            Assert.DoesNotContain("Logging", handler.GetType().Name);
            handler = delegating.InnerHandler!;
        }

        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseCookies);
        await using var client = services.GetRequiredService<IWorkIqFactory>().Create(_ => throw new InvalidOperationException("No token needed yet."));
    }

    [Theory]
    [InlineData("https://my-resource.openai.azure.com/", "https://my-resource.openai.azure.com/openai/v1/")]
    [InlineData("https://my-resource.openai.azure.com", "https://my-resource.openai.azure.com/openai/v1/")]
    public void Azure_openai_adds_the_v1_path(string endpoint, string expected)
    {
        var options = new LlmOptions { Endpoint = endpoint, Deployment = "gpt" };

        Assert.Equal(LlmProvider.AzureOpenAI, options.Provider);
        Assert.Equal(new Uri(expected), options.ApiBase());
        Assert.Null(options.Problem());
    }

    [Theory]
    [InlineData("", "https://api.openai.com/v1/")]
    [InlineData("http://localhost:11434/v1", "http://localhost:11434/v1/")]
    public void Openai_uses_the_endpoint_as_given(string endpoint, string expected)
    {
        var options = new LlmOptions { Provider = LlmProvider.OpenAI, Endpoint = endpoint, Deployment = "gpt", ApiKey = "key" };

        Assert.Equal(new Uri(expected), options.ApiBase());
        Assert.Null(options.Problem());
    }

    [Fact]
    public void Explains_what_is_missing()
    {
        Assert.Contains("AzureOpenAI:Endpoint", new LlmOptions { Deployment = "gpt" }.Problem());
        Assert.Contains("AzureOpenAI:Deployment", new LlmOptions { Endpoint = "https://r.openai.azure.com/" }.Problem());
        Assert.Contains("AzureOpenAI:ApiKey", new LlmOptions { Provider = LlmProvider.OpenAI, Deployment = "gpt" }.Problem());
        Assert.Contains("absolute URL", new LlmOptions { Provider = LlmProvider.OpenAI, Endpoint = "api/v1", Deployment = "gpt", ApiKey = "key" }.Problem());
    }

    [Fact]
    public void Prompts_use_the_configured_name_and_role()
    {
        Assert.StartsWith("You are Iris, the helpdesk engineering team's incident agent.", Prompts.IntentSystem("Iris"));
        Assert.StartsWith("You are Iris, the helpdesk engineering team's incident agent.", Prompts.IntentSystem("Iris", new AgentOptions().Role));
        Assert.StartsWith("You are Sam, the payments team's on-call assistant.", Prompts.SummarySystem("Sam", "the payments team's on-call assistant"));
        Assert.StartsWith("You are Sam, the payments team's on-call assistant.", Prompts.AnswerSystem("Sam", "runbook.md", " the payments team's on-call assistant "));
    }
}
