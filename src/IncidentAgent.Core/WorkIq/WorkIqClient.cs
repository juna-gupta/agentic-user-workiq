using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace IncidentAgent.WorkIq;

/// <summary>
/// Work IQ over MCP (Streamable HTTP), called with the agent user's token. One MCP session per instance; create one
/// per piece of work with <see cref="WorkIqClientFactory"/>.
/// </summary>
public sealed partial class WorkIqClient(
    HttpClient httpClient,
    WorkIqOptions options,
    Func<CancellationToken, Task<string>> accessToken,
    ILogger logger,
    HttpClient fileTransferHttpClient) : IWorkIq, IAsyncDisposable
{
    private readonly SemaphoreSlim _connect = new(1, 1);
    private McpClient? _client;
    private HttpClientTransport? _transport;

    public async Task<IReadOnlyList<FetchResult>> FetchAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (paths.Count == 0)
        {
            return [];
        }

        var args = new Dictionary<string, object?> { ["entityUrls"] = paths.ToArray() };
        var result = await CallToolAsync("fetch", args, cancellationToken);
        return WorkIqEnvelope.ParseFetch(WorkIqEnvelope.FromToolResult("fetch", result), paths);
    }

    public async Task<BlobResult> FetchBlobAsync(string path, CancellationToken cancellationToken)
    {
        var result = await CallToolAsync("fetch_blob", new Dictionary<string, object?> { ["path"] = path }, cancellationToken);
        return WorkIqEnvelope.ParseBlob(WorkIqEnvelope.FromToolResult("fetch_blob", result), path);
    }

    public async Task<IReadOnlyList<PathCapability>> SearchPathsAsync(string filter, CancellationToken cancellationToken)
    {
        var result = await CallToolAsync("search_paths", new Dictionary<string, object?> { ["filter"] = filter }, cancellationToken);
        return WorkIqEnvelope.ParsePaths(WorkIqEnvelope.FromToolResult("search_paths", result));
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }

        _connect.Dispose();
    }

    private async Task<CallToolResult> CallToolAsync(string tool, Dictionary<string, object?> args, CancellationToken cancellationToken, bool sensitiveResult = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
        try
        {
            var client = await ConnectAsync(timeout.Token);
            return await client.CallToolAsync(tool, args, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkIqException(WorkIqFailure.Unavailable, $"Work IQ {tool} timed out after {options.TimeoutSeconds}s.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(sensitiveResult ? null : ex, "Work IQ {Tool} transport failure ({Status})", tool, ex.StatusCode);
            throw new WorkIqException(WorkIqException.KindFor(ex.StatusCode), $"Work IQ {tool} failed ({(int?)ex.StatusCode ?? 0}).", (int?)ex.StatusCode, sensitiveResult ? null : ex);
        }
        catch (McpException ex)
        {
            logger.LogWarning(sensitiveResult ? null : ex, "Work IQ {Tool} MCP error", tool);
            throw new WorkIqException(WorkIqFailure.Unavailable,
                sensitiveResult ? $"Work IQ {tool} could not prepare the file transfer. Check provider availability and authorization." : $"Work IQ {tool} error: {ex.Message}",
                inner: sensitiveResult ? null : ex);
        }
    }

    private async Task<McpClient> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _connect.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var token = await accessToken(cancellationToken);
            _transport = new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = options.Endpoint,
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + token },
            }, httpClient);
            _client = await McpClient.CreateAsync(_transport, cancellationToken: cancellationToken);
            return _client;
        }
        finally
        {
            _connect.Release();
        }
    }
}

/// <summary>A Work IQ tool payload: the structured JSON envelope when the tool returned one, plus the text blocks in order.</summary>
public sealed record WorkIqPayload(JsonElement? Json, IReadOnlyList<string> TextBlocks)
{
    public string Text => string.Join("\n", TextBlocks).Trim();
}

public static class WorkIqEnvelope
{
    internal static JsonElement TransferData(string tool, CallToolResult result)
    {
        var payload = Payload(result);
        var status = payload.Json is { } json && json.TryGetProperty("statusCode", out var sc)
            && sc.ValueKind == JsonValueKind.Number && sc.TryGetInt32(out var code) ? code : 0;
        if (result.IsError is true || status is < 200 or >= 300)
        {
            // A provider failure can arrive as a 200 envelope marked isError, with the provider's status in a nested text item.
            if (status is 0 or (>= 200 and < 300) && ProviderStatus(payload.Json) is { } provider)
            {
                status = provider;
            }

            int? reported = status is 0 or (>= 200 and < 300) ? null : status;
            throw new WorkIqFileTransferException(reported is { } s ? WorkIqException.KindFor((System.Net.HttpStatusCode)s) : WorkIqFailure.Unavailable,
                $"Work IQ {tool} could not prepare the file transfer ({reported ?? 0}). Check provider availability and authorization.",
                statusCode: reported);
        }

        if (payload.Json is not { } root || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new WorkIqFileTransferException(WorkIqFailure.Unavailable, "Work IQ returned no supported file-transfer data. Check provider bindings and response conversion.");
        }

        return data;
    }

    public static WorkIqPayload FromToolResult(string tool, CallToolResult result)
    {
        var payload = Payload(result);
        var perPath = payload.Json is { } j && j.TryGetProperty("results", out var r) && r.ValueKind == JsonValueKind.Array;

        // fetch returns each path's own status, and marks the whole call as an error when any one path fails.
        if (result.IsError is true && !(perPath && tool == "fetch"))
        {
            var detail = perPath ? FirstError(payload.Json!.Value) : ErrorOf(payload);

            // fetch_blob reports an HTTP status (for example 404 when the file isn't there), so classify by it.
            int? status = !perPath && payload.Json is { } p && p.TryGetProperty("statusCode", out var sc) && sc.TryGetInt32(out var code) && code is < 200 or >= 300 ? code : null;
            var failure = status is { } s ? WorkIqException.KindFor((System.Net.HttpStatusCode)s) : WorkIqFailure.Unavailable;
            var shown = status is { } st ? $" ({st})" : string.Empty;
            throw new WorkIqException(failure, $"Work IQ {tool} returned an error{shown}: {Shorten(detail ?? "no details")}", status);
        }

        return payload;
    }

    public static IReadOnlyList<FetchResult> ParseFetch(WorkIqPayload payload, IReadOnlyList<string> paths)
    {
        if (payload.Json is not { } root || !root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            throw new WorkIqException(WorkIqFailure.Unavailable, $"Work IQ fetch returned an unexpected shape: {Shorten(payload.Text)}");
        }

        var list = new List<FetchResult>(paths.Count);
        var i = 0;
        foreach (var r in results.EnumerateArray())
        {
            var path = i < paths.Count ? paths[i] : $"#{i}";
            var status = r.TryGetProperty("statusCode", out var sc) && sc.TryGetInt32(out var code) ? code : 0;
            JsonElement? data = r.TryGetProperty("data", out var d) && d.ValueKind != JsonValueKind.Null ? d.Clone() : null;
            list.Add(new FetchResult(path, status, data, r.TryGetProperty("error", out var e) ? ErrorText(e) : null));
            i++;
        }

        for (; i < paths.Count; i++)
        {
            list.Add(new FetchResult(paths[i], 0, null, "Work IQ returned no result for this path."));
        }

        return list;
    }

    /// <summary><c>fetch_blob</c> returns <c>{statusCode, contentType, blobName, sizeBytes, base64Content}</c>.</summary>
    public static BlobResult ParseBlob(WorkIqPayload payload, string path)
    {
        if (payload.Json is not { } json)
        {
            throw new WorkIqException(WorkIqFailure.Unavailable, $"Work IQ fetch_blob returned an unexpected shape: {Shorten(payload.Text)}");
        }

        var status = json.TryGetProperty("statusCode", out var sc) && sc.TryGetInt32(out var code) ? code : 200;
        if (status is < 200 or >= 300 || Str(json, "base64Content") is not { } data)
        {
            var error = json.TryGetProperty("error", out var e) ? ErrorText(e) : null;
            throw new WorkIqException(WorkIqException.KindFor((System.Net.HttpStatusCode)status), $"Work IQ couldn't download {path} ({status}): {error ?? "no content"}", status);
        }

        return new BlobResult(Str(json, "blobName") ?? string.Empty, Str(json, "contentType") ?? "application/octet-stream", Convert.FromBase64String(data));
    }

    /// <summary><c>search_paths</c> returns <c>{"paths":[{"path":"…","operations":["fetch",…]}]}</c>.</summary>
    public static IReadOnlyList<PathCapability> ParsePaths(WorkIqPayload payload)
    {
        if (payload.Json is not { } json || !json.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return paths.EnumerateArray()
            .Where(p => Str(p, "path") is not null)
            .Select(p => new PathCapability(
                Str(p, "path")!,
                p.TryGetProperty("operations", out var ops) && ops.ValueKind == JsonValueKind.Array
                    ? ops.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.String).Select(o => o.GetString()!).ToList()
                    : []))
            .ToList();
    }

    /// <summary>Errors arrive nested (<c>{"error":{"message":…}}</c>) or as a plain string.</summary>
    public static string? ErrorText(JsonElement error)
    {
        switch (error.ValueKind)
        {
            case JsonValueKind.String:
                return error.GetString();
            case JsonValueKind.Object:
                if (error.TryGetProperty("error", out var inner))
                {
                    return ErrorText(inner);
                }

                var message = Str(error, "message");
                if (string.IsNullOrWhiteSpace(message) && error.TryGetProperty("innerError", out var ie))
                {
                    return ErrorText(ie);
                }

                return message ?? error.GetRawText();
            case JsonValueKind.Null or JsonValueKind.Undefined:
                return null;
            default:
                return error.GetRawText();
        }
    }

    /// <summary>Pulls the first JSON object out of model output that may be wrapped in a code fence or prose.</summary>
    public static bool TryParseObject(string? text, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static WorkIqPayload Payload(CallToolResult result)
    {
        var blocks = result.Content.OfType<TextContentBlock>().Select(t => t.Text).ToList();
        JsonElement? json = result.StructuredContent is JsonElement s && s.ValueKind == JsonValueKind.Object ? s.Clone() : null;
        for (var i = blocks.Count - 1; json is null && i >= 0; i--)
        {
            var t = blocks[i].Trim();
            if (t.StartsWith('{') && t.EndsWith('}') && TryParseObject(t, out var parsed))
            {
                json = parsed;
            }
        }

        return new WorkIqPayload(json, blocks);
    }

    private static int? ProviderStatus(JsonElement? root)
    {
        if (root is not { } r || !r.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in data.EnumerateArray())
        {
            if (TryParseObject(Str(item, "text"), out var inner)
                && (inner.TryGetProperty("StatusCode", out var sc) || inner.TryGetProperty("statusCode", out sc))
                && sc.ValueKind == JsonValueKind.Number && sc.TryGetInt32(out var code) && code is >= 400 and < 600)
            {
                return code;
            }
        }

        return null;
    }

    /// <summary>Tool errors come back as <c>{"error":"…","requestId":"…"}</c> text.</summary>
    private static string? ErrorOf(WorkIqPayload payload) =>
        payload.Json is { } j && j.TryGetProperty("error", out var e) ? ErrorText(e) : payload.Text.Length > 0 ? payload.Text : null;

    private static string? FirstError(JsonElement root)
    {
        foreach (var r in root.GetProperty("results").EnumerateArray())
        {
            if (r.TryGetProperty("error", out var e) && ErrorText(e) is { } text)
            {
                return text;
            }
        }

        return null;
    }

    private static string? Str(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Shorten(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
