using LiteDB;
using TVE.Core.Analysis;
using TVE.Core.Services;

namespace TVE.Core.Storage;

/// <summary>
/// Обёртка над LiteDB для хранения говорящих и их истории реплик.
/// Пишет события в EventBus.
/// </summary>
public class VoiceDatabase : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Speaker> _speakers;
    private readonly ILiteCollection<Utterance> _utterances;
    private readonly object _lock = new();
    private readonly string _dbPath;

    /// <summary>Порог срабатывания идентификации.</summary>
    public const float IdentifyThreshold = 0.55f;

    /// <summary>Порог обновления эмбеддинга (защита от выбросов).</summary>
    public const float UpdateThreshold = 0.60f;

    public static string DefaultPath
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TVE");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "voices.db");
        }
    }

    public VoiceDatabase(string? dbPath = null)
    {
        _dbPath = dbPath ?? DefaultPath;

        EventBus.Info("DB", $"Открытие базы: {_dbPath}");
        _db = new LiteDatabase(_dbPath);
        _speakers = _db.GetCollection<Speaker>("speakers");
        _utterances = _db.GetCollection<Utterance>("utterances");

        _speakers.EnsureIndex(x => x.DisplayName);
        _speakers.EnsureIndex(x => x.LastSeen);
        _utterances.EnsureIndex(x => x.SpeakerId);
        _utterances.EnsureIndex(x => x.Timestamp);

        RefreshStats();

        EventBus.Ok("DB", $"База открыта: {ModuleStatus.SpeakerCount} голосов, {ModuleStatus.UtteranceCount} реплик");
    }

    // ==========================================
    // ===== ВЫБОРКА =====
    // ==========================================

    public List<Speaker> GetAllSpeakers()
    {
        lock (_lock)
        {
            return _speakers.Query()
                .OrderByDescending(s => s.LastSeen)
                .ToList();
        }
    }

    public List<Speaker> GetAllSpeakers(SpeakerSortOrder order)
    {
        lock (_lock)
        {
            var all = _speakers.FindAll().ToList();

            switch (order)
            {
                case SpeakerSortOrder.NameAsc:
                    return all
                        .OrderBy(s => s.DisplayName,
                            StringComparer.Create(
                                System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
                                ignoreCase: true))
                        .ToList();

                case SpeakerSortOrder.NameDesc:
                    return all
                        .OrderByDescending(s => s.DisplayName,
                            StringComparer.Create(
                                System.Globalization.CultureInfo.GetCultureInfo("ru-RU"),
                                ignoreCase: true))
                        .ToList();

                case SpeakerSortOrder.DateOldFirst:
                    return all.OrderBy(s => s.LastSeen).ToList();

                case SpeakerSortOrder.UtteranceCountDesc:
                    {
                        var counts = new Dictionary<int, int>();
                        foreach (var u in _utterances.FindAll())
                        {
                            counts.TryGetValue(u.SpeakerId, out int c);
                            counts[u.SpeakerId] = c + 1;
                        }

                        return all
                            .OrderByDescending(s => counts.TryGetValue(s.Id, out int c) ? c : 0)
                            .ThenByDescending(s => s.LastSeen)
                            .ToList();
                    }

                case SpeakerSortOrder.DateNewFirst:
                default:
                    return all.OrderByDescending(s => s.LastSeen).ToList();
            }
        }
    }

    public int GetUtteranceCount(int speakerId)
    {
        lock (_lock)
        {
            return _utterances.Count(u => u.SpeakerId == speakerId);
        }
    }

    public Speaker? GetSpeaker(int id)
    {
        lock (_lock) return _speakers.FindById(id);
    }

    public int InsertSpeaker(Speaker speaker)
    {
        lock (_lock)
        {
            var id = _speakers.Insert(speaker).AsInt32;
            EventBus.Ok("DB", $"Новый голос: {speaker.DisplayName} (id={id})");
            return id;
        }
    }

    public void UpdateSpeaker(Speaker speaker)
    {
        lock (_lock) _speakers.Update(speaker);
    }

    public void DeleteSpeaker(int id)
    {
        lock (_lock)
        {
            var spk = _speakers.FindById(id);
            _utterances.DeleteMany(u => u.SpeakerId == id);
            _speakers.Delete(id);
            EventBus.Warn("DB", $"Голос удалён: {spk?.DisplayName ?? id.ToString()} (id={id})");
        }
    }

    public bool NameExists(string displayName, int? excludeSpeakerId = null)
    {
        lock (_lock)
        {
            return _speakers.Query()
                .Where(s => s.DisplayName == displayName)
                .ToList()
                .Any(s => !excludeSpeakerId.HasValue || s.Id != excludeSpeakerId.Value);
        }
    }

    public string NextUnknownName()
    {
        lock (_lock)
        {
            int maxN = 0;
            foreach (var s in _speakers.FindAll())
            {
                var name = s.DisplayName;
                if (name.StartsWith("unknown_", StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(name.Substring("unknown_".Length), out int n))
                {
                    if (n > maxN) maxN = n;
                }
            }
            return $"unknown_{maxN + 1}";
        }
    }

    // ==========================================
    // ===== ИДЕНТИФИКАЦИЯ =====
    // ==========================================

    public Speaker? FindClosestSpeaker(
        float[] embedding,
        float threshold = 0.55f,
        float minMargin = 0.05f)
    {
        var (speaker, _) = FindClosestSpeakerWithScore(embedding, threshold, minMargin);
        return speaker;
    }

    /// <summary>
    /// Возвращает говорящего и его similarity к искомому эмбеддингу.
    /// Нужен для умного слияния (взвешивание по уверенности).
    /// </summary>
    public (Speaker? speaker, float similarity) FindClosestSpeakerWithScore(
        float[] embedding,
        float threshold = 0.55f,
        float minMargin = 0.05f)
    {
        if (embedding.Length == 0) return (null, 0f);

        lock (_lock)
        {
            var candidates = new List<(Speaker spk, float sim)>();

            foreach (var spk in _speakers.FindAll())
            {
                if (spk.VoiceEmbedding.Length != embedding.Length) continue;
                float sim = EmbeddingMath.CosineSimilarity(embedding, spk.VoiceEmbedding);
                candidates.Add((spk, sim));
            }

            if (candidates.Count == 0)
            {
                EventBus.Info("DB", "База пуста - сравнение невозможно");
                return (null, 0f);
            }

            candidates.Sort((a, b) => b.sim.CompareTo(a.sim));
            var best = candidates[0];

            if (best.sim < threshold)
            {
                EventBus.Info("DB",
                    $"Совпадение ниже порога: {best.sim:F3} < {threshold:F2} -> новый голос");
                return (null, best.sim);
            }

            if (candidates.Count >= 2)
            {
                float margin = best.sim - candidates[1].sim;
                if (margin < minMargin)
                {
                    EventBus.Warn("DB",
                        $"Неоднозначно: лучший={best.sim:F3} ({best.spk.DisplayName}), " +
                        $"второй={candidates[1].sim:F3}, разрыв={margin:F3} < {minMargin:F2}");
                    return (null, best.sim);
                }
            }

            EventBus.Ok("DB",
                $"Голос опознан: {best.spk.DisplayName} (сходство {best.sim:F3})");
            return (best.spk, best.sim);
        }
    }

    // ==========================================
    // ===== СЛИЯНИЕ ЭМБЕДДИНГОВ =====
    // ==========================================

    /// <summary>
    /// Старый метод: линейное усреднение с фиксированным весом.
    /// Сохранён для обратной совместимости.
    /// </summary>
    [Obsolete("Используй UpdateEmbeddingSmart для сферического слияния")]
    public void UpdateEmbeddingAveraged(Speaker speaker, float[] newEmbedding, float weightNew = 0.25f)
    {
        if (speaker.VoiceEmbedding.Length != newEmbedding.Length || speaker.VoiceEmbedding.Length == 0)
        {
            speaker.VoiceEmbedding = newEmbedding;
        }
        else
        {
            var updated = new float[speaker.VoiceEmbedding.Length];
            for (int i = 0; i < updated.Length; i++)
                updated[i] = speaker.VoiceEmbedding[i] * (1 - weightNew) + newEmbedding[i] * weightNew;
            speaker.VoiceEmbedding = updated;
        }

        speaker.LastSeen = DateTime.UtcNow;
        UpdateSpeaker(speaker);
    }

    /// <summary>
    /// Умное слияние эмбеддингов через SLERP с адаптивным весом.
    ///
    /// Правила:
    ///   1. Защита от выбросов: если similarity < UpdateThreshold (0.60) — не обновляем.
    ///   2. Вес нового наблюдения зависит от уверенности:
    ///        t = clamp((sim - IdentifyThreshold) / (1 - IdentifyThreshold), 0.05, 0.5)
    ///   3. Слияние через SLERP — результат остаётся на единичной сфере.
    ///   4. Сохраняем историю последнего слияния для визуализации.
    /// </summary>
    public float UpdateEmbeddingSmart(
    Speaker speaker,
    float[] newEmbedding,
    float similarity)
    {
        if (newEmbedding.Length == 0) return -1f;

        // Нормализуем входящий эмбеддинг: модель sherpa может вернуть
        // не единичный вектор, а всё остальное в базе — нормализовано.
        // Без этого шага эмбеддинги оказываются на разных масштабах.
        newEmbedding = EmbeddingMath.L2Normalize(newEmbedding);

        // Случай 1: у говорящего ещё нет эмбеддинга.
        if (speaker.VoiceEmbedding.Length == 0)
        {
            speaker.VoiceEmbedding = newEmbedding;
            speaker.LastSeen = DateTime.UtcNow;
            UpdateSpeaker(speaker);
            EventBus.Info("DB",
                $"Первый эмбеддинг для {speaker.DisplayName} ({newEmbedding.Length} float)");
            return 1f;
        }

        // Случай 2: размерности не совпадают.
        if (speaker.VoiceEmbedding.Length != newEmbedding.Length)
        {
            EventBus.Warn("DB",
                $"Размерности эмбеддингов не совпадают: " +
                $"старый={speaker.VoiceEmbedding.Length}, новый={newEmbedding.Length}");
            return -1f;
        }

        // Защита от выбросов.
        if (similarity < UpdateThreshold)
        {
            EventBus.Info("DB",
                $"Слияние отменено: similarity={similarity:F3} < {UpdateThreshold:F2} " +
                $"(защита от выбросов) для {speaker.DisplayName}");
            speaker.LastSeen = DateTime.UtcNow;
            UpdateSpeaker(speaker);
            return -1f;
        }

        // Вес по уверенности.
        float t = Math.Clamp(
            (similarity - IdentifyThreshold) / (1f - IdentifyThreshold),
            0.05f,
            0.5f);

        // SLERP.
        var oldEmb = speaker.VoiceEmbedding;
        var merged = EmbeddingMath.Slerp(oldEmb, newEmbedding, t);

        float oldVsNew = similarity;
        float oldVsMerged = EmbeddingMath.CosineSimilarity(oldEmb, merged);
        float newVsMerged = EmbeddingMath.CosineSimilarity(newEmbedding, merged);

        // История последнего слияния.
        speaker.LastMergeOld = oldEmb;
        speaker.LastMergeNew = newEmbedding;
        speaker.LastMergeMerged = merged;
        speaker.LastMergeSimilarity = similarity;
        speaker.LastMergeT = t;
        speaker.LastMergeAt = DateTime.UtcNow;

        speaker.VoiceEmbedding = merged;
        speaker.LastSeen = DateTime.UtcNow;
        UpdateSpeaker(speaker);

        EventBus.Ok("DB",
            $"Слияние для {speaker.DisplayName}: sim={similarity:F3}, t={t:F3} | " +
            $"cos(old,merged)={oldVsMerged:F3}, cos(new,merged)={newVsMerged:F3}");

        return oldVsNew;
    }

    // ==========================================
    // ===== РЕПЛИКИ =====
    // ==========================================

    public void AddUtterance(Utterance utterance)
    {
        lock (_lock)
        {
            _utterances.Insert(utterance);

            var spk = _speakers.FindById(utterance.SpeakerId);
            string name = spk?.DisplayName ?? $"id={utterance.SpeakerId}";

            if (utterance.IsSystem)
                EventBus.Info("DB", $"Системная запись для {name}: {utterance.Text}");
            else
                EventBus.Ok("DB", $"Реплика сохранена ({name}): «{Shorten(utterance.Text)}»");
        }
    }

    public List<Utterance> GetUtterancesForSpeaker(int speakerId)
    {
        lock (_lock)
        {
            return _utterances.Query()
                .Where(u => u.SpeakerId == speakerId)
                .OrderBy(u => u.Timestamp)
                .ToList();
        }
    }

    public List<Utterance> GetUtterancesForSpeaker(int speakerId, DateTime? from, DateTime? to)
    {
        var all = GetUtterancesForSpeaker(speakerId);
        if (from.HasValue) all = all.Where(u => u.Timestamp >= from.Value).ToList();
        if (to.HasValue)
        {
            var end = to.Value.Date.AddDays(1);
            all = all.Where(u => u.Timestamp < end).ToList();
        }
        return all;
    }

    public void RefreshStats()
    {
        lock (_lock)
        {
            try
            {
                int speakerCount = _speakers.Count();
                int utteranceCount = _utterances.Count();

                long fileSize = 0;
                try
                {
                    if (File.Exists(_dbPath))
                        fileSize = new FileInfo(_dbPath).Length;
                }
                catch { }

                ModuleStatus.SetDatabase(_dbPath, fileSize, speakerCount, utteranceCount);
            }
            catch { }
        }
    }

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text.Length <= 50 ? text : text.Substring(0, 50) + "...";
    }

    public void Dispose()
    {
        EventBus.Info("DB", "Закрытие базы данных");
        _db.Dispose();
    }
}