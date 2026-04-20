using Microsoft.Extensions.Logging;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;
using System.Runtime.CompilerServices;

namespace SharePointRag.Core.Connectors;

/// <summary>
/// Connector for local or network .xlsx / .csv files.
/// Uses ClosedXML for Excel and built-in CSV parsing for comma-separated files.
///
/// Required properties:
///   FilePaths      – comma-separated glob patterns or absolute paths
///                    e.g. "/data/contracts/*.xlsx,/data/projects.csv"
///
/// Optional properties:
///   SheetName      – Excel sheet name (empty = first sheet)
///   ContentColumn  – column name or 0-based index to embed (default: all columns joined)
///   TitleColumn    – column for display title
///   UrlColumn      – column for deep-link URL
///   HeaderRow      – 0-based row index of header (default 0)
///
/// Example appsettings entry:
/// {
///   "Name": "ContractMatrix",
///   "Type": "Excel",
///   "Properties": {
///     "FilePaths":     "/mnt/data/contracts/*.xlsx",
///     "SheetName":     "Contracts",
///     "ContentColumn": "Description",
///     "TitleColumn":   "ContractNumber",
///     "UrlColumn":     "SharePointLink"
///   }
/// }
/// </summary>
public sealed class ExcelConnector : IDataSourceConnector
{
    private readonly DataSourceDefinition _def;
    private readonly ILogger              _logger;

    public string DataSourceName => _def.Name;
    public DataSourceType ConnectorType => DataSourceType.Excel;

    public ExcelConnector(DataSourceDefinition def, ILogger logger)
    {
        _def    = def;
        _logger = logger;
    }

    public async IAsyncEnumerable<SourceRecord> GetRecordsAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var file in ResolveFiles())
        {
            _logger.LogDebug("[{Src}] Reading {File}", _def.Name, file);

            await foreach (var r in ReadFileAsync(file, ct))
                yield return r;
        }
    }

    public IAsyncEnumerable<SourceRecord> GetModifiedRecordsAsync(
        DateTimeOffset since, CancellationToken ct = default)
    {
        // Excel files don't have row-level change tracking.
        // Filter at file level by last write time.
        return GetRecordsFromModifiedFilesAsync(since, ct);
    }

    public Task<string> TestConnectionAsync(CancellationToken ct = default)
    {
        var files = ResolveFiles().ToList();
        return Task.FromResult(files.Count == 0
            ? "No matching files found for the configured FilePaths."
            : $"Found {files.Count} file(s): {string.Join(", ", files.Select(Path.GetFileName))}");
    }

    private async IAsyncEnumerable<SourceRecord> GetRecordsFromModifiedFilesAsync(
        DateTimeOffset since,
        [EnumeratorCancellation] CancellationToken ct)
    {
        foreach (var file in ResolveFiles())
        {
            if (File.GetLastWriteTimeUtc(file) < since.UtcDateTime) continue;
            await foreach (var r in ReadFileAsync(file, ct))
                yield return r;
        }
    }

    private IEnumerable<string> ResolveFiles()
    {
        var patterns = _def.Get(ExcelProps.FilePaths)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var pattern in patterns)
        {
            var dir  = Path.GetDirectoryName(pattern) ?? ".";
            var glob = Path.GetFileName(pattern);

            if (!Directory.Exists(dir))
            {
                _logger.LogWarning("[{Src}] Directory not found: {Dir}", _def.Name, dir);
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(dir, glob, SearchOption.AllDirectories))
                yield return file;
        }
    }

    private async IAsyncEnumerable<SourceRecord> ReadFileAsync(
        string filePath,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".csv")
        {
            await foreach (var r in ReadCsvAsync(filePath, ct))
                yield return r;
        }
        else if (ext is ".xlsx" or ".xls")
        {
            await foreach (var r in ReadExcelAsync(filePath, ct))
                yield return r;
        }
        else
        {
            _logger.LogWarning("[{Src}] Unsupported file extension: {Ext}", _def.Name, ext);
        }
    }

    // ── CSV reader (no external package needed) ───────────────────────────────

    private async IAsyncEnumerable<SourceRecord> ReadCsvAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(path);
        string? headerLine = await reader.ReadLineAsync(ct);
        if (headerLine == null) yield break;

        var headers    = ParseCsvLine(headerLine);
        var contentIdx = ResolveColumnIndex(headers, _def.Get(ExcelProps.ContentColumn), -1);
        var titleIdx   = ResolveColumnIndex(headers, _def.Get(ExcelProps.TitleColumn),   -1);
        var urlIdx     = ResolveColumnIndex(headers, _def.Get(ExcelProps.UrlColumn),      -1);

        int rowNum = 1;
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            ct.ThrowIfCancellationRequested();
            var cols    = ParseCsvLine(line);
            var content = contentIdx >= 0 && contentIdx < cols.Count
                ? cols[contentIdx]
                : string.Join(" | ", cols);

            if (string.IsNullOrWhiteSpace(content)) { rowNum++; continue; }

            var title = titleIdx >= 0 && titleIdx < cols.Count ? cols[titleIdx] : $"Row {rowNum}";
            var url   = urlIdx   >= 0 && urlIdx   < cols.Count ? cols[urlIdx]   : string.Empty;

            var meta = headers.Select((h, i) => (h, v: i < cols.Count ? cols[i] : ""))
                              .ToDictionary(x => x.h, x => x.v);

            yield return new SourceRecord
            {
                Id             = $"{_def.Name}::{Path.GetFileNameWithoutExtension(path)}::row{rowNum}",
                Title          = title,
                Url            = url,
                Content        = content,
                LastModified   = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                DataSourceName = _def.Name,
                Metadata       = meta
            };
            rowNum++;
        }
    }

    // ── Excel reader (uses ClosedXML if installed, falls back to CSV stub) ────

    private async IAsyncEnumerable<SourceRecord> ReadExcelAsync(
        string path, [EnumeratorCancellation] CancellationToken ct)
    {
        // ClosedXML dynamic invocation — avoids hard package reference.
        // Install: dotnet add package ClosedXML
        var closedXmlType = Type.GetType("ClosedXML.Excel.XLWorkbook, ClosedXML");
        if (closedXmlType is null)
        {
            _logger.LogWarning(
                "[{Src}] ClosedXML package not installed. Add 'dotnet add package ClosedXML' " +
                "to read Excel files. Skipping {File}.", _def.Name, path);
            yield break;
        }

        object workbook;
        try
        {
            workbook = Activator.CreateInstance(closedXmlType, path)!;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Src}] Failed to open workbook {File}", _def.Name, path);
            yield break;
        }

        using var wb = (IDisposable)workbook;

        // Resolve the target worksheet
        var sheetName   = _def.Get(ExcelProps.SheetName);
        var worksheets  = (System.Collections.IEnumerable)closedXmlType
            .GetProperty("Worksheets")!.GetValue(workbook)!;

        object? ws = null;
        foreach (var sheet in worksheets)
        {
            var name = sheet.GetType().GetProperty("Name")!.GetValue(sheet)!.ToString();
            if (string.IsNullOrEmpty(sheetName) || name == sheetName) { ws = sheet; break; }
        }

        if (ws is null)
        {
            _logger.LogWarning("[{Src}] Sheet '{Sheet}' not found in {File}", _def.Name, sheetName, path);
            yield break;
        }

        var rowsUsed = ws.GetType().GetMethod("RowsUsed", [])!.Invoke(ws, null)
            as System.Collections.IEnumerable;
        if (rowsUsed is null) yield break;

        List<string>? headers = null;
        int headerRow  = _def.GetInt(ExcelProps.HeaderRow, 0);
        int rowNum     = 0;

        foreach (var row in rowsUsed)
        {
            ct.ThrowIfCancellationRequested();
            var cells = row.GetType().GetMethod("Cells", [])!.Invoke(row, null)
                as System.Collections.IEnumerable;
            if (cells is null) { rowNum++; continue; }

            var vals = new List<string>();
            foreach (var cell in cells)
            {
                var val = cell.GetType().GetProperty("Value")!.GetValue(cell);
                vals.Add(val?.ToString() ?? string.Empty);
            }

            if (rowNum == headerRow) { headers = vals; rowNum++; continue; }
            if (headers is null) { rowNum++; continue; }

            var contentIdx = ResolveColumnIndex(headers, _def.Get(ExcelProps.ContentColumn), -1);
            var titleIdx   = ResolveColumnIndex(headers, _def.Get(ExcelProps.TitleColumn),   -1);
            var urlIdx     = ResolveColumnIndex(headers, _def.Get(ExcelProps.UrlColumn),      -1);

            var content = contentIdx >= 0 && contentIdx < vals.Count
                ? vals[contentIdx]
                : string.Join(" | ", vals);

            if (string.IsNullOrWhiteSpace(content)) { rowNum++; continue; }

            var title = titleIdx >= 0 && titleIdx < vals.Count ? vals[titleIdx] : $"Row {rowNum}";
            var url   = urlIdx   >= 0 && urlIdx   < vals.Count ? vals[urlIdx]   : string.Empty;

            var meta = headers.Select((h, i) => (h, v: i < vals.Count ? vals[i] : ""))
                              .ToDictionary(x => x.h, x => x.v);

            yield return new SourceRecord
            {
                Id             = $"{_def.Name}::{Path.GetFileNameWithoutExtension(path)}::row{rowNum}",
                Title          = title,
                Url            = url,
                Content        = content,
                LastModified   = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero),
                DataSourceName = _def.Name,
                Metadata       = meta
            };
            rowNum++;
        }

        await Task.CompletedTask; // satisfy async enumerable
    }

    private static int ResolveColumnIndex(List<string> headers, string nameOrIndex, int fallback)
    {
        if (string.IsNullOrEmpty(nameOrIndex)) return fallback;
        if (int.TryParse(nameOrIndex, out var idx)) return idx;
        for (int i = 0; i < headers.Count; i++)
            if (headers[i].Equals(nameOrIndex, StringComparison.OrdinalIgnoreCase)) return i;
        return fallback;
    }

    private static List<string> ParseCsvLine(string line)
    {
        var result = new List<string>();
        bool inQuotes = false;
        var current = new System.Text.StringBuilder();

        foreach (char c in line)
        {
            if (c == '"') { inQuotes = !inQuotes; }
            else if (c == ',' && !inQuotes) { result.Add(current.ToString()); current.Clear(); }
            else { current.Append(c); }
        }
        result.Add(current.ToString());
        return result;
    }
}

public sealed class ExcelConnectorFactory(
    ILogger<ExcelConnector> logger) : IDataSourceConnectorFactory
{
    public bool CanCreate(DataSourceType type) => type == DataSourceType.Excel;
    public IDataSourceConnector Create(DataSourceDefinition def) =>
        new ExcelConnector(def, logger);
}
