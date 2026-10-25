namespace TVE.Core.Settings;

/// <summary>
/// Все пользовательские настройки приложения.
/// Сохраняется в %AppData%\TVE\settings.json.
/// </summary>
public class AppSettings
{
    public int? InputDeviceNumber { get; set; }
    public string Theme { get; set; } = "System";
    public string DatabasePath { get; set; } = string.Empty;
    public bool CreateDiagnosticFiles { get; set; } = false;

    /// <summary>
    /// Порог косинусного сходства для отображения линий связей
    /// на карте голосов и в списке проблемных пар.
    /// Одно значение для обоих окон.
    /// </summary>
    public float SpeakerThreshold { get; set; } = 0.50f;
}