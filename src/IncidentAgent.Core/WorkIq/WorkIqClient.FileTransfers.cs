using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace IncidentAgent.WorkIq;

public sealed partial class WorkIqClient
{
    private const int FileBufferSize = 64 * 1024;
    private const int MaximumStatusBytes = 64 * 1024;
    private const int MaximumChunkSizeExclusive = 60 * 1024 * 1024;

    public async Task<FileTransferResult> DownloadFileAsync(DownloadFileRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireValue(request.DocumentLibraryId, nameof(request.DocumentLibraryId));
        RequireValue(request.FileId, nameof(request.FileId));
        var destination = AbsoluteFilePath(request.DestinationPath);
        var parent = Path.GetDirectoryName(destination)!;
        if (!Directory.Exists(parent))
        {
            throw new DirectoryNotFoundException("The download's parent directory must already exist.");
        }

        if (Directory.Exists(destination) || (!request.Overwrite && File.Exists(destination)))
        {
            throw new IOException("The download destination already exists.");
        }

        var prepared = await PrepareFileAsync(false, new Dictionary<string, object?>
        {
            ["functionUrl"] = WorkIqPaths.DownloadFileDetails,
            ["jsonBody"] = new { documentLibraryId = request.DocumentLibraryId, fileId = request.FileId },
        }, request.FileId, null, cancellationToken);
        var temporary = Path.Combine(parent, ".workiq-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            await TransferRequestAsync(prepared, HttpMethod.Get, false, null, async (response, ct) =>
            {
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentRange is not null
                    || (response.Content.Headers.ContentLength is { } length && length != prepared.SizeBytes))
                {
                    throw ContractError(prepared.TransferId);
                }

                await using var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    FileBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
                created = true;
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[FileBufferSize];
                long written = 0;
                while (true)
                {
                    int count;
                    try
                    {
                        count = await input.ReadAsync(buffer, ct);
                    }
                    catch (IOException)
                    {
                        throw new WorkIqFileTransferException(WorkIqFailure.Unavailable, "The Work IQ download stream was interrupted.", prepared.TransferId);
                    }

                    if (count == 0)
                    {
                        break;
                    }

                    if (count > prepared.SizeBytes - written)
                    {
                        throw ContractError(prepared.TransferId);
                    }

                    await target.WriteAsync(buffer.AsMemory(0, count), ct);
                    written += count;
                }

                if (written != prepared.SizeBytes)
                {
                    throw new WorkIqFileTransferException(WorkIqFailure.Unavailable, "The Work IQ download was truncated.", prepared.TransferId);
                }

                await target.FlushAsync(ct);
                return true;
            }, cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, request.Overwrite);
            return Receipt(prepared, destination);
        }
        finally
        {
            if (created)
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<FileTransferResult> UploadFileAsync(UploadFileRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        RequireValue(request.DocumentLibraryId, nameof(request.DocumentLibraryId));
        RequireValue(request.ParentFolderId, nameof(request.ParentFolderId));
        var sourcePath = AbsoluteFilePath(request.SourcePath);
        var fileName = request.FileName ?? Path.GetFileName(sourcePath);
        RequireValue(fileName, nameof(request.FileName));
        if (fileName is "." or ".." || fileName.Contains('/') || fileName.Contains('\\'))
        {
            throw new ArgumentException("The remote name must be a file name, not a path.", nameof(request));
        }

        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out _))
        {
            throw new ArgumentException("A valid upload content type is required.", nameof(request));
        }

        var conflict = request.ConflictBehavior switch
        {
            UploadConflictBehavior.Fail => "fail",
            UploadConflictBehavior.Replace => "replace",
            UploadConflictBehavior.Rename => "rename",
            _ => throw new ArgumentOutOfRangeException(nameof(request), "Unsupported upload conflict behavior."),
        };
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            FileBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var total = source.Length;
        if (total == 0)
        {
            throw new ArgumentException("Work IQ upload sessions require a nonempty file.", nameof(request));
        }

        var prepared = await PrepareFileAsync(true, new Dictionary<string, object?>
        {
            ["actionUrl"] = WorkIqPaths.UploadFileDetails,
            ["jsonBody"] = new
            {
                documentLibraryId = request.DocumentLibraryId,
                fileName,
                fileSize = total,
                parentFolderId = request.ParentFolderId,
                mimeType = request.ContentType,
                conflictBehavior = conflict,
            },
        }, fileName, total, cancellationToken);

        long offset = 0;
        while (offset < total)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Length != total)
            {
                throw new IOException("The upload source changed during transfer.");
            }

            var length = (int)Math.Min(prepared.ChunkBytes, total - offset);
            var next = offset + length;
            using var fragment = new FileFragmentContent(source, offset, length, total, request.ContentType);
            try
            {
                await TransferRequestAsync(prepared, HttpMethod.Put, false, fragment, async (response, ct) =>
                {
                    if (response.StatusCode != (next == total ? HttpStatusCode.OK : HttpStatusCode.Accepted))
                    {
                        throw ContractError(prepared.TransferId);
                    }

                    var progress = await ReadProgressAsync(response, prepared, ct);
                    if (!ConfirmsProgress(progress, next, total))
                    {
                        throw UnknownUpload(prepared, progress.RecoveryReason);
                    }

                    return true;
                }, cancellationToken);
            }
            catch (WorkIqFileTransferException failure)
            {
                await ReconcileUploadAsync(prepared, next, failure, cancellationToken);
            }

            offset = next;
        }

        if (source.Length != total)
        {
            throw new IOException("The upload source changed during transfer.");
        }

        return Receipt(prepared, sourcePath);
    }

    private async Task<PreparedFile> PrepareFileAsync(bool upload, Dictionary<string, object?> arguments, string fallbackName,
        long? expectedSize, CancellationToken cancellationToken)
    {
        var tool = upload ? "do_action" : "call_function";
        var result = await CallToolAsync(tool, arguments, cancellationToken, sensitiveResult: true);
        var body = WorkIqEnvelope.TransferData(tool, result);
        var action = ObjectField(ObjectField(body, "_meta"), "com.microsoft.workiq/clientAction");
        var constraints = ObjectField(action, "constraints");
        var size = LongField(constraints, "expectedSize");
        if (StringField(action, "contractVersion") != "1.0" || StringField(action, "kind") != "file-transfer"
            || StringField(action, "direction") != (upload ? "upload" : "download")
            || StringField(action, "nextActor") != "client" || !TrueField(action, "requiresFollowUp")
            || StringField(ObjectField(body, "transfer"), "method") != (upload ? "PUT" : "GET")
            || size < 0 || (expectedSize is { } expected && expected != size))
        {
            throw ContractError();
        }

        var transferId = StringField(action, "fileId");
        if (string.IsNullOrWhiteSpace(transferId) || !action.TryGetProperty("expiresAt", out var expiry)
            || expiry.ValueKind != JsonValueKind.String || !expiry.TryGetDateTimeOffset(out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow)
        {
            throw ContractError();
        }

        var contentUrl = TransferUri(StringField(action, "contentUrl"));
        var statusUrl = TransferUri(StringField(action, "statusUrl"));
        if (contentUrl == statusUrl)
        {
            throw ContractError();
        }

        var chunkBytes = 0;
        if (upload)
        {
            var recommended = LongField(constraints, "recommendedChunkSize");
            var alignment = LongField(constraints, "chunkSizeMultiple");
            var maximum = LongField(constraints, "maximumChunkSizeExclusive");
            if (StringField(action, "transferMode") != "upload-session" || !TrueField(constraints, "sequentialRangesRequired")
                || size <= 0 || recommended <= 0 || alignment <= 0 || maximum <= 1
                || recommended >= maximum || recommended % alignment != 0)
            {
                throw ContractError();
            }

            var bounded = Math.Min(recommended, Math.Min(maximum, MaximumChunkSizeExclusive) - 1);
            bounded -= bounded % alignment;
            if (bounded <= 0)
            {
                throw ContractError();
            }

            chunkBytes = (int)bounded;
        }

        var name = fallbackName;
        var contentType = "application/octet-stream";
        if (body.TryGetProperty("file", out var file) && file.ValueKind == JsonValueKind.Object)
        {
            name = StringField(file, "name") ?? name;
            contentType = StringField(file, "mimeType") ?? contentType;
            if (file.TryGetProperty("size", out _) && LongField(file, "size") != size)
            {
                throw ContractError();
            }
        }

        return new PreparedFile(transferId, contentUrl, statusUrl, expiresAt, size, name, contentType, chunkBytes);
    }

    private Uri TransferUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || value.Contains('\\')
            || value.StartsWith("//", StringComparison.Ordinal) || !options.Endpoint.IsAbsoluteUri)
        {
            throw ContractError();
        }

        Uri? uri;
        if (value.StartsWith('/'))
        {
            if (!Uri.TryCreate(new Uri(options.Endpoint.GetLeftPart(UriPartial.Authority)), value, out uri))
            {
                throw ContractError();
            }
        }
        else if (!Uri.TryCreate(value, UriKind.Absolute, out uri))
        {
            throw ContractError();
        }

        if (uri.Scheme != options.Endpoint.Scheme || uri.IdnHost != options.Endpoint.IdnHost || uri.Port != options.Endpoint.Port
            || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
            || (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && options.Endpoint.IsLoopback)))
        {
            throw ContractError();
        }

        return uri;
    }

    private async Task<T> TransferRequestAsync<T>(PreparedFile prepared, HttpMethod method, bool status, HttpContent? content,
        Func<HttpResponseMessage, CancellationToken, Task<T>> readResponse, CancellationToken cancellationToken)
    {
        if (prepared.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            throw new WorkIqFileTransferException(WorkIqFailure.Unavailable, "The Work IQ file-transfer ticket has expired.", prepared.TransferId);
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.FileTransferTimeoutSeconds);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(options.FileTransferTimeoutSeconds));
        using var request = new HttpRequestMessage(method, status ? prepared.StatusUrl : prepared.ContentUrl) { Content = content };
        try
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await accessToken(timeout.Token));
            using var response = await fileTransferHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                throw new WorkIqFileTransferException(WorkIqException.KindFor(response.StatusCode),
                    $"Work IQ file transfer failed ({(int)response.StatusCode}).", prepared.TransferId, (int)response.StatusCode);
            }

            var result = await readResponse(response, timeout.Token);
            timeout.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WorkIqFileTransferException(WorkIqFailure.Unavailable,
                $"Work IQ file transfer timed out after {options.FileTransferTimeoutSeconds}s.", prepared.TransferId);
        }
        catch (HttpRequestException ex)
        {
            throw new WorkIqFileTransferException(WorkIqException.KindFor(ex.StatusCode),
                "The Work IQ file-transfer HTTP request failed.", prepared.TransferId, (int?)ex.StatusCode);
        }
    }

    private async Task ReconcileUploadAsync(PreparedFile prepared, long expectedOffset, WorkIqFileTransferException failure, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        UploadProgress progress;
        try
        {
            progress = await TransferRequestAsync(prepared, HttpMethod.Get, true, null, async (response, ct) =>
            {
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    throw ContractError(prepared.TransferId);
                }

                return await ReadProgressAsync(response, prepared, ct);
            }, cancellationToken);
        }
        catch (WorkIqFileTransferException statusFailure)
        {
            throw UnknownUpload(prepared, statusFailure.RecoveryReason
                ?? (statusFailure.StatusCode is 404 or 410 ? "upload_session_unavailable" : "upload_outcome_unknown"), failure);
        }

        if (!ConfirmsProgress(progress, expectedOffset, prepared.SizeBytes))
        {
            throw UnknownUpload(prepared, progress.RecoveryReason, failure);
        }
    }

    private static bool ConfirmsProgress(UploadProgress progress, long expectedOffset, long total) =>
        progress.RecoveryReason is null && (expectedOffset == total
            ? progress.State == "completed"
            : progress.State == "active" && progress.NextOffset == expectedOffset);

    private static async Task<UploadProgress> ReadProgressAsync(HttpResponseMessage response, PreparedFile prepared, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaximumStatusBytes)
        {
            throw ContractError(prepared.TransferId);
        }

        var buffer = new byte[MaximumStatusBytes + 1];
        var count = 0;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            while (count < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(count, Math.Min(FileBufferSize, buffer.Length - count)), ct);
                if (read == 0)
                {
                    break;
                }

                count += read;
            }

            if (count > MaximumStatusBytes)
            {
                throw ContractError(prepared.TransferId);
            }

            using var document = JsonDocument.Parse(buffer.AsMemory(0, count));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (root.TryGetProperty("recoveryReason", out var reason) && reason.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)))
            {
                throw ContractError(prepared.TransferId);
            }

            var state = StringField(root, "state");
            var recovery = StringField(root, "recoveryReason") switch
            {
                "upload_outcome_unknown" => "upload_outcome_unknown",
                "upload_session_unavailable" => "upload_session_unavailable",
                null => null,
                _ => "upload_outcome_unknown",
            };
            if (StringField(root, "direction") != "upload" || LongField(root, "totalBytes") != prepared.SizeBytes
                || !root.TryGetProperty("expiresAt", out var expiry) || expiry.ValueKind != JsonValueKind.String
                || !expiry.TryGetDateTimeOffset(out var expiresAt) || expiresAt <= DateTimeOffset.UtcNow
                || !root.TryGetProperty("nextExpectedRanges", out var ranges) || ranges.ValueKind != JsonValueKind.Array)
            {
                throw ContractError(prepared.TransferId);
            }

            if (state == "completed" && ranges.GetArrayLength() == 0)
            {
                return new UploadProgress(state, prepared.SizeBytes, recovery);
            }

            if (state == "indeterminate")
            {
                return new UploadProgress(state, null, recovery ?? "upload_outcome_unknown");
            }

            if (state is not ("prepared" or "active") || ranges.GetArrayLength() != 1
                || ranges[0].ValueKind != JsonValueKind.String || ranges[0].GetString() is not { } range
                || !range.EndsWith('-') || !long.TryParse(range.AsSpan(0, range.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var offset)
                || offset < 0 || offset >= prepared.SizeBytes || (state == "prepared" && offset != 0))
            {
                throw ContractError(prepared.TransferId);
            }

            return new UploadProgress(state, offset, recovery);
        }
        catch (JsonException)
        {
            throw ContractError(prepared.TransferId);
        }
        catch (IOException)
        {
            throw new WorkIqFileTransferException(WorkIqFailure.Unavailable, "The Work IQ file-transfer status stream was interrupted.", prepared.TransferId);
        }
    }

    private static string AbsoluteFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || string.IsNullOrEmpty(Path.GetFileName(path)))
        {
            throw new ArgumentException("An absolute local file path is required.", nameof(path));
        }

        return Path.GetFullPath(path);
    }

    private static void RequireValue(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A nonempty value is required.", name);
        }
    }

    private static FileTransferResult Receipt(PreparedFile file, string path) =>
        new(path, file.Name, file.ContentType, file.SizeBytes, file.TransferId);

    private static WorkIqFileTransferException ContractError(string? id = null) =>
        new(WorkIqFailure.Unavailable, "Work IQ returned an unsupported file-transfer contract. Check provider bindings, file-transfer capability, and response conversion.", id);

    private static WorkIqFileTransferException UnknownUpload(PreparedFile prepared, string? reason, WorkIqFileTransferException? cause = null) =>
        new(cause?.Failure ?? WorkIqFailure.Unavailable, "Work IQ could not confirm the upload outcome; no fragment was replayed.",
            prepared.TransferId, cause?.StatusCode, reason ?? "upload_outcome_unknown");

    private static JsonElement ObjectField(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object
            ? child : throw ContractError();

    private static string? StringField(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.String
            ? child.GetString() : null;

    private static bool TrueField(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.True;

    private static long LongField(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out var child)
            && child.ValueKind == JsonValueKind.Number && child.TryGetInt64(out var number) ? number : throw ContractError();

    private sealed record PreparedFile(string TransferId, Uri ContentUrl, Uri StatusUrl, DateTimeOffset ExpiresAt,
        long SizeBytes, string Name, string ContentType, int ChunkBytes);

    private sealed record UploadProgress(string State, long? NextOffset, string? RecoveryReason);

    private sealed class FileFragmentContent : HttpContent
    {
        private readonly FileStream _source;
        private readonly long _offset;
        private readonly int _length;

        public FileFragmentContent(FileStream source, long offset, int length, long total, string contentType)
        {
            _source = source;
            _offset = offset;
            _length = length;
            Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
            Headers.ContentLength = length;
            Headers.ContentRange = new ContentRangeHeaderValue(offset, offset + length - 1, total);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _length;
            return true;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            _source.Position = _offset;
            var buffer = new byte[FileBufferSize];
            var remaining = _length;
            while (remaining > 0)
            {
                var read = await _source.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), cancellationToken);
                if (read == 0)
                {
                    throw new IOException("The upload source changed during transfer.");
                }

                await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
    }
}
