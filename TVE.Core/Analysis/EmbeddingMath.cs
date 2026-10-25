namespace TVE.Core.Analysis;

/// <summary>
/// Математика для работы с эмбеддингами на единичной сфере.
///
/// Все эмбеддинги в TVE (512 float от sherpa-onnx) — единичные
/// векторы: их L2-норма равна 1. Сравнение идёт через косинусное
/// сходство, поэтому линейное усреднение (0.75·a + 0.25·b) неверно —
/// оно уводит результат внутрь сферы и искажает геометрию.
///
/// Правильная операция — SLERP (Spherical Linear intERPolation):
/// движение по дуге большого круга между двумя векторами.
/// Результат всегда остаётся на сфере.
/// </summary>
public static class EmbeddingMath
{
    /// <summary>
    /// Косинусное сходство двух векторов.
    /// Возвращает значение [-1..1]: 1 — идентичные, 0 — ортогональные,
    /// -1 — противоположные.
    ///
    /// Автоматически нормирует на длины, поэтому работает даже если
    /// входные векторы не единичные.
    /// </summary>
    public static float CosineSimilarity(float[] a, float[] b)
    {
        if (a == null || b == null) return 0f;
        if (a.Length == 0 || b.Length == 0) return 0f;
        if (a.Length != b.Length) return 0f;

        float dot = 0f, na = 0f, nb = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        float denom = MathF.Sqrt(na) * MathF.Sqrt(nb);
        if (denom < 1e-12f) return 0f;
        return dot / denom;
    }

    /// <summary>
    /// L2-нормализация: возвращает новый массив,
    /// у которого L2-норма (корень из суммы квадратов) равна 1.
    /// Если вектор нулевой — возвращает копию без изменений.
    /// </summary>
    public static float[] L2Normalize(float[] v)
    {
        var result = new float[v.Length];
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += v[i] * (double)v[i];

        double norm = Math.Sqrt(sum);
        if (norm < 1e-12)
        {
            Array.Copy(v, result, v.Length);
            return result;
        }

        for (int i = 0; i < v.Length; i++)
            result[i] = (float)(v[i] / norm);
        return result;
    }

    /// <summary>
    /// SLERP — сферическая линейная интерполяция между a и b.
    ///
    /// Формула:
    ///   θ = arccos(a · b)
    ///   slerp(a, b, t) = sin((1−t)·θ)/sin(θ) · a + sin(t·θ)/sin(θ) · b
    ///
    /// Свойства:
    ///   slerp(a, b, 0) = a
    ///   slerp(a, b, 1) = b
    ///   |slerp(a, b, t)| = 1 для всех t
    ///
    /// Особые случаи:
    ///   - Если векторы почти совпадают (θ ≈ 0) — возвращаем L2Normalize(lerp).
    ///   - Если почти противоположны (θ ≈ π) — интерполяция неоднозначна,
    ///     берём L2Normalize(lerp) как разумный fallback.
    /// </summary>
    /// <param name="a">Начальный единичный вектор.</param>
    /// <param name="b">Конечный единичный вектор.</param>
    /// <param name="t">Параметр интерполяции [0..1]. Клампится.</param>
    public static float[] Slerp(float[] a, float[] b, float t)
    {
        if (a == null || b == null || a.Length == 0 || b.Length != a.Length)
            return Array.Empty<float>();

        if (t <= 0f) return L2Normalize(a);
        if (t >= 1f) return L2Normalize(b);

        // Скалярное произведение (оба вектора нормированы на единичную сферу,
        // но на всякий случай нормируем ещё раз — а вдруг входные данные не нормированы).
        var an = L2Normalize(a);
        var bn = L2Normalize(b);

        double dot = 0;
        for (int i = 0; i < an.Length; i++) dot += an[i] * (double)bn[i];

        // Клампим на [-1, 1] — иначе arccos может вернуть NaN из-за погрешности.
        if (dot > 1.0) dot = 1.0;
        if (dot < -1.0) dot = -1.0;

        double theta = Math.Acos(dot);

        // Случай 1: векторы почти совпадают — линейная интерполяция неотличима от SLERP.
        // Порог 1e-4 рад ≈ 0.006°.
        if (theta < 1e-4)
            return L2Normalize(LinearInterp(an, bn, t));

        // Случай 2: векторы почти противоположны — sin(theta) → 0, формула неустойчива.
        // Порог: sin(theta) < 1e-4.
        double sinTheta = Math.Sin(theta);
        if (Math.Abs(sinTheta) < 1e-4)
            return L2Normalize(LinearInterp(an, bn, t));

        // Основная формула SLERP.
        double w1 = Math.Sin((1.0 - t) * theta) / sinTheta;
        double w2 = Math.Sin(t * theta) / sinTheta;

        var result = new float[an.Length];
        for (int i = 0; i < an.Length; i++)
            result[i] = (float)(w1 * an[i] + w2 * bn[i]);

        // Финальная нормировка для надёжности.
        return L2Normalize(result);
    }

    /// <summary>
    /// Усреднение набора единичных векторов на сфере.
    /// Использует итеративный SLERP:
    ///   m_0 = v_0
    ///   m_k = slerp(m_{k-1}, v_k, 1/k)
    /// Даёт устойчивую «среднюю точку» на сфере — в отличие от
    /// линейного усреднения, которое уходит внутрь.
    /// </summary>
    public static float[] MeanOnSphere(IEnumerable<float[]> vectors)
    {
        var list = vectors.Where(v => v != null && v.Length > 0).ToList();
        if (list.Count == 0) return Array.Empty<float>();
        if (list.Count == 1) return L2Normalize(list[0]);

        // Приводим все к одной длине — берём длину первого.
        int dim = list[0].Length;
        var cleaned = list.Where(v => v.Length == dim).ToList();
        if (cleaned.Count == 0) return Array.Empty<float>();

        var mean = L2Normalize(cleaned[0]);
        for (int i = 1; i < cleaned.Count; i++)
        {
            float t = 1.0f / (i + 1);
            mean = Slerp(mean, cleaned[i], t);
        }

        return mean;
    }

    // ============================================================
    // ===== ВСПОМОГАТЕЛЬНОЕ =====
    // ============================================================

    /// <summary>
    /// Простая линейная интерполяция: (1−t)·a + t·b.
    /// Используется как fallback в SLERP для вырожденных случаев.
    /// </summary>
    private static float[] LinearInterp(float[] a, float[] b, float t)
    {
        var r = new float[a.Length];
        for (int i = 0; i < a.Length; i++)
            r[i] = a[i] * (1f - t) + b[i] * t;
        return r;
    }
}