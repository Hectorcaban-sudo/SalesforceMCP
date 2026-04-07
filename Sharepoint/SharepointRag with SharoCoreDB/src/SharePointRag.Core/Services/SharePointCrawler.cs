using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Runtime.CompilerServices;

namespace SharePointRag.Core.Services;

/// <summary>
/// Crawls a SharePoint document library using Microsoft Graph, yielding files
/// with automatic paging and parallelism control.
/// </summary>
public sealed class SharePointCrawler(
    GraphServiceClient graph,
    IOptions<SharePointOptions> opts,
    ILogger<SharePointCrawler> logger) : ISharePointCrawler
{
    private readonly SharePointOptions _opts = opts.Value;

    // ── Public API ────────────────────────────────────────────────────────────

    public async IAsyncEnumerable<SharePointFile> GetFilesAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (siteId, driveId) = await ResolveIdsAsync(ct);
        await foreach (var f in EnumerateDriveItemsAsync(siteId, driveId, null, ct))
            yield return f;
    }

    public async IAsyncEnumerable<SharePointFile> GetModifiedFilesAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (siteId, driveId) = await ResolveIdsAsync(ct);
        // Use Graph delta query for efficient change tracking
        await foreach (var f in EnumerateDeltaAsync(siteId, driveId, since, ct))
            yield return f;
    }

    public async Task<Stream> DownloadFileAsync(SharePointFile file, CancellationToken ct = default)
    {
        var (siteId, driveId) = await ResolveIdsAsync(ct);
        var stream = await graph.Sites[siteId]
            .Drives[driveId]
            .Items[file.DriveItemId]
            .Content
            .GetAsync(cancellationToken: ct);

        return stream ?? throw new InvalidOperationException($"No content stream for {file.Name}");
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<(string SiteId, string DriveId)> ResolveIdsAsync(CancellationToken ct)
    {
        var uri = new Uri(_opts.SiteUrl);
        var hostAndPath = $"{uri.Host}:{uri.AbsolutePath}";

        var site = await graph.Sites[hostAndPath].GetAsync(cancellationToken: ct)
                   ?? throw new InvalidOperationException($"Site not found: {_opts.SiteUrl}");

        var drives = await graph.Sites[site.Id].Drives.GetAsync(cancellationToken: ct);
        var drive = drives?.Value?.FirstOrDefault(d =>
                        string.Equals(d.Name, _opts.LibraryName, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Library '{_opts.LibraryName}' not found.");

        return (site.Id!, drive.Id!);
    }

    private async IAsyncEnumerable<SharePointFile> EnumerateDriveItemsAsync(
        string siteId, string driveId, string? folderId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var itemsResponse = folderId == null
            ? await graph.Sites[siteId].Drives[driveId].Root.Children
                .GetAsync(r => r.QueryParameters.Top = 200, ct)
            : await graph.Sites[siteId].Drives[driveId].Items[folderId].Children
                .GetAsync(r => r.QueryParameters.Top = 200, ct);

        var pageIterator = PageIterator<DriveItem, DriveItemCollectionResponse>.CreatePageIterator(
            graph, itemsResponse!, async item =>
            {
                if (item.Folder != null)
                {
                    // Recurse into sub-folders (breadth-first via iterator)
                    await foreach (var child in EnumerateDriveItemsAsync(siteId, driveId, item.Id, ct))
                        // We can't yield inside a lambda, so we buffer here
                        _buffer.Add(child);
                }
                else if (item.File != null && IsAllowed(item))
                {
                    _buffer.Add(ToSharePointFile(item));
                }
                return true;
            });

        await pageIterator.IterateAsync(ct);

        foreach (var f in _buffer) yield return f;
        _buffer.Clear();
    }

    // Small buffer for items collected inside the PageIterator lambda
    private readonly List<SharePointFile> _buffer = [];

    private async IAsyncEnumerable<SharePointFile> EnumerateDeltaAsync(
        string siteId, string driveId, DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Graph delta returns ALL items; we filter by lastModified client-side
        var deltaResponse = await graph.Sites[siteId].Drives[driveId].Root
            .Delta.GetAsDeltaGetResponseAsync(cancellationToken: ct);

        var pageIterator = PageIterator<DriveItem, Microsoft.Graph.Drives.Item.Root.Delta.DeltaGetResponse>.CreatePageIterator(
            graph, deltaResponse!, item =>
            {
                if (item.File != null && IsAllowed(item)
                    && item.LastModifiedDateTime >= since)
                    _buffer.Add(ToSharePointFile(item));
                return true;
            });

        await pageIterator.IterateAsync(ct);

        foreach (var f in _buffer) yield return f;
        _buffer.Clear();
    }

    private bool IsAllowed(DriveItem item)
    {
        if (item.Size > _opts.MaxFileSizeMb * 1024L * 1024L)
        {
            logger.LogDebug("Skipping {Name} – exceeds size limit", item.Name);
            return false;
        }
        if (_opts.AllowedExtensions.Count == 0) return true;
        var ext = Path.GetExtension(item.Name ?? "").ToLowerInvariant();
        return _opts.AllowedExtensions.Contains(ext);
    }

    private static SharePointFile ToSharePointFile(DriveItem item) => new(
        DriveItemId: item.Id!,
        Name: item.Name ?? "unknown",
        WebUrl: item.WebUrl ?? string.Empty,
        MimeType: item.File?.MimeType ?? "application/octet-stream",
        SizeBytes: item.Size ?? 0,
        LastModified: item.LastModifiedDateTime ?? DateTimeOffset.UtcNow,
        LibraryPath: item.ParentReference?.Path ?? "/",
        Author: item.LastModifiedBy?.User?.DisplayName
    );
}
