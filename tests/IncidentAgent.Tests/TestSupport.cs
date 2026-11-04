using System.Runtime.CompilerServices;
using System.Text.Json;
using IncidentAgent.Agent;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.AI;

namespace IncidentAgent.Tests;

/// <summary>A scripted Work IQ: fetch and blob answers come from maps; every call is recorded.</summary>
internal sealed class FakeWorkIq : IWorkIq
{
    private readonly Dictionary<string, (int Status, string Json)> _fetch = new(StringComparer.Ordinal);
    private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WorkIqFailure> _blobErrors = new(StringComparer.Ordinal);

    public List<string> Fetched { get; } = [];

    public FakeWorkIq On(string path, string json, int status = 200)
    {
        _fetch[path] = (status, json);
        return this;
    }

    public FakeWorkIq Blob(string path, byte[] content)
    {
        _blobs[path] = content;
        return this;
    }

    /// <summary>Downloading <paramref name="path"/> fails this way. Unknown paths fail as not found.</summary>
    public FakeWorkIq BlobError(string path, WorkIqFailure failure)
    {
        _blobErrors[path] = failure;
        return this;
    }


    public Task<IReadOnlyList<FetchResult>> FetchAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var results = new List<FetchResult>();
        foreach (var path in paths)
        {
            Fetched.Add(path);
            if (!_fetch.TryGetValue(path, out var r))
            {
                throw new InvalidOperationException($"Unexpected fetch: {path}");
            }

            using var doc = JsonDocument.Parse(r.Json);
            results.Add(r.Status is >= 200 and < 300
                ? new FetchResult(path, r.Status, doc.RootElement.Clone(), null)
                : new FetchResult(path, r.Status, null, WorkIqEnvelope.ErrorText(doc.RootElement)));
        }

        return Task.FromResult<IReadOnlyList<FetchResult>>(results);
    }

    public Task<BlobResult> FetchBlobAsync(string path, CancellationToken cancellationToken)
    {
        Fetched.Add(path);
        if (_blobErrors.TryGetValue(path, out var failure))
        {
            throw new WorkIqException(failure, $"Blob failed at {path}");
        }

        return _blobs.TryGetValue(path, out var bytes)
            ? Task.FromResult(new BlobResult("file", "application/octet-stream", bytes))
            : throw new WorkIqException(WorkIqFailure.NotFound, $"No blob at {path}");
    }


    public Task<IReadOnlyList<PathCapability>> SearchPathsAsync(string filter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PathCapability>>([]);

    public Task<FileTransferResult> DownloadFileAsync(DownloadFileRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unexpected file download.");

    public Task<FileTransferResult> UploadFileAsync(UploadFileRequest request, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Unexpected file upload.");

    public static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

/// <summary>A chat client that replays scripted responses and records what it was sent.</summary>
internal sealed class FakeChatClient : IChatClient
{
    private readonly Queue<ChatResponse> _responses = new();

    public List<List<ChatMessage>> Requests { get; } = [];

    public FakeChatClient Reply(string text)
    {
        _responses.Enqueue(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));
        return this;
    }

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToList());
        return _responses.Count == 0
            ? throw new InvalidOperationException("No scripted response left.")
            : Task.FromResult(_responses.Dequeue());
    }

    /// <summary>Replays the next scripted response a few characters at a time.</summary>
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var text = (await GetResponseAsync(messages, options, cancellationToken)).Text;
        for (var i = 0; i < text.Length; i += 8)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, text.Substring(i, Math.Min(8, text.Length - i)));
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

/// <summary>Records what the agent posts, edits and deletes. Posted messages get IDs 1001, 1002 and so on.</summary>
internal sealed class RecordingOutput : IAgentOutput
{
    private int _next = 1000;

    public List<AgentReply> Replies { get; } = [];

    public List<string> Posts => [.. Replies.Select(r => r.Markdown)];

    public List<(string MessageId, AgentReply Reply)> Updates { get; } = [];

    public List<string> Deletes { get; } = [];

    /// <summary>Whether edits succeed; false acts like a message that's gone.</summary>
    public bool CanUpdate { get; set; } = true;

    /// <summary>The stream handed out by <see cref="OpenStream"/>; null acts like a channel without streaming.</summary>
    public RecordingStream? Stream { get; set; }

    public Task<string?> PostAsync(AgentReply reply, CancellationToken cancellationToken)
    {
        Replies.Add(reply);
        return Task.FromResult<string?>((++_next).ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public Task<bool> UpdateAsync(string messageId, AgentReply reply, CancellationToken cancellationToken)
    {
        Updates.Add((messageId, reply));
        return Task.FromResult(CanUpdate);
    }

    public Task<bool> DeleteAsync(string messageId, CancellationToken cancellationToken)
    {
        Deletes.Add(messageId);
        return Task.FromResult(true);
    }

    public IReplyStream? OpenStream() => Stream;
}

internal sealed class RecordingStream : IReplyStream
{
    public List<string> Informed { get; } = [];

    public List<string> Appended { get; } = [];

    public bool Cancelled { get; set; }

    public Task InformAsync(string text, CancellationToken cancellationToken)
    {
        Informed.Add(text);
        return Task.CompletedTask;
    }

    public Task AppendAsync(string text, CancellationToken cancellationToken)
    {
        Appended.Add(text);
        return Task.CompletedTask;
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}


internal static class Graph
{
    public const string Self = "iris-oid";

    public static JsonElement Message(string id, string author, DateTimeOffset created, string html, string? authorId = null, string type = "message", string? mentionId = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["messageType"] = type,
            ["createdDateTime"] = created.ToString("o"),
            ["deletedDateTime"] = null,
            ["from"] = new { user = new { id = authorId ?? "u-" + author, displayName = author } },
            ["body"] = new { contentType = "html", content = html },
            ["attachments"] = Array.Empty<object>(),
            ["mentions"] = mentionId is null
                ? Array.Empty<object>()
                : new object[] { new { id = 0, mentionText = "Iris", mentioned = new { user = new { id = mentionId, displayName = "Iris" } } } },
        };
        return FakeWorkIq.Json(JsonSerializer.Serialize(message));
    }
}

internal static class Fixtures
{
    public static byte[] Read(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}
