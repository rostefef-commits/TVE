namespace TVE.Core.Analysis;

/// <summary>
/// Простой K-Means для 2D-точек (для раскраски кластеров на карте голосов).
/// </summary>
public static class SimpleKMeans
{
    /// <summary>
    /// Разбивает точки на K кластеров. Возвращает массив меток (0..K-1).
    /// </summary>
    public static int[] Cluster((double X, double Y)[] points, int k, int maxIterations = 50)
    {
        int n = points.Length;
        if (n == 0) return Array.Empty<int>();
        if (k <= 0) k = 1;
        if (k > n) k = n;

        var labels = new int[n];
        var rnd = new Random(42);

        // Инициализация: k-means++ (разнесённые начальные центроиды)
        var centroids = InitializeCentroids(points, k, rnd);

        for (int iter = 0; iter < maxIterations; iter++)
        {
            bool changed = false;

            // Назначаем каждой точке ближайший центроид
            for (int i = 0; i < n; i++)
            {
                int bestK = 0;
                double bestDist = double.MaxValue;
                for (int c = 0; c < k; c++)
                {
                    double dx = points[i].X - centroids[c].X;
                    double dy = points[i].Y - centroids[c].Y;
                    double dist = dx * dx + dy * dy;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestK = c;
                    }
                }
                if (labels[i] != bestK)
                {
                    labels[i] = bestK;
                    changed = true;
                }
            }

            if (!changed) break;

            // Пересчёт центроидов
            var sums = new (double X, double Y, int Count)[k];
            for (int i = 0; i < n; i++)
            {
                var c = labels[i];
                sums[c].X += points[i].X;
                sums[c].Y += points[i].Y;
                sums[c].Count++;
            }

            for (int c = 0; c < k; c++)
            {
                if (sums[c].Count > 0)
                    centroids[c] = (sums[c].X / sums[c].Count, sums[c].Y / sums[c].Count);
            }
        }

        return labels;
    }

    private static (double X, double Y)[] InitializeCentroids(
        (double X, double Y)[] points, int k, Random rnd)
    {
        int n = points.Length;
        var centroids = new (double X, double Y)[k];

        // Первый центроид — случайная точка
        centroids[0] = points[rnd.Next(n)];

        // Остальные — по алгоритму k-means++
        var distances = new double[n];
        for (int c = 1; c < k; c++)
        {
            double total = 0;
            for (int i = 0; i < n; i++)
            {
                double minDist = double.MaxValue;
                for (int j = 0; j < c; j++)
                {
                    double dx = points[i].X - centroids[j].X;
                    double dy = points[i].Y - centroids[j].Y;
                    double d = dx * dx + dy * dy;
                    if (d < minDist) minDist = d;
                }
                distances[i] = minDist;
                total += minDist;
            }

            double r = rnd.NextDouble() * total;
            double cum = 0;
            int chosen = 0;
            for (int i = 0; i < n; i++)
            {
                cum += distances[i];
                if (cum >= r)
                {
                    chosen = i;
                    break;
                }
            }
            centroids[c] = points[chosen];
        }

        return centroids;
    }
}