namespace IncidentAgent.WorkIq;

public sealed record DownloadFileRequest(string DocumentLibraryId, string FileId, string DestinationPath)
{
    public bool Overwrite { get; init; }
}

public enum UploadConflictBehavior
{
    Fail,
    Replace,
    Rename,
}

public sealed record UploadFileRequest(string SourcePath, string DocumentLibraryId)
{
    public string ParentFolderId { get; init; } = "root";

    public string? FileName { get; init; }

    public string ContentType { get; init; } = "application/octet-stream";

    public UploadConflictBehavior ConflictBehavior { get; init; } = UploadConflictBehavior.Fail;
}

/// <summary>A metadata-only receipt. TransferId is an opaque Work IQ ticket, not a driveItem ID.</summary>
public sealed record FileTransferResult(string LocalPath, string Name, string ContentType, long SizeBytes, string TransferId);

public sealed class WorkIqFileTransferException(
    WorkIqFailure failure,
    string message,
    string? transferId = null,
    int? statusCode = null,
    string? recoveryReason = null) : WorkIqException(failure, message, statusCode)
{
    public string? TransferId { get; } = transferId;

    public string? RecoveryReason { get; } = recoveryReason;
}
