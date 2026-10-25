using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Series;
using TVE.Core.Analysis;
using TVE.Core.Services;

namespace TVE.Ui.Views
{
    public partial class HarmonicAnalysisWindow : Window
    {
        private readonly string _filePath;
        private readonly float[] _samples;
        private readonly HarmonicAnalysisResult _result;

        private PlotModel? _spectrogramModel;
        private PlotModel? _waveformModel;

        private DispatcherTimer? _themeWatchdog;
        private bool _lastDark;

        public ObservableCollection<HarmonicRowVm> Harmonics { get; } = new();
        public ObservableCollection<FormantRowVm> Formants { get; } = new();

        public HarmonicAnalysisWindow(
            string filePath,
            float[] samples,
            HarmonicAnalysisResult result)
        {
            InitializeComponent();

            _filePath = filePath;
            _samples = samples;
            _result = result;

            FillHeader();
            FillSummary();
            FillHarmonics();
            FillFormants();

            HarmonicsList.ItemsSource = Harmonics;
            FormantsList.ItemsSource = Formants;

            // ===== Фикс пустых столбцов: пересчёт ширины последней колонки =====
            HarmonicsList.SizeChanged += OnListSizeChanged;
            FormantsList.SizeChanged += OnListSizeChanged;
            Loaded += (_, _) => UpdateLastColumnWidths();
            // ===================================================================

            Loaded += OnLoaded;
            Closed += OnClosed;

            EventBus.Ok("Harmonic",
                $"Открыто окно анализа: {Path.GetFileName(filePath)}, F0={result.F0:F1} Гц");
        }

        // ==========================================
        // ===== РАСТЯГИВАНИЕ ПОСЛЕДНЕЙ КОЛОНКИ =====
        // ==========================================

        private void OnListSizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateLastColumnWidths();
        }

        /// <summary>
        /// Пересчитывает ширину последней колонки каждой таблицы так,
        /// чтобы она занимала всё оставшееся место в ListView.
        /// Вызывается при первом показе и при каждом изменении размера.
        /// </summary>
        private void UpdateLastColumnWidths()
        {
            // Гармоники: Номер(60) + Частота(110) + Ампл.(100) = 270
            StretchLastColumn(HarmonicsList, fixedColumnsWidth: 270);

            // Форманты: Номер(70) = 70
            StretchLastColumn(FormantsList, fixedColumnsWidth: 70);
        }

        /// <summary>
        /// Устанавливает ширину последней колонки в ListView так,
        /// чтобы она заняла всё оставшееся место.
        /// </summary>
        private static void StretchLastColumn(ListView listView, double fixedColumnsWidth)
        {
            if (listView.View is not GridView grid) return;
            if (grid.Columns.Count == 0) return;

            double total = listView.ActualWidth;
            if (total <= 0) return;

            // Запас на вертикальный скроллбар и границы
            const double Reserve = 22.0;

            double lastWidth = total - fixedColumnsWidth - Reserve;
            if (lastWidth < 60) lastWidth = 60;

            grid.Columns[^1].Width = lastWidth;
        }

        // ==========================================
        // ===== ЖИЗНЕННЫЙ ЦИКЛ =====
        // ==========================================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _lastDark = GetPlotColors().isDark;
            BuildPlots();

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
                BuildPlots();
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
        // ===== ПОСТРОЕНИЕ ВСЕХ ГРАФИКОВ =====
        // ==========================================

        private void BuildPlots()
        {
            BuildSpectrogram();
            BuildWaveform();
        }

        // ==========================================
        // ===== СПЕКТРОГРАММА =====
        // ==========================================

        private void BuildSpectrogram()
        {
            var spec = HarmonicAnalyzer.ComputeSpectrogramDb(_samples);
            if (spec.FrameCount == 0) return;

            var (_, textColor, borderColor) = GetPlotColors();

            double axisMax = spec.TotalSeconds * 1.15;
            double harmLabelX = spec.TotalSeconds * 1.01;
            double formLabelX = spec.TotalSeconds * 1.045;

            _spectrogramModel = new PlotModel
            {
                Title = "Спектрограмма",
                TitleColor = textColor,
                TitleFontSize = 14,
                Background = OxyColors.Transparent,
                PlotAreaBackground = OxyColors.Transparent,
                PlotAreaBorderColor = borderColor,
                DefaultFontSize = 11
            };

            var colorAxis = new LinearColorAxis
            {
                Position = AxisPosition.Right,
                Palette = OxyPalettes.Jet(256),
                Minimum = -90,
                Maximum = -20,
                Title = "дБ",
                TextColor = textColor,
                TitleColor = textColor,
                AxislineColor = textColor,
                TicklineColor = textColor,
                TitleFontSize = 11,
                FontSize = 10
            };
            _spectrogramModel.Axes.Add(colorAxis);

            _spectrogramModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Время (с)",
                Minimum = 0,
                Maximum = axisMax,
                TextColor = textColor,
                TitleColor = textColor,
                AxislineColor = textColor,
                TicklineColor = textColor,
                TitleFontSize = 11,
                FontSize = 10,
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None
            });

            _spectrogramModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Частота (Гц)",
                Minimum = 0,
                Maximum = spec.MaxFreqHz,
                TextColor = textColor,
                TitleColor = textColor,
                AxislineColor = textColor,
                TicklineColor = textColor,
                TitleFontSize = 11,
                FontSize = 10,
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None
            });

            var heat = new HeatMapSeries
            {
                X0 = 0,
                X1 = spec.TotalSeconds,
                Y0 = 0,
                Y1 = spec.MaxFreqHz,
                Data = spec.Data,
                RenderMethod = HeatMapRenderMethod.Bitmap,
                Interpolate = false
            };
            _spectrogramModel.Series.Add(heat);

            AddLabeledLines(
                _spectrogramModel,
                _result.HarmonicFrequencies,
                prefix: "k",
                lineColor: OxyColors.White,
                style: LineStyle.Dash,
                thickness: 2.0,
                xStart: 0,
                xEnd: spec.TotalSeconds,
                maxFreq: spec.MaxFreqHz,
                labelX: harmLabelX);

            AddLabeledLines(
                _spectrogramModel,
                _result.Formants,
                prefix: "F",
                lineColor: OxyColor.FromRgb(0xFF, 0x20, 0x20),
                style: LineStyle.Solid,
                thickness: 2.5,
                xStart: 0,
                xEnd: spec.TotalSeconds,
                maxFreq: spec.MaxFreqHz,
                labelX: formLabelX);

            SpectrogramPlot.Model = _spectrogramModel;
            SpectrogramPlot.InvalidatePlot(true);
        }

        private static void AddLabeledLines(
            PlotModel model,
            float[]? frequencies,
            string prefix,
            OxyColor lineColor,
            LineStyle style,
            double thickness,
            double xStart,
            double xEnd,
            double maxFreq,
            double labelX)
        {
            if (frequencies == null) return;

            for (int i = 0; i < frequencies.Length; i++)
            {
                float freq = frequencies[i];
                if (freq <= 0f || freq > maxFreq) continue;

                var line = new LineSeries
                {
                    Color = lineColor,
                    StrokeThickness = thickness,
                    LineStyle = style
                };
                line.Points.Add(new DataPoint(xStart, freq));
                line.Points.Add(new DataPoint(xEnd, freq));
                model.Series.Add(line);

                var label = new TextAnnotation
                {
                    Text = $"{prefix}{i + 1}",
                    TextPosition = new DataPoint(labelX, freq),
                    TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Left,
                    TextVerticalAlignment = OxyPlot.VerticalAlignment.Middle,
                    TextColor = lineColor,
                    Background = OxyColor.FromAColor(180, OxyColor.FromRgb(0x18, 0x18, 0x18)),
                    Stroke = OxyColors.Transparent,
                    StrokeThickness = 0,
                    Padding = new OxyThickness(4, 2, 4, 2),
                    FontSize = 11,
                    FontWeight = OxyPlot.FontWeights.Bold,
                    ClipByXAxis = false,
                    ClipByYAxis = false
                };
                model.Annotations.Add(label);
            }
        }

        // ==========================================
        // ===== WAVEFORM =====
        // ==========================================

        private void BuildWaveform()
        {
            if (_samples.Length == 0) return;

            var (_, textColor, borderColor) = GetPlotColors();

            _waveformModel = new PlotModel
            {
                Title = "Сигнал",
                TitleColor = textColor,
                TitleFontSize = 12,
                Background = OxyColors.Transparent,
                PlotAreaBackground = OxyColors.Transparent,
                PlotAreaBorderColor = borderColor,
                DefaultFontSize = 10
            };

            double totalSeconds = (double)_samples.Length / HarmonicAnalyzer.SampleRate;

            _waveformModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = null,
                Minimum = 0,
                Maximum = totalSeconds,
                TextColor = textColor,
                AxislineColor = textColor,
                TicklineColor = textColor,
                FontSize = 10,
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None
            });

            _waveformModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Minimum = -1,
                Maximum = 1,
                TextColor = textColor,
                AxislineColor = textColor,
                TicklineColor = textColor,
                FontSize = 10,
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None,
                IsZoomEnabled = false,
                IsPanEnabled = false
            });

            var line = new LineSeries
            {
                Color = OxyColor.FromRgb(0x2E, 0x7D, 0xFF),
                StrokeThickness = 1.0
            };

            const int MaxPoints = 3000;
            int step = Math.Max(1, _samples.Length / MaxPoints);
            double sr = HarmonicAnalyzer.SampleRate;

            for (int i = 0; i < _samples.Length; i += step)
            {
                int end = Math.Min(i + step, _samples.Length);
                float maxAbs = 0f;
                float value = 0f;
                for (int j = i; j < end; j++)
                {
                    float abs = Math.Abs(_samples[j]);
                    if (abs > maxAbs) { maxAbs = abs; value = _samples[j]; }
                }
                line.Points.Add(new DataPoint(i / sr, value));
            }

            _waveformModel.Series.Add(line);
            WaveformPlot.Model = _waveformModel;
            WaveformPlot.InvalidatePlot(true);
        }

        // ==========================================
        // ===== ЗАПОЛНЕНИЕ ПОЛЕЙ =====
        // ==========================================

        private void FillHeader()
        {
            string name = Path.GetFileName(_filePath);
            FileNameText.Text = $"📄  {name}";

            double seconds = _samples.Length / 16000.0;
            FileStatsText.Text =
                $"{_samples.Length:N0} сэмплов   •   " +
                $"{seconds:F2} сек   •   " +
                $"16 000 Гц, моно, float32";
        }

        private void FillSummary()
        {
            F0Text.Text = _result.F0 > 0
                ? $"{_result.F0:F1} Гц"
                : "не определена";

            F0DetailText.Text =
                $"Кепстр: {_result.F0Cepstrum:F1}  •  " +
                $"HPS: {_result.F0Hps:F1}  •  " +
                $"АКФ: {_result.F0Autocorrelation:F1}";

            RmsText.Text = _result.Rms.ToString("F5");
            SnrText.Text = $"{_result.Snr:F1} дБ";
            F0AgreedText.Text = _result.F0Agreed ? "да" : "нет";
        }

        private void FillHarmonics()
        {
            for (int k = 0; k < _result.HarmonicFrequencies.Length; k++)
            {
                Harmonics.Add(new HarmonicRowVm
                {
                    Number = k + 1,
                    Frequency = _result.HarmonicFrequencies[k],
                    Amplitude = _result.HarmonicAmplitudes[k],
                    AmplitudeDb = _result.HarmonicAmplitudesDb[k]
                });
            }
        }

        private void FillFormants()
        {
            for (int i = 0; i < _result.Formants.Length; i++)
            {
                Formants.Add(new FormantRowVm
                {
                    Number = i + 1,
                    Frequency = _result.Formants[i]
                });
            }
        }

        // ==========================================
        // ===== ЭКСПОРТ CSV =====
        // ==========================================

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Экспорт гармонического анализа в CSV",
                Filter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*",
                FileName = $"TVE_harmonic_{Path.GetFileNameWithoutExtension(_filePath)}_" +
                           $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();

                sb.Append("Файл;F0;Cepstrum;HPS;Autocorr;RMS;SNR");
                for (int k = 1; k <= 8; k++) sb.Append($";k{k}");
                for (int i = 1; i <= 4; i++) sb.Append($";F{i}");
                sb.AppendLine();

                var ci = CultureInfo.InvariantCulture;

                sb.Append(EscapeCsv(Path.GetFileName(_filePath)));
                sb.Append(';').Append(_result.F0.ToString("F2", ci));
                sb.Append(';').Append(_result.F0Cepstrum.ToString("F2", ci));
                sb.Append(';').Append(_result.F0Hps.ToString("F2", ci));
                sb.Append(';').Append(_result.F0Autocorrelation.ToString("F2", ci));
                sb.Append(';').Append(_result.Rms.ToString("F5", ci));
                sb.Append(';').Append(_result.Snr.ToString("F2", ci));

                for (int k = 0; k < 8; k++)
                {
                    float f = k < _result.HarmonicFrequencies.Length
                        ? _result.HarmonicFrequencies[k] : 0f;
                    sb.Append(';').Append(f.ToString("F2", ci));
                }
                for (int i = 0; i < 4; i++)
                {
                    float f = i < _result.Formants.Length ? _result.Formants[i] : 0f;
                    sb.Append(';').Append(f.ToString("F0", ci));
                }
                sb.AppendLine();

                File.WriteAllText(dlg.FileName, sb.ToString(), new UTF8Encoding(true));

                EventBus.Ok("Harmonic", $"CSV сохранён: {dlg.FileName}");

                MessageBox.Show(
                    $"CSV сохранён:\n{dlg.FileName}",
                    "Экспорт",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось сохранить CSV:\n{ex.Message}",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
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
            if (_spectrogramModel == null)
            {
                MessageBox.Show("Спектрограмма ещё не построена.",
                    "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить спектрограмму",
                Filter = "PNG (*.png)|*.png",
                FileName = $"TVE_spectrogram_{Path.GetFileNameWithoutExtension(_filePath)}_" +
                           $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var saved = SwitchToDarkPalette(_spectrogramModel);

                try
                {
                    var exporter = new OxyPlot.Wpf.PngExporter
                    {
                        Width = 1600,
                        Height = 900
                    };
                    using var stream = File.Create(dlg.FileName);
                    exporter.Export(_spectrogramModel, stream);
                }
                finally
                {
                    RestorePalette(_spectrogramModel, saved);
                }

                EventBus.Ok("Harmonic", $"PNG сохранён: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось сохранить PNG:\n{ex.Message}",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
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

    // ==========================================
    // ===== VIEW-МОДЕЛИ =====
    // ==========================================

    public class HarmonicRowVm
    {
        public int Number { get; set; }
        public float Frequency { get; set; }
        public float Amplitude { get; set; }
        public float AmplitudeDb { get; set; }

        public string NumberText => $"k{Number}";
        public string FrequencyText => Frequency >= 1f ? $"{Frequency:F1} Гц" : "-";
        public string AmplitudeText => Amplitude > 0f ? $"{Amplitude:F4}" : "-";
        public string AmplitudeDbText => AmplitudeDb > -100f ? $"{AmplitudeDb:F1} дБ" : "-";
    }

    public class FormantRowVm
    {
        public int Number { get; set; }
        public float Frequency { get; set; }

        public string NumberText => $"F{Number}";
        public string FrequencyText => Frequency > 0f
            ? $"{Frequency:F0} Гц"
            : "не найдена";
    }
}