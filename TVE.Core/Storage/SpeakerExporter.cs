using System.Globalization;
using System.Text;
using TVE.Core.Services;

namespace TVE.Core.Storage;

/// <summary>
/// Экспорт голосовых эмбеддингов (Speaker.VoiceEmbedding) в CSV.
///
/// Поддерживает два формата:
///   - Широкий: одна строка на говорящего, 512 числовых колонок.
///   - Длинный: одна строка на компоненту вектора.
///
/// Разделитель - точка с запятой (для Excel в русской локали).
/// Кодировка - UTF-8 с BOM (кириллица читается корректно).
/// Числа - через CultureInfo.InvariantCulture (точка как десятичный разделитель).
/// </summary>
public static class SpeakerExporter
{
    /// <summary>
    /// Экспорт в широком формате:
    /// <c>Имя;v1;v2;...;v512</c> - одна строка на говорящего.
    /// </summary>
    /// <param name="filePath">Путь к CSV-файлу.</param>
    /// <param name="speakers">Говорящие для экспорта.</param>
    public static void ExportWide(string filePath, IReadOnlyList<Speaker> speakers)
    {
        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));

        // Заголовок
        var sb = new StringBuilder();
        sb.Append("Имя");
        for (int i = 1; i <= 512; i++) sb.Append(";v").Append(i);
        writer.WriteLine(sb.ToString());

        // Строки
        var ci = CultureInfo.InvariantCulture;
        foreach (var spk in speakers)
        {
            sb.Clear();
            sb.Append(EscapeCsv(spk.DisplayName));

            for (int i = 0; i < 512; i++)
            {
                float v = i < spk.VoiceEmbedding.Length ? spk.VoiceEmbedding[i] : 0f;
                sb.Append(';').Append(v.ToString("F6", ci));
            }
            writer.WriteLine(sb.ToString());
        }

        EventBus.Ok("Export",
            $"Широкий экспорт: {speakers.Count} говорящих -> {filePath}");
    }

    /// <summary>
    /// Экспорт в длинном формате:
    /// <c>Имя;Номер;Значение</c> - одна строка на компоненту вектора.
    /// </summary>
    /// <param name="filePath">Путь к CSV-файлу.</param>
    /// <param name="speakers">Говорящие для экспорта.</param>
    public static void ExportLong(string filePath, IReadOnlyList<Speaker> speakers)
    {
        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(true));
        writer.WriteLine("Имя;Номер;Значение");

        var ci = CultureInfo.InvariantCulture;
        foreach (var spk in speakers)
        {
            string name = EscapeCsv(spk.DisplayName);

            for (int i = 0; i < spk.VoiceEmbedding.Length; i++)
            {
                writer.Write(name);
                writer.Write(';');
                writer.Write(i + 1);
                writer.Write(';');
                writer.WriteLine(spk.VoiceEmbedding[i].ToString("F6", ci));
            }
        }

        EventBus.Ok("Export",
            $"Длинный экспорт: {speakers.Count} говорящих -> {filePath}");
    }

    /// <summary>
    /// Экранирует точку с запятой и кавычки в имени говорящего.
    /// </summary>
    private static string EscapeCsv(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        if (s.Contains(';') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }
}