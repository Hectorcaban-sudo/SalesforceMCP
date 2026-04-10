using Microsoft.Extensions.Logging;
using SharePointRag.Core.Interfaces;

namespace SharePointRag.Core.Services;

/// <summary>
/// Dispatches to format-specific extractors.  Add new extractors by implementing
/// ITextExtractor and registering them in DI – no changes here needed.
/// </summary>
public sealed class CompositeTextExtractor(
    IEnumerable<ITextExtractor> extractors,
    ILogger<CompositeTextExtractor> logger) : ITextExtractor
{
    public bool CanHandle(string mimeType, string fileName) => true; // fallback

    public async Task<string> ExtractAsync(Stream content, string mimeType, string fileName, CancellationToken ct = default)
    {
        var extractor = extractors.FirstOrDefault(e => e.CanHandle(mimeType, fileName));
        if (extractor is null)
        {
            logger.LogWarning("No extractor for {MimeType} / {File} – skipping", mimeType, fileName);
            return string.Empty;
        }
        return await extractor.ExtractAsync(content, mimeType, fileName, ct);
    }
}

/// <summary>Plain text / markdown extractor.</summary>
public sealed class PlainTextExtractor : ITextExtractor
{
    private static readonly HashSet<string> _mimes =
    [
        "text/plain", "text/markdown", "text/csv",
        "application/json", "application/xml", "text/html"
    ];
    private static readonly HashSet<string> _exts =
        [".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm"];

    public bool CanHandle(string mimeType, string fileName)
        => _mimes.Contains(mimeType) || _exts.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    public async Task<string> ExtractAsync(Stream content, string mimeType, string fileName, CancellationToken ct = default)
    {
        using var reader = new StreamReader(content, leaveOpen: false);
        return await reader.ReadToEndAsync(ct);
    }
}

/// <summary>
/// DOCX extractor using DocumentFormat.OpenXml.
/// Install:  dotnet add package DocumentFormat.OpenXml
/// </summary>
public sealed class DocxExtractor : ITextExtractor
{
    public bool CanHandle(string mimeType, string fileName)
        => mimeType is "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
           || Path.GetExtension(fileName).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExtractAsync(Stream content, string mimeType, string fileName, CancellationToken ct = default)
    {
        // Avoid loading the whole assembly if OpenXml isn't installed.
        // Swap this block with real OpenXml code once the package is added.
        try
        {
            // DocumentFormat.OpenXml usage (add package reference to use):
            // using DocumentFormat.OpenXml.Packaging;
            // using DocumentFormat.OpenXml.Wordprocessing;
            // using var doc = WordprocessingDocument.Open(content, false);
            // var body = doc.MainDocumentPart?.Document?.Body;
            // return Task.FromResult(body?.InnerText ?? string.Empty);

            // ── Stub until package is referenced ──
            return Task.FromResult("[DOCX extraction requires DocumentFormat.OpenXml package]");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"[DOCX extraction failed: {ex.Message}]");
        }
    }
}

/// <summary>
/// PDF extractor using PdfPig.
/// Install:  dotnet add package PdfPig
/// </summary>
public sealed class PdfExtractor : ITextExtractor
{
    public bool CanHandle(string mimeType, string fileName)
        => mimeType is "application/pdf"
           || Path.GetExtension(fileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<string> ExtractAsync(Stream content, string mimeType, string fileName, CancellationToken ct = default)
    {
        try
        {
            // PdfPig usage (add package reference to use):
            // using UglyToad.PdfPig;
            // using var doc = PdfDocument.Open(content);
            // var sb = new System.Text.StringBuilder();
            // foreach (var page in doc.GetPages())
            //     sb.AppendLine(string.Join(" ", page.GetWords().Select(w => w.Text)));
            // return Task.FromResult(sb.ToString());

            return Task.FromResult("[PDF extraction requires PdfPig package]");
        }
        catch (Exception ex)
        {
            return Task.FromResult($"[PDF extraction failed: {ex.Message}]");
        }
    }
}
