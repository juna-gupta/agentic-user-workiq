using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace IncidentAgent.WorkIq;

/// <summary>
/// Builds the Work IQ paths the agent reads and validates the IDs that go into them. The paths are hard-coded for now.
/// The recommended approach is to discover them at run time with Work IQ's <c>search_paths</c> and <c>get_schema</c>.
/// </summary>
public static partial class WorkIqPaths
{
    private static readonly string[] GraphRoots = ["https://graph.microsoft.com/v1.0", "https://graph.microsoft.com/beta"];

    public const string DriveItemFields = "id,name,webUrl,lastModifiedDateTime,size,file,folder,parentReference";

    public const string EventFields = "id,subject,start,end,organizer,attendees,body,webLink,isCancelled";

    public static string ChannelMessage(string teamId, string channelId, string messageId) =>
        $"/teams/{Id(teamId)}/channels/{Id(channelId)}/messages/{Id(messageId)}";

    public static string ChannelReplies(string teamId, string channelId, string messageId, int top) =>
        $"{ChannelMessage(teamId, channelId, messageId)}/replies?$top={top}";

    /// <summary>
    /// Most recently modified first. Older pages are read with
    /// <c>lastModifiedDateTime lt</c> (<paramref name="before"/>).
    /// </summary>
    public static string ChatMessages(string chatId, int top, DateTimeOffset? before = null) =>
        $"/chats/{Id(chatId)}/messages?$top={top}&$orderby=lastModifiedDateTime desc"
        + (before is { } b ? "&$filter=lastModifiedDateTime lt " + Utc(b) : string.Empty);

    /// <summary>The agent user's own calendar between two instants, earliest first.</summary>
    public static string CalendarView(DateTimeOffset start, DateTimeOffset end) =>
        $"/me/calendarView?startDateTime={Utc(start)}&endDateTime={Utc(end)}&$select={EventFields}&$orderby=start/dateTime&$top=50";

    /// <summary>Resolves a OneDrive or SharePoint link (for example one in a meeting invite) to its drive item.</summary>
    public static string SharedItem(string url) => $"/shares/{EncodeSharingUrl(url)}/driveItem?$select={DriveItemFields}";

    public static string DriveChildren(string driveId, string itemId, int top) =>
        $"/drives/{Id(driveId)}/items/{Id(itemId)}/children?$select={DriveItemFields}&$top={top}";

    public static string DriveContent(string driveId, string itemId) => $"/drives/{Id(driveId)}/items/{Id(itemId)}/content";

    /// <summary>A file in the agent user's own OneDrive by path, e.g. <c>/Runbooks/runbook.md</c>.</summary>
    public static string MyDriveFileContent(string path)
    {
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(s => s is "." or ".."))
        {
            throw new ArgumentException($"'{path}' isn't a valid OneDrive path.", nameof(path));
        }

        return $"/me/drive/root:/{string.Join('/', segments.Select(Uri.EscapeDataString))}:/content";
    }

    /// <summary>Sharing-URL encoding for <c>/shares</c> paths: <c>u!</c> plus unpadded base64url of the URL.</summary>
    public static string EncodeSharingUrl(string url) =>
        "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(url)).TrimEnd('=').Replace('/', '_').Replace('+', '-');

    /// <summary>Turns an absolute <c>@odata.nextLink</c> into the relative path Work IQ expects.</summary>
    public static string? FromNextLink(string nextLink)
    {
        foreach (var root in GraphRoots)
        {
            if (nextLink.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                return nextLink[root.Length..];
            }
        }

        return nextLink.StartsWith('/') ? nextLink : null;
    }

    /// <summary>Rejects anything that could change the path (slashes, query, fragments) before it's placed in a URL.</summary>
    public static string Id(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeId().IsMatch(value))
        {
            throw new ArgumentException($"'{value}' isn't a valid ID.", nameof(value));
        }

        return value;
    }

    private static string Utc(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // Drive IDs look like b!N9ZyVo9o…; chat and channel IDs like 19:abc@thread.v2.
    [GeneratedRegex(@"^[A-Za-z0-9:@._\-=+%!]+$")]
    private static partial Regex SafeId();
}
