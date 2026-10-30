using IncidentAgent.Agent;
using IncidentAgent.Llm;

namespace IncidentAgent.Tests;

public class ConfigurationTests
{
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
