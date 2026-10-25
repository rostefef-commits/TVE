using NAudio.Vorbis;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace TVE.Core.Audio;

/// <summary>
/// Декодирование аудиофайлов и нарезка на сегменты речи.
/// Используется и для drag-and-drop файлов в UI,
/// и для внешнего анализа (гармоники).
/// </summary>
public static class Mp3Processor
{
    public const int TargetSampleRate = 16000;
    private const float SilenceThreshold = 0.01f;
    private const int SilenceTimeoutMs = 800;
    private const int MinSpeechMs = 500;
    private const int ChunkMs = 50;

    private static string DebugFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "debug_decoded");

    private static string DebugLogPath => Path.Combine(DebugFolder, "mp3_debug.log");

    public static List<float[]> ExtractSegments(string filePath)
    {
        Directory.CreateDirectory(DebugFolder);
        Log($"===== ExtractSegments start: {Path.GetFileName(filePath)} =====");

        var samples = DecodeToMono16k(filePath);
        Log($"Decoded samples={samples.Length} " +
            $"({samples.Length * 1000 / TargetSampleRate}ms)");

        SaveDebugWav(samples, filePath);

        // Статистика по RMS
        float maxRms = 0f;
        float avgRms = 0f;
        int chunkSize = TargetSampleRate * ChunkMs / 1000;
        int chunks = 0;
        for (int i = 0; i < samples.Length; i += chunkSize)
        {
            int size = Math.Min(chunkSize, samples.Length - i);
            var chunk = new float[size];
            Array.Copy(samples, i, chunk, 0, size);
            float rms = CalculateRms(chunk);
            if (rms > maxRms) maxRms = rms;
            avgRms += rms;
            chunks++;
        }
        avgRms = chunks > 0 ? avgRms / chunks : 0;

        Log($"maxRMS={maxRms:F5}, avgRMS={avgRms:F5}, chunks={chunks}, " +
            $"threshold={SilenceThreshold}");

        var segments = SplitIntoSegments(samples);
        Log($"Segments found: {segments.Count}");
        Log($"===== ExtractSegments end =====");

        return segments;
    }

    private static void Log(string message)
    {
        try
        {
            File.AppendAllText(DebugLogPath,
                $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static void SaveDebugWav(float[] samples, string originalPath)
    {
        try
        {
            string baseName = Path.GetFileNameWithoutExtension(originalPath);
            string filePath = Path.Combine(DebugFolder,
                $"{baseName}_decoded_{DateTime.Now:HH-mm-ss}.wav");

            using var writer = new WaveFileWriter(filePath, new WaveFormat(TargetSampleRate, 1));
            writer.WriteSamples(samples, 0, samples.Length);

            Log($"Decoded WAV saved: {filePath}");
        }
        catch (Exception ex)
        {
            Log($"Debug save failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Декодирует аудиофайл в моно-сигнал float[] с частотой 16 кГц.
    /// Поддерживает mp3, wav, m4a, flac, aac через NAudio,
    /// ogg/oga — через NAudio.Vorbis.
    /// </summary>
    /// <param name="filePath">Путь к аудиофайлу.</param>
    /// <returns>Массив сэмплов; амплитуды в диапазоне [-1..1].</returns>
    public static float[] DecodeToMono16k(string filePath)
    {
        using WaveStream reader = CreateReader(filePath);
        Log($"Reader format: {reader.WaveFormat.SampleRate} Hz, " +
            $"{reader.WaveFormat.Channels} ch, {reader.WaveFormat.BitsPerSample} bit");

        ISampleProvider provider = reader.ToSampleProvider();

        if (provider.WaveFormat.SampleRate != TargetSampleRate)
            provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);

        if (provider.WaveFormat.Channels > 1)
            provider = new StereoToMonoSampleProvider(provider);

        var result = new List<float>();
        float[] buffer = new float[8192];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
                result.Add(buffer[i]);
        }

        return result.ToArray();
    }

    private static WaveStream CreateReader(string filePath)
    {
        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        Log($"Creating reader for ext='{ext}'");
        return ext switch
        {
            ".ogg" => new VorbisWaveReader(filePath),
            ".oga" => new VorbisWaveReader(filePath),
            _ => new AudioFileReader(filePath)
        };
    }

    private static List<float[]> SplitIntoSegments(float[] samples)
    {
        var segments = new List<float[]>();
        var buffer = new List<float>();
        bool isSpeaking = false;
        int lastSpeechEndIndex = 0;

        int chunkSize = TargetSampleRate * ChunkMs / 1000;

        for (int i = 0; i < samples.Length; i += chunkSize)
        {
            int size = Math.Min(chunkSize, samples.Length - i);
            var chunk = new float[size];
            Array.Copy(samples, i, chunk, 0, size);

            float rms = CalculateRms(chunk);
            bool isSpeech = rms > SilenceThreshold;

            if (isSpeech)
            {
                buffer.AddRange(chunk);
                lastSpeechEndIndex = i + size;
                isSpeaking = true;
            }
            else if (isSpeaking)
            {
                int silenceSamples = (i + size) - lastSpeechEndIndex;
                int silenceMs = silenceSamples * 1000 / TargetSampleRate;

                if (silenceMs > SilenceTimeoutMs)
                {
                    int durationMs = buffer.Count * 1000 / TargetSampleRate;
                    if (durationMs >= MinSpeechMs)
                        segments.Add(buffer.ToArray());

                    buffer.Clear();
                    isSpeaking = false;
                }
            }
        }

        if (buffer.Count > 0)
        {
            int durationMs = buffer.Count * 1000 / TargetSampleRate;
            if (durationMs >= MinSpeechMs)
                segments.Add(buffer.ToArray());
        }

        return segments;
    }

    /// <summary>
    /// Считает среднеквадратичное значение (RMS) сигнала.
    /// Используется и для VAD внутри модуля, и для внешнего анализа
    /// (например, в <c>HarmonicAnalyzer</c>).
    /// </summary>
    public static float CalculateRms(float[] samples)
    {
        if (samples.Length == 0) return 0;
        float sum = 0;
        foreach (var s in samples) sum += s * s;
        return MathF.Sqrt(sum / samples.Length);
    }
}