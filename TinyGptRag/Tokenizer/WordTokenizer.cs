using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TinyGptRag.Tokenizer
{
    /// <summary>
    /// A simple word/punctuation-level tokenizer whose vocabulary is built entirely
    /// from your own corpus (no external vocab file, no pretrained tokenizer).
    /// </summary>
    public class WordTokenizer
    {
        public const string Unk = "<unk>";
        public const string Pad = "<pad>";
        public const string Bos = "<bos>";
        public const string Eos = "<eos>";

        public Dictionary<string, int> TokenToId = new();
        public List<string> IdToToken = new();

        private static readonly Regex TokenRegex = new(@"[A-Za-z0-9]+|[^\sA-Za-z0-9]", RegexOptions.Compiled);

        public static List<string> SplitWords(string text)
        {
            text = text.ToLowerInvariant();
            var matches = TokenRegex.Matches(text);
            var result = new List<string>(matches.Count);
            foreach (Match m in matches) result.Add(m.Value);
            return result;
        }

        /// <summary>Build the vocabulary from raw corpus text, keeping the most frequent maxVocabSize words.</summary>
        public static WordTokenizer Train(string corpusText, int maxVocabSize)
        {
            var words = SplitWords(corpusText);
            var freq = new Dictionary<string, int>();
            foreach (var w in words)
                freq[w] = freq.GetValueOrDefault(w) + 1;

            var special = new[] { Pad, Unk, Bos, Eos };
            var chosen = freq.OrderByDescending(kv => kv.Value)
                              .Select(kv => kv.Key)
                              .Where(w => !special.Contains(w))
                              .Take(Math.Max(0, maxVocabSize - special.Length))
                              .ToList();

            var tok = new WordTokenizer();
            foreach (var s in special) tok.AddToken(s);
            foreach (var w in chosen) tok.AddToken(w);
            return tok;
        }

        private void AddToken(string token)
        {
            if (TokenToId.ContainsKey(token)) return;
            TokenToId[token] = IdToToken.Count;
            IdToToken.Add(token);
        }

        public int VocabSize => IdToToken.Count;

        public int[] Encode(string text, bool addBosEos = false)
        {
            var words = SplitWords(text);
            var ids = new List<int>();
            if (addBosEos) ids.Add(TokenToId[Bos]);
            int unkId = TokenToId[Unk];
            foreach (var w in words)
                ids.Add(TokenToId.TryGetValue(w, out var id) ? id : unkId);
            if (addBosEos) ids.Add(TokenToId[Eos]);
            return ids.ToArray();
        }

        public string Decode(IEnumerable<int> ids)
        {
            var parts = new List<string>();
            foreach (var id in ids)
            {
                if (id < 0 || id >= IdToToken.Count) continue;
                var tok = IdToToken[id];
                if (tok is Pad or Bos or Eos) continue;
                parts.Add(tok);
            }
            // naive detokenization: no space before punctuation
            var sb = new System.Text.StringBuilder();
            foreach (var p in parts)
            {
                bool isPunct = p.Length == 1 && !char.IsLetterOrDigit(p[0]);
                if (sb.Length > 0 && !isPunct) sb.Append(' ');
                sb.Append(p);
            }
            return sb.ToString();
        }

        public void Save(string path)
        {
            var json = JsonSerializer.Serialize(IdToToken);
            File.WriteAllText(path, json);
        }

        public static WordTokenizer Load(string path)
        {
            var json = File.ReadAllText(path);
            var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            var tok = new WordTokenizer();
            foreach (var s in list) tok.AddToken(s);
            return tok;
        }
    }
}
