using TVE.Core.Services;

namespace TVE.Core.Analysis;

/// <summary>
/// Режим пакетного анализа.
/// </summary>
public enum BatchAnalysisMode
{
    /// <summary>VAD-нарезка + полный анализ каждого сегмента.</summary>
    BySegments,

    /// <summary>Скользящее окно 64 мс / hop 32 мс, только F0(t).</summary>
    ByFrames
}

/// <summary>
/// Одна точка пакетного анализа.
/// </summary>
public class BatchAnalysisPoint
{
    public int Index { get; set; }
    public double StartSec { get; set; }
    public double DurationSec { get; set; }

    public float F0 { get; set; }
    public float F0Cepstrum { get; set; }
    public float F0Hps { get; set; }
    public float F0Autocorrelation { get; set; }
    public bool F0Agreed { get; set; }

    public float Rms { get; set; }
    public float Snr { get; set; }

    public float[] HarmonicFrequencies { get; set; } = Array.Empty<float>();
    public float[] Formants { get; set; } = Array.Empty<float>();

    // ===== Тексты для таблицы =====
    public string TimeText => $"{StartSec:F2} c";
    public string DurationText => $"{DurationSec:F2} c";
    public string F0Text => F0 > 0f ? $"{F0:F1}" : "-";
    public string CepstrumText => F0Cepstrum > 0f ? $"{F0Cepstrum:F1}" : "-";
    public string HpsText => F0Hps > 0f ? $"{F0Hps:F1}" : "-";
    public string AutocorrText => F0Autocorrelation > 0f ? $"{F0Autocorrelation:F1}" : "-";
    public string AgreedText => F0 > 0f ? (F0Agreed ? "да" : "нет") : "-";
    public string RmsText => Rms.ToString("F5");
    public string SnrText => $"{Snr:F1}";

    public string KText => HarmonicFrequencies.Length >= 8
        ? string.Join(" / ", HarmonicFrequencies.Take(8)
            .Select(f => f > 0f ? $"{f:F0}" : "-"))
        : "-";

    public string FText => Formants.Length >= 4
        ? string.Join(" / ", Formants.Take(4)
            .Select(f => f > 0f ? $"{f:F0}" : "-"))
        : "-";
}

/// <summary>
/// Результат пакетного анализа всего файла.
/// </summary>
public class BatchAnalysisResult
{
    public BatchAnalysisMode Mode { get; set; }
    public double TotalSeconds { get; set; }
    public int TotalPoints { get; set; }
    public int ValidPoints { get; set; }

    public List<BatchAnalysisPoint> Points { get; set; } = new();

    public float F0Mean { get; set; }
    public float F0Median { get; set; }
    public float F0Min { get; set; }
    public float F0Max { get; set; }
    public float F0StdDev { get; set; }

    public float[] FormantsMean { get; set; } = new float[4];
}

/// <summary>
/// Пакетный анализ аудиофайла — по VAD-сегментам или по кадрам.
/// </summary>
public static class HarmonicBatchAnalysis
{
    public static BatchAnalysisResult Analyze(
        float[] samples,
        BatchAnalysisMode mode,
        IProgress<double>? progress = null)
    {
        if (samples == null || samples.Length < 1024)
        {
            EventBus.Warn("HarmonicBatch",
                "Сигнал слишком короткий для пакетного анализа");
            return new BatchAnalysisResult { Mode = mode };
        }

        return mode switch
        {
            BatchAnalysisMode.BySegments => AnalyzeBySegments(samples, progress),
            BatchAnalysisMode.ByFrames => AnalyzeByFrames(samples, progress),
            _ => new BatchAnalysisResult { Mode = mode }
        };
    }

    private static BatchAnalysisResult AnalyzeBySegments(
        float[] samples,
        IProgress<double>? progress)
    {
        var result = new BatchAnalysisResult
        {
            Mode = BatchAnalysisMode.BySegments,
            TotalSeconds = (double)samples.Length / HarmonicAnalyzer.SampleRate
        };

        var segments = SplitIntoSegmentsWithTimes(samples);
        if (segments.Count == 0)
        {
            EventBus.Warn("HarmonicBatch", "VAD не нашёл сегментов речи");
            return result;
        }

        result.TotalPoints = segments.Count;

        var f0List = new List<float>();
        var f1List = new List<float>();
        var f2List = new List<float>();
        var f3List = new List<float>();
        var f4List = new List<float>();

        for (int i = 0; i < segments.Count; i++)
        {
            var (startSample, segSamples) = segments[i];
            var analysis = HarmonicAnalyzer.Analyze(segSamples);

            var point = new BatchAnalysisPoint
            {
                Index = i + 1,
                StartSec = (double)startSample / HarmonicAnalyzer.SampleRate,
                DurationSec = (double)segSamples.Length / HarmonicAnalyzer.SampleRate,
                F0 = analysis.F0,
                F0Cepstrum = analysis.F0Cepstrum,
                F0Hps = analysis.F0Hps,
                F0Autocorrelation = analysis.F0Autocorrelation,
                F0Agreed = analysis.F0Agreed,
                Rms = analysis.Rms,
                Snr = analysis.Snr,
                HarmonicFrequencies = analysis.HarmonicFrequencies,
                Formants = analysis.Formants
            };

            result.Points.Add(point);

            if (analysis.F0 > 0f) f0List.Add(analysis.F0);

            if (analysis.Formants.Length >= 4)
            {
                if (analysis.Formants[0] > 0f) f1List.Add(analysis.Formants[0]);
                if (analysis.Formants[1] > 0f) f2List.Add(analysis.Formants[1]);
                if (analysis.Formants[2] > 0f) f3List.Add(analysis.Formants[2]);
                if (analysis.Formants[3] > 0f) f4List.Add(analysis.Formants[3]);
            }

            progress?.Report((double)(i + 1) / segments.Count);
        }

        result.ValidPoints = f0List.Count;
        ComputeF0Metrics(result, f0List);

        result.FormantsMean = new float[4]
        {
            f1List.Count > 0 ? f1List.Average() : 0f,
            f2List.Count > 0 ? f2List.Average() : 0f,
            f3List.Count > 0 ? f3List.Average() : 0f,
            f4List.Count > 0 ? f4List.Average() : 0f
        };

        EventBus.Ok("HarmonicBatch",
            $"Пакетный анализ (по сегментам): {segments.Count} сегментов, " +
            $"F0 mean={result.F0Mean:F1}, std={result.F0StdDev:F1} Гц");

        return result;
    }

    private static BatchAnalysisResult AnalyzeByFrames(
        float[] samples,
        IProgress<double>? progress)
    {
        const int FrameLen = 1024;
        const int Hop = 512;
        const int MaxPoints = 20000;

        var result = new BatchAnalysisResult
        {
            Mode = BatchAnalysisMode.ByFrames,
            TotalSeconds = (double)samples.Length / HarmonicAnalyzer.SampleRate
        };

        if (samples.Length < FrameLen) return result;

        int total = (samples.Length - FrameLen) / Hop + 1;

        if (total > MaxPoints)
        {
            EventBus.Warn("HarmonicBatch",
                $"Слишком длинный файл ({total} кадров). " +
                $"Ограничиваем до {MaxPoints} (первые " +
                $"{(double)MaxPoints * Hop / HarmonicAnalyzer.SampleRate:F1} сек)");
            total = MaxPoints;
        }

        result.TotalPoints = total;

        var points = new List<BatchAnalysisPoint>(total);
        var f0List = new List<float>();

        for (int i = 0; i < total; i++)
        {
            int start = i * Hop;
            float[] frame = new float[FrameLen];
            Array.Copy(samples, start, frame, 0, FrameLen);

            var f0res = HarmonicAnalyzer.AnalyzeF0Only(frame);

            var point = new BatchAnalysisPoint
            {
                Index = i + 1,
                StartSec = (double)start / HarmonicAnalyzer.SampleRate,
                DurationSec = (double)FrameLen / HarmonicAnalyzer.SampleRate,
                F0 = f0res.F0,
                F0Cepstrum = f0res.F0Cepstrum,
                F0Hps = f0res.F0Hps,
                F0Autocorrelation = f0res.F0Autocorrelation,
                F0Agreed = f0res.F0Agreed,
                Rms = f0res.Rms,
                Snr = f0res.Snr
            };

            points.Add(point);
            if (f0res.F0 > 0f) f0List.Add(f0res.F0);

            if (i % 50 == 0 || i == total - 1)
                progress?.Report((double)(i + 1) / total);
        }

        result.Points = points;
        result.ValidPoints = f0List.Count;
        ComputeF0Metrics(result, f0List);

        EventBus.Ok("HarmonicBatch",
            $"Пакетный анализ (по кадрам): {total} кадров, " +
            $"{result.ValidPoints} валидных, " +
            $"F0 mean={result.F0Mean:F1}, std={result.F0StdDev:F1} Гц");

        return result;
    }

    private static List<(int StartSample, float[] Samples)>
        SplitIntoSegmentsWithTimes(float[] samples)
    {
        const int SampleRate = HarmonicAnalyzer.SampleRate;
        const int ChunkMs = 50;
        const float SilenceThreshold = 0.01f;
        const int SilenceTimeoutMs = 800;
        const int MinSpeechMs = 500;

        var result = new List<(int, float[])>();
        var buffer = new List<float>();
        int segmentStart = -1;
        int lastSpeechEnd = 0;
        bool isSpeaking = false;

        int chunkSize = SampleRate * ChunkMs / 1000;

        for (int i = 0; i < samples.Length; i += chunkSize)
        {
            int size = Math.Min(chunkSize, samples.Length - i);

            float sum = 0;
            for (int k = 0; k < size; k++)
            {
                float s = samples[i + k];
                sum += s * s;
            }
            float rms = MathF.Sqrt(sum / size);
            bool isSpeech = rms > SilenceThreshold;

            if (isSpeech)
            {
                if (!isSpeaking)
                {
                    segmentStart = i;
                    isSpeaking = true;
                }
                for (int k = 0; k < size; k++)
                    buffer.Add(samples[i + k]);
                lastSpeechEnd = i + size;
            }
            else if (isSpeaking)
            {
                int silenceSamples = (i + size) - lastSpeechEnd;
                int silenceMs = silenceSamples * 1000 / SampleRate;

                if (silenceMs > SilenceTimeoutMs)
                {
                    int durationMs = buffer.Count * 1000 / SampleRate;
                    if (durationMs >= MinSpeechMs)
                        result.Add((segmentStart, buffer.ToArray()));

                    buffer.Clear();
                    isSpeaking = false;
                    segmentStart = -1;
                }
            }
        }

        if (buffer.Count > 0)
        {
            int durationMs = buffer.Count * 1000 / SampleRate;
            if (durationMs >= MinSpeechMs)
                result.Add((segmentStart, buffer.ToArray()));
        }

        return result;
    }

    private static void ComputeF0Metrics(
        BatchAnalysisResult result, List<float> f0List)
    {
        if (f0List.Count == 0)
        {
            result.F0Mean = 0f;
            result.F0Median = 0f;
            result.F0Min = 0f;
            result.F0Max = 0f;
            result.F0StdDev = 0f;
            return;
        }

        var sorted = f0List.OrderBy(f => f).ToList();

        result.F0Mean = f0List.Average();
        result.F0Median = sorted[sorted.Count / 2];
        result.F0Min = sorted[0];
        result.F0Max = sorted[^1];

        double mean = result.F0Mean;
        double sumSq = 0;
        foreach (var f in f0List)
        {
            double d = f - mean;
            sumSq += d * d;
        }
        result.F0StdDev = (float)Math.Sqrt(sumSq / f0List.Count);
    }
}