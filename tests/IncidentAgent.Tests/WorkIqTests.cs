using IncidentAgent.Text;
using IncidentAgent.WorkIq;
using ModelContextProtocol.Protocol;

namespace IncidentAgent.Tests;

public class WorkIqTests
{
    [Fact]
    public void Encodes_sharing_urls_the_way_graph_documents()
    {
        // A known example of the u! sharing-URL encoding.
        Assert.Equal(
            "u!aHR0cHM6Ly9vbmVkcml2ZS5saXZlLmNvbS9yZWRpcj9yZXNpZD0xMjMxMjQ0MTkzOTEyITEyJmF1dGhLZXk9MTIwMTkxOSExMjkyMSEx",
            WorkIqPaths.EncodeSharingUrl("https://onedrive.live.com/redir?resid=1231244193912!12&authKey=1201919!12921!1"));
    }

    [Theory]
    [InlineData("b!abc/../../me")]
    [InlineData("item?x=1")]
    [InlineData("")]
    public void Rejects_ids_that_could_change_the_path(string id)
    {
        Assert.Throws<ArgumentException>(() => WorkIqPaths.DriveContent(id, "item"));
    }

    [Fact]
    public void Builds_onedrive_paths_safely()
    {
        Assert.Equal("/me/drive/root:/Runbooks/helpdesk%20runbook.md:/content", WorkIqPaths.MyDriveFileContent(@"\Runbooks\helpdesk runbook.md"));
        Assert.Throws<ArgumentException>(() => WorkIqPaths.MyDriveFileContent("/Runbooks/../secrets.md"));
    }


    [Fact]
    public void Finds_https_links_once()
    {
        var html = """<a href="https://a.sharepoint.com/x?a=1&amp;b=2">x</a> <a href='http://insecure.example'>y</a> <a href="https://a.sharepoint.com/x?a=1&amp;b=2">again</a>""";

        Assert.Equal(["https://a.sharepoint.com/x?a=1&b=2"], HtmlText.Links(html));
        Assert.Empty(HtmlText.Links(null));
    }

    [Fact]
    public void Reads_graph_error_text_at_any_depth()
    {
        Assert.Equal("Unauthenticated", WorkIqEnvelope.ErrorText(FakeWorkIq.Json("""{"error":{"code":"unauthenticated","message":"Unauthenticated"}}""")));
        Assert.Equal("plain", WorkIqEnvelope.ErrorText(FakeWorkIq.Json("\"plain\"")));
    }

    [Fact]
    public void Classifies_a_tool_error_by_the_graph_status_it_carries()
    {
        var result = new CallToolResult
        {
            IsError = true,
            StructuredContent = FakeWorkIq.Json("""{"statusCode":404,"error":{"error":{"code":"itemNotFound","message":"The resource could not be found."}}}"""),
        };

        var ex = Assert.Throws<WorkIqException>(() => WorkIqEnvelope.FromToolResult("fetch_blob", result));

        Assert.Equal(WorkIqFailure.NotFound, ex.Failure);
        Assert.Equal(404, ex.StatusCode);
        Assert.Contains("The resource could not be found.", ex.Message);
    }

    [Fact]
    public void A_tool_error_without_a_status_is_unavailable()
    {
        var result = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "Something failed." }] };

        var ex = Assert.Throws<WorkIqException>(() => WorkIqEnvelope.FromToolResult("fetch_blob", result));

        Assert.Equal(WorkIqFailure.Unavailable, ex.Failure);
        Assert.Null(ex.StatusCode);
    }
}
