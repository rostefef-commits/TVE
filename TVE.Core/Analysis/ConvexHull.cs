namespace TVE.Core.Analysis;

/// <summary>
/// Выпуклая оболочка (convex hull) для 2D-точек.
/// Алгоритм Эндрю (monotone chain).
/// </summary>
public static class ConvexHull
{
    public static List<(double X, double Y)> Compute(List<(double X, double Y)> points)
    {
        if (points.Count < 3) return new List<(double X, double Y)>(points);

        var sorted = points.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();

        var lower = new List<(double X, double Y)>();
        foreach (var p in sorted)
        {
            while (lower.Count >= 2 && Cross(lower[^2], lower[^1], p) <= 0)
                lower.RemoveAt(lower.Count - 1);
            lower.Add(p);
        }

        var upper = new List<(double X, double Y)>();
        for (int i = sorted.Count - 1; i >= 0; i--)
        {
            var p = sorted[i];
            while (upper.Count >= 2 && Cross(upper[^2], upper[^1], p) <= 0)
                upper.RemoveAt(upper.Count - 1);
            upper.Add(p);
        }

        lower.RemoveAt(lower.Count - 1);
        upper.RemoveAt(upper.Count - 1);

        lower.AddRange(upper);
        return lower;
    }

    private static double Cross((double X, double Y) o, (double X, double Y) a, (double X, double Y) b)
        => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
}