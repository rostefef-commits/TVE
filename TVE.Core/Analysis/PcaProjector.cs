namespace TVE.Core.Analysis;

/// <summary>
/// PCA (Principal Component Analysis) — снижение размерности.
/// Превращает N-мерные эмбеддинги в 2D-координаты для визуализации.
/// Также возвращает explained variance — какую долю информации несёт каждая ось.
/// </summary>
public static class PcaProjector
{
    public class PcaResult
    {
        public (double X, double Y)[] Points { get; set; } = Array.Empty<(double, double)>();
        /// <summary>Доля дисперсии, объяснённая 1-й компонентой (0..1).</summary>
        public double Variance1 { get; set; }
        /// <summary>Доля дисперсии, объяснённая 2-й компонентой (0..1).</summary>
        public double Variance2 { get; set; }
    }

    public static PcaResult ProjectTo2D(float[][] embeddings)
    {
        var result = new PcaResult();
        int n = embeddings.Length;
        if (n == 0) return result;
        if (n == 1)
        {
            result.Points = new[] { (0.0, 0.0) };
            return result;
        }

        int dim = embeddings[0].Length;

        // 1. Центрирование
        var mean = new double[dim];
        for (int i = 0; i < n; i++)
            for (int j = 0; j < dim; j++)
                mean[j] += embeddings[i][j];
        for (int j = 0; j < dim; j++)
            mean[j] /= n;

        var centered = new double[n][];
        for (int i = 0; i < n; i++)
        {
            centered[i] = new double[dim];
            for (int j = 0; j < dim; j++)
                centered[i][j] = embeddings[i][j] - mean[j];
        }

        // 2. Первые две главные компоненты
        var comp1 = PowerIteration(centered, n, dim, seed: 1, previous: null);
        var comp2 = PowerIteration(centered, n, dim, seed: 2, previous: comp1);

        // 3. Проекция
        var points = new (double X, double Y)[n];
        for (int i = 0; i < n; i++)
        {
            double x = 0, y = 0;
            for (int j = 0; j < dim; j++)
            {
                x += centered[i][j] * comp1[j];
                y += centered[i][j] * comp2[j];
            }
            points[i] = (x, y);
        }
        result.Points = points;

        // 4. Explained variance
        double var1 = 0, var2 = 0, total = 0;
        for (int i = 0; i < n; i++)
        {
            var1 += points[i].X * points[i].X;
            var2 += points[i].Y * points[i].Y;
            for (int j = 0; j < dim; j++)
                total += centered[i][j] * centered[i][j];
        }
        if (total > 1e-12)
        {
            result.Variance1 = var1 / total;
            result.Variance2 = var2 / total;
        }

        return result;
    }

    private static double[] PowerIteration(
        double[][] centered, int n, int dim, int seed, double[]? previous)
    {
        var v = new double[dim];
        var rnd = new Random(seed);
        for (int i = 0; i < dim; i++)
            v[i] = rnd.NextDouble() - 0.5;

        if (previous != null) Orthogonalize(v, previous);
        Normalize(v);

        const int maxIter = 100;
        const double epsilon = 1e-7;

        for (int iter = 0; iter < maxIter; iter++)
        {
            var temp = new double[n];
            for (int i = 0; i < n; i++)
            {
                double sum = 0;
                for (int j = 0; j < dim; j++)
                    sum += centered[i][j] * v[j];
                temp[i] = sum;
            }

            var w = new double[dim];
            for (int i = 0; i < n; i++)
                for (int j = 0; j < dim; j++)
                    w[j] += centered[i][j] * temp[i];

            if (previous != null) Orthogonalize(w, previous);
            Normalize(w);

            double diff = 0;
            for (int i = 0; i < dim; i++)
                diff += Math.Abs(w[i] - v[i]);
            v = w;

            if (diff < epsilon) break;
        }

        return v;
    }

    private static void Orthogonalize(double[] v, double[] basis)
    {
        double dot = 0;
        for (int i = 0; i < v.Length; i++)
            dot += v[i] * basis[i];
        for (int i = 0; i < v.Length; i++)
            v[i] -= dot * basis[i];
    }

    private static void Normalize(double[] v)
    {
        double norm = 0;
        for (int i = 0; i < v.Length; i++)
            norm += v[i] * v[i];
        norm = Math.Sqrt(norm);
        if (norm < 1e-12) return;
        for (int i = 0; i < v.Length; i++)
            v[i] /= norm;
    }
}