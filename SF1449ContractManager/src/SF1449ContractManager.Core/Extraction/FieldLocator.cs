using SF1449ContractManager.Core.Models;

namespace SF1449ContractManager.Core.Extraction;

/// <summary>
/// Runs after <see cref="Sf1449ExtractionAgent"/>. The LLM tells us WHAT it read and
/// WHICH page it came from, but not WHERE on the page - LLMs don't reliably reason
/// about pixel/point coordinates. This does a text-matching pass against the page's
/// actual word geometry (from PdfPig) to find that location, so the review screen can
/// draw a highlight box on the PDF itself instead of only listing the field in a side
/// panel.
///
/// This is a heuristic (sliding-window substring match over normalized word text), not
/// a guarantee - short/common values (e.g. a bare "X" checkbox mark) or values that
/// don't appear verbatim on the page (summaries, inferred booleans) won't resolve to a
/// box, and that's fine: <see cref="FieldExtraction.HasBoundingBox"/> is false and the
/// UI just falls back to "click to jump to page" without a drawn box.
/// </summary>
public class FieldLocator
{
    /// <summary>Mutates each FieldExtraction on the contract in place, setting Box*Pct where a match is found.</summary>
    public void LocateFields(Sf1449Contract contract, IReadOnlyList<PdfPageContent> pages)
    {
        var pagesByNumber = pages.ToDictionary(p => p.PageNumber);

        foreach (var field in contract.FieldExtractions)
        {
            if (field.SourcePageNumber is not { } pageNumber) continue;
            if (!pagesByNumber.TryGetValue(pageNumber, out var page)) continue;
            if (string.IsNullOrWhiteSpace(field.ExtractedValueRaw)) continue;

            var box = TryLocate(field.ExtractedValueRaw!, page);
            if (box is null) continue;

            field.BoxLeftPct = box.Value.LeftPct;
            field.BoxTopPct = box.Value.TopPct;
            field.BoxWidthPct = box.Value.WidthPct;
            field.BoxHeightPct = box.Value.HeightPct;
        }
    }

    private readonly record struct PctBox(double LeftPct, double TopPct, double WidthPct, double HeightPct);

    private static PctBox? TryLocate(string rawValue, PdfPageContent page)
    {
        var targetTokens = Tokenize(rawValue);
        if (targetTokens.Count == 0 || page.Words.Count == 0) return null;

        var targetJoined = string.Concat(targetTokens);
        if (targetJoined.Length < 2) return null; // too short to reliably match, avoid false positives

        var normalizedWords = page.Words.Select(w => Normalize(w.Text)).ToArray();
        var maxWindow = Math.Min(targetTokens.Count + 2, 12); // a little slack for tokens the LLM merged/split differently than the PDF's word boundaries

        for (var start = 0; start < page.Words.Count; start++)
        {
            if (normalizedWords[start].Length == 0) continue;

            var candidate = string.Empty;
            for (var len = 1; len <= maxWindow && start + len <= page.Words.Count; len++)
            {
                candidate += normalizedWords[start + len - 1];

                if (candidate.Length >= targetJoined.Length &&
                    (candidate.Contains(targetJoined, StringComparison.Ordinal) ||
                     IsCloseMatch(candidate, targetJoined)))
                {
                    return BuildBox(page, start, len);
                }

                // Growing window is already well past the target length with no match - stop
                // extending from this start index and move on.
                if (candidate.Length > targetJoined.Length + 20) break;
            }
        }

        return null;
    }

    /// <summary>Cheap near-match: target is (almost) fully contained even allowing for a couple of stray/misread characters.</summary>
    private static bool IsCloseMatch(string candidate, string target)
    {
        if (target.Length < 4) return false; // too risky for very short values
        var window = candidate.Length > target.Length + 4
            ? candidate[..(target.Length + 4)]
            : candidate;

        var matches = 0;
        var minLen = Math.Min(window.Length, target.Length);
        for (var i = 0; i < minLen; i++)
        {
            if (window[i] == target[i]) matches++;
        }
        return minLen > 0 && matches / (double)target.Length >= 0.85;
    }

    private static PctBox BuildBox(PdfPageContent page, int startWordIndex, int wordCount)
    {
        var slice = page.Words.Skip(startWordIndex).Take(wordCount).ToList();
        var left = slice.Min(w => w.Left);
        var right = slice.Max(w => w.Right);
        var top = slice.Max(w => w.Top);       // PdfPig: larger Y = higher on the page
        var bottom = slice.Min(w => w.Bottom);

        const double padFraction = 0.0035; // small visual padding around the matched text

        var leftPct = Clamp01(left / page.Width - padFraction);
        var rightPct = Clamp01(right / page.Width + padFraction);
        var topPct = Clamp01((page.Height - top) / page.Height - padFraction);   // convert to top-left origin
        var bottomPct = Clamp01((page.Height - bottom) / page.Height + padFraction);

        return new PctBox(leftPct, topPct, Math.Max(0, rightPct - leftPct), Math.Max(0, bottomPct - topPct));
    }

    private static double Clamp01(double v) => Math.Min(1, Math.Max(0, v));

    private static List<string> Tokenize(string value) =>
        value.Split(new[] { ' ', '\t', '\n', '\r', ',', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries)
             .Select(Normalize)
             .Where(t => t.Length > 0)
             .ToList();

    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
