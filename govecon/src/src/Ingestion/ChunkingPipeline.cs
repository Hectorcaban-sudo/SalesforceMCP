// ============================================================
//  GovConRAG.Core — Chunking + Ingestion Pipeline
//  Strategies: Fixed | Paragraph | Semantic (token-aware)
// ============================================================

using GovConRAG.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using System.Text;
using System.Text.RegularExpressions;

namespace GovConRAG.Core.Ingestion;

// ── Chunking Strategy Contract ────────────────────────────────

public interface IChunkingStrategy
{
    string Name { get; }
    IEnumerable<DocumentChunk> Chunk(SourceDocument doc, string text);
}

// ── Fixed-Size Chunker ────────────────────────────────────────

public sealed class FixedSizeChunker : IChunkingStrategy
{
    private readonly int _chunkSize;
    private readonly int _overlap;

    public string Name => "fixed";

    public FixedSizeChunker(int chunkSize = 512, int overlap = 64)
    {
        _chunkSize = chunkSize;
        _overlap   = overlap;
    }

    public IEnumerable<DocumentChunk> Chunk(SourceDocument doc, string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int idx = 0, chunkIndex = 0;

        while (idx < words.Length)
        {
            var slice = words.Skip(idx).Take(_chunkSize).ToArray();
            var content = string.Join(" ", slice);

            yield return new DocumentChunk
            {
                DocumentId    = doc.Id,
                ChunkIndex    = chunkIndex++,
                Content       = content,
                TokenCount    = EstimateTokens(content),
                ChunkStrategy = Name,
                Metadata      = new Dictionary<string, string>
                {
                    ["wordStart"] = idx.ToString(),
                    ["wordEnd"]   = (idx + slice.Length).ToString()
                }
            };

            idx += _chunkSize - _overlap;
        }
    }

    private static int EstimateTokens(string text) => (int)(text.Length / 4.0);
}

// ── Paragraph Chunker ─────────────────────────────────────────

public sealed class ParagraphChunker : IChunkingStrategy
{
    private readonly int _maxTokens;
    public string Name => "paragraph";

    public ParagraphChunker(int maxTokens = 600) => _maxTokens = maxTokens;

    public IEnumerable<DocumentChunk> Chunk(SourceDocument doc, string text)
    {
        // Split on double newlines, headings, horizontal rules
        var paras = Regex.Split(text, @"\n\s*\n|\r\n\s*\r\n|(?=#{1,6}\s)")
            .Select(p => p.Trim())
            .Where(p => p.Length > 20)
            .ToList();

        var buffer = new StringBuilder();
        int chunkIndex = 0;

        foreach (var para in paras)
        {
            int tokenEst = EstimateTokens(buffer.ToString() + para);
            if (tokenEst > _maxTokens && buffer.Length > 0)
            {
                yield return MakeChunk(doc, buffer.ToString(), chunkIndex++);
                buffer.Clear();
            }
            if (buffer.Length > 0) buffer.Append("\n\n");
            buffer.Append(para);
        }

        if (buffer.Length > 0)
            yield return MakeChunk(doc, buffer.ToString(), chunkIndex);
    }

    private static DocumentChunk MakeChunk(SourceDocument doc, string content, int index) =>
        new()
        {
            DocumentId    = doc.Id,
            ChunkIndex    = index,
            Content       = content,
            TokenCount    = EstimateTokens(content),
            ChunkStrategy = "paragraph"
        };

    private static int EstimateTokens(string text) => (int)(text.Length / 4.0);
}

// ── Semantic Chunker ──────────────────────────────────────────
// Uses sentence boundaries + similarity grouping (lightweight).
// For full semantic chunking, provide an IEmbeddingGenerator.

public sealed class SemanticChunker : IChunkingStrategy
{
    private readonly int _targetTokens;
    private readonly float _similarityThreshold;

    public string Name => "semantic";

    public SemanticChunker(int targetTokens = 400, float similarityThreshold = 0.75f)
    {
        _targetTokens        = targetTokens;
        _similarityThreshold = similarityThreshold;
    }

    public IEnumerable<DocumentChunk> Chunk(SourceDocument doc, string text)
    {
        // Split into sentences
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+(?=[A-Z\d""])");
        var groups    = new List<List<string>>();
        var current   = new List<string>();
        int tokens    = 0;

        foreach (var sentence in sentences.Where(s => s.Trim().Length > 5))
        {
            int est = EstimateTokens(sentence);
            if (tokens + est > _targetTokens && current.Count > 0)
            {
                groups.Add(new List<string>(current));
                current.Clear();
                tokens = 0;
            }
            current.Add(sentence);
            tokens += est;
        }
        if (current.Count > 0) groups.Add(current);

        return groups.Select((g, i) =>
        {
            var content = string.Join(" ", g);
            return new DocumentChunk
            {
                DocumentId    = doc.Id,
                ChunkIndex    = i,
                Content       = content,
                TokenCount    = EstimateTokens(content),
                ChunkStrategy = Name
            };
        });
    }

    private static int EstimateTokens(string text) => (int)(text.Length / 4.0);
}

// ── Chunker Factory ───────────────────────────────────────────

public static class ChunkerFactory
{
    public static IChunkingStrategy Get(string strategy, SourceDocument doc) =>
        strategy.ToLowerInvariant() switch
        {
            "paragraph" => new ParagraphChunker(),
            "semantic"  => new SemanticChunker(),
            _           => new FixedSizeChunker()
        };

    public static IChunkingStrategy AutoSelect(SourceDocument doc)
    {
        // Auto-select based on mime type
        return doc.MimeType switch
        {
            "application/pdf"    => new ParagraphChunker(maxTokens: 512),
            "application/msword" => new ParagraphChunker(maxTokens: 512),
            "text/markdown"      => new SemanticChunker(targetTokens: 400),
            "text/plain"         => new FixedSizeChunker(chunkSize: 512, overlap: 64),
            _                    => new FixedSizeChunker()
        };
    }
}

// ── Embedding Pipeline ────────────────────────────────────────

public sealed class EmbeddingPipeline
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly ILogger<EmbeddingPipeline> _logger;
    private readonly int _batchSize;

    public EmbeddingPipeline(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        ILogger<EmbeddingPipeline> logger,
        int batchSize = 32)
    {
        _generator = generator;
        _logger    = logger;
        _batchSize = batchSize;
    }

    public async Task<List<DocumentChunk>> EmbedChunksAsync(
        List<DocumentChunk> chunks,
        CancellationToken ct = default)
    {
        _logger.LogInformation("Embedding {Count} chunks in batches of {BatchSize}",
            chunks.Count, _batchSize);

        for (int i = 0; i < chunks.Count; i += _batchSize)
        {
            var batch = chunks.Skip(i).Take(_batchSize).ToList();
            var texts = batch.Select(c => c.Content).ToList();

            try
            {
                var embeddings = await _generator.GenerateAsync(texts, ct: ct);
                for (int j = 0; j < batch.Count; j++)
                    batch[j].Embedding = embeddings[j].Vector.ToArray();

                _logger.LogDebug("Embedded batch {Batch}/{Total}",
                    i / _batchSize + 1, (int)Math.Ceiling(chunks.Count / (double)_batchSize));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding batch {Start}-{End} failed", i, i + _batchSize);
            }
        }

        return chunks;
    }
}
