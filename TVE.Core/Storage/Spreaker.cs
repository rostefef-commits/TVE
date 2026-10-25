using LiteDB;

namespace TVE.Core.Storage;

/// <summary>
/// Модель говорящего (пользователя), которого программа узнаёт по голосу.
/// </summary>
public class Speaker
{
    /// <summary>
    /// Уникальный идентификатор в базе. Автоматически присваивается LiteDB.
    /// </summary>
    [BsonId]
    public int Id { get; set; }

    /// <summary>
    /// Отображаемое имя. По умолчанию - «unknown_N».
    /// Может быть изменено на ФИО через команду-пароль или вручную.
    /// </summary>
    public string DisplayName { get; set; } = "unknown_0";

    /// <summary>
    /// Описание говорящего (до ~200 слов). Заполняется вручную.
    /// </summary>
    public string Description { get; set; } = "пока тут пусто";

    /// <summary>
    /// Голосовой отпечаток (эмбеддинг). Вектор float, 512 чисел.
    /// Используется для распознавания говорящего.
    /// </summary>
    public float[] VoiceEmbedding { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Дата первой встречи с этим голосом.
    /// </summary>
    public DateTime FirstSeen { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Дата последнего взаимодействия (обновляется при каждой реплике).
    /// </summary>
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    // ==========================================
    // ===== ИСТОРИЯ ПОСЛЕДНЕГО СЛИЯНИЯ =====
    // Используется для визуализации в окне «Анализ».
    // Хранится в БД, чтобы сохраняться между запусками.
    // ==========================================

    /// <summary>
    /// Эмбеддинг ДО последнего слияния.
    /// </summary>
    public float[] LastMergeOld { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Новое наблюдение, которое пришло на слияние.
    /// </summary>
    public float[] LastMergeNew { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Результат слияния (SLERP).
    /// </summary>
    public float[] LastMergeMerged { get; set; } = Array.Empty<float>();

    /// <summary>
    /// Косинусное сходство между old и new в момент слияния.
    /// </summary>
    public float LastMergeSimilarity { get; set; }

    /// <summary>
    /// Коэффициент t (вес нового наблюдения) в момент слияния.
    /// </summary>
    public float LastMergeT { get; set; }

    /// <summary>
    /// Когда произошло последнее слияние.
    /// </summary>
    public DateTime LastMergeAt { get; set; }

    /// <summary>
    /// Есть ли сохранённая история слияния (три вектора ненулевой длины).
    /// </summary>
    public bool HasMergeHistory =>
        LastMergeOld.Length > 0
        && LastMergeNew.Length > 0
        && LastMergeMerged.Length > 0;
}