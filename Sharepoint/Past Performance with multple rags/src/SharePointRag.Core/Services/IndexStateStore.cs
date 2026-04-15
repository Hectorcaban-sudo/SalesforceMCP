using Microsoft.Extensions.Logging;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SharePointRag.Core.Services;

/// <summary>
/// JSON-file-backed index state store scoped to a single named RAG system.
/// Stored at {DataDirectory}/{systemName}/index-state.json
/// Keys are "{libraryName}::{driveItemId}" to support the same file in multiple systems.
/// </summary>
public sealed class JsonFileIndexStateStore : IIndexStateStore
{
    private readonly string _stateFilePath;
    private readonly ILogger<JsonFileIndexStateStore> _logger;
    private readonly ConcurrentDictionary<string, IndexingRecord> _records = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastFull = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _loaded;

    public string SystemName { get; }

    public JsonFileIndexStateStore(
        string systemName,
        string dataDirectory,
        ILogger<JsonFileIndexStateStore> logger)
    {
        SystemName     = systemName;
        _stateFilePath = Path.Combine(dataDirectory, systemName, "index-state.json");
        _logger        = logger;
    }

    private static string Key(string driveItemId, string libraryName) =>
        $"{libraryName}::{driveItemId}";

    public async Task<IndexingRecord?> GetAsync(
        string driveItemId, string libraryName, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        _records.TryGetValue(Key(driveItemId, libraryName), out var rec);
        return rec;
    }

    public async Task SaveAsync(IndexingRecord record, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        _records[Key(record.DriveItemId, record.LibraryName)] = record;
        await PersistAsync(ct);
    }

    public async Task<DateTimeOffset?> GetLastFullIndexTimeAsync(
        string libraryName, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _lastFull.TryGetValue(libraryName, out var t) ? t : null;
    }

    public async Task SetLastFullIndexTimeAsync(
        string libraryName, DateTimeOffset time, CancellationToken ct = default)
    {
        _lastFull[libraryName] = time;
        await PersistAsync(ct);
    }

    public async Task<int> GetIndexedFileCountAsync(
        string libraryName, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _records.Values.Count(r =>
            r.LibraryName == libraryName && r.Status == IndexingStatus.Indexed);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            if (File.Exists(_stateFilePath))
            {
                await using var fs = File.OpenRead(_stateFilePath);
                var envelope = await JsonSerializer.DeserializeAsync<StateEnvelope>(fs, cancellationToken: ct);
                if (envelope is not null)
                {
                    foreach (var r in envelope.Records)
                        _records[Key(r.DriveItemId, r.LibraryName)] = r;
                    foreach (var kv in envelope.LastFullIndex)
                        _lastFull[kv.Key] = kv.Value;
                }
            }
            _loaded = true;
        }
        finally { _lock.Release(); }
    }

    private async Task PersistAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_stateFilePath)!);
            var envelope = new StateEnvelope
            {
                Records       = [.. _records.Values],
                LastFullIndex = new Dictionary<string, DateTimeOffset>(_lastFull)
            };
            await using var fs = File.Create(_stateFilePath);
            await JsonSerializer.SerializeAsync(fs, envelope, cancellationToken: ct);
        }
        finally { _lock.Release(); }
    }

    private sealed class StateEnvelope
    {
        public List<IndexingRecord> Records { get; set; } = [];
        public Dictionary<string, DateTimeOffset> LastFullIndex { get; set; } = [];
    }
}
