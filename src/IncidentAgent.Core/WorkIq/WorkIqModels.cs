using System.Net;
using System.Text.Json;

namespace IncidentAgent.WorkIq;

/// <summary>One result from Work IQ <c>fetch</c>; results come back in the order the paths were sent.</summary>
public sealed record FetchResult(string Path, int StatusCode, JsonElement? Data, string? Error)
{
    public bool IsSuccess => StatusCode is >= 200 and < 300 && Data is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined };

    /// <summary>The <c>value</c> array of a collection response, or empty.</summary>
    public IEnumerable<JsonElement> Items =>
        IsSuccess && Data!.Value.ValueKind == JsonValueKind.Object && Data.Value.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray()
            : [];

    /// <summary>The next page as a relative Work IQ path, if there is one.</summary>
    public string? NextPath =>
        IsSuccess && Data!.Value.ValueKind == JsonValueKind.Object && Data.Value.TryGetProperty("@odata.nextLink", out var next) && next.ValueKind == JsonValueKind.String
            ? WorkIqPaths.FromNextLink(next.GetString()!)
            : null;

    /// <summary>Throws a <see cref="WorkIqException"/> when the fetch didn't succeed.</summary>
    public FetchResult EnsureSuccess()
    {
        if (IsSuccess)
        {
            return this;
        }

        throw new WorkIqException(WorkIqException.KindFor((HttpStatusCode)StatusCode), $"Work IQ couldn't read {Shorten(Path)} ({StatusCode}): {Error ?? "no data"}", StatusCode);
    }

    private static string Shorten(string path)
    {
        var q = path.IndexOf('?');
        return q < 0 ? path : path[..q];
    }
}

/// <summary>A file read with <c>fetch_blob</c> (Work IQ returns up to 4 MB, base64 encoded).</summary>
public sealed record BlobResult(string Name, string ContentType, byte[] Content);

/// <summary>A path Work IQ exposes and the operations it allows there (<c>fetch</c>, <c>create</c>, <c>update</c>, <c>delete</c>, …).</summary>
public sealed record PathCapability(string Path, IReadOnlyList<string> Operations);

public enum WorkIqFailure
{
    /// <summary>The token was rejected, or the agent user hasn't been granted Work IQ.</summary>
    Unauthorized,

    /// <summary>The agent user can't use Work IQ or can't see this item (licence, policy, or permission).</summary>
    Forbidden,

    NotFound,
    Throttled,
    BadRequest,
    Unavailable,
}

public class WorkIqException(WorkIqFailure failure, string message, int? statusCode = null, Exception? inner = null)
    : Exception(message, inner)
{
    public WorkIqFailure Failure { get; } = failure;

    public int? StatusCode { get; } = statusCode;

    public static WorkIqFailure KindFor(HttpStatusCode? status) => status switch
    {
        HttpStatusCode.Unauthorized => WorkIqFailure.Unauthorized,
        HttpStatusCode.Forbidden => WorkIqFailure.Forbidden,
        HttpStatusCode.NotFound or HttpStatusCode.Gone => WorkIqFailure.NotFound,
        HttpStatusCode.TooManyRequests => WorkIqFailure.Throttled,
        HttpStatusCode.BadRequest => WorkIqFailure.BadRequest,
        _ => WorkIqFailure.Unavailable,
    };
}

/// <summary>Work IQ as the agent user, for the length of one piece of work.</summary>
public interface IWorkIq
{
    /// <summary>Reads Work IQ paths (one round trip for all of them).</summary>
    Task<IReadOnlyList<FetchResult>> FetchAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken);

    /// <summary>Downloads a file's bytes, e.g. <c>/drives/{id}/items/{id}/content</c>.</summary>
    Task<BlobResult> FetchBlobAsync(string path, CancellationToken cancellationToken);

    /// <summary>Streams a prepared Work IQ download to an absolute local path, without returning file bytes.</summary>
    Task<FileTransferResult> DownloadFileAsync(DownloadFileRequest request, CancellationToken cancellationToken);

    /// <summary>Streams a local file through a prepared Work IQ upload session, without MCP content payloads.</summary>
    Task<FileTransferResult> UploadFileAsync(UploadFileRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the paths matching <paramref name="filter"/> and what Work IQ allows on each.</summary>
    Task<IReadOnlyList<PathCapability>> SearchPathsAsync(string filter, CancellationToken cancellationToken);
}

public static class WorkIqExtensions
{
    public static async Task<FetchResult> FetchAsync(this IWorkIq workIq, string path, CancellationToken cancellationToken) =>
        (await workIq.FetchAsync([path], cancellationToken))[0];
}
