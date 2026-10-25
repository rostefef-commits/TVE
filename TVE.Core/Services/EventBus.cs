using System.Collections.Generic;
using System.Linq;

namespace TVE.Core.Services;

/// <summary>
/// Уровень важности записи в логе.
/// Info  — обычное событие (например, "запись началась").
/// Ok    — успешное действие (например, "реплика сохранена").
/// Warn  — предупреждение (например, "плохой звук").
/// Error — ошибка (например, "микрофон не найден").
/// </summary>
public enum LogLevel
{
    Info,
    Ok,
    Warn,
    Error
}

/// <summary>
/// Одна запись лога — то, что «пишется на доску объявлений».
/// </summary>
public class LogEntry
{
    /// <summary>
    /// Когда это событие произошло.
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>
    /// Из какого модуля пришло событие (например, "Whisper", "VAD", "DB").
    /// </summary>
    public string Module { get; set; } = string.Empty;

    /// <summary>
    /// Текст события.
    /// </summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// Уровень важности (Info / Ok / Warn / Error).
    /// </summary>
    public LogLevel Level { get; set; } = LogLevel.Info;

    /// <summary>
    /// Удобное строковое представление времени для отображения в UI.
    /// Например: "14:35:22.123"
    /// </summary>
    public string TimeText => Timestamp.ToString("HH:mm:ss.fff");
}

/// <summary>
/// Глобальная шина событий для логирования.
/// Работает как «доска объявлений»: любой модуль может написать,
/// а окно анализа — подписаться и читать.
/// </summary>
public static class EventBus
{
    /// <summary>
    /// Событие: срабатывает каждый раз, когда кто-то вызывает Log().
    /// Сюда подписываются окна и модули, которые хотят получать уведомления.
    /// Знак "?" означает, что подписчиков может и не быть.
    /// </summary>
    public static event Action<LogEntry>? LogEmitted;

    /// <summary>
    /// Внутренний список последних событий.
    /// Хранится в памяти, чтобы окно анализа могло показать историю.
    /// </summary>
    private static readonly List<LogEntry> _history = new();

    /// <summary>
    /// Замок для потокобезопасности — чтобы несколько потоков
    /// не поломали список при одновременной записи.
    /// </summary>
    private static readonly object _lock = new();

    /// <summary>
    /// Сколько последних записей хранить. Старые удаляются.
    /// 1000 — это ~несколько сотен килобайт, не нагружает память.
    /// </summary>
    private const int MaxHistory = 1000;

    /// <summary>
    /// Возвращает копию истории событий — для отображения в UI.
    /// Копия нужна, чтобы вызывающий код не мог случайно изменить оригинал.
    /// </summary>
    public static IReadOnlyList<LogEntry> GetHistory()
    {
        lock (_lock) return _history.ToList();
    }

    /// <summary>
    /// Основной метод — записывает событие в «доску объявлений».
    /// </summary>
    public static void Log(string module, string message, LogLevel level = LogLevel.Info)
    {
        // 1. Создаём запись
        var entry = new LogEntry
        {
            Module = module,
            Message = message,
            Level = level
        };

        // 2. Добавляем в историю (потокобезопасно)
        lock (_lock)
        {
            _history.Add(entry);

            // Если накопилось больше лимита — удаляем самое старое
            while (_history.Count > MaxHistory)
                _history.RemoveAt(0);
        }

        // 3. Уведомляем всех подписчиков (окна, другие модули)
        //    try/catch — чтобы ошибка в одном подписчике не сломала остальных
        try { LogEmitted?.Invoke(entry); }
        catch { }
    }

    // ===== Удобные сокращения =====
    // Вместо EventBus.Log("VAD", "текст", LogLevel.Ok)
    // можно писать EventBus.Ok("VAD", "текст")

    public static void Info(string module, string message) => Log(module, message, LogLevel.Info);
    public static void Ok(string module, string message) => Log(module, message, LogLevel.Ok);
    public static void Warn(string module, string message) => Log(module, message, LogLevel.Warn);
    public static void Error(string module, string message) => Log(module, message, LogLevel.Error);
}