using System;
using System.Windows;
using TVE.Core.Services;
using TVE.Core.Settings;
using TVE.Ui.Services;

namespace TVE.Ui
{
    public partial class App : Application
    {
        /// <summary>
        /// Глобальные настройки приложения — доступны отовсюду через App.Settings.
        /// </summary>
        public static AppSettings Settings { get; private set; } = new();

        /// <summary>
        /// Сервис для сохранения/загрузки настроек.
        /// </summary>
        public static SettingsService SettingsService { get; private set; } = new();

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 1. Загружаем настройки
            SettingsService = new SettingsService();
            Settings = SettingsService.Load();

            // 2. Применяем тему (или системную)
            ThemeManager.Apply(Settings.Theme);

            // 3. Уведомляем ModuleStatus о текущей теме — для окна анализа
            ModuleStatus.SetTheme(Settings.Theme);

            // 4. Создаём и показываем главное окно
            var mainWindow = new MainWindow();
            mainWindow.Show();
        }

        /// <summary>
        /// Сохраняет текущие настройки в файл.
        /// </summary>
        public static void SaveSettings()
        {
            SettingsService.Save(Settings);
        }

        /// <summary>
        /// Принудительное завершение процесса при выходе.
        /// Нужно на случай, если фоновые потоки Whisper.net / sherpa-onnx /
        /// watchdog-таймеры остаются висеть и не дают процессу завершиться.
        /// </summary>
        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                // Даём 1.5 секунды на корректное завершение фоновых потоков
                var shutdownThread = new System.Threading.Thread(() =>
                {
                    System.Threading.Thread.Sleep(1500);
                    // Если процесс всё ещё жив — убиваем принудительно
                    Environment.Exit(0);
                });
                shutdownThread.IsBackground = true;
                shutdownThread.Start();

                base.OnExit(e);
            }
            catch
            {
                // Если что-то пошло не так — прибиваем процесс немедленно
                Environment.Exit(0);
            }
        }
    }
}