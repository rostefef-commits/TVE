using System.IO;
using System.Text.Json;

namespace TVE.Core.Settings;

/// <summary>
/// Загружает и сохраняет настройки приложения в JSON-файл.
/// </summary>
public class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    /// <summary>
    /// Путь к файлу настроек по умолчанию: %AppData%\TVE\settings.json
    /// </summary>
    public static string DefaultPath
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TVE");
            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "settings.json");
        }
    }

    public SettingsService(string? path = null)
    {
        _settingsPath = path ?? DefaultPath;
    }

    /// <summary>
    /// Загружает настройки из файла. Если файла нет или он повреждён — возвращает настройки по умолчанию.
    /// </summary>
    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
                return new AppSettings();

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            // Если что-то сломалось — вернуть дефолт, не валить приложение
            return new AppSettings();
        }
    }

    /// <summary>
    /// Сохраняет настройки в файл.
    /// </summary>
    public void Save(AppSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch
        {
            // Тихо игнорируем ошибки записи (например, нет прав)
        }
    }
}