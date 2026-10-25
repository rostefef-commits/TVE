using System.Text.RegularExpressions;

namespace TVE.Core.Command;

/// <summary>
/// Извлекает ФИО из распознанной фразы.
/// Убирает вводные слова, приводит регистр к «Фамилия Имя Отчество».
/// </summary>
public static class NameExtractor
{
    private static readonly Dictionary<string, string> NameFixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Сюда добавляй исправления частых ошибок Whisper:
        // ["антуфеев"] = "Антюфеев",
        // ["ростеслав"] = "Ростислав",
    };

    /// <summary>
    /// Извлекает ФИО из текста. Возвращает строку в формате
    /// «Фамилия Имя Отчество», либо пустую строку, если это не похоже на ФИО.
    /// </summary>
    public static string Extract(string text)
    {
        // Убираем пунктуацию и лишние пробелы
        text = Regex.Replace(text, @"[^\w\s\-]", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();

        // Убираем вводные конструкции
        text = Regex.Replace(
            text,
            @"^(меня зовут|мо[её] имя|это|я|зови меня|запиши меня как|я есть)\s+",
            "",
            RegexOptions.IgnoreCase).Trim();

        // Убираем хвостовые частицы
        text = Regex.Replace(
            text,
            @"\s+(пожалуйста|вот|да|ну|и всё|всё)$",
            "",
            RegexOptions.IgnoreCase).Trim();

        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Применяем словарь исправлений
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (NameFixes.TryGetValue(words[i], out var fixedWord))
                words[i] = fixedWord;
        }

        // Приводим регистр
        var capitalized = words.Select(Capitalize).ToArray();

        // Проверяем, что похоже на ФИО
        if (!LooksLikeFullName(capitalized))
            return string.Empty;

        // Если три слова — нормализуем порядок до «Фамилия Имя Отчество»
        if (capitalized.Length == 3)
            return ReorderToFio(capitalized);

        return string.Join(' ', capitalized);
    }

    private static string Capitalize(string word)
    {
        if (string.IsNullOrEmpty(word)) return word;
        var culture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        return char.ToUpper(word[0], culture) + word.Substring(1).ToLower(culture);
    }

    private static bool LooksLikeFullName(string[] words)
    {
        if (words.Length < 2 || words.Length > 4) return false;
        return words.All(w => w.Length >= 2 && w.All(char.IsLetter));
    }

    private static bool LooksLikePatronymic(string w)
        => w.EndsWith("вич", StringComparison.OrdinalIgnoreCase)
        || w.EndsWith("вна", StringComparison.OrdinalIgnoreCase)
        || w.EndsWith("ична", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeSurname(string w)
        => w.EndsWith("ов", StringComparison.OrdinalIgnoreCase)
        || w.EndsWith("ев", StringComparison.OrdinalIgnoreCase)
        || w.EndsWith("ин", StringComparison.OrdinalIgnoreCase)
        || w.EndsWith("ый", StringComparison.OrdinalIgnoreCase)
        || w.EndsWith("ий", StringComparison.OrdinalIgnoreCase)
        || w.EndsWith("ко", StringComparison.OrdinalIgnoreCase);

    private static string ReorderToFio(string[] words)
    {
        int patronIdx = Array.FindIndex(words, LooksLikePatronymic);
        if (patronIdx < 0) return string.Join(' ', words);

        var others = words.Where((_, i) => i != patronIdx).ToArray();
        string surname = others.FirstOrDefault(LooksLikeSurname) ?? others[0];
        string given = others.FirstOrDefault(w => w != surname) ?? others[1];
        return $"{surname} {given} {words[patronIdx]}";
    }
}