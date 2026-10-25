using SherpaOnnx;
using TVE.Core.Services;

namespace TVE.Core.Speakers;

/// <summary>
/// Извлекает голосовой отпечаток (эмбеддинг) через sherpa-onnx.
/// Пишет статус в ModuleStatus и события в EventBus.
/// </summary>
public class VoiceEmbedder : IDisposable
{
    private readonly SpeakerEmbeddingExtractor _extractor;

    public int Dimension => _extractor.Dim;

    public VoiceEmbedder(string modelPath)
    {
        if (!File.Exists(modelPath))
            throw new FileNotFoundException($"Модель эмбеддингов не найдена: {modelPath}");

        EventBus.Info("Sherpa", "Загрузка модели эмбеддингов...");

        var config = new SpeakerEmbeddingExtractorConfig
        {
            Model = modelPath,
            NumThreads = 2,
            Debug = 0,
            Provider = "cpu"
        };

        _extractor = new SpeakerEmbeddingExtractor(config);

        long size = 0;
        try { size = new FileInfo(modelPath).Length; } catch { }
        ModuleStatus.SetEmbedder(modelPath, size, _extractor.Dim, loaded: true);

        EventBus.Ok("Sherpa", $"Модель загружена (размерность={_extractor.Dim})");
    }

    public float[] Extract(float[] samples)
    {
        if (samples.Length == 0)
            return Array.Empty<float>();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var stream = _extractor.CreateStream();
        stream.AcceptWaveform(16000, samples);
        stream.InputFinished();

        if (!_extractor.IsReady(stream))
        {
            EventBus.Warn("Sherpa", "Эмбеддинг не готов (IsReady=false)");
            return Array.Empty<float>();
        }

        var embedding = _extractor.Compute(stream);
        stopwatch.Stop();

        EventBus.Info("Sherpa",
            $"Эмбеддинг извлечён: {embedding.Length} float за {stopwatch.ElapsedMilliseconds} мс");

        return embedding;
    }

    public void Dispose()
    {
        _extractor?.Dispose();
        ModuleStatus.SetEmbedder(
            ModuleStatus.EmbedderModelPath,
            ModuleStatus.EmbedderModelSize,
            ModuleStatus.EmbedderDimension,
            loaded: false);
        EventBus.Info("Sherpa", "Модель выгружена");
    }
}