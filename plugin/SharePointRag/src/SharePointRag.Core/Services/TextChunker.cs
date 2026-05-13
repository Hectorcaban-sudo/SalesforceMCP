using Microsoft.Extensions.Options;
using SharePointRag.Core.Configuration;
using SharePointRag.Core.Interfaces;

namespace SharePointRag.Core.Services;

/// <summary>
/// Splits text into overlapping windows measured in approximate tokens
/// (characters / 4 is a close approximation for English text).
/// For production, replace the character heuristic with a tiktoken-based
/// tokenizer such as the SharpToken NuGet package.
/// </summary>
public sealed class TextChunker(IOptions<ChunkingOptions> opts) : ITextChunker
{
    private readonly ChunkingOptions _opts = opts.Value;

    // Rough token → character multiplier
    private const int CharsPerToken = 4;

    public IReadOnlyList<string> Chunk(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];

        int maxChars = _opts.MaxTokensPerChunk * CharsPerToken;
        int overlapChars = _opts.OverlapTokens * CharsPerToken;

        var chunks = new List<string>();
        int start = 0;

        while (start < text.Length)
        {
            int end = Math.Min(start + maxChars, text.Length);

            // Try to break at a sentence boundary
            if (end < text.Length)
            {
                int breakAt = FindSentenceBreak(text, start, end);
                if (breakAt > start) end = breakAt;
            }

            chunks.Add(text[start..end].Trim());
            if (end >= text.Length) break;

            start = Math.Max(start + 1, end - overlapChars);
        }

        return chunks.Where(c => c.Length > 0).ToList();
    }

    private static int FindSentenceBreak(string text, int start, int end)
    {
        // Walk backwards from end looking for ". ", "! ", "? ", "\n\n"
        for (int i = end; i > start + (end - start) / 2; i--)
        {
            char c = text[i - 1];
            if ((c is '.' or '!' or '?') && i < text.Length && text[i] == ' ')
                return i;
            if (i >= 2 && text[i - 2] == '\n' && text[i - 1] == '\n')
                return i;
        }
        return end;
    }
}
