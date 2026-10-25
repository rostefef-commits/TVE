namespace TVE.Core.Speakers;

/// <summary>
/// Жадная кластеризация эмбеддингов по косинусному сходству.
/// Используется при обработке файлов: похожие сегменты собираются в один кластер.
/// </summary>
public static class SpeakerClusterer
{
    /// <summary>
    /// Возвращает массив меток кластеров (0..K-1) для каждого эмбеддинга.
    /// </summary>
    /// <param name="embeddings">Массив эмбеддингов</param>
    /// <param name="threshold">Минимальное сходство, чтобы считать сегменты «одним голосом»</param>
    public static int[] Cluster(float[][] embeddings, float threshold = 0.6f)
    {
        int n = embeddings.Length;
        var labels = new int[n];
        for (int i = 0; i < n; i++) labels[i] = -1;

        int nextLabel = 0;

        for (int i = 0; i < n; i++)
        {
            if (labels[i] != -1) continue;

            // Начинаем новый кластер
            labels[i] = nextLabel;

            for (int j = i + 1; j < n; j++)
            {
                if (labels[j] != -1) continue;
                float sim = CosineSimilarity(embeddings[i], embeddings[j]);
                if (sim >= threshold)
                    labels[j] = nextLabel;
            }

            nextLabel++;
        }

        return labels;
    }

    /// <summary>
    /// Вычисляет центроид (усреднённый и нормализованный вектор) для группы эмбеддингов.
    /// </summary>
    public static float[] ComputeCentroid(IEnumerable<float[]> embeddings)
    {
        var list = embeddings.ToList();
        if (list.Count == 0) return Array.Empty<float>();

        int dim = list[0].Length;
        var sum = new float[dim];

        foreach (var emb in list)
        {
            if (emb.Length != dim) continue;
            for (int i = 0; i < dim; i++)
                sum[i] += emb[i];
        }

        // Усреднение
        for (int i = 0; i < dim; i++)
            sum[i] /= list.Count;

        // Нормализация (L2) — чтобы косинусное сходство работало корректно
        float norm = 0;
        for (int i = 0; i < dim; i++) norm += sum[i] * sum[i];
        norm = MathF.Sqrt(norm);
        if (norm > 0)
        {
            for (int i = 0; i < dim; i++)
                sum[i] /= norm;
        }

        return sum;
    }

    private static float CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        float dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        if (na == 0 || nb == 0) return 0;
        return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}