using System;
using System.Collections.Generic;
using System.IO;
using TinyGptRag.Autograd;

namespace TinyGptRag.Model
{
    /// <summary>
    /// A small decoder-only transformer (GPT-style), implemented from scratch on top of
    /// the custom Tensor autodiff engine. No pretrained weights are used anywhere -
    /// all parameters below start as random noise and are only meaningful after you
    /// train the model on your own corpus.
    /// </summary>
    public class TinyGpt
    {
        public GptConfig Cfg;

        public Tensor TokEmb;   // [vocab, d]
        public Tensor PosEmb;   // [blockSize, d]
        public Tensor LnfGamma, LnfBeta; // [1, d]
        public Tensor OutBias;  // [1, vocab]

        public List<Block> Blocks = new();

        public class Block
        {
            public Tensor Ln1G = null!, Ln1B = null!, Ln2G = null!, Ln2B = null!;
            public Tensor Wq = null!, Bq = null!, Wk = null!, Bk = null!, Wv = null!, Bv = null!, Wo = null!, Bo = null!;
            public Tensor W1 = null!, B1 = null!, W2 = null!, B2 = null!;
        }

        public TinyGpt(GptConfig cfg, int? seed = null)
        {
            Cfg = cfg;
            if (cfg.DModel % cfg.NHead != 0)
                throw new ArgumentException("DModel must be divisible by NHead");

            var rng = seed.HasValue ? new Random(seed.Value) : new Random();
            double s = 0.02;

            TokEmb = Tensor.RandomUniform(cfg.VocabSize, cfg.DModel, s, rng);
            PosEmb = Tensor.RandomUniform(cfg.BlockSize, cfg.DModel, s, rng);
            LnfGamma = Tensor.From(Ones(1, cfg.DModel));
            LnfBeta = Tensor.Zeros(1, cfg.DModel);
            OutBias = Tensor.Zeros(1, cfg.VocabSize);

            for (int l = 0; l < cfg.NLayer; l++)
            {
                var b = new Block
                {
                    Ln1G = Tensor.From(Ones(1, cfg.DModel)),
                    Ln1B = Tensor.Zeros(1, cfg.DModel),
                    Ln2G = Tensor.From(Ones(1, cfg.DModel)),
                    Ln2B = Tensor.Zeros(1, cfg.DModel),
                    Wq = Tensor.RandomUniform(cfg.DModel, cfg.DModel, s, rng),
                    Bq = Tensor.Zeros(1, cfg.DModel),
                    Wk = Tensor.RandomUniform(cfg.DModel, cfg.DModel, s, rng),
                    Bk = Tensor.Zeros(1, cfg.DModel),
                    Wv = Tensor.RandomUniform(cfg.DModel, cfg.DModel, s, rng),
                    Bv = Tensor.Zeros(1, cfg.DModel),
                    Wo = Tensor.RandomUniform(cfg.DModel, cfg.DModel, s, rng),
                    Bo = Tensor.Zeros(1, cfg.DModel),
                    W1 = Tensor.RandomUniform(cfg.DModel, cfg.DFeedForward, s, rng),
                    B1 = Tensor.Zeros(1, cfg.DFeedForward),
                    W2 = Tensor.RandomUniform(cfg.DFeedForward, cfg.DModel, s, rng),
                    B2 = Tensor.Zeros(1, cfg.DModel),
                };
                Blocks.Add(b);
            }
        }

        private static double[,] Ones(int r, int c)
        {
            var d = new double[r, c];
            for (int i = 0; i < r; i++) for (int j = 0; j < c; j++) d[i, j] = 1.0;
            return d;
        }

        private static double[,] CausalMask(int len)
        {
            var m = new double[len, len];
            for (int i = 0; i < len; i++)
                for (int j = 0; j < len; j++)
                    m[i, j] = (j <= i) ? 0.0 : -1e9;
            return m;
        }

        /// <summary>Runs the transformer body and returns final layer-normed hidden states [seqLen, d].</summary>
        public Tensor ForwardHidden(int[] ids)
        {
            if (ids.Length > Cfg.BlockSize)
                throw new ArgumentException($"Sequence length {ids.Length} exceeds BlockSize {Cfg.BlockSize}");

            var tok = Tensor.EmbeddingLookup(TokEmb, ids);
            var pos = Tensor.SliceRows(PosEmb, 0, ids.Length);
            Tensor x = Tensor.Add(tok, pos);

            var mask = CausalMask(ids.Length);

            foreach (var blk in Blocks)
            {
                var ln1 = Tensor.LayerNorm(x, blk.Ln1G, blk.Ln1B);

                var q = Tensor.AddBiasRow(Tensor.MatMul(ln1, blk.Wq), blk.Bq);
                var k = Tensor.AddBiasRow(Tensor.MatMul(ln1, blk.Wk), blk.Bk);
                var v = Tensor.AddBiasRow(Tensor.MatMul(ln1, blk.Wv), blk.Bv);

                int hd = Cfg.HeadDim;
                var heads = new List<Tensor>();
                for (int h = 0; h < Cfg.NHead; h++)
                {
                    var qh = Tensor.SliceCols(q, h * hd, hd);
                    var kh = Tensor.SliceCols(k, h * hd, hd);
                    var vh = Tensor.SliceCols(v, h * hd, hd);

                    var scores = Tensor.Scale(Tensor.MatMul(qh, Tensor.Transpose(kh)), 1.0 / Math.Sqrt(hd));
                    var attn = Tensor.MaskedSoftmaxRows(scores, mask);
                    var headOut = Tensor.MatMul(attn, vh);
                    heads.Add(headOut);
                }
                var concat = Tensor.ConcatCols(heads);
                var attnOut = Tensor.AddBiasRow(Tensor.MatMul(concat, blk.Wo), blk.Bo);
                x = Tensor.Add(x, attnOut);

                var ln2 = Tensor.LayerNorm(x, blk.Ln2G, blk.Ln2B);
                var ff1 = Tensor.ReLU(Tensor.AddBiasRow(Tensor.MatMul(ln2, blk.W1), blk.B1));
                var ff2 = Tensor.AddBiasRow(Tensor.MatMul(ff1, blk.W2), blk.B2);
                x = Tensor.Add(x, ff2);
            }

            return Tensor.LayerNorm(x, LnfGamma, LnfBeta);
        }

        /// <summary>Full forward pass returning logits [seqLen, vocab] (weight-tied to token embedding).</summary>
        public Tensor Forward(int[] ids)
        {
            var hidden = ForwardHidden(ids);
            var logits = Tensor.AddBiasRow(Tensor.MatMul(hidden, Tensor.Transpose(TokEmb)), OutBias);
            return logits;
        }

        /// <summary>Mean-pooled sentence/chunk embedding, useful for RAG retrieval. Uses no_grad (fast, no graph).</summary>
        public double[] Embed(int[] ids)
        {
            bool prev = Tensor.NoGrad;
            Tensor.NoGrad = true;
            try
            {
                var hidden = ForwardHidden(ids);
                var vec = new double[Cfg.DModel];
                for (int i = 0; i < hidden.Rows; i++)
                    for (int j = 0; j < Cfg.DModel; j++)
                        vec[j] += hidden.Data[i, j];
                for (int j = 0; j < Cfg.DModel; j++) vec[j] /= hidden.Rows;
                return vec;
            }
            finally { Tensor.NoGrad = prev; }
        }

        public List<Tensor> GetParameters()
        {
            var ps = new List<Tensor> { TokEmb, PosEmb, LnfGamma, LnfBeta, OutBias };
            foreach (var b in Blocks)
            {
                ps.AddRange(new[]
                {
                    b.Ln1G, b.Ln1B, b.Ln2G, b.Ln2B,
                    b.Wq, b.Bq, b.Wk, b.Bk, b.Wv, b.Bv, b.Wo, b.Bo,
                    b.W1, b.B1, b.W2, b.B2
                });
            }
            return ps;
        }

        // ---- persistence: simple binary format, no external serializer needed ----
        public void Save(string path)
        {
            using var fs = new FileStream(path, FileMode.Create);
            using var w = new BinaryWriter(fs);
            w.Write(Cfg.VocabSize); w.Write(Cfg.DModel); w.Write(Cfg.NHead);
            w.Write(Cfg.NLayer); w.Write(Cfg.DFeedForward); w.Write(Cfg.BlockSize);
            foreach (var p in GetParameters())
                WriteTensor(w, p);
        }

        private static void WriteTensor(BinaryWriter w, Tensor t)
        {
            w.Write(t.Rows); w.Write(t.Cols);
            for (int i = 0; i < t.Rows; i++)
                for (int j = 0; j < t.Cols; j++)
                    w.Write(t.Data[i, j]);
        }

        private static void ReadTensor(BinaryReader r, Tensor t)
        {
            int rows = r.ReadInt32(), cols = r.ReadInt32();
            if (rows != t.Rows || cols != t.Cols)
                throw new InvalidDataException("Checkpoint shape does not match model config.");
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    t.Data[i, j] = r.ReadDouble();
        }

        public static TinyGpt Load(string path)
        {
            using var fs = new FileStream(path, FileMode.Open);
            using var r = new BinaryReader(fs);
            var cfg = new GptConfig
            {
                VocabSize = r.ReadInt32(),
                DModel = r.ReadInt32(),
                NHead = r.ReadInt32(),
                NLayer = r.ReadInt32(),
                DFeedForward = r.ReadInt32(),
                BlockSize = r.ReadInt32(),
            };
            var model = new TinyGpt(cfg);
            foreach (var p in model.GetParameters())
                ReadTensor(r, p);
            return model;
        }
    }
}
