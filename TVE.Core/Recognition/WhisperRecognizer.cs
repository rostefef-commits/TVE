using Whisper.net;
using TVE.Core.Services;

namespace TVE.Core.Recognition;

/// <summary>
/// Обёртка над Whisper.net для распознавания речи.
/// Пишет статус модели в ModuleStatus, события — в EventBus.
/// </summary>
public class WhisperRecognizer : IDisposable
{
    private readonly WhisperFactory _factory;
    private readonly WhisperProcessor _processor;

    public WhisperRecognizer(string modelPath, string language = "ru")
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Модель Whisper не найдена: {modelPath}");

        EventBus.Info("Whisper", "Загрузка модели...");

        _factory = WhisperFactory.FromPath(modelPath);

        _processor = _factory.CreateBuilder()
            .WithLanguage(language)
            .Build();

        long size = 0;
        try { size = new FileInfo(modelPath).Length; } catch { }
        ModuleStatus.SetWhisper(modelPath, language, size, loaded: true);

        EventBus.Ok("Whisper", $"Модель загружена ({size / (1024.0 * 1024):F1} МБ, язык={language})");
    }

    public async Task<string> TranscribeAsync(float[] samples)
    {
        if (samples.Length == 0) return string.Empty;

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var textParts = new List<string>();

        await foreach (var segment in _processor.ProcessAsync(samples))
        {
            textParts.Add(segment.Text);
        }

        stopwatch.Stop();
        var result = string.Join(" ", textParts).Trim();

        if (!string.IsNullOrWhiteSpace(result))
        {
            EventBus.Ok("Whisper",
                $"Распознано за {stopwatch.ElapsedMilliseconds} мс: «{Shorten(result)}»");
        }
        else
        {
            EventBus.Warn("Whisper", $"Пустой результат за {stopwatch.ElapsedMilliseconds} мс");
        }

        return result;
    }

    public string Transcribe(float[] samples)
    {
        return TranscribeAsync(samples).GetAwaiter().GetResult();
    }

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length <= 60 ? text : text.Substring(0, 60) + "...";
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
        ModuleStatus.SetWhisper(
            ModuleStatus.WhisperModelPath,
            ModuleStatus.WhisperLanguage,
            ModuleStatus.WhisperModelSize,
            loaded: false);
        EventBus.Info("Whisper", "Модель выгружена");
    }
}