using UglyToad.PdfPig;

namespace SF1449ContractManager.Core.Extraction;

/// <summary>One word's bounding box in PDF point-space (origin bottom-left, per PdfPig convention).</summary>
public record PdfWordBox(string Text, double Left, double Bottom, double Right, double Top);

/// <summary>Everything pulled from one page: linear text (for the LLM prompt) plus word geometry (for highlighting).</summary>
public record PdfPageContent(int PageNumber, string Text, double Width, double Height, IReadOnlyList<PdfWordBox> Words);

/// <summary>
/// Wraps PdfPig to pull raw text AND word-level geometry out of a "fully executed"
/// SF-1449 PDF. The text feeds the AI extraction prompt; the geometry lets
/// <see cref="FieldLocator"/> figure out where on the page each extracted value sits
/// so the review screen can draw a highlight box directly on the PDF (like the
/// side-panel field list, but on the document itself). If a solicitation is a
/// scanned image with no text layer, run it through an OCR step first (e.g. Azure
/// AI Document Intelligence / Tesseract) that also emits word bounding boxes, and
/// adapt this class to read that output instead.
/// </summary>
public class PdfTextExtractor
{
    public IReadOnlyList<PdfPageContent> ExtractPages(string filePath)
    {
        using var document = PdfDocument.Open(filePath);
        return ExtractPages(document);
    }

    public IReadOnlyList<PdfPageContent> ExtractPages(Stream pdfStream)
    {
        using var document = PdfDocument.Open(pdfStream);
        return ExtractPages(document);
    }

    private static IReadOnlyList<PdfPageContent> ExtractPages(PdfDocument document)
    {
        var pages = new List<PdfPageContent>();

        foreach (var page in document.GetPages())
        {
            var words = page.GetWords()
                .Select(w => new PdfWordBox(
                    w.Text,
                    w.BoundingBox.Left,
                    w.BoundingBox.Bottom,
                    w.BoundingBox.Right,
                    w.BoundingBox.Top))
                .ToList();

            pages.Add(new PdfPageContent(page.Number, page.Text, page.Width, page.Height, words));
        }

        return pages;
    }

    /// <summary>Concatenates all pages with a `[[PAGE n]]` marker so the LLM can cite source pages.</summary>
    public string BuildTaggedFullText(IReadOnlyList<PdfPageContent> pages)
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
