using System.Text.RegularExpressions;

namespace TVE.Core.Command;

/// <summary>
/// Ищет фразу-пароль «один два три четыре четыре три два один»
/// в нормализованном тексте. Использует скользящее окно слов,
/// чтобы ловить фразу, даже если STT разбил её на несколько сегментов.
/// </summary>
public class CommandDetector
{
    private static readonly Regex CommandRegex = new(
        @"\bодин\s+два\s+три\s+четыре\s+четыре\s+три\s+два\s+один\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Queue<string> _recentWords = new();
    private const int BufferSize = 15;

    private static readonly Dictionary<string, string> DigitToWord = new()
    {
        ["0"] = "ноль",
        ["1"] = "один",
        ["2"] = "два",
        ["3"] = "три",
        ["4"] = "четыре",
        ["5"] = "пять",
        ["6"] = "шесть",
        ["7"] = "семь",
        ["8"] = "восемь",
        ["9"] = "девять",
        ["10"] = "десять",
        ["11"] = "одиннадцать",
        ["12"] = "двенадцать",
        ["13"] = "тринадцать",
        ["14"] = "четырнадцать",
        ["15"] = "пятнадцать",
        ["16"] = "шестнадцать",
        ["17"] = "семнадцать",
        ["18"] = "восемнадцать",
        ["19"] = "девятнадцать",
        ["20"] = "двадцать"
    };

    /// <summary>
    /// Известные ошибки Whisper в произношении чисел.
    /// Меняем их на «канонический» вид перед проверкой регуляркой.
    /// </summary>
    private static readonly Dictionary<string, string> WordFixes = new(StringComparer.OrdinalIgnoreCase)
    {
        // Варианты «четыре»
        ["четыри"] = "четыре",
        ["четырех"] = "четыре",
        ["четыре-"] = "четыре",

        // Варианты «один»
        ["раз"] = "один",
        ["одна"] = "один",
        ["одно"] = "один",
        ["одной"] = "один",

        // Варианты «два»
        ["две"] = "два",
        ["двух"] = "два",

        // Варианты «три»
        ["трі"] = "три",
        ["тры"] = "три",

        // Варианты «семь»
        ["сем"] = "семь",
    };

    /// <summary>
    /// Добавляет текст в буфер и проверяет, не появилась ли команда.
    /// </summary>
    public bool FeedAndCheck(string rawText)
    {
        var normalized = Normalize(rawText);
        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            _recentWords.Enqueue(word);
            if (_recentWords.Count > BufferSize)
                _recentWords.Dequeue();
        }

        var window = string.Join(' ', _recentWords);
        if (CommandRegex.IsMatch(window))
        {
            _recentWords.Clear();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Сбрасывает буфер (например, при старте новой сессии записи).
    /// </summary>
    public void Reset() => _recentWords.Clear();

    private static string Normalize(string text)
    {
        text = text.ToLowerInvariant().Replace('ё', 'е');
        text = Regex.Replace(text, @"[^\w\s]", " ");

        // Заменяем цифры на слова ("1" → "один", "15" → "пятнадцать")
        foreach (var (digit, word) in DigitToWord)
            text = Regex.Replace(text, $@"\b{digit}\b", word);

        // Применяем словарь исправлений (после замены цифр)
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length; i++)
        {
            if (WordFixes.TryGetValue(words[i], out var fixedWord))
                words[i] = fixedWord;
        }
        text = string.Join(' ', words);

        text = Regex.Replace(text, @"\s+", " ").Trim();
        return text;
    }
}