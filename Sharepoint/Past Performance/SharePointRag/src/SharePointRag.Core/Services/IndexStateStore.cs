using Microsoft.Extensions.Logging;
using SharePointRag.Core.Interfaces;
using SharePointRag.Core.Models;
using System.Collections.Concurrent;
using System.Text.Json;

namespace SharePointRag.Core.Services;

/// <summary>
/// Lightweight file-based state store for local development.
/// Replace with Azure Table Storage / Cosmos DB for production scale.
/// </summary>
public sealed class JsonFileIndexStateStore(
    string stateFilePath,
    ILogger<JsonFileIndexStateStore> logger) : IIndexStateStore
{
    private readonly ConcurrentDictionary<string, IndexingRecord> _records = new();
    private DateTimeOffset? _lastFullIndex;
    private bool _loaded;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IndexingRecord?> GetAsync(string driveItemId, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        _records.TryGetValue(driveItemId, out var rec);
        return rec;
    }

    public async Task SaveAsync(IndexingRecord record, CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        _records[record.DriveItemId] = record;
        await PersistAsync(ct);
    }

    public async Task<DateTimeOffset?> GetLastFullIndexTimeAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct);
        return _lastFullIndex;
    }

    public async Task SetLastFullIndexTimeAsync(DateTimeOffset time, CancellationToken ct = default)
    {
        _lastFullIndex = time;
        await PersistAsync(ct);
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded) return;
        await _lock.WaitAsync(ct);
        try
        {
            if (_loaded) return;
            if (File.Exists(stateFilePath))
            {
                await using var fs = File.OpenRead(stateFilePath);
                var envelope = await JsonSerializer.DeserializeAsync<StateEnvelope>(fs, cancellationToken: ct);
                if (envelope is not null)
                {
                    _lastFullIndex = envelope.LastFullIndex;
                    foreach (var r in envelope.Records)
                        _records[r.DriveItemId] = r;
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
            var envelope = new StateEnvelope
            {
                LastFullIndex = _lastFullIndex,
                Records = [.. _records.Values]
            };
            var dir = Path.GetDirectoryName(stateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await using var fs = File.Create(stateFilePath);
            await JsonSerializer.SerializeAsync(fs, envelope, cancellationToken: ct);
        }
        finally { _lock.Release(); }
    }

    private sealed class StateEnvelope
    {
        public DateTimeOffset? LastFullIndex { get; set; }
        public List<IndexingRecord> Records { get; set; } = [];
    }
}
