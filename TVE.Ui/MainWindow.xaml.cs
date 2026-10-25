using System;
using System.IO;
using System.Windows;
using TVE.Core.Audio;
using TVE.Core.Recognition;
using TVE.Core.Speakers;
using TVE.Core.Storage;
using TVE.Ui.Views;

namespace TVE.Ui
{
    public partial class MainWindow : Window
    {
        private readonly HomeView _homeView;
        private readonly VoicesView _voicesView;
        private readonly SettingsView _settingsView;

        private readonly VoiceDatabase _database;
        private readonly MicrophoneCapture _capture;
        private readonly WhisperRecognizer _recognizer;
        private readonly VoiceEmbedder _embedder;

        // Ссылка на окно анализа. Храним, чтобы:
        //   1) не открывать второе окно, если уже открыто;
        //   2) активировать его повторным нажатием;
        //   3) закрыть при выходе из главного окна.
        private AnalysisWindow? _analysisWindow;

        public MainWindow()
        {
            InitializeComponent();

            // 1. База данных
            var dbPath = string.IsNullOrWhiteSpace(App.Settings.DatabasePath)
                ? null
                : App.Settings.DatabasePath;
            _database = new VoiceDatabase(dbPath);

            System.Diagnostics.Debug.WriteLine($"[DIAG] After DB: path={TVE.Core.Services.ModuleStatus.DbPath}, speakers={TVE.Core.Services.ModuleStatus.SpeakerCount}");

            // 2. Захват микрофона
            _capture = new MicrophoneCapture();

            // 3. Whisper — модель в папке Models
            string whisperPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Models", "ggml-base.bin");
            _recognizer = new WhisperRecognizer(whisperPath, language: "ru");

            System.Diagnostics.Debug.WriteLine($"[DIAG] After Whisper: loaded={TVE.Core.Services.ModuleStatus.WhisperLoaded}, path={TVE.Core.Services.ModuleStatus.WhisperModelPath}");
            System.Diagnostics.Debug.WriteLine($"[DIAG] Embedder dim={TVE.Core.Services.ModuleStatus.EmbedderDimension}, loaded={TVE.Core.Services.ModuleStatus.EmbedderLoaded}");

            // 4. sherpa-onnx — модель эмбеддингов
            string embedderPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "Models",
                "3dspeaker_speech_eres2net_base_200k_sv_zh-cn_16k-common.onnx");
            _embedder = new VoiceEmbedder(embedderPath);

            System.Diagnostics.Debug.WriteLine($"[DIAG] After Embedder: dim={TVE.Core.Services.ModuleStatus.EmbedderDimension}, loaded={TVE.Core.Services.ModuleStatus.EmbedderLoaded}");

            // 5. Экраны
            _homeView = new HomeView();
            _voicesView = new VoicesView();
            _settingsView = new SettingsView();

            _homeView.Initialize(_capture, _recognizer, _embedder, _database);
            _voicesView.SetDatabase(_database);

            // 6. Подписки на события экранов
            _homeView.NavigateRequested += NavigateTo;
            _homeView.AnalysisRequested += OpenAnalysisWindow;   // ← ДОБАВЛЕНО
            _voicesView.BackRequested += GoHome;
            _settingsView.BackRequested += GoHome;

            MainContent.Content = _homeView;
        }

        private void NavigateTo(string target)
        {
            switch (target)
            {
                case "Home":
                    MainContent.Content = _homeView;
                    break;
                case "Voices":
                    _voicesView.Refresh();
                    MainContent.Content = _voicesView;
                    break;
                case "Settings":
                    _settingsView.Refresh();
                    MainContent.Content = _settingsView;
                    break;
                default:
                    MainContent.Content = _homeView;
                    break;
            }
        }

        private void GoHome() => NavigateTo("Home");

        /// <summary>
        /// Открывает окно «Анализ» (немодальное).
        /// Если окно уже открыто — просто активирует его.
        /// </summary>
        private void OpenAnalysisWindow()
        {
            // Если окно уже существует и не закрыто — выводим на передний план
            if (_analysisWindow != null && _analysisWindow.IsLoaded)
            {
                _analysisWindow.Activate();
                return;
            }

            // Создаём новое окно, передаём ему БД
            _analysisWindow = new AnalysisWindow(_database)
            {
                // Owner = this — окно анализа закрывается вместе с главным
                // и не отображается отдельной иконкой в панели задач
                Owner = this
            };

            // Show() — немодальное окно (главное остаётся активным)
            _analysisWindow.Show();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e)
            => Close();

        protected override void OnClosed(EventArgs e)
        {
            // Закрываем окно анализа, если оно открыто
            try { _analysisWindow?.Close(); } catch { }

            _capture?.Dispose();
            _recognizer?.Dispose();
            _embedder?.Dispose();
            _database?.Dispose();
            base.OnClosed(e);
        }
    }
}