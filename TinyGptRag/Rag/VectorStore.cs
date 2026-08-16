using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using TinyGptRag.Model;
using TinyGptRag.Tokenizer;

namespace TinyGptRag.Rag
{
    public class Chunk
    {
        public string Text { get; set; } = "";
        public double[] Vector { get; set; } = Array.Empty<double>();
    }

    /// <summary>
    /// Minimal in-memory / on-disk vector store. Embeddings come from the same
    /// from-scratch model (mean-pooled final hidden states) - no external embedding API.
    /// </summary>
    public class VectorStore
    {
        public List<Chunk> Chunks { get; set; } = new();

        public void IngestDocument(string text, TinyGpt model, WordTokenizer tokenizer, int chunkTokens = 100, int overlapTokens = 20)
        {
            var ids = tokenizer.Encode(text);
            int step = Math.Max(1, chunkTokens - overlapTokens);
            for (int start = 0; start < ids.Length; start += step)
            {
                int len = Math.Min(chunkTokens, ids.Length - start);
                if (len <= 0) break;
                len = Math.Min(len, model.Cfg.BlockSize);
                var slice = new int[len];
                Array.Copy(ids, start, slice, 0, len);
                var vector = model.Embed(slice);
                var chunkText = tokenizer.Decode(slice);
                Chunks.Add(new Chunk { Text = chunkText, Vector = vector });
                if (start + len >= ids.Length) break;
            }
        }

        public List<(Chunk chunk, double score)> Query(string queryText, TinyGpt model, WordTokenizer tokenizer, int topK = 3)
        {
            var ids = tokenizer.Encode(queryText);
            if (ids.Length == 0) return new List<(Chunk, double)>();
            if (ids.Length > model.Cfg.BlockSize) ids = ids.Take(model.Cfg.BlockSize).ToArray();
            var qVec = model.Embed(ids);

            return Chunks
                .Select(c => (c, score: CosineSim(qVec, c.Vector)))
                .OrderByDescending(x => x.score)
                .Take(topK)
                .ToList();
        }

        private static double CosineSim(double[] a, double[] b)
        {
            double dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            if (na == 0 || nb == 0) return 0;
            return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }

        public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));

        public static VectorStore Load(string path)
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<VectorStore>(json) ?? new VectorStore();
        }
    }
}
