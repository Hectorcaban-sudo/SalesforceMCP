using System;
using TinyGptRag.Autograd;
using TinyGptRag.Model;
using TinyGptRag.Optim;

namespace TinyGptRag.Training
{
    public class Trainer
    {
        private readonly TinyGpt _model;
        private readonly int[] _corpusIds;
        private readonly Adam _optim;
        private readonly Random _rng = new();

        public Trainer(TinyGpt model, int[] corpusIds, double lr = 3e-4)
        {
            _model = model;
            _corpusIds = corpusIds;
            _optim = new Adam(model.GetParameters(), lr);

            if (corpusIds.Length < model.Cfg.BlockSize + 1)
                throw new ArgumentException("Corpus is too short for the configured BlockSize. Add more training text or reduce BlockSize.");
        }

        /// <summary>Runs `steps` training iterations, each on one randomly sampled window. Returns the final loss.</summary>
        public double Run(int steps, Action<int, double>? onStep = null)
        {
            double lastLoss = 0;
            int blockSize = _model.Cfg.BlockSize;

            for (int step = 0; step < steps; step++)
            {
                int start = _rng.Next(0, _corpusIds.Length - blockSize - 1);
                var input = new int[blockSize];
                var target = new int[blockSize];
                Array.Copy(_corpusIds, start, input, 0, blockSize);
                Array.Copy(_corpusIds, start + 1, target, 0, blockSize);

                _optim.ZeroGrad();
                var logits = _model.Forward(input);
                var loss = Tensor.CrossEntropyMean(logits, target);
                loss.Backward();
                _optim.Step();

                lastLoss = loss.Data[0, 0];
                onStep?.Invoke(step, lastLoss);
            }
            return lastLoss;
        }
    }
}
