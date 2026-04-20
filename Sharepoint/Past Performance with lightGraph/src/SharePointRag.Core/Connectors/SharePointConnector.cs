using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using System.Runtime.CompilerServices;

namespace SharePointRag.Core.Connectors;

/// <summary>
/// Connector for Microsoft SharePoint Online document libraries.
///
/// Required properties:
///   SiteUrl          – full SharePoint site URL
///   LibraryName      – document library name (e.g. "Documents")
///   TenantId         – Entra ID tenant (falls back to GlobalGraph)
///   ClientId         – app registration client ID
///   ClientSecret     – app registration client secret
///
/// Optional properties:
///   AllowedExtensions – comma-separated list, e.g. ".pdf,.docx"
///   MaxFileSizeMb     – integer, default 50
///   RootFolderPath    – restrict crawl to a sub-folder
/// </summary>
public sealed class SharePointConnector : IDataSourceConnector
{
    private readonly DataSourceDefinition _def;
    private readonly GlobalGraphOptions   _globalGraph;
    private readonly ITextExtractor       _extractor;
    private readonly ILogger              _logger;
    private readonly GraphServiceClient   _graph;
    private readonly List<string>         _allowedExts;
    private readonly long                 _maxBytes;
    private readonly List<SourceRecord>   _buffer = [];

    public string DataSourceName => _def.Name;
    public DataSourceType ConnectorType => DataSourceType.SharePoint;

    public SharePointConnector(
        DataSourceDefinition def,
        GlobalGraphOptions globalGraph,
        ITextExtractor extractor,
        ILogger logger)
    {
        _def         = def;
        _globalGraph = globalGraph;
        _extractor   = extractor;
        _logger      = logger;

        var tenantId     = def.Get(SharePointProps.TenantId)     .IfEmpty(globalGraph.TenantId);
        var clientId     = def.Get(SharePointProps.ClientId)     .IfEmpty(globalGraph.ClientId);
        var clientSecret = def.Get(SharePointProps.ClientSecret) .IfEmpty(globalGraph.ClientSecret);

        _graph = new GraphServiceClient(
            new ClientSecretCredential(tenantId, clientId, clientSecret),
            ["https://graph.microsoft.com/.default"]);

        var extStr = def.Get(SharePointProps.AllowedExtensions);
        _allowedExts = string.IsNullOrEmpty(extStr)
            ? [".pdf", ".docx", ".pptx", ".xlsx", ".txt", ".md", ".html"]
            : [.. extStr.Split(',').Select(e => e.Trim().ToLowerInvariant())];

        _maxBytes = def.GetInt(SharePointProps.MaxFileSizeMb, 50) * 1024L * 1024L;
    }

    public async IAsyncEnumerable<SourceRecord> GetRecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (siteId, driveId) = await ResolveIdsAsync(ct);
        await foreach (var r in EnumerateDriveItemsAsync(siteId, driveId, null, ct))
            yield return r;
    }

    public async IAsyncEnumerable<SourceRecord> GetModifiedRecordsAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var (siteId, driveId) = await ResolveIdsAsync(ct);
        var deltaResponse = await _graph.Sites[siteId].Drives[driveId].Root
            .Delta.GetAsDeltaGetResponseAsync(cancellationToken: ct);

        var iter = PageIterator<DriveItem,
            Microsoft.Graph.Drives.Item.Root.Delta.DeltaGetResponse>.CreatePageIterator(
            _graph, deltaResponse!, item =>
            {
                if (item.File != null && IsAllowed(item) && item.LastModifiedDateTime >= since)
                    _buffer.Add(ToSourceRecord(item, string.Empty));
                return true;
            });

        await iter.IterateAsync(ct);
        foreach (var r in _buffer) yield return r;
        _buffer.Clear();
    }

    public async Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        try
        {
            await ResolveIdsAsync(ct);
            return $"Connected to {_def.Get(SharePointProps.SiteUrl)}/{_def.Get(SharePointProps.LibraryName)}";
        }
        catch (Exception ex)
        {
            return $"Connection failed: {ex.Message}";
        }
    }

    private async Task<(string SiteId, string DriveId)> ResolveIdsAsync(CancellationToken ct)
    {
        var siteUrl     = _def.Get(SharePointProps.SiteUrl);
        var libraryName = _def.Get(SharePointProps.LibraryName, "Documents");
        var uri         = new Uri(siteUrl);
        var hostPath    = $"{uri.Host}:{uri.AbsolutePath}";

        var site   = await _graph.Sites[hostPath].GetAsync(cancellationToken: ct)
                     ?? throw new InvalidOperationException($"Site not found: {siteUrl}");
        var drives = await _graph.Sites[site.Id].Drives.GetAsync(cancellationToken: ct);
        var drive  = drives?.Value?.FirstOrDefault(d =>
                         string.Equals(d.Name, libraryName, StringComparison.OrdinalIgnoreCase))
                     ?? throw new InvalidOperationException($"Library '{libraryName}' not found.");

        return (site.Id!, drive.Id!);
    }

    private async IAsyncEnumerable<SourceRecord> EnumerateDriveItemsAsync(
        string siteId, string driveId, string? folderId,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var resp = folderId == null
            ? await _graph.Sites[siteId].Drives[driveId].Root.Children
                .GetAsync(r => r.QueryParameters.Top = 200, ct)
            : await _graph.Sites[siteId].Drives[driveId].Items[folderId].Children
                .GetAsync(r => r.QueryParameters.Top = 200, ct);

        var iter = PageIterator<DriveItem, DriveItemCollectionResponse>.CreatePageIterator(
            _graph, resp!, async item =>
            {
                if (item.Folder != null)
                    await foreach (var child in EnumerateDriveItemsAsync(siteId, driveId, item.Id, ct))
                        _buffer.Add(child);
                else if (item.File != null && IsAllowed(item))
                {
                    // Download and extract text inline
                    try
                    {
                        var stream = await _graph.Sites[siteId].Drives[driveId]
                            .Items[item.Id!].Content.GetAsync(cancellationToken: ct);
                        if (stream != null)
                        {
                            var text = await _extractor.ExtractAsync(
                                stream, item.File.MimeType ?? "application/octet-stream", item.Name ?? "", ct);
                            _buffer.Add(ToSourceRecord(item, text));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[{Src}] Could not extract {Name}", _def.Name, item.Name);
                    }
                }
                return true;
            });

        await iter.IterateAsync(ct);
        foreach (var r in _buffer) yield return r;
        _buffer.Clear();
    }

    private bool IsAllowed(DriveItem item)
    {
        if (item.Size > _maxBytes) return false;
        if (_allowedExts.Count == 0) return true;
        var ext = Path.GetExtension(item.Name ?? "").ToLowerInvariant();
        return _allowedExts.Contains(ext);
    }

    private SourceRecord ToSourceRecord(DriveItem item, string extractedText) => new()
    {
        Id           = item.Id!,
        Title        = item.Name ?? "unknown",
        Url          = item.WebUrl ?? string.Empty,
        Author       = item.LastModifiedBy?.User?.DisplayName,
        LastModified = item.LastModifiedDateTime ?? DateTimeOffset.UtcNow,
        MimeType     = item.File?.MimeType ?? "text/plain",
        Content      = extractedText,
        DataSourceName = _def.Name,
        Metadata     = new Dictionary<string, string>
        {
            ["LibraryPath"]  = item.ParentReference?.Path ?? "/",
            ["DriveItemId"]  = item.Id ?? string.Empty
        }
    };
}

public sealed class SharePointConnectorFactory(
    GlobalGraphOptions globalGraph,
    ITextExtractor extractor,
    ILogger<SharePointConnector> logger) : IDataSourceConnectorFactory
{
    public bool CanCreate(DataSourceType type) => type == DataSourceType.SharePoint;
    public IDataSourceConnector Create(DataSourceDefinition def) =>
        new SharePointConnector(def, globalGraph, extractor, logger);
}

internal static class StringExtensions
{
    public static string IfEmpty(this string s, string fallback) =>
        string.IsNullOrEmpty(s) ? fallback : s;
}
