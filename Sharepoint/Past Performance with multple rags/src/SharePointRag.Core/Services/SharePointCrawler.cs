using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Runtime.CompilerServices;

namespace SharePointRag.Core.Services;

/// <summary>
/// Crawls a single named SharePoint document library.
/// One instance is created per LibraryDefinition via the ILibraryRegistry.
/// </summary>
public sealed class SharePointCrawler : ISharePointCrawler
{
    private readonly LibraryDefinition _lib;
    private readonly GraphServiceClient _graph;
    private readonly ILogger<SharePointCrawler> _logger;
    private readonly List<SharePointFile> _buffer = [];

    public string LibraryName => _lib.Name;

    public SharePointCrawler(
        LibraryDefinition lib,
        GlobalGraphOptions globalGraph,
        ILogger<SharePointCrawler> logger)
    {
        _lib    = lib;
        _logger = logger;

        // Use library-specific credentials if provided; fall back to global
        var tenantId     = string.IsNullOrEmpty(lib.TenantId)     ? globalGraph.TenantId     : lib.TenantId;
        var clientId     = string.IsNullOrEmpty(lib.ClientId)     ? globalGraph.ClientId     : lib.ClientId;
        var clientSecret = string.IsNullOrEmpty(lib.ClientSecret) ? globalGraph.ClientSecret : lib.ClientSecret;

        var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
        _graph = new GraphServiceClient(credential, ["https://graph.microsoft.com/.default"]);
    }

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
        await foreach (var f in EnumerateDeltaAsync(siteId, driveId, since, ct))
            yield return f;
    }

    public async Task<Stream> DownloadFileAsync(SharePointFile file, CancellationToken ct = default)
    {
        var (siteId, driveId) = await ResolveIdsAsync(ct);
        var stream = await _graph.Sites[siteId]
            .Drives[driveId]
            .Items[file.DriveItemId]
            .Content
            .GetAsync(cancellationToken: ct);
        return stream ?? throw new InvalidOperationException($"No content stream for {file.Name}");
    }

    private async Task<(string SiteId, string DriveId)> ResolveIdsAsync(CancellationToken ct)
    {
        var uri         = new Uri(_lib.SiteUrl);
        var hostAndPath = $"{uri.Host}:{uri.AbsolutePath}";

        var site = await _graph.Sites[hostAndPath].GetAsync(cancellationToken: ct)
                   ?? throw new InvalidOperationException($"Site not found: {_lib.SiteUrl}");

        var drives = await _graph.Sites[site.Id].Drives.GetAsync(cancellationToken: ct);
        var drive  = drives?.Value?.FirstOrDefault(d =>
                         string.Equals(d.Name, _lib.LibraryName, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException(
                            $"Library '{_lib.LibraryName}' not found on site '{_lib.SiteUrl}'.");

        return (site.Id!, drive.Id!);
    }

    private async IAsyncEnumerable<SharePointFile> EnumerateDriveItemsAsync(
        string siteId, string driveId, string? folderId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var itemsResponse = folderId == null
            ? await _graph.Sites[siteId].Drives[driveId].Root.Children
                .GetAsync(r => r.QueryParameters.Top = 200, ct)
            : await _graph.Sites[siteId].Drives[driveId].Items[folderId].Children
                .GetAsync(r => r.QueryParameters.Top = 200, ct);

        var pageIterator = PageIterator<DriveItem, DriveItemCollectionResponse>.CreatePageIterator(
            _graph, itemsResponse!, async item =>
            {
                if (item.Folder != null)
                    await foreach (var child in EnumerateDriveItemsAsync(siteId, driveId, item.Id, ct))
                        _buffer.Add(child);
                else if (item.File != null && IsAllowed(item))
                    _buffer.Add(ToSharePointFile(item));
                return true;
            });

        await pageIterator.IterateAsync(ct);
        foreach (var f in _buffer) yield return f;
        _buffer.Clear();
    }

    private async IAsyncEnumerable<SharePointFile> EnumerateDeltaAsync(
        string siteId, string driveId, DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var deltaResponse = await _graph.Sites[siteId].Drives[driveId].Root
            .Delta.GetAsDeltaGetResponseAsync(cancellationToken: ct);

        var pageIterator = PageIterator<DriveItem,
            Microsoft.Graph.Drives.Item.Root.Delta.DeltaGetResponse>.CreatePageIterator(
            _graph, deltaResponse!, item =>
            {
                if (item.File != null && IsAllowed(item) && item.LastModifiedDateTime >= since)
                    _buffer.Add(ToSharePointFile(item));
                return true;
            });

        await pageIterator.IterateAsync(ct);
        foreach (var f in _buffer) yield return f;
        _buffer.Clear();
    }

    private bool IsAllowed(DriveItem item)
    {
        if (item.Size > _lib.MaxFileSizeMb * 1024L * 1024L)
        {
            _logger.LogDebug("[{Lib}] Skipping {Name} – exceeds size limit", _lib.Name, item.Name);
            return false;
        }
        if (_lib.AllowedExtensions.Count == 0) return true;
        var ext = Path.GetExtension(item.Name ?? "").ToLowerInvariant();
        return _lib.AllowedExtensions.Contains(ext);
    }

    private SharePointFile ToSharePointFile(DriveItem item) => new(
        DriveItemId:  item.Id!,
        Name:         item.Name ?? "unknown",
        WebUrl:       item.WebUrl ?? string.Empty,
        MimeType:     item.File?.MimeType ?? "application/octet-stream",
        SizeBytes:    item.Size ?? 0,
        LastModified: item.LastModifiedDateTime ?? DateTimeOffset.UtcNow,
        LibraryPath:  item.ParentReference?.Path ?? "/",
        Author:       item.LastModifiedBy?.User?.DisplayName,
        LibraryName:  _lib.Name
    );
}
