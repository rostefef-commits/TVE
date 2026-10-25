using LiteDB;

namespace TVE.Core.Storage;

/// <summary>
/// Модель одной реплики (или системного события) в истории говорящего.
/// </summary>
public class Utterance
{
    [BsonId]
    public int Id { get; set; }

    /// <summary>
    /// К какому говорящему относится эта реплика.
    /// </summary>
    public int SpeakerId { get; set; }

    /// <summary>
    /// Текст реплики. Для системных записей — описание события.
    /// </summary>
    public string Text { get; set; } = string.Empty;

    /// <summary>
    /// Момент времени, когда реплика была произнесена (UTC).
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Системная запись (например, «вставлен .mp3: recording.mp3»,
    /// «Имя изменено: unknown_1 → Антюфеев Р.О.») или обычная реплика.
    /// </summary>
    public bool IsSystem { get; set; } = false;
}