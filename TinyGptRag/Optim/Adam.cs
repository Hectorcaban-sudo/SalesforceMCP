using System;
using System.Collections.Generic;
using TinyGptRag.Autograd;

namespace TinyGptRag.Optim
{
    public class Adam
    {
        private readonly List<Tensor> _params;
        private readonly double _lr, _beta1, _beta2, _eps;
        private readonly double[][,] _m, _v;
        private int _t;

        public Adam(List<Tensor> parameters, double lr = 3e-4, double beta1 = 0.9, double beta2 = 0.999, double eps = 1e-8)
        {
            _params = parameters;
            _lr = lr; _beta1 = beta1; _beta2 = beta2; _eps = eps;
            _m = new double[parameters.Count][,];
            _v = new double[parameters.Count][,];
            for (int i = 0; i < parameters.Count; i++)
            {
                _m[i] = new double[parameters[i].Rows, parameters[i].Cols];
                _v[i] = new double[parameters[i].Rows, parameters[i].Cols];
            }
        }

        public void ZeroGrad()
        {
            foreach (var p in _params) p.ZeroGrad();
        }

        public void Step()
        {
            _t++;
            double bc1 = 1 - Math.Pow(_beta1, _t);
            double bc2 = 1 - Math.Pow(_beta2, _t);

            for (int pi = 0; pi < _params.Count; pi++)
            {
                var p = _params[pi];
                var m = _m[pi]; var v = _v[pi];
                for (int i = 0; i < p.Rows; i++)
                    for (int j = 0; j < p.Cols; j++)
                    {
                        double g = p.Grad[i, j];
                        m[i, j] = _beta1 * m[i, j] + (1 - _beta1) * g;
                        v[i, j] = _beta2 * v[i, j] + (1 - _beta2) * g * g;
                        double mHat = m[i, j] / bc1;
                        double vHat = v[i, j] / bc2;
                        p.Data[i, j] -= _lr * mHat / (Math.Sqrt(vHat) + _eps);
                    }
            }
        }
    }
}
