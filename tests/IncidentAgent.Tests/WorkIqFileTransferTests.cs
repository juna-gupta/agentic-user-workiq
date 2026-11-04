using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.Logging.Abstractions;

namespace IncidentAgent.Tests;

public class WorkIqFileTransferTests
{
    [Theory]
    [InlineData(true, 5 * 1024 * 1024 + 17)]
    [InlineData(false, 37)]
    [InlineData(true, 0)]
    public async Task Downloads_binary_files_without_putting_content_in_mcp_or_receipts(bool structured, int size)
    {
        await using var h = new Harness { Structured = structured };
        var bytes = Data(size);
        h.Preparation = Handoff(size, "download");
        h.Send = (_, _) => Task.FromResult(ContentResponse(bytes));

        var receipt = await h.Client.DownloadFileAsync(new DownloadFileRequest("drive!+%", "item:@", h.PathFor("out.bin")), default);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(receipt.LocalPath));
        Assert.Equal(size, receipt.SizeBytes);
        Assert.Equal("ticket", receipt.TransferId);
        Assert.Equal("server.bin", receipt.Name);
        var call = Assert.Single(h.Tools);
        Assert.Equal("call_function", call.Name);
        Assert.Equal(WorkIqPaths.DownloadFileDetails, call.Arguments.GetProperty("functionUrl").GetString());
        var body = call.Arguments.GetProperty("jsonBody");
        Assert.Equal("drive!+%", body.GetProperty("documentLibraryId").GetString());
        Assert.Equal("item:@", body.GetProperty("fileId").GetString());
        Assert.Equal(2, body.EnumerateObject().Count());
        Assert.DoesNotContain("base64", JsonSerializer.Serialize(receipt), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(2, h.TokenCalls);
        var request = Assert.Single(h.Requests);
        Assert.Equal(new Uri("https://workiq.example/api/beta/files/ticket/content"), request.Url);
        Assert.Equal("Bearer token-2", request.Authorization);
        Assert.Equal(["out.bin"], Directory.GetFiles(h.Root).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Uploads_sequential_fragments_with_metadata_only_preparation(bool structured)
    {
        await using var h = new Harness { Structured = structured };
        var bytes = Data(11);
        var source = h.PathFor("source.bin");
        await File.WriteAllBytesAsync(source, bytes);
        h.Preparation = Handoff(bytes.Length, "upload");
        using var received = new MemoryStream();
        var ranges = new List<string>();
        h.Send = async (request, ct) =>
        {
            Assert.Equal(HttpMethod.Put, request.Method);
            Assert.Equal(Math.Min(4, bytes.Length - received.Length), request.Content!.Headers.ContentLength);
            ranges.Add(request.Content.Headers.ContentRange!.ToString());
            await request.Content.CopyToAsync(received, ct);
            return StatusResponse(bytes.Length, received.Length);
        };

        var result = await h.Client.UploadFileAsync(new UploadFileRequest(source, "drive"), default);

        Assert.Equal(bytes, received.ToArray());
        Assert.Equal(["bytes 0-3/11", "bytes 4-7/11", "bytes 8-10/11"], ranges);
        Assert.Equal(11, result.SizeBytes);
        Assert.Equal("ticket", result.TransferId);
        var call = Assert.Single(h.Tools);
        Assert.Equal("do_action", call.Name);
        Assert.Equal(WorkIqPaths.UploadFileDetails, call.Arguments.GetProperty("actionUrl").GetString());
        var body = call.Arguments.GetProperty("jsonBody");
        Assert.Equal("source.bin", body.GetProperty("fileName").GetString());
        Assert.Equal(11, body.GetProperty("fileSize").GetInt64());
        Assert.Equal("fail", body.GetProperty("conflictBehavior").GetString());
        Assert.Equal("root", body.GetProperty("parentFolderId").GetString());
        Assert.Equal(6, body.EnumerateObject().Count());
        Assert.Equal(4, h.TokenCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Requires_explicit_overwrite_and_publishes_only_a_complete_download(bool overwrite)
    {
        await using var h = new Harness();
        var destination = h.PathFor("out.bin");
        var old = Data(9);
        var replacement = Data(13);
        await File.WriteAllBytesAsync(destination, old);
        h.Preparation = Handoff(replacement.Length, "download");
        h.Send = (_, _) => Task.FromResult(ContentResponse(replacement));
        var request = new DownloadFileRequest("drive", "item", destination) { Overwrite = overwrite };

        if (overwrite)
        {
            await h.Client.DownloadFileAsync(request, default);
            Assert.Equal(replacement, await File.ReadAllBytesAsync(destination));
            Assert.Single(h.Tools);
        }
        else
        {
            await Assert.ThrowsAsync<IOException>(() => h.Client.DownloadFileAsync(request, default));
            Assert.Equal(old, await File.ReadAllBytesAsync(destination));
            Assert.Empty(h.Tools);
            Assert.Empty(h.Requests);
        }

        Assert.Equal([destination], Directory.GetFiles(h.Root));
    }

    [Fact]
    public async Task Preserves_a_destination_created_while_downloading()
    {
        await using var h = new Harness();
        var destination = h.PathFor("out.bin");
        h.Preparation = Handoff(8, "download");
        h.Send = async (_, ct) =>
        {
            await File.WriteAllBytesAsync(destination, Data(17), ct);
            return ContentResponse(Data(8));
        };

        await Assert.ThrowsAsync<IOException>(() => h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", destination), default));

        Assert.Equal(Data(17), await File.ReadAllBytesAsync(destination));
        Assert.Equal([destination], Directory.GetFiles(h.Root));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("excess")]
    [InlineData("partial")]
    [InlineData("range")]
    [InlineData("length")]
    [InlineData("interrupted")]
    public async Task Failed_downloads_preserve_the_old_file_and_clean_up_temporary_files(string failure)
    {
        await using var h = new Harness();
        var destination = h.PathFor("out.bin");
        await File.WriteAllBytesAsync(destination, Data(19));
        h.Preparation = Handoff(8, "download");
        h.Send = (_, _) =>
        {
            var response = ContentResponse(Data(failure == "truncated" ? 7 : failure == "excess" ? 9 : 8));
            response.Content.Headers.ContentLength = failure == "length" ? 10 : null;
            if (failure == "partial")
            {
                response.StatusCode = HttpStatusCode.PartialContent;
            }
            else if (failure == "range")
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(0, 7, 8);
            }
            else if (failure == "interrupted")
            {
                response.Content = new StreamContent(new IncrementalReadStream(Data(8), (read, _) =>
                    read == 1 ? Task.FromException(new IOException("secret transfer URL")) : Task.CompletedTask));
            }

            return Task.FromResult(response);
        };

        var error = await Assert.ThrowsAsync<WorkIqFileTransferException>(() =>
            h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", destination) { Overwrite = true }, default));

        Assert.DoesNotContain("secret transfer URL", error.ToString());
        Assert.Equal(Data(19), await File.ReadAllBytesAsync(destination));
        Assert.Equal([destination], Directory.GetFiles(h.Root));
    }

    [Theory]
    [InlineData(401, WorkIqFailure.Unauthorized)]
    [InlineData(403, WorkIqFailure.Forbidden)]
    [InlineData(404, WorkIqFailure.NotFound)]
    [InlineData(409, WorkIqFailure.Unavailable)]
    [InlineData(413, WorkIqFailure.Unavailable)]
    [InlineData(429, WorkIqFailure.Throttled)]
    [InlineData(503, WorkIqFailure.Unavailable)]
    [InlineData(302, WorkIqFailure.Unavailable)]
    public async Task Maps_http_failures_without_reading_or_disclosing_error_bodies(int code, WorkIqFailure failure)
    {
        await using var h = new Harness();
        h.Send = (_, _) =>
        {
            var response = JsonResponse(new { secret = "secret transfer URL" }, (HttpStatusCode)code);
            response.Headers.Location = new Uri("https://foreign.example/content");
            return Task.FromResult(response);
        };

        var error = await Assert.ThrowsAsync<WorkIqFileTransferException>(() =>
            h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", h.PathFor("out.bin")), default));

        Assert.Equal(code, error.StatusCode);
        Assert.Equal(failure, error.Failure);
        Assert.DoesNotContain("secret transfer URL", error.ToString());
        Assert.Single(h.Requests);
        Assert.Empty(Directory.GetFiles(h.Root));
    }

    public static IEnumerable<object[]> UnsafeUrls()
    {
        string[] urls =
        [
            "https://contoso.sharepoint.com/native",
            "//foreign.example/content",
            "https://user@workiq.example/content",
            "https://workiq.example/content#fragment",
            "https://workiq.example:444/content",
            "http://workiq.example/content",
            "relative/content",
            @"/\foreign.example/content",
            "file:///C:/content",
            "",
        ];
        return from field in new[] { "contentUrl", "statusUrl" } from url in urls select new object[] { field, url };
    }

    [Theory]
    [MemberData(nameof(UnsafeUrls))]
    public async Task Rejects_unsafe_urls_before_dispatching_any_authenticated_http(string field, string url)
    {
        await using var h = new Harness();
        Action(h.Preparation)[field] = url;

        await Assert.ThrowsAsync<WorkIqFileTransferException>(() =>
            h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", h.PathFor("out.bin")), default));

        Assert.Empty(h.Requests);
        Assert.Equal(1, h.TokenCalls);
        Assert.Empty(Directory.GetFiles(h.Root));
    }

    [Theory]
    [InlineData("https://workiq.example/api/v1/files/legacy/content")]
    [InlineData("/api/v1/files/legacy/content")]
    public async Task Follows_issued_absolute_and_legacy_urls_instead_of_rebuilding_them(string url)
    {
        await using var h = new Harness();
        Action(h.Preparation)["contentUrl"] = url;
        h.Send = (_, _) => Task.FromResult(ContentResponse([]));

        await h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", h.PathFor("out.bin")), default);

        Assert.Equal("https://workiq.example/api/v1/files/legacy/content", Assert.Single(h.Requests).Url.AbsoluteUri);
    }

    [Theory]
    [InlineData("contractVersion", "2.0")]
    [InlineData("direction", "upload")]
    [InlineData("kind", "other")]
    [InlineData("nextActor", "server")]
    [InlineData("fileId", "")]
    [InlineData("expiresAt", "2000-01-01T00:00:00Z")]
    public async Task Rejects_unsupported_or_expired_handoffs(string field, string value)
    {
        await using var h = new Harness();
        Action(h.Preparation)[field] = value;

        await Assert.ThrowsAsync<WorkIqFileTransferException>(() =>
            h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", h.PathFor("out.bin")), default));

        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Does_not_accept_mcp_transport_metadata_as_the_business_handoff()
    {
        await using var h = new Harness();
        h.TransportMetadata = h.Preparation["_meta"]!.DeepClone().AsObject();
        h.Preparation.Remove("_meta");
        h.Preparation["transfer"]!["url"] = "https://contoso.sharepoint.com/native";

        await Assert.ThrowsAsync<WorkIqFileTransferException>(() =>
            h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", h.PathFor("out.bin")), default));

        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("out.bin")]
    [InlineData("C:out.bin")]
    public async Task Rejects_non_absolute_local_paths_before_mcp(string path)
    {
        await using var h = new Harness();

        await Assert.ThrowsAsync<ArgumentException>(() => h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", path), default));
        await Assert.ThrowsAsync<ArgumentException>(() => h.Client.UploadFileAsync(new UploadFileRequest(path, "drive"), default));

        Assert.Empty(h.Tools);
    }

    [Fact]
    public async Task Requires_an_existing_download_parent_directory()
    {
        await using var h = new Harness();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", h.PathFor(Path.Combine("missing", "out.bin"))), default));

        Assert.Empty(h.Tools);
        Assert.Empty(Directory.GetDirectories(h.Root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Distinguishes_caller_cancellation_from_timeout_during_body_streaming(bool callerCancellation)
    {
        await using var h = new Harness();
        h.Options.FileTransferTimeoutSeconds = callerCancellation ? 30 : 1;
        h.Preparation = Handoff(8, "download");
        var destination = h.PathFor("out.bin");
        await File.WriteAllBytesAsync(destination, Data(17));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new IncrementalReadStream(Data(8), async (read, ct) =>
            {
                if (read == 1)
                {
                    entered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
            })),
        });
        using var cancellation = new CancellationTokenSource();
        var operation = h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", destination) { Overwrite = true }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (callerCancellation)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        else
        {
            var error = await Assert.ThrowsAsync<WorkIqFileTransferException>(() => operation);
            Assert.Contains("timed out", error.Message);
        }

        Assert.Equal(Data(17), await File.ReadAllBytesAsync(destination));
        Assert.Equal([destination], Directory.GetFiles(h.Root));
    }

    [Fact]
    public async Task Makes_filesystem_progress_before_the_download_body_finishes()
    {
        await using var h = new Harness();
        var bytes = Data(256 * 1024);
        h.Preparation = Handoff(bytes.Length, "download");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Send = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new IncrementalReadStream(bytes, async (read, ct) =>
            {
                if (read == 32)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(ct);
                }
            })),
        });
        var destination = h.PathFor("out.bin");
        var operation = h.Client.DownloadFileAsync(new DownloadFileRequest("drive", "item", destination), default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(File.Exists(destination));
            var temporary = Assert.Single(Directory.GetFiles(h.Root));
            Assert.EndsWith(".tmp", temporary);
            Assert.True(new FileInfo(temporary).Length > 0);
        }
        finally
        {
            release.TrySetResult();
            await operation;
        }

        Assert.Equal(bytes, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task Uploads_large_files_incrementally_with_exact_fragment_boundaries()
    {
        await using var h = new Harness();
        var bytes = Data(5 * 1024 * 1024 + 17);
        var source = h.PathFor("large.bin");
        await File.WriteAllBytesAsync(source, bytes);
        h.Preparation = Handoff(bytes.Length, "upload");
        var constraints = Action(h.Preparation)["constraints"]!.AsObject();
        constraints["recommendedChunkSize"] = 1024 * 1024;
        constraints["chunkSizeMultiple"] = 256 * 1024;
        constraints["maximumChunkSizeExclusive"] = 2 * 1024 * 1024;
        using var received = new MemoryStream();
        using var sink = new IncrementalWriteStream(received);
        h.Send = async (request, ct) =>
        {
            var headers = request.Content!.Headers;
            var start = sink.Length;
            Assert.Equal(start, headers.ContentRange!.From);
            Assert.Equal(bytes.Length, headers.ContentRange.Length);
            await request.Content.CopyToAsync(sink, ct);
            Assert.Equal(headers.ContentLength, sink.Length - start);
            Assert.Equal(sink.Length - 1, headers.ContentRange.To);
            return StatusResponse(bytes.Length, sink.Length);
        };

        await h.Client.UploadFileAsync(new UploadFileRequest(source, "drive"), default);

        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(received.ToArray()));
        Assert.Equal(6, h.Requests.Count);
        Assert.All(h.Tools, tool => Assert.True(tool.Arguments.GetRawText().Length < 1024));
    }

    [Fact]
    public async Task Clamps_fragments_below_the_exclusive_workiq_http_ingress_limit()
    {
        await using var h = new Harness();
        const long total = 61 * 1024 * 1024;
        var source = h.PathFor("large.bin");
        using (var file = File.Create(source))
        {
            file.SetLength(total);
        }

        h.Preparation = Handoff(total, "upload");
        var constraints = Action(h.Preparation)["constraints"]!.AsObject();
        constraints["recommendedChunkSize"] = 60 * 1024 * 1024;
        constraints["chunkSizeMultiple"] = 1024 * 1024;
        constraints["maximumChunkSizeExclusive"] = 64 * 1024 * 1024;
        using var sink = new IncrementalWriteStream();
        var lengths = new List<long>();
        h.Send = async (request, ct) =>
        {
            lengths.Add(request.Content!.Headers.ContentLength!.Value);
            await request.Content.CopyToAsync(sink, ct);
            return StatusResponse(total, sink.Length);
        };

        await h.Client.UploadFileAsync(new UploadFileRequest(source, "drive"), default);

        Assert.Equal([59L * 1024 * 1024, 2L * 1024 * 1024], lengths);
        Assert.Equal(total, sink.Length);
    }

    [Theory]
    [InlineData(UploadConflictBehavior.Replace, "replace")]
    [InlineData(UploadConflictBehavior.Rename, "rename")]
    public async Task Preserves_explicit_upload_metadata_as_json_parameters(UploadConflictBehavior conflict, string expected)
    {
        await using var h = new Harness();
        var source = h.PathFor("source.bin");
        await File.WriteAllBytesAsync(source, Data(3));
        h.Preparation = Handoff(3, "upload");
        h.Send = async (request, ct) =>
        {
            Assert.Equal("text/plain", request.Content!.Headers.ContentType!.ToString());
            await request.Content.CopyToAsync(Stream.Null, ct);
            return StatusResponse(3, 3);
        };
        const string name = "quoted \"name\" +%#.txt";
        const string drive = "opaque/drive?+%#\"";
        const string folder = "opaque/folder?+%#\"";

        await h.Client.UploadFileAsync(new UploadFileRequest(source, drive)
        {
            FileName = name,
            ParentFolderId = folder,
            ContentType = "text/plain",
            ConflictBehavior = conflict,
        }, default);

        var body = Assert.Single(h.Tools).Arguments.GetProperty("jsonBody");
        Assert.Equal(name, body.GetProperty("fileName").GetString());
        Assert.Equal(drive, body.GetProperty("documentLibraryId").GetString());
        Assert.Equal(folder, body.GetProperty("parentFolderId").GetString());
        Assert.Equal(expected, body.GetProperty("conflictBehavior").GetString());
    }

    [Fact]
    public async Task Rejects_missing_and_empty_sources_before_preparing_an_upload()
    {
        await using var h = new Harness();
        var source = h.PathFor("source.bin");
        await Assert.ThrowsAsync<FileNotFoundException>(() => h.Client.UploadFileAsync(new UploadFileRequest(source, "drive"), default));
        await File.WriteAllBytesAsync(source, []);

        await Assert.ThrowsAsync<ArgumentException>(() => h.Client.UploadFileAsync(new UploadFileRequest(source, "drive"), default));

        Assert.Empty(h.Tools);
        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData("expectedSize", 9L)]
    [InlineData("recommendedChunkSize", 0L)]
    [InlineData("recommendedChunkSize", 6L)]
    [InlineData("recommendedChunkSize", long.MaxValue)]
    [InlineData("chunkSizeMultiple", 0L)]
    [InlineData("maximumChunkSizeExclusive", 4L)]
    [InlineData("maximumChunkSizeExclusive", 1L)]
    public async Task Rejects_unsafe_upload_constraints_before_sending_content(string field, long value)
    {
        await using var h = new Harness();
        var source = h.PathFor("source.bin");
        await File.WriteAllBytesAsync(source, Data(8));
        h.Preparation = Handoff(8, "upload");
        Action(h.Preparation)["constraints"]![field] = value;

        await Assert.ThrowsAsync<WorkIqFileTransferException>(() => h.Client.UploadFileAsync(new UploadFileRequest(source, "drive"), default));

        Assert.Empty(h.Requests);
    }

    [Theory]
    [InlineData("lost", false)]
    [InlineData("lost", true)]
    [InlineData("malformed", false)]
    [InlineData("wrong-progress", false)]
    public async Task Continues_only_after_status_confirms_the_exact_uncertain_fragment(string failure, bool final)
    {
        await using var h = new Harness();
        var source = h.PathFor("source.bin");
        var bytes = Data(8);
        await File.WriteAllBytesAsync(source, bytes);
        h.Preparation = Handoff(bytes.Length, "upload");
        Action(h.Preparation)["statusUrl"] = "/api/v1/files/independent-status";
        using var received = new MemoryStream();
        var ranges = new List<string>();
        h.Send = async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal("/api/v1/files/independent-status", request.RequestUri!.AbsolutePath);
                return StatusResponse(bytes.Length, received.Length, HttpStatusCode.OK);
            }

            ranges.Add(request.Content!.Headers.ContentRange!.ToString());
            await request.Content.CopyToAsync(received, ct);
            if (received.Length == (final ? 8 : 4))
            {
                return failure switch
                {
                    "lost" => throw new HttpRequestException("secret transfer URL"),
                    "malformed" => JsonResponse(new { unexpected = true }, HttpStatusCode.Accepted),
                    _ => StatusResponse(bytes.Length, 0),
                };
            }

            return StatusResponse(bytes.Length, received.Length);
        };

        await h.Client.UploadFileAsync(new UploadFileRequest(source, "drive"), default);

        Assert.Equal(bytes, received.ToArray());
        Assert.Equal(["bytes 0-3/8", "bytes 4-7/8"], ranges);
        Assert.Single(h.Requests, r => r.Method == HttpMethod.Get);
        Assert.Equal(4, h.TokenCalls);
        Assert.Equal(3, h.Requests.Select(r => r.Authorization).Distinct().Count());
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("partial")]
    [InlineData("excessive")]
    [InlineData("premature-completion")]
    [InlineData("indeterminate")]
    [InlineData("missing-session")]
    [InlineData("invalid-json")]
    [InlineData("expired")]
    [InlineData("multiple-ranges")]
    [InlineData("overflow-range")]
    [InlineData("finite-range")]
    [InlineData("wrong-direction")]
    [InlineData("wrong-total")]
    [InlineData("invalid-recovery")]
    [InlineData("oversized")]
    public async Task Never_replays_or_advances_when_status_cannot_confirm_the_upload(string failure)
    {
        await using var h = new Harness();
        var source = h.PathFor("source.bin");
        await File.WriteAllBytesAsync(source, Data(8));
        h.Preparation = Handoff(8, "upload");
        h.Send = async (request, ct) =>
        {
            if (request.Method == HttpMethod.Put)
            {
                await request.Content!.CopyToAsync(Stream.Null, ct);
                throw new HttpRequestException("secret transfer URL");
            }

            var body = Progress(8, 4);
            switch (failure)
            {
                case "unchanged": body = Progress(8, 0); break;
                case "partial": body = Progress(8, 2); break;
                case "excessive": body = Progress(8, 6); break;
                case "premature-completion": body = Progress(8, 8); break;
                case "indeterminate":
                    body["state"] = "indeterminate";
                    body["recoveryReason"] = "upload_outcome_unknown";
                    break;
                case "missing-session": return JsonResponse(new { secret = "secret transfer URL" }, HttpStatusCode.NotFound);
                case "invalid-json": return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not JSON") };
                case "expired": body["expiresAt"] = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "multiple-ranges": body["nextExpectedRanges"] = new JsonArray("4-", "6-"); break;
                case "overflow-range": body["nextExpectedRanges"] = new JsonArray("9223372036854775808-"); break;
                case "finite-range": body["nextExpectedRanges"] = new JsonArray("4-7"); break;
                case "wrong-direction": body["direction"] = "download"; break;
                case "wrong-total": body["totalBytes"] = 9; break;
                case "invalid-recovery": body["recoveryReason"] = 42; break;
                case "oversized": body["unexpected"] = new string('x', 64 * 1024); break;
            }

            return JsonResponse(body);
        };

        var error = await Assert.ThrowsAsync<WorkIqFileTransferException>(() =>
            h.Client.UploadFileAsync(new UploadFileRequest(source, "drive"), default));

        Assert.Equal("ticket", error.TransferId);
        Assert.Equal(failure == "missing-session" ? "upload_session_unavailable" : "upload_outcome_unknown", error.RecoveryReason);
        Assert.DoesNotContain("secret transfer URL", error.ToString());
        Assert.Single(h.Tools);
        Assert.Single(h.Requests, r => r.Method == HttpMethod.Put);
        Assert.Single(h.Requests, r => r.Method == HttpMethod.Get);
        Assert.Equal(Data(8), await File.ReadAllBytesAsync(source));
    }

    [Theory]
    [InlineData(401, WorkIqFailure.Unauthorized)]
    [InlineData(403, WorkIqFailure.Forbidden)]
    [InlineData(404, WorkIqFailure.NotFound)]
    [InlineData(409, WorkIqFailure.Unavailable)]
    [InlineData(413, WorkIqFailure.Unavailable)]
    [InlineData(429, WorkIqFailure.Throttled)]
    [InlineData(503, WorkIqFailure.Unavailable)]
    public async Task Retains_upload_failure_classification_when_reconciliation_is_unavailable(int code, WorkIqFailure failure)
    {
        await using var h = new Harness();
        var source = h.PathFor("source.bin");
        await File.WriteAllBytesAsync(source, Data(8));
        h.Preparation = Handoff(8, "upload");
        h.Send = (request, _) => Task.FromResult(JsonResponse(new { secret = "secret transfer URL" },
            request.Method == HttpMethod.Put ? (HttpStatusCode)code : HttpStatusCode.NotFound));

        var error = await Assert.ThrowsAsync<WorkIqFileTransferException>(() =>
            h.Client.UploadFileAsync(new UploadFileRequest(source, "drive"), default));

        Assert.Equal(code, error.StatusCode);
        Assert.Equal(failure, error.Failure);
        Assert.Equal("upload_session_unavailable", error.RecoveryReason);
        Assert.DoesNotContain("secret transfer URL", error.ToString());
        Assert.Equal(2, h.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stops_a_cancelled_upload_and_reconciles_a_timed_out_upload_without_replay(bool callerCancellation)
    {
        await using var h = new Harness();
        h.Options.FileTransferTimeoutSeconds = callerCancellation ? 30 : 1;
        var source = h.PathFor("source.bin");
        await File.WriteAllBytesAsync(source, Data(8));
        h.Preparation = Handoff(8, "upload");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Send = async (request, ct) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return StatusResponse(8, 0, HttpStatusCode.OK);
            }

            await request.Content!.CopyToAsync(Stream.Null, ct);
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The delay should have been cancelled.");
        };
        using var cancellation = new CancellationTokenSource();
        var operation = h.Client.UploadFileAsync(new UploadFileRequest(source, "drive"), cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (callerCancellation)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.DoesNotContain(h.Requests, r => r.Method == HttpMethod.Get);
        }
        else
        {
            var error = await Assert.ThrowsAsync<WorkIqFileTransferException>(() => operation);
            Assert.Equal("upload_outcome_unknown", error.RecoveryReason);
            Assert.Single(h.Requests, r => r.Method == HttpMethod.Get);
        }

        Assert.Single(h.Requests, r => r.Method == HttpMethod.Put);
        Assert.Equal(Data(8), await File.ReadAllBytesAsync(source));
    }

    private static byte[] Data(int size)
    {
        var data = new byte[size];
        new Random(42).NextBytes(data);
        return data;
    }

    private static JsonObject Handoff(long size, string direction)
    {
        return new JsonObject
        {
            ["file"] = new JsonObject { ["name"] = "server.bin", ["mimeType"] = "application/octet-stream", ["size"] = size },
            ["transfer"] = new JsonObject
            {
                ["method"] = direction == "upload" ? "PUT" : "GET",
                ["requiredHeaders"] = new JsonObject { ["Authorization"] = "native-token", ["X-native"] = "not forwarded" },
            },
            ["_meta"] = new JsonObject
            {
                ["com.microsoft.workiq/clientAction"] = new JsonObject
                {
                    ["contractVersion"] = "1.0",
                    ["kind"] = "file-transfer",
                    ["direction"] = direction,
                    ["requiresFollowUp"] = true,
                    ["nextActor"] = "client",
                    ["fileId"] = "ticket",
                    ["contentUrl"] = "/api/beta/files/ticket/content",
                    ["statusUrl"] = "/api/beta/files/ticket",
                    ["expiresAt"] = DateTimeOffset.UtcNow.AddHours(1),
                    ["transferMode"] = direction == "upload" ? "upload-session" : null,
                    ["constraints"] = new JsonObject
                    {
                        ["expectedSize"] = size,
                        ["supportsRanges"] = true,
                        ["recommendedChunkSize"] = 4,
                        ["chunkSizeMultiple"] = 4,
                        ["maximumChunkSizeExclusive"] = 12,
                        ["sequentialRangesRequired"] = true,
                    },
                },
            },
        };
    }

    private static JsonObject Action(JsonObject body) => body["_meta"]!["com.microsoft.workiq/clientAction"]!.AsObject();

    private static HttpResponseMessage ContentResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new IncrementalReadStream(bytes)) };
        response.Content.Headers.ContentLength = bytes.Length;
        response.Content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileName = "ignored-server-path.bin" };
        return response;
    }

    private static JsonObject Progress(long total, long next) =>
        JsonSerializer.SerializeToNode(new
        {
            state = next == total ? "completed" : next == 0 ? "prepared" : "active",
            direction = "upload",
            totalBytes = total,
            nextExpectedRanges = next == total ? Array.Empty<string>() : [$"{next}-"],
            expiresAt = DateTimeOffset.UtcNow.AddHours(1),
        })!.AsObject();

    private static HttpResponseMessage StatusResponse(long total, long next, HttpStatusCode? code = null) =>
        JsonResponse(Progress(total, next), code ?? (next == total ? HttpStatusCode.OK : HttpStatusCode.Accepted));

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly HttpClient _mcp;
        private readonly HttpClient _files;

        public Harness()
        {
            Root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "iris-workiq-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            _mcp = new HttpClient(new Handler(McpAsync));
            _files = new HttpClient(new Handler(FileAsync)) { Timeout = Timeout.InfiniteTimeSpan };
            Client = new WorkIqClient(_mcp, Options, _ => Task.FromResult("token-" + ++TokenCalls), NullLogger.Instance, _files);
        }

        public string Root { get; }
        public WorkIqOptions Options { get; } = new() { Endpoint = new Uri("https://workiq.example/mcp") };
        public WorkIqClient Client { get; }
        public JsonObject Preparation { get; set; } = Handoff(0, "download");
        public bool Structured { get; init; } = true;
        public JsonObject? TransportMetadata { get; set; }
        public int TokenCalls;
        public List<(string Name, JsonElement Arguments)> Tools { get; } = [];
        public List<(HttpMethod Method, Uri Url, string? Authorization)> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Send { get; set; } =
            (_, _) => throw new InvalidOperationException("Unexpected transfer HTTP request.");

        public string PathFor(string name) => System.IO.Path.Combine(Root, name);

        private async Task<HttpResponseMessage> McpAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method != HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = json.RootElement;
            if (!root.TryGetProperty("id", out var id))
            {
                return new HttpResponseMessage(HttpStatusCode.Accepted);
            }

            var method = root.GetProperty("method").GetString();
            if (method == "server/discover")
            {
                return JsonResponse(new { jsonrpc = "2.0", id = id.Clone(), error = new { code = -32601, message = "Method not found" } });
            }

            object result;
            if (method == "initialize")
            {
                result = new { protocolVersion = "2025-11-25", capabilities = new { tools = new { } }, serverInfo = new { name = "test", version = "1" } };
            }
            else if (method == "tools/call")
            {
                var args = root.GetProperty("params");
                Tools.Add((args.GetProperty("name").GetString()!, args.GetProperty("arguments").Clone()));
                var envelope = new { statusCode = 200, data = Preparation };
                result = Structured
                    ? (object)new { structuredContent = envelope, content = Array.Empty<object>(), _meta = TransportMetadata }
                    : new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(envelope) } }, _meta = TransportMetadata };
            }
            else
            {
                throw new InvalidOperationException("Unexpected MCP method: " + method);
            }

            return JsonResponse(new { jsonrpc = "2.0", id = id.Clone(), result });
        }

        private Task<HttpResponseMessage> FileAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.False(request.Headers.Contains("X-native"));
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString()));
            return Send(request, ct);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _mcp.Dispose();
            _files.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }

    private sealed class IncrementalReadStream(byte[] bytes, Func<int, CancellationToken, Task>? beforeRead = null) : Stream
    {
        private int _position;
        private int _reads;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Use asynchronous reads.");
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.InRange(buffer.Length, 1, 64 * 1024);
            if (beforeRead is not null)
            {
                await beforeRead(_reads++, cancellationToken);
            }

            var count = Math.Min(Math.Min(buffer.Length, 4096), bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class IncrementalWriteStream(Stream? destination = null) : Stream
    {
        private long _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Assert.InRange(buffer.Length, 1, 64 * 1024);
            if (destination is not null)
            {
                await destination.WriteAsync(buffer, cancellationToken);
            }

            _written += buffer.Length;
        }
        public override void Write(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Use asynchronous writes.");
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
