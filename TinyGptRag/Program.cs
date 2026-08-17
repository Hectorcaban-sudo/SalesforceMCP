using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TinyGptRag.Autograd;
using TinyGptRag.Model;
using TinyGptRag.Rag;
using TinyGptRag.Tokenizer;
using TinyGptRag.Training;

namespace TinyGptRag
{
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 1;
            }

            var opts = ParseOptions(args.Skip(1).ToArray());

            switch (args[0])
            {
                case "train": return CmdTrain(opts);
                case "ingest": return CmdIngest(opts);
                case "chat": return CmdChat(opts);
                default:
                    PrintUsage();
                    return 1;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"TinyGptRag - a from-scratch transformer + RAG toolkit

Usage:
  dotnet run -- train  --corpus <file-or-dir> --out <model-dir> [--vocab 4000] [--dmodel 64] [--nhead 4] [--nlayer 4] [--dff 256] [--block 128] [--steps 3000] [--lr 0.0003]
  dotnet run -- ingest --model <model-dir> --docs <file-or-dir> --out <rag-dir> [--chunk 100] [--overlap 20]
  dotnet run -- chat   --model <model-dir> --rag <rag-dir> [--topk 3] [--maxnew 60] [--temp 0.9] [--topkgen 40]
");
        }

        private static Dictionary<string, string> ParseOptions(string[] args)
        {
            var dict = new Dictionary<string, string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--"))
                {
                    string key = args[i].Substring(2);
                    string val = (i + 1 < args.Length && !args[i + 1].StartsWith("--")) ? args[++i] : "true";
                    dict[key] = val;
                }
            }
            return dict;
        }

        private static List<string> ReadTextFiles(string path)
        {
            if (Directory.Exists(path))
                return Directory.GetFiles(path, "*.txt", SearchOption.AllDirectories)
                    .Select(File.ReadAllText)
                    .ToList();
            return new List<string> { File.ReadAllText(path) };
        }

        private static int CmdTrain(Dictionary<string, string> o)
        {
            if (!o.TryGetValue("corpus", out var corpusPath) || !o.TryGetValue("out", out var outDir))
            {
                Console.WriteLine("train requires --corpus <file-or-dir> --out <model-dir>");
                return 1;
            }
            Directory.CreateDirectory(outDir);

            var cfg = new GptConfig
            {
                VocabSize = GetInt(o, "vocab", 4000),
                DModel = GetInt(o, "dmodel", 64),
                NHead = GetInt(o, "nhead", 4),
                NLayer = GetInt(o, "nlayer", 4),
                DFeedForward = GetInt(o, "dff", 256),
                BlockSize = GetInt(o, "block", 128),
            };
            int steps = GetInt(o, "steps", 3000);
            double lr = GetDouble(o, "lr", 3e-4);

            Console.WriteLine("Reading corpus...");
            var fileTexts = ReadTextFiles(corpusPath).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            if (fileTexts.Count == 0)
            {
                Console.WriteLine("Corpus is empty.");
                return 1;
            }
            Console.WriteLine($"Found {fileTexts.Count} file(s).");

            Console.WriteLine("Training tokenizer on your corpus...");
            var tokenizer = WordTokenizer.Train(string.Join("\n", fileTexts), cfg.VocabSize);
            cfg.VocabSize = tokenizer.VocabSize; // actual vocab may be smaller than requested
            Console.WriteLine($"Vocabulary size: {cfg.VocabSize}");

            // Encode each file separately and stitch them together with a real <eos> token
            // in between, so the model can learn "a new, unrelated document starts here"
            // instead of seeing an abrupt, meaningless jump in an unbroken token stream.
            int eosId = tokenizer.TokenToId[WordTokenizer.Eos];
            var idsList = new List<int>();
            for (int i = 0; i < fileTexts.Count; i++)
            {
                idsList.AddRange(tokenizer.Encode(fileTexts[i]));
                if (i < fileTexts.Count - 1) idsList.Add(eosId);
            }
            var ids = idsList.ToArray();
            Console.WriteLine($"Corpus length: {ids.Length} tokens (including {fileTexts.Count - 1} file-boundary <eos> tokens)");

            Console.WriteLine("Initializing model (random weights, no pretraining)...");
            var model = new TinyGpt(cfg);
            var trainer = new Trainer(model, ids, lr);

            Console.WriteLine($"Training for {steps} steps...");
            trainer.Run(steps, (step, loss) =>
            {
                if (step % 50 == 0 || step == steps - 1)
                    Console.WriteLine($"  step {step,6} / {steps}   loss = {loss:F4}");
            });

            model.Save(Path.Combine(outDir, "model.bin"));
            tokenizer.Save(Path.Combine(outDir, "tokenizer.json"));
            Console.WriteLine($"Saved model to {outDir}");
            return 0;
        }

        private static int CmdIngest(Dictionary<string, string> o)
        {
            if (!o.TryGetValue("model", out var modelDir) || !o.TryGetValue("docs", out var docsPath) || !o.TryGetValue("out", out var ragDir))
            {
                Console.WriteLine("ingest requires --model <model-dir> --docs <file-or-dir> --out <rag-dir>");
                return 1;
            }
            Directory.CreateDirectory(ragDir);

            var model = TinyGpt.Load(Path.Combine(modelDir, "model.bin"));
            var tokenizer = WordTokenizer.Load(Path.Combine(modelDir, "tokenizer.json"));

            int chunkTokens = GetInt(o, "chunk", 100);
            int overlap = GetInt(o, "overlap", 20);

            var store = new VectorStore();

            var files = Directory.Exists(docsPath)
                ? Directory.GetFiles(docsPath, "*.txt", SearchOption.AllDirectories)
                : new[] { docsPath };

            foreach (var f in files)
            {
                Console.WriteLine($"Ingesting {f} ...");
                var text = File.ReadAllText(f);
                store.IngestDocument(text, model, tokenizer, chunkTokens, overlap);
            }

            store.Save(Path.Combine(ragDir, "vectorstore.json"));
            Console.WriteLine($"Ingested {store.Chunks.Count} chunks into {ragDir}");
            return 0;
        }

        private static int CmdChat(Dictionary<string, string> o)
        {
            if (!o.TryGetValue("model", out var modelDir))
            {
                Console.WriteLine("chat requires --model <model-dir> [--rag <rag-dir>]");
                return 1;
            }

            var model = TinyGpt.Load(Path.Combine(modelDir, "model.bin"));
            var tokenizer = WordTokenizer.Load(Path.Combine(modelDir, "tokenizer.json"));

            VectorStore? store = null;
            if (o.TryGetValue("rag", out var ragDir))
            {
                var path = Path.Combine(ragDir, "vectorstore.json");
                if (File.Exists(path))
                {
                    store = VectorStore.Load(path);
                    Console.WriteLine($"Loaded RAG store with {store.Chunks.Count} chunks.");
                }
            }

            int topK = GetInt(o, "topk", 3);
            int maxNew = GetInt(o, "maxnew", 60);
            double temp = GetDouble(o, "temp", 0.9);
            int topKGen = GetInt(o, "topkgen", 40);

            Console.WriteLine("Chat ready. Type a message (or 'exit').");
            Console.WriteLine("NOTE: this is a small model trained only on your own corpus - expect limited, narrow-domain answers, not general world knowledge.");

            while (true)
            {
                Console.Write("\nYou: ");
                var userInput = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(userInput) || userInput.Trim().ToLowerInvariant() == "exit")
                    break;

                string prompt;
                if (store != null && store.Chunks.Count > 0)
                {
                    var hits = store.Query(userInput, model, tokenizer, topK);
                    var context = string.Join(" ", hits.Select(h => h.chunk.Text));
                    prompt = $"context: {context} question: {userInput} answer:";
                }
                else
                {
                    prompt = $"question: {userInput} answer:";
                }

                var response = Generate(model, tokenizer, prompt, maxNew, temp, topKGen);
                Console.WriteLine($"Bot: {response}");
            }
            return 0;
        }

        /// <summary>Autoregressive generation with temperature + top-k sampling.</summary>
        private static string Generate(TinyGpt model, WordTokenizer tokenizer, string prompt, int maxNewTokens, double temperature, int topK)
        {
            var rng = new Random();
            var ids = tokenizer.Encode(prompt).ToList();
            int eosId = tokenizer.TokenToId[WordTokenizer.Eos];

            bool prevNoGrad = Tensor.NoGrad;
            Tensor.NoGrad = true;
            try
            {
                for (int step = 0; step < maxNewTokens; step++)
                {
                    var window = ids.Count > model.Cfg.BlockSize
                        ? ids.Skip(ids.Count - model.Cfg.BlockSize).ToArray()
                        : ids.ToArray();

                    var logits = model.Forward(window);
                    int lastRow = logits.Rows - 1;

                    var row = new double[logits.Cols];
                    for (int j = 0; j < logits.Cols; j++) row[j] = logits.Data[lastRow, j] / Math.Max(temperature, 1e-6);

                    int nextId = SampleTopK(row, topK, rng);
                    if (nextId == eosId) break;
                    ids.Add(nextId);
                }
            }
            finally { Tensor.NoGrad = prevNoGrad; }

            int promptLen = tokenizer.Encode(prompt).Length;
            var generatedIds = ids.Skip(promptLen);
            return tokenizer.Decode(generatedIds);
        }

        private static int SampleTopK(double[] logits, int topK, Random rng)
        {
            var indices = Enumerable.Range(0, logits.Length)
                                     .OrderByDescending(i => logits[i])
                                     .Take(Math.Max(1, Math.Min(topK, logits.Length)))
                                     .ToArray();
            double max = indices.Select(i => logits[i]).Max();
            var expVals = indices.Select(i => Math.Exp(logits[i] - max)).ToArray();
            double sum = expVals.Sum();
            double r = rng.NextDouble() * sum;
            double acc = 0;
            for (int k = 0; k < indices.Length; k++)
            {
                acc += expVals[k];
                if (r <= acc) return indices[k];
            }
            return indices[^1];
        }

        private static int GetInt(Dictionary<string, string> o, string key, int def) =>
            o.TryGetValue(key, out var v) && int.TryParse(v, out var i) ? i : def;

        private static double GetDouble(Dictionary<string, string> o, string key, double def) =>
            o.TryGetValue(key, out var v) && double.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : def;
    }
}
