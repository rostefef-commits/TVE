using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using TVE.Core.Analysis;
using TVE.Core.Audio;
using TVE.Core.Services;

namespace TVE.Ui.Views
{
    public partial class HarmonicBatchWindow : Window
    {
        private readonly string _filePath;
        private float[]? _samples;
        private BatchAnalysisResult? _result;

        private PlotModel? _f0PlotModel;
        private DispatcherTimer? _themeWatchdog;
        private bool _lastDark;

        public ObservableCollection<BatchAnalysisPoint> Points { get; } = new();

        public HarmonicBatchWindow(string filePath)
        {
            InitializeComponent();

            _filePath = filePath;
            ResultsGrid.ItemsSource = Points;

            FillFileHeader();
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _lastDark = GetPlotColors().isDark;

            _themeWatchdog = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _themeWatchdog.Tick += OnThemeTick;
            _themeWatchdog.Start();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            _themeWatchdog?.Stop();
            _themeWatchdog = null;
        }

        private void OnThemeTick(object? sender, EventArgs e)
        {
            bool dark = GetPlotColors().isDark;
            if (dark != _lastDark)
            {
                _lastDark = dark;
                if (_result != null) BuildF0Plot();
            }
        }

        // ==========================================
        // ===== ТЕМА =====
        // ==========================================

        private static (bool isDark, OxyColor text, OxyColor border) GetPlotColors()
        {
            bool dark = false;
            try
            {
                if (Application.Current?.Resources["AppBackgroundBrush"]
                    is System.Windows.Media.SolidColorBrush bg)
                {
                    var c = bg.Color;
                    double lum = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                    dark = lum < 0.5;
                }
            }
            catch { }

            return dark
                ? (true,
                   OxyColor.FromRgb(0xEA, 0xEA, 0xEA),
                   OxyColor.FromRgb(0x3A, 0x3A, 0x3A))
                : (false,
                   OxyColor.FromRgb(0x1A, 0x1A, 0x1A),
                   OxyColor.FromRgb(0xA0, 0xA0, 0xA0));
        }

        // ==========================================
        // ===== ФАЙЛ =====
        // ==========================================

        private void FillFileHeader()
        {
            string name = Path.GetFileName(_filePath);
            FileNameText.Text = $"📄  {name}";
            FileStatsText.Text = "Ожидание запуска анализа";
        }

        // ==========================================
        // ===== ЗАПУСК АНАЛИЗА =====
        // ==========================================

        private async void BtnRunAnalysis_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(StatusText.Text)
                && StatusText.Text.Contains("идёт"))
                return;

            BtnRunAnalysis.IsEnabled = false;
            BtnExportCsv.IsEnabled = false;
            BtnExportPng.IsEnabled = false;
            ProgressBar.Value = 0;
            Points.Clear();
            _result = null;

            try
            {
                StatusText.Text = "Декодирование файла...";
                ProgressText.Text = "0 %";

                _samples = await Task.Run(() => Mp3Processor.DecodeToMono16k(_filePath));

                if (_samples.Length < 1024)
                {
                    MessageBox.Show("Файл слишком короткий для анализа.",
                        "Пакетный анализ", MessageBoxButton.OK, MessageBoxImage.Warning);
                    StatusText.Text = "Файл слишком короткий";
                    return;
                }

                double seconds = _samples.Length / 16000.0;
                FileStatsText.Text =
                    $"{_samples.Length:N0} сэмплов   •   " +
                    $"{seconds:F2} сек   •   " +
                    $"16 000 Гц, моно, float32";

                var mode = RadioBySegments.IsChecked == true
                    ? BatchAnalysisMode.BySegments
                    : BatchAnalysisMode.ByFrames;

                StatusText.Text = mode == BatchAnalysisMode.BySegments
                    ? "Пакетный анализ (по сегментам)..."
                    : "Пакетный анализ (по кадрам)...";

                var progress = new Progress<double>(p =>
                {
                    ProgressBar.Value = p;
                    ProgressText.Text = $"{p * 100:F0} %";
                });

                _result = await Task.Run(() =>
                    HarmonicBatchAnalysis.Analyze(_samples, mode, progress));

                foreach (var pt in _result.Points)
                    Points.Add(pt);

                FillMetrics(_result);
                BuildF0Plot();

                StatusText.Text =
                    $"Готово. Точек: {_result.TotalPoints}, " +
                    $"валидных F0: {_result.ValidPoints}";

                BtnExportCsv.IsEnabled = _result.Points.Count > 0;
                BtnExportPng.IsEnabled = _f0PlotModel != null;
            }
            catch (FileNotFoundException)
            {
                StatusText.Text = "Не удалось прочитать файл";
                MessageBox.Show("Файл не найден или повреждён.",
                    "Пакетный анализ", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Ошибка: {ex.Message}";
                MessageBox.Show($"Ошибка анализа:\n{ex.Message}",
                    "Пакетный анализ", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                BtnRunAnalysis.IsEnabled = true;
                ProgressBar.Value = 0;
                ProgressText.Text = "";
            }
        }

        // ==========================================
        // ===== МЕТРИКИ =====
        // ==========================================

        private void FillMetrics(BatchAnalysisResult r)
        {
            TotalPointsText.Text = r.TotalPoints.ToString();
            ValidPointsText.Text = r.ValidPoints.ToString();

            if (r.ValidPoints == 0)
            {
                F0MeanText.Text = "-";
                F0MedianText.Text = "-";
                F0MinMaxText.Text = "-";
                F0StdDevText.Text = "-";
            }
            else
            {
                F0MeanText.Text = $"{r.F0Mean:F1} Гц";
                F0MedianText.Text = $"{r.F0Median:F1} Гц";
                F0MinMaxText.Text = $"{r.F0Min:F0} / {r.F0Max:F0}";
                F0StdDevText.Text = $"{r.F0StdDev:F1} Гц";
            }

            if (r.Mode == BatchAnalysisMode.BySegments
                && r.FormantsMean.Any(f => f > 0f))
            {
                FormantsMeanText.Text = string.Join(" / ",
                    r.FormantsMean.Select(f => f > 0f ? $"{f:F0}" : "-"));
            }
            else
            {
                FormantsMeanText.Text = "-";
            }
        }

        // ==========================================
        // ===== ГРАФИК F0(t) =====
        // ==========================================

        private void BuildF0Plot()
        {
            if (_result == null || _result.Points.Count == 0) return;

            var (_, textColor, borderColor) = GetPlotColors();

            _f0PlotModel = new PlotModel
            {
                Title = _result.Mode == BatchAnalysisMode.BySegments
                    ? "F0 по VAD-сегментам"
                    : "F0(t) по кадрам (64 мс / hop 32 мс)",
                TitleColor = textColor,
                TitleFontSize = 12,
                Background = OxyColors.Transparent,
                PlotAreaBackground = OxyColors.Transparent,
                PlotAreaBorderColor = borderColor,
                DefaultFontSize = 10
            };

            _f0PlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Время (с)",
                Minimum = 0,
                Maximum = _result.TotalSeconds,
                TextColor = textColor,
                TitleColor = textColor,
                AxislineColor = textColor,
                TicklineColor = textColor,
                FontSize = 10,
                MajorGridlineStyle = LineStyle.Dot,
                MinorGridlineStyle = LineStyle.None
            });

            _f0PlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "F0 (Гц)",
                Minimum = 30,
                Maximum = 600,
                TextColor = textColor,
                TitleColor = textColor,
                AxislineColor = textColor,
                TicklineColor = textColor,
                FontSize = 10,
                MajorGridlineStyle = LineStyle.Dot,
                MinorGridlineStyle = LineStyle.None
            });

            var series = new LineSeries
            {
                Color = OxyColor.FromRgb(0x2E, 0x7D, 0xFF),
                StrokeThickness = 1.5,
                Title = "F0"
            };

            foreach (var pt in _result.Points)
            {
                if (pt.F0 > 0f)
                    series.Points.Add(new DataPoint(pt.StartSec, pt.F0));
            }

            if (series.Points.Count > 0)
                _f0PlotModel.Series.Add(series);

            if (_result.F0Median > 0)
            {
                var medianLine = new LineSeries
                {
                    Color = OxyColor.FromRgb(0xFF, 0xC1, 0x07),
                    StrokeThickness = 1.0,
                    LineStyle = LineStyle.Dash,
                    Title = $"Медиана {_result.F0Median:F1} Гц"
                };
                medianLine.Points.Add(new DataPoint(0, _result.F0Median));
                medianLine.Points.Add(new DataPoint(_result.TotalSeconds, _result.F0Median));
                _f0PlotModel.Series.Add(medianLine);
            }

            _f0PlotModel.Legends.Add(new Legend
            {
                LegendPosition = LegendPosition.TopRight,
                LegendPlacement = LegendPlacement.Inside,
                LegendTextColor = textColor
            });

            F0Plot.Model = _f0PlotModel;
            F0Plot.InvalidatePlot(true);
        }

        // ==========================================
        // ===== ЭКСПОРТ CSV =====
        // ==========================================

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            if (_result == null || _result.Points.Count == 0)
            {
                MessageBox.Show("Нет данных для экспорта.",
                    "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт пакетного анализа в CSV",
                Filter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*",
                FileName = $"TVE_batch_{Path.GetFileNameWithoutExtension(_filePath)}_" +
                           $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();
                var ci = CultureInfo.InvariantCulture;

                sb.Append("Файл;Режим;№;Время_с;Длит_с;" +
                          "F0;Cepstrum;HPS;Autocorr;Согласие;RMS;SNR;");
                for (int k = 1; k <= 8; k++) sb.Append($"k{k};");
                for (int i = 1; i <= 4; i++) sb.Append($"F{i}{(i < 4 ? ";" : "")}");
                sb.AppendLine();

                string fname = Path.GetFileName(_filePath);
                string modeStr = _result.Mode == BatchAnalysisMode.BySegments
                    ? "BySegments" : "ByFrames";

                foreach (var pt in _result.Points)
                {
                    sb.Append(EscapeCsv(fname)).Append(';');
                    sb.Append(modeStr).Append(';');
                    sb.Append(pt.Index).Append(';');
                    sb.Append(pt.StartSec.ToString("F3", ci)).Append(';');
                    sb.Append(pt.DurationSec.ToString("F3", ci)).Append(';');

                    sb.Append(pt.F0.ToString("F2", ci)).Append(';');
                    sb.Append(pt.F0Cepstrum.ToString("F2", ci)).Append(';');
                    sb.Append(pt.F0Hps.ToString("F2", ci)).Append(';');
                    sb.Append(pt.F0Autocorrelation.ToString("F2", ci)).Append(';');
                    sb.Append(pt.F0Agreed ? "да" : "нет").Append(';');
                    sb.Append(pt.Rms.ToString("F5", ci)).Append(';');
                    sb.Append(pt.Snr.ToString("F2", ci)).Append(';');

                    for (int k = 0; k < 8; k++)
                    {
                        float v = k < pt.HarmonicFrequencies.Length
                            ? pt.HarmonicFrequencies[k] : 0f;
                        sb.Append(v.ToString("F2", ci)).Append(';');
                    }
                    for (int i = 0; i < 4; i++)
                    {
                        float v = i < pt.Formants.Length ? pt.Formants[i] : 0f;
                        sb.Append(v.ToString("F0", ci));
                        if (i < 3) sb.Append(';');
                    }
                    sb.AppendLine();
                }

                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));

                EventBus.Ok("HarmonicBatch", $"CSV сохранён: {dlg.FileName}");
                StatusText.Text = $"CSV сохранён: {Path.GetFileName(dlg.FileName)}";

                MessageBox.Show(
                    $"CSV сохранён:\n{dlg.FileName}",
                    "Экспорт завершён",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить CSV:\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string EscapeCsv(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            if (s.Contains(';') || s.Contains('"') || s.Contains('\n') || s.Contains('\r'))
                return "\"" + s.Replace("\"", "\"\"") + "\"";
            return s;
        }

        // ==========================================
        // ===== ЭКСПОРТ PNG =====
        // ==========================================

        private void BtnExportPng_Click(object sender, RoutedEventArgs e)
        {
            if (_f0PlotModel == null)
            {
                MessageBox.Show("График ещё не построен.",
                    "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить график F0(t)",
                Filter = "PNG (*.png)|*.png",
                FileName = $"TVE_f0plot_{Path.GetFileNameWithoutExtension(_filePath)}_" +
                           $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var saved = SwitchToDarkPalette(_f0PlotModel);

                try
                {
                    var exporter = new OxyPlot.Wpf.PngExporter
                    {
                        Width = 1600,
                        Height = 700
                    };
                    using var stream = File.Create(dlg.FileName);
                    exporter.Export(_f0PlotModel, stream);
                }
                finally
                {
                    RestorePalette(_f0PlotModel, saved);
                }

                EventBus.Ok("HarmonicBatch", $"PNG сохранён: {dlg.FileName}");
                StatusText.Text = $"PNG сохранён: {Path.GetFileName(dlg.FileName)}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить PNG:\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ==========================================
        // ===== ХЕЛПЕРЫ ДЛЯ ЭКСПОРТА PNG =====
        // ==========================================

        private struct PlotPaletteSnapshot
        {
            public OxyColor Title;
            public OxyColor Border;
            public OxyColor Background;
            public OxyColor PlotAreaBackground;
            public (OxyColor Text, OxyColor Title, OxyColor Axisline, OxyColor Tickline)[] Axes;
            public OxyColor[] LegendTexts;
        }

        private static PlotPaletteSnapshot SwitchToDarkPalette(PlotModel model)
        {
            var snap = new PlotPaletteSnapshot
            {
                Title = model.TitleColor,
                Border = model.PlotAreaBorderColor,
                Background = model.Background,
                PlotAreaBackground = model.PlotAreaBackground,
                Axes = model.Axes
                    .Select(a => (a.TextColor, a.TitleColor, a.AxislineColor, a.TicklineColor))
                    .ToArray(),
                LegendTexts = model.Legends.Select(l => l.LegendTextColor).ToArray()
            };

            var white = OxyColor.FromRgb(0xEA, 0xEA, 0xEA);
            var darkBg = OxyColor.FromRgb(0x1E, 0x1E, 0x1E);
            var darkBorder = OxyColor.FromRgb(0x3A, 0x3A, 0x3A);

            model.TitleColor = white;
            model.PlotAreaBorderColor = darkBorder;
            model.Background = darkBg;
            model.PlotAreaBackground = darkBg;

            foreach (var ax in model.Axes)
            {
                ax.TextColor = white;
                ax.TitleColor = white;
                ax.AxislineColor = white;
                ax.TicklineColor = white;
            }
            foreach (var leg in model.Legends)
                leg.LegendTextColor = white;

            model.InvalidatePlot(false);
            return snap;
        }

        private static void RestorePalette(PlotModel model, PlotPaletteSnapshot snap)
        {
            model.TitleColor = snap.Title;
            model.PlotAreaBorderColor = snap.Border;
            model.Background = snap.Background;
            model.PlotAreaBackground = snap.PlotAreaBackground;

            for (int i = 0; i < model.Axes.Count && i < snap.Axes.Length; i++)
            {
                model.Axes[i].TextColor = snap.Axes[i].Text;
                model.Axes[i].TitleColor = snap.Axes[i].Title;
                model.Axes[i].AxislineColor = snap.Axes[i].Axisline;
                model.Axes[i].TicklineColor = snap.Axes[i].Tickline;
            }
            for (int i = 0; i < model.Legends.Count && i < snap.LegendTexts.Length; i++)
                model.Legends[i].LegendTextColor = snap.LegendTexts[i];

            model.InvalidatePlot(false);
        }

        // ==========================================
        // ===== КНОПКИ ЗАГОЛОВКА =====
        // ==========================================

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
    }
}