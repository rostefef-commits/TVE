namespace TVE.Core.Audio;

/// <summary>
/// Причина, по которой сегмент считается «плохим».
/// </summary>
public enum SegmentBadReason
{
    None,               // всё в порядке
    TooShort,           // слишком короткий / пустой после Whisper
    TooQuiet,           // слишком тихий / шумно
    MultipleSpeakers    // в сегменте слышны два разных голоса
}

/// <summary>
/// Проверки качества аудио-сегмента перед распознаванием и сохранением.
/// </summary>
public static class SegmentQualityChecker
{
    // ===== Пороги (смягчены) =====
    public const float MinRms = 0.008f;                                // было 0.01
    public const int MinSamples = 16000 * 400 / 1000;                  // было 300 мс
    public const int MinSamplesForSplitCheck = 16000 * 3500 / 1000;    // было 1500 → 3500 мс
    public const float SameSpeakerThreshold = 0.55f;                   // было 0.7 → 0.55
    public const int MaxConsecutiveBad = 5;

    // ===== Сообщения =====
    public const string MsgUnclear = "Говорите разборчивее";
    public const string MsgNoisy = "Высокий уровень стороннего шума";
    public const string MsgOneAtATime = "Пожалуйста, говорите по одному";
    public const string MsgCheckMic = "Проверьте микрофон в Настройках";

    /// <summary>
    /// Базовая проверка: длительность и громкость.
    /// </summary>
    public static SegmentBadReason CheckBasicQuality(float[] samples)
    {
        if (samples.Length < MinSamples)
            return SegmentBadReason.TooShort;

        float rms = CalculateRms(samples);
        if (rms < MinRms)
            return SegmentBadReason.TooQuiet;

        return SegmentBadReason.None;
    }

    /// <summary>
    /// Проверка «два голоса»: сравниваем эмбеддинг первой и второй половин.
    /// Возвращает true, если сходство ниже порога (значит, голоса разные).
    /// </summary>
    public static bool LooksLikeMultipleSpeakers(float[] samples, float[] emb1, float[] emb2)
    {
        if (samples.Length < MinSamplesForSplitCheck) return false;
        if (emb1.Length == 0 || emb2.Length == 0) return false;

        float sim = CosineSimilarity(emb1, emb2);
        return sim < SameSpeakerThreshold;
    }

    /// <summary>
    /// Похож ли текст на мусор (пусто, только пунктуация, «[музыка]», «[шум]» и т.п.).
    /// </summary>
    public static bool IsLikelyGarbage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return true;
        var t = text.Trim();

        if (t.Length < 2) return true;
        if (t.All(c => !char.IsLetterOrDigit(c))) return true;
        if (t.StartsWith("[") && t.EndsWith("]")) return true;

        return false;
    }

    /// <summary>
    /// Сообщение для пользователя по причине «плохого» сегмента.
    /// </summary>
    public static string GetMessage(SegmentBadReason reason) => reason switch
    {
        SegmentBadReason.TooShort => MsgUnclear,
        SegmentBadReason.TooQuiet => MsgNoisy,
        SegmentBadReason.MultipleSpeakers => MsgOneAtATime,
        _ => string.Empty
    };

    // ===== Вспомогательное =====

    private static float CalculateRms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        float sum = 0;
        foreach (var s in samples) sum += s * s;
        return MathF.Sqrt(sum / samples.Length);
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