using System;
using System.Windows;
using Microsoft.Win32;

namespace TVE.Ui.Services;

/// <summary>
/// Управляет применением тем: Light / Dark / System.
/// </summary>
public static class ThemeManager
{
    public const string Light = "Light";
    public const string Dark = "Dark";
    public const string System = "System";

    /// <summary>
    /// Применяет тему по её имени.
    /// </summary>
    public static void Apply(string theme)
    {
        string actual = theme switch
        {
            Light => Light,
            Dark => Dark,
            _ => GetSystemTheme()
        };

        var uri = new Uri($"pack://application:,,,/Themes/{actual}.xaml", UriKind.Absolute);
        var newDict = new ResourceDictionary { Source = uri };

        var app = Application.Current;
        if (app == null) return;

        // Ищем СТАРЫЙ словарь темы — Light.xaml или Dark.xaml (но НЕ Styles.xaml)
        ResourceDictionary? oldTheme = null;
        foreach (var d in app.Resources.MergedDictionaries)
        {
            var src = d.Source?.OriginalString ?? "";
            if (src.EndsWith("Light.xaml") || src.EndsWith("Dark.xaml"))
            {
                oldTheme = d;
                break;
            }
        }
        if (oldTheme != null)
            app.Resources.MergedDictionaries.Remove(oldTheme);

        // Вставляем новую тему ПЕРЕД Styles.xaml, чтобы стили могли её использовать
        app.Resources.MergedDictionaries.Insert(0, newDict);
    }

    /// <summary>
    /// Возвращает актуальную тему системы (Light или Dark) из реестра Windows.
    /// </summary>
    public static string GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i && i == 0) return Dark;
            return Light;
        }
        catch
        {
            return Light; // если не удалось прочитать — светлая
        }
    }
}