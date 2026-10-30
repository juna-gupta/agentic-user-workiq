using System.Collections.Concurrent;
using System.Text;
using IncidentAgent.WorkIq;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentAgent.Knowledge;

public sealed class RunbookOptions
{
    public const string Section = "Runbook";

    /// <summary>Where the runbook lives in the agent user's own OneDrive.</summary>
    public string Path { get; set; } = "/Runbooks/helpdesk-bot-runbook.md";

    /// <summary>How long a downloaded runbook is reused before it's read again (edits show up after this).</summary>
    public int CacheMinutes { get; set; } = 30;

    /// <summary>Up to this many cases go into the model's catalog; a bigger runbook is pre-ranked by keyword match first.</summary>
    public int MaxCatalog { get; set; } = 40;
}

/// <summary>
/// The agent's runbook, read from its own OneDrive with Work IQ <c>fetch_blob</c>. The agent user owns the file like
/// any employee would, so whoever maintains it just edits it in OneDrive.
/// </summary>
public sealed class RunbookStore(IOptions<RunbookOptions> options, TimeProvider clock, ILogger<RunbookStore> logger)
{
    private readonly ConcurrentDictionary<string, (KnowledgeBase Runbook, DateTimeOffset Loaded)> _cache = new(StringComparer.OrdinalIgnoreCase);

    public string FileName => System.IO.Path.GetFileName(options.Value.Path);

    /// <param name="ownerId">The agent user's ID; each agent user has its own OneDrive.</param>
    /// <exception cref="RunbookNotFoundException">The runbook isn't in the agent user's OneDrive at <see cref="RunbookOptions.Path"/>.</exception>
    public async Task<KnowledgeBase> GetAsync(string ownerId, IWorkIq workIq, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (_cache.TryGetValue(ownerId, out var hit) && now - hit.Loaded < TimeSpan.FromMinutes(options.Value.CacheMinutes))
        {
            return hit.Runbook;
        }

        var path = options.Value.Path;
        BlobResult blob;
        try
        {
            blob = await workIq.FetchBlobAsync(WorkIqPaths.MyDriveFileContent(path), cancellationToken);
        }
        catch (WorkIqException ex) when (ex.Failure == WorkIqFailure.NotFound)
        {
            logger.LogWarning(
                "The runbook isn't in the agent user's OneDrive at {Path}. Upload it with scripts/seed_onedrive.py, or set Runbook:Path. ({Error})",
                path, ex.Message);
            throw new RunbookNotFoundException(path, ex);
        }

        var runbook = KnowledgeBase.Parse(Encoding.UTF8.GetString(blob.Content), $"OneDrive {path}");
        logger.LogInformation("Read {Count} runbook cases from OneDrive {Path} ({Bytes} bytes)", runbook.Cases.Count, path, blob.Content.Length);
        _cache[ownerId] = (runbook, now);
        return runbook;
    }
}

/// <summary>The runbook file isn't in the agent user's OneDrive. Not cached, so it's picked up as soon as it's uploaded.</summary>
public sealed class RunbookNotFoundException(string path, Exception? inner = null)
    : WorkIqException(WorkIqFailure.NotFound, $"The runbook isn't in my OneDrive at {path}.", 404, inner)
{
    public string Path { get; } = path;
}
