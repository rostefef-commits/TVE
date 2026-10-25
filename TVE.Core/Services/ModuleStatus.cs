namespace TVE.Core.Services;

/// <summary>
/// Глобальный «портрет» состояния всех модулей программы.
/// Модули пишут сюда свои данные, окно анализа читает.
/// Не требует ссылок — доступен всем через статическое обращение.
/// </summary>
public static class ModuleStatus
{
    private static readonly object _lock = new();

    // ========== АУДИО ==========
    /// <summary>Есть ли доступный микрофон в системе.</summary>
    public static bool MicAvailable { get; private set; }

    /// <summary>Идёт ли запись прямо сейчас.</summary>
    public static bool MicRecording { get; private set; }

    /// <summary>Имя текущего устройства ввода.</summary>
    public static string MicDeviceName { get; private set; } = "—";

    /// <summary>Текущий уровень RMS (обновляется в реальном времени).</summary>
    public static float MicCurrentRms { get; private set; }

    /// <summary>Частота дискретизации, используемая для записи.</summary>
    public static int SampleRate { get; private set; } = 16000;

    /// <summary>Количество каналов (у нас всегда 1 = моно).</summary>
    public static int Channels { get; private set; } = 1;

    // ========== WHISPER (распознавание речи) ==========
    /// <summary>Путь к модели Whisper.</summary>
    public static string WhisperModelPath { get; private set; } = "—";

    /// <summary>Размер файла модели (байт).</summary>
    public static long WhisperModelSize { get; private set; }

    /// <summary>Язык распознавания (обычно "ru").</summary>
    public static string WhisperLanguage { get; private set; } = "—";

    /// <summary>Загружена ли модель в память.</summary>
    public static bool WhisperLoaded { get; private set; }

    // ========== SHERPA-ONNX (идентификация голоса) ==========
    /// <summary>Путь к модели эмбеддингов.</summary>
    public static string EmbedderModelPath { get; private set; } = "—";

    /// <summary>Размер файла модели (байт).</summary>
    public static long EmbedderModelSize { get; private set; }

    /// <summary>Размерность вектора эмбеддинга (192 или 512).</summary>
    public static int EmbedderDimension { get; private set; }

    /// <summary>Загружена ли модель.</summary>
    public static bool EmbedderLoaded { get; private set; }

    // ========== БАЗА ДАННЫХ ==========
    /// <summary>Путь к файлу базы данных.</summary>
    public static string DbPath { get; private set; } = "—";

    /// <summary>Размер файла БД (байт).</summary>
    public static long DbFileSize { get; private set; }

    /// <summary>Количество голосов в базе.</summary>
    public static int SpeakerCount { get; private set; }

    /// <summary>Количество реплик в БД.</summary>
    public static int UtteranceCount { get; private set; }

    // ========== ПРИЛОЖЕНИЕ ==========
    /// <summary>Момент запуска приложения.</summary>
    public static DateTime StartedAt { get; } = DateTime.Now;

    /// <summary>Текущая тема оформления ("Light", "Dark", "System").</summary>
    public static string Theme { get; private set; } = "System";

    /// <summary>Версия приложения.</summary>
    public static string Version { get; } = "0.1.0";

    // ========== СЕТТЕРЫ ==========
    // Модули вызывают эти методы, чтобы обновить своё состояние.

    public static void SetMic(bool available, bool recording)
    {
        lock (_lock)
        {
            MicAvailable = available;
            MicRecording = recording;
        }
    }

    public static void SetMicDevice(string deviceName)
    {
        lock (_lock) MicDeviceName = string.IsNullOrWhiteSpace(deviceName) ? "—" : deviceName;
    }

    public static void SetMicRms(float rms)
    {
        lock (_lock) MicCurrentRms = rms;
    }

    public static void SetWhisper(string modelPath, string language, long modelSize, bool loaded)
    {
        lock (_lock)
        {
            WhisperModelPath = modelPath;
            WhisperLanguage = language;
            WhisperModelSize = modelSize;
            WhisperLoaded = loaded;
        }
    }

    public static void SetEmbedder(string modelPath, long modelSize, int dimension, bool loaded)
    {
        lock (_lock)
        {
            EmbedderModelPath = modelPath;
            EmbedderModelSize = modelSize;
            EmbedderDimension = dimension;
            EmbedderLoaded = loaded;
        }
    }

    public static void SetDatabase(string path, long fileSize, int speakers, int utterances)
    {
        lock (_lock)
        {
            DbPath = path;
            DbFileSize = fileSize;
            SpeakerCount = speakers;
            UtteranceCount = utterances;
        }
    }

    public static void SetTheme(string theme)
    {
        lock (_lock) Theme = theme ?? "System";
    }
}