using TVE.Core.Storage;

namespace TVE.Core.Analysis;

public class ProblematicPair
{
    public string NameA { get; set; } = "";
    public string NameB { get; set; } = "";
    public float Similarity { get; set; }
    public int ClusterA { get; set; }
    public int ClusterB { get; set; }
    public string SimilarityText => Similarity.ToString("F3");
    public string ClustersText => $"Кластер {ClusterA + 1} ↔ Кластер {ClusterB + 1}";
}

/// <summary>
/// Находит голоса, которые похожи (similarity выше порога),
/// но попали в разные кластеры — риск путаницы при идентификации.
/// </summary>
public static class ProblematicPairFinder
{
    public static List<ProblematicPair> Find(
        Speaker[] speakers,
        int[] clusterLabels,
        float threshold = 0.55f)
    {
        var result = new List<ProblematicPair>();
        int n = speakers.Length;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 1; j < n; j++)
            {
                int ci = i < clusterLabels.Length ? clusterLabels[i] : 0;
                int cj = j < clusterLabels.Length ? clusterLabels[j] : 0;

                float sim = ComputeSimilarity(speakers[i].VoiceEmbedding, speakers[j].VoiceEmbedding);
                if (sim < threshold) continue;

                result.Add(new ProblematicPair
                {
                    NameA = speakers[i].DisplayName,
                    NameB = speakers[j].DisplayName,
                    Similarity = sim,
                    ClusterA = ci,
                    ClusterB = cj
                });
            }
        }

        return result.OrderByDescending(p => p.Similarity).ToList();
    }

    private static float ComputeSimilarity(float[] a, float[] b)
    {
        if (a == null || b == null || a.Length == 0 || b.Length == 0) return 0;
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