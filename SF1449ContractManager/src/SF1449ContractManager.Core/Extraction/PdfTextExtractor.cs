using UglyToad.PdfPig;

namespace SF1449ContractManager.Core.Extraction;

public record PdfPageText(int PageNumber, string Text);

/// <summary>
/// Wraps PdfPig to pull raw text per page out of a "fully executed" SF-1449 PDF.
/// This is intentionally dumb (no layout/OCR reconstruction) - the AI extraction
/// agent is responsible for interpreting the linearized text into structured
/// fields. If a solicitation is a scanned image with no text layer, run it through
/// an OCR step first (e.g. Azure AI Document Intelligence / Tesseract) and feed the
/// resulting text into <see cref="Sf1449ExtractionAgent"/> the same way.
/// </summary>
public class PdfTextExtractor
{
    public IReadOnlyList<PdfPageText> ExtractPages(string filePath)
    {
        using var document = PdfDocument.Open(filePath);
        var pages = new List<PdfPageText>();

        foreach (var page in document.GetPages())
        {
            pages.Add(new PdfPageText(page.Number, page.Text));
        }

        return pages;
    }

    public IReadOnlyList<PdfPageText> ExtractPages(Stream pdfStream)
    {
        using var document = PdfDocument.Open(pdfStream);
        var pages = new List<PdfPageText>();

        foreach (var page in document.GetPages())
        {
            pages.Add(new PdfPageText(page.Number, page.Text));
        }

        return pages;
    }

    /// <summary>Concatenates all pages with a `[[PAGE n]]` marker so the LLM can cite source pages.</summary>
    public string BuildTaggedFullText(IReadOnlyList<PdfPageText> pages)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var page in pages)
        {
            sb.AppendLine($"[[PAGE {page.PageNumber}]]");
            sb.AppendLine(page.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
