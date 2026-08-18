using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace TinyGptRag.Autograd
{
    /// <summary>
    /// A minimal reverse-mode autodiff engine over 2D arrays (rows x cols).
    /// Every op below builds a small computation graph node so Backward() can
    /// propagate gradients through the whole model. This is the "from scratch"
    /// core: there is no dependency on any ML library or pretrained model.
    /// </summary>
    public class Tensor
    {
        public double[,] Data;
        public double[,] Grad;
        public int Rows, Cols;
        public bool RequiresGrad;

        private List<Tensor> _prev = new();
        private Action? _backward;

        // When true, ops skip building the graph (saves memory during inference/generation).
        public static bool NoGrad = false;

        public Tensor(int rows, int cols, bool requiresGrad = true)
        {
            Rows = rows; Cols = cols; RequiresGrad = requiresGrad;
            Data = new double[rows, cols];
            Grad = new double[rows, cols];
        }

        public static Tensor From(double[,] data, bool requiresGrad = true)
        {
            var t = new Tensor(data.GetLength(0), data.GetLength(1), requiresGrad);
            Array.Copy(data, t.Data, data.Length);
            return t;
        }

        public static Tensor Zeros(int rows, int cols, bool requiresGrad = true) => new Tensor(rows, cols, requiresGrad);

        public static Tensor RandomUniform(int rows, int cols, double scale, Random rng, bool requiresGrad = true)
        {
            var t = new Tensor(rows, cols, requiresGrad);
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                    t.Data[i, j] = (rng.NextDouble() * 2 - 1) * scale;
            return t;
        }

        public void ZeroGrad()
        {
            Array.Clear(Grad, 0, Grad.Length);
        }

        public void Backward()
        {
            var topo = new List<Tensor>();
            var visited = new HashSet<Tensor>();
            void Visit(Tensor t)
            {
                if (visited.Contains(t)) return;
                visited.Add(t);
                foreach (var p in t._prev) Visit(p);
                topo.Add(t);
            }
            Visit(this);

            if (Rows != 1 || Cols != 1)
                throw new InvalidOperationException("Backward() must be called on a scalar (1x1) tensor.");
            Grad[0, 0] = 1.0;

            for (int i = topo.Count - 1; i >= 0; i--)
                topo[i]._backward?.Invoke();
        }

        // ---- ops ----

        public static Tensor MatMul(Tensor a, Tensor b)
        {
            if (a.Cols != b.Rows) throw new InvalidOperationException($"MatMul shape mismatch {a.Rows}x{a.Cols} * {b.Rows}x{b.Cols}");
            var result = new Tensor(a.Rows, b.Cols, true);

            // Forward: each row i of the output only depends on row i of `a`, so rows
            // can be computed on separate threads with no shared-state conflicts.
            Parallel.For(0, a.Rows, i =>
            {
                for (int k = 0; k < a.Cols; k++)
                {
                    double av = a.Data[i, k];
                    if (av == 0) continue;
                    for (int j = 0; j < b.Cols; j++)
                        result.Data[i, j] += av * b.Data[k, j];
                }
            });

            if (!NoGrad)
            {
                result.AttachParents(a, b);
                result.SetBackward(() =>
                {
                    // dA: row i of a.Grad only depends on row i of result.Grad -> safe to parallelize over i.
                    Parallel.For(0, a.Rows, i =>
                    {
                        for (int j = 0; j < b.Cols; j++)
                        {
                            double g = result.Grad[i, j];
                            if (g == 0) continue;
                            for (int k = 0; k < a.Cols; k++)
                                a.Grad[i, k] += g * b.Data[k, j];
                        }
                    });

                    // dB: row k of b.Grad only depends on column k of a -> safe to parallelize over k
                    // (kept as a separate pass from dA so no two threads ever touch the same array cell).
                    Parallel.For(0, b.Rows, k =>
                    {
                        var rowGrad = new double[b.Cols];
                        for (int i = 0; i < a.Rows; i++)
                        {
                            double av = a.Data[i, k];
                            if (av == 0) continue;
                            for (int j = 0; j < b.Cols; j++)
                                rowGrad[j] += av * result.Grad[i, j];
                        }
                        for (int j = 0; j < b.Cols; j++)
                            b.Grad[k, j] += rowGrad[j];
                    });
                });
            }
            return result;
        }

        public static Tensor AddBiasRow(Tensor a, Tensor biasRow)
        {
            if (biasRow.Rows != 1 || biasRow.Cols != a.Cols)
                throw new InvalidOperationException("Bias must be a 1 x Cols row vector matching input columns.");
            var result = new Tensor(a.Rows, a.Cols, true);
            for (int i = 0; i < a.Rows; i++)
                for (int j = 0; j < a.Cols; j++)
                    result.Data[i, j] = a.Data[i, j] + biasRow.Data[0, j];

            if (!NoGrad)
            {
                result.AttachParents(a, biasRow);
                result.SetBackward(() =>
                {
                    for (int i = 0; i < a.Rows; i++)
                        for (int j = 0; j < a.Cols; j++)
                        {
                            double g = result.Grad[i, j];
                            a.Grad[i, j] += g;
                            biasRow.Grad[0, j] += g;
                        }
                });
            }
            return result;
        }

        public static Tensor Add(Tensor a, Tensor b)
        {
            if (a.Rows != b.Rows || a.Cols != b.Cols) throw new InvalidOperationException("Add shape mismatch");
            var result = new Tensor(a.Rows, a.Cols, true);
            for (int i = 0; i < a.Rows; i++)
                for (int j = 0; j < a.Cols; j++)
                    result.Data[i, j] = a.Data[i, j] + b.Data[i, j];
            if (!NoGrad)
            {
                result.AttachParents(a, b);
                result.SetBackward(() =>
                {
                    for (int i = 0; i < a.Rows; i++)
                        for (int j = 0; j < a.Cols; j++)
                        {
                            double g = result.Grad[i, j];
                            a.Grad[i, j] += g;
                            b.Grad[i, j] += g;
                        }
                });
            }
            return result;
        }

        public static Tensor Transpose(Tensor a)
        {
            var result = new Tensor(a.Cols, a.Rows, true);
            for (int i = 0; i < a.Rows; i++)
                for (int j = 0; j < a.Cols; j++)
                    result.Data[j, i] = a.Data[i, j];
            if (!NoGrad)
            {
                result.AttachParents(a);
                result.SetBackward(() =>
                {
                    for (int i = 0; i < a.Rows; i++)
                        for (int j = 0; j < a.Cols; j++)
                            a.Grad[i, j] += result.Grad[j, i];
                });
            }
            return result;
        }

        public static Tensor Scale(Tensor a, double scalar)
        {
            var result = new Tensor(a.Rows, a.Cols, true);
            for (int i = 0; i < a.Rows; i++)
                for (int j = 0; j < a.Cols; j++)
                    result.Data[i, j] = a.Data[i, j] * scalar;
            if (!NoGrad)
            {
                result.AttachParents(a);
                result.SetBackward(() =>
                {
                    for (int i = 0; i < a.Rows; i++)
                        for (int j = 0; j < a.Cols; j++)
                            a.Grad[i, j] += result.Grad[i, j] * scalar;
                });
            }
            return result;
        }

        public static Tensor ReLU(Tensor a)
        {
            var result = new Tensor(a.Rows, a.Cols, true);
            for (int i = 0; i < a.Rows; i++)
                for (int j = 0; j < a.Cols; j++)
                    result.Data[i, j] = Math.Max(0, a.Data[i, j]);
            if (!NoGrad)
            {
                result.AttachParents(a);
                result.SetBackward(() =>
                {
                    for (int i = 0; i < a.Rows; i++)
                        for (int j = 0; j < a.Cols; j++)
                            a.Grad[i, j] += (a.Data[i, j] > 0 ? 1.0 : 0.0) * result.Grad[i, j];
                });
            }
            return result;
        }

        public static Tensor SliceCols(Tensor a, int start, int len)
        {
            var result = new Tensor(a.Rows, len, true);
            for (int i = 0; i < a.Rows; i++)
                for (int j = 0; j < len; j++)
                    result.Data[i, j] = a.Data[i, start + j];
            if (!NoGrad)
            {
                result.AttachParents(a);
                result.SetBackward(() =>
                {
                    for (int i = 0; i < a.Rows; i++)
                        for (int j = 0; j < len; j++)
                            a.Grad[i, start + j] += result.Grad[i, j];
                });
            }
            return result;
        }

        public static Tensor SliceRows(Tensor a, int start, int len)
        {
            var result = new Tensor(len, a.Cols, true);
            for (int i = 0; i < len; i++)
                for (int j = 0; j < a.Cols; j++)
                    result.Data[i, j] = a.Data[start + i, j];
            if (!NoGrad)
            {
                result.AttachParents(a);
                result.SetBackward(() =>
                {
                    for (int i = 0; i < len; i++)
                        for (int j = 0; j < a.Cols; j++)
                            a.Grad[start + i, j] += result.Grad[i, j];
                });
            }
            return result;
        }

        public static Tensor ConcatCols(IReadOnlyList<Tensor> parts)
        {
            int rows = parts[0].Rows;
            int totalCols = 0;
            foreach (var p in parts) totalCols += p.Cols;
            var result = new Tensor(rows, totalCols, true);
            int offset = 0;
            foreach (var p in parts)
            {
                for (int i = 0; i < rows; i++)
                    for (int j = 0; j < p.Cols; j++)
                        result.Data[i, offset + j] = p.Data[i, j];
                offset += p.Cols;
            }
            if (!NoGrad)
            {
                result.AttachParents(parts.ToArray());
                result.SetBackward(() =>
                {
                    int off = 0;
                    foreach (var p in parts)
                    {
                        for (int i = 0; i < rows; i++)
                            for (int j = 0; j < p.Cols; j++)
                                p.Grad[i, j] += result.Grad[i, off + j];
                        off += p.Cols;
                    }
                });
            }
            return result;
        }

        /// <summary>Row-wise softmax with an additive mask (use -1e9 to suppress positions, 0 to keep).</summary>
        public static Tensor MaskedSoftmaxRows(Tensor a, double[,]? additiveMask)
        {
            var result = new Tensor(a.Rows, a.Cols, true);
            for (int i = 0; i < a.Rows; i++)
            {
                double max = double.NegativeInfinity;
                for (int j = 0; j < a.Cols; j++)
                {
                    double v = a.Data[i, j] + (additiveMask?[i, j] ?? 0);
                    if (v > max) max = v;
                }
                double sum = 0;
                var row = new double[a.Cols];
                for (int j = 0; j < a.Cols; j++)
                {
                    double v = a.Data[i, j] + (additiveMask?[i, j] ?? 0);
                    double e = Math.Exp(v - max);
                    row[j] = e;
                    sum += e;
                }
                for (int j = 0; j < a.Cols; j++)
                    result.Data[i, j] = row[j] / sum;
            }
            if (!NoGrad)
            {
                result.AttachParents(a);
                result.SetBackward(() =>
                {
                    for (int i = 0; i < a.Rows; i++)
                    {
                        double dot = 0;
                        for (int j = 0; j < a.Cols; j++) dot += result.Grad[i, j] * result.Data[i, j];
                        for (int j = 0; j < a.Cols; j++)
                            a.Grad[i, j] += result.Data[i, j] * (result.Grad[i, j] - dot);
                    }
                });
            }
            return result;
        }

        public static Tensor LayerNorm(Tensor a, Tensor gamma, Tensor beta, double eps = 1e-5)
        {
            int n = a.Cols;
            var result = new Tensor(a.Rows, a.Cols, true);
            var means = new double[a.Rows];
            var stds = new double[a.Rows];
            var xhat = new double[a.Rows, a.Cols];

            for (int i = 0; i < a.Rows; i++)
            {
                double mean = 0;
                for (int j = 0; j < n; j++) mean += a.Data[i, j];
                mean /= n;
                double variance = 0;
                for (int j = 0; j < n; j++) variance += (a.Data[i, j] - mean) * (a.Data[i, j] - mean);
                variance /= n;
                double std = Math.Sqrt(variance + eps);
                means[i] = mean; stds[i] = std;
                for (int j = 0; j < n; j++)
                {
                    double xh = (a.Data[i, j] - mean) / std;
                    xhat[i, j] = xh;
                    result.Data[i, j] = xh * gamma.Data[0, j] + beta.Data[0, j];
                }
            }

            if (!NoGrad)
            {
                result.AttachParents(a, gamma, beta);
                result.SetBackward(() =>
                {
                    for (int i = 0; i < a.Rows; i++)
                    {
                        double std = stds[i];
                        double dxhatDotXhat = 0, dxhatSum = 0;
                        var dxhat = new double[n];
                        for (int j = 0; j < n; j++)
                        {
                            double dy = result.Grad[i, j];
                            gamma.Grad[0, j] += dy * xhat[i, j];
                            beta.Grad[0, j] += dy;
                            dxhat[j] = dy * gamma.Data[0, j];
                            dxhatDotXhat += dxhat[j] * xhat[i, j];
                            dxhatSum += dxhat[j];
                        }
                        for (int j = 0; j < n; j++)
                        {
                            double dx = (dxhat[j] - dxhatSum / n - xhat[i, j] * dxhatDotXhat / n) / std;
                            a.Grad[i, j] += dx;
                        }
                    }
                });
            }
            return result;
        }

        /// <summary>Embedding lookup: table is [vocab, dim], ids selects rows -> [ids.Length, dim].</summary>
        public static Tensor EmbeddingLookup(Tensor table, int[] ids)
        {
            var result = new Tensor(ids.Length, table.Cols, true);
            for (int i = 0; i < ids.Length; i++)
                for (int j = 0; j < table.Cols; j++)
                    result.Data[i, j] = table.Data[ids[i], j];
            if (!NoGrad)
            {
                result.AttachParents(table);
                result.SetBackward(() =>
                {
                    for (int i = 0; i < ids.Length; i++)
                        for (int j = 0; j < table.Cols; j++)
                            table.Grad[ids[i], j] += result.Grad[i, j];
                });
            }
            return result;
        }

        /// <summary>Softmax cross-entropy over rows of logits vs integer targets. Returns a scalar (mean) loss.</summary>
        public static Tensor CrossEntropyMean(Tensor logits, int[] targets)
        {
            int rows = logits.Rows, cols = logits.Cols;
            var probs = new double[rows, cols];
            double loss = 0;
            for (int i = 0; i < rows; i++)
            {
                double max = double.NegativeInfinity;
                for (int j = 0; j < cols; j++) if (logits.Data[i, j] > max) max = logits.Data[i, j];
                double sum = 0;
                for (int j = 0; j < cols; j++) { probs[i, j] = Math.Exp(logits.Data[i, j] - max); sum += probs[i, j]; }
                for (int j = 0; j < cols; j++) probs[i, j] /= sum;
                loss += -Math.Log(Math.Max(probs[i, targets[i]], 1e-12));
            }
            loss /= rows;

            var result = new Tensor(1, 1, true);
            result.Data[0, 0] = loss;
            if (!NoGrad)
            {
                result.AttachParents(logits);
                result.SetBackward(() =>
                {
                    double g = result.Grad[0, 0] / rows;
                    for (int i = 0; i < rows; i++)
                        for (int j = 0; j < cols; j++)
                        {
                            double target = (j == targets[i]) ? 1.0 : 0.0;
                            logits.Grad[i, j] += g * (probs[i, j] - target);
                        }
                });
            }
            return result;
        }

        // ---- small helpers to attach graph state after construction ----
        private void AttachParents(params Tensor[] parents) => _prev.AddRange(parents);
        private void SetBackward(Action backward) => _backward = backward;
    }
}
