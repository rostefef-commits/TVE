using OxyPlot;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TVE.Core.Analysis;
using TVE.Core.Audio;
using TVE.Core.Services;
using TVE.Core.Speakers;
using TVE.Core.Storage;
using TVE.Ui.Services;

namespace TVE.Ui.Views
{
    public partial class AnalysisWindow : Window
    {
        private readonly VoiceDatabase? _db;
        private DispatcherTimer? _autoRefreshTimer;

        private ObservableCollection<LogEntry> _allLogs = new();
        private ICollectionView? _logView;
        private string _selectedModule = "Все";
        private string _selectedLevel = "Все";

        private PlotModel? _plotModel;
        private LineSeries? _seriesA;
        private LineSeries? _seriesB;
        private LinearAxis? _xAxis;
        private LinearAxis? _yAxis;
        private Legend? _legend;
        private bool _currentDarkTheme;

        private const int PlaceholderSpeakerId = -1;
        private int _selectedColumnIndex = -1;
        private List<Speaker> _lastSpeakers = new();

        private int _lastKnownSpeakerCount = -1;

        private PlotModel? _voiceMapModel;
        private (double X, double Y)[] _voiceMapPoints = Array.Empty<(double, double)>();
        private int[] _voiceMapClusters = Array.Empty<int>();
        private Speaker[] _voiceMapSpeakers = Array.Empty<Speaker>();

        private float _lineThreshold = 0.50f;
        private int _highlightSpeakerIndex = -1;
        private int _lastClickIndex = -1;
        private DateTime _lastClickTime = DateTime.MinValue;

        // ===== Слияние эмбеддингов =====
        private Speaker? _mergeSelectedSpeaker;
        private PlotModel? _mergePlotModel;

        private static readonly OxyColor[] ClusterColors =
        {
            OxyColor.FromRgb(0x2E, 0x7D, 0xFF),
            OxyColor.FromRgb(0xE5, 0x39, 0x35),
            OxyColor.FromRgb(0x43, 0xA0, 0x47),
            OxyColor.FromRgb(0xF9, 0xA8, 0x25),
            OxyColor.FromRgb(0x9C, 0x27, 0xB0),
            OxyColor.FromRgb(0x00, 0xAC, 0xC1),
            OxyColor.FromRgb(0xFF, 0x70, 0x43),
        };

        public class MatrixRowData
        {
            public string Name { get; set; } = "";
            public SolidColorBrush RowBackground { get; set; } = Brushes.Transparent;
            public List<MatrixCellData> Cells { get; set; } = new();
        }

        public class MatrixCellData
        {
            public string Display { get; set; } = "";
            public SolidColorBrush Background { get; set; } = Brushes.Transparent;
            public SolidColorBrush Foreground { get; set; } = Brushes.Black;
        }

        public AnalysisWindow(VoiceDatabase? db)
        {
            InitializeComponent();
            _db = db;
            _currentDarkTheme = IsDarkTheme();
            Loaded += AnalysisWindow_Loaded;
            Closed += AnalysisWindow_Closed;
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

        private void AnalysisWindow_Loaded(object sender, RoutedEventArgs e)
        {
            _lineThreshold = App.Settings.SpeakerThreshold;
            InitializeLineThresholdCombo();

            UpdateAll();
            _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autoRefreshTimer.Tick += (s, ev) => OnTick();
            _autoRefreshTimer.Start();

            _allLogs = new ObservableCollection<LogEntry>(EventBus.GetHistory());
            _logView = CollectionViewSource.GetDefaultView(_allLogs);
            _logView.Filter = LogFilter;
            LogList.ItemsSource = _logView;
            EventBus.LogEmitted += OnLogEmitted;
            ScrollLogToEnd();

            InitPlot();
            InitVoiceMap();
            InitMergeTab();
            LoadSpeakersIntoCombos();
            RefreshMatrix();

            _lastKnownSpeakerCount = ModuleStatus.SpeakerCount;
        }

        private void AnalysisWindow_Closed(object? sender, EventArgs e)
        {
            _autoRefreshTimer?.Stop();
            _autoRefreshTimer = null;
            EventBus.LogEmitted -= OnLogEmitted;
        }

        private void OnTick()
        {
            try
            {
                var actualTheme = App.Settings.Theme ?? "System";
                if (ModuleStatus.Theme != actualTheme)
                    ModuleStatus.SetTheme(actualTheme);
            }
            catch { }

            UpdateAll();

            if (Math.Abs(App.Settings.SpeakerThreshold - _lineThreshold) > 0.001f)
            {
                _lineThreshold = App.Settings.SpeakerThreshold;
                SelectThresholdInCombo(_lineThreshold);

                if (_voiceMapModel != null && _highlightSpeakerIndex < 0)
                    DrawSimilarityLinesOnly();
            }

            bool dark = IsDarkTheme();
            if (dark != _currentDarkTheme)
            {
                _currentDarkTheme = dark;
                ApplyPlotTheme();
                ApplyVoiceMapTheme();
                UpdateAnnotationsTheme();

                // Перестраиваем график слияния под новую тему
                if (_mergeSelectedSpeaker != null && _mergeSelectedSpeaker.HasMergeHistory)
                    UpdateMergeView();
            }

            if (_lastKnownSpeakerCount != ModuleStatus.SpeakerCount)
            {
                _lastKnownSpeakerCount = ModuleStatus.SpeakerCount;

                if (_selectedColumnIndex >= ModuleStatus.SpeakerCount)
                    _selectedColumnIndex = -1;

                _highlightSpeakerIndex = -1;

                RefreshMatrix();
                LoadSpeakersIntoCombos();
                DrawVoiceMap();

                // Обновляем список на вкладке «Слияние».
                int previousId = (_mergeSelectedSpeaker?.Id) ?? -1;
                InitMergeTab();
                if (previousId >= 0 && MergeSpeakerCombo.ItemsSource is List<Speaker> list)
                {
                    int idx = list.FindIndex(s => s.Id == previousId);
                    if (idx >= 0) MergeSpeakerCombo.SelectedIndex = idx;
                }
            }
        }

        // ==========================================
        // ===== МОДУЛИ =====
        // ==========================================

        private void UpdateAll()
        {
            MicDeviceText.Text = ModuleStatus.MicDeviceName;
            if (!ModuleStatus.MicAvailable)
                MicStatusText.Text = "❌ Устройство не найдено";
            else if (ModuleStatus.MicRecording)
                MicStatusText.Text = "🟢 Идёт запись";
            else
                MicStatusText.Text = "⏸ Ожидание";

            MicFormatText.Text = $"{ModuleStatus.SampleRate} Hz, {ModuleStatus.Channels} кан. (моно), float";
            MicRmsText.Text = ModuleStatus.MicCurrentRms.ToString("F5");

            WhisperModelText.Text = ShortenPath(ModuleStatus.WhisperModelPath);
            WhisperSizeText.Text = FormatSize(ModuleStatus.WhisperModelSize);
            WhisperLanguageText.Text = ModuleStatus.WhisperLanguage;
            WhisperStatusText.Text = ModuleStatus.WhisperLoaded ? "✅ Загружена" : "⏸ Не загружена";

            EmbedderModelText.Text = ShortenPath(ModuleStatus.EmbedderModelPath);
            EmbedderSizeText.Text = FormatSize(ModuleStatus.EmbedderModelSize);
            EmbedderDimText.Text = ModuleStatus.EmbedderDimension > 0
                ? $"{ModuleStatus.EmbedderDimension} float" : "-";
            EmbedderStatusText.Text = ModuleStatus.EmbedderLoaded ? "✅ Загружена" : "⏸ Не загружена";

            try { _db?.RefreshStats(); } catch { }
            DbPathText.Text = ModuleStatus.DbPath;
            DbSizeText.Text = FormatSize(ModuleStatus.DbFileSize);
            DbSpeakersText.Text = ModuleStatus.SpeakerCount.ToString();
            DbUtterancesText.Text = ModuleStatus.UtteranceCount.ToString();

            AppVersionText.Text = ModuleStatus.Version;
            AppThemeText.Text = TranslateTheme(ModuleStatus.Theme);
            AppUptimeText.Text = FormatUptime(DateTime.Now - ModuleStatus.StartedAt);
        }

        // ==========================================
        // ===== ЛОГ =====
        // ==========================================

        private void OnLogEmitted(LogEntry entry)
        {
            Dispatcher.Invoke(() =>
            {
                _allLogs.Add(entry);
                ScrollLogToEnd();
            });
        }

        private bool LogFilter(object obj)
        {
            if (obj is not LogEntry entry) return false;
            if (_selectedModule != "Все" && entry.Module != _selectedModule) return false;
            if (_selectedLevel != "Все" && entry.Level.ToString() != _selectedLevel) return false;
            return true;
        }

        private void ModuleFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_logView == null) return;
            _selectedModule = (ModuleFilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Все";
            _logView.Refresh();
        }

        private void LevelFilter_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_logView == null) return;
            _selectedLevel = (LevelFilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Все";
            _logView.Refresh();
        }

        private void ScrollLogToEnd()
        {
            if (LogList.Items.Count == 0) return;
            try { LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]); }
            catch { }
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e) => _allLogs.Clear();

        private void SaveLog_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Текстовые файлы (*.txt)|*.txt",
                FileName = $"TVE_log_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("TVE - экспорт лога");
                sb.AppendLine($"Дата: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
                sb.AppendLine($"Записей: {_allLogs.Count}");
                sb.AppendLine(new string('-', 80));
                sb.AppendLine();
                foreach (var entry in _allLogs)
                    sb.AppendLine($"[{entry.TimeText}] [{entry.Level,-5}] [{entry.Module}] {entry.Message}");
                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                EventBus.Ok("UI", $"Лог сохранён: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить файл:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ==========================================
        // ===== МАТРИЦА СХОДСТВА =====
        // ==========================================

        private void MatrixGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var header = FindVisualParent<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (header == null) return;

            int colIdx = MatrixGrid.Columns.IndexOf(header.Column);
            if (colIdx <= 0) return;

            int speakerIdx = colIdx - 1;
            if (speakerIdx < 0 || speakerIdx >= _lastSpeakers.Count) return;

            _selectedColumnIndex = (_selectedColumnIndex == speakerIdx) ? -1 : speakerIdx;
            RefreshMatrix();
            e.Handled = true;
        }

        private void MatrixGrid_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            var header = FindVisualParent<DataGridColumnHeader>(e.OriginalSource as DependencyObject);
            if (header == null) return;
            _selectedColumnIndex = -1;
            RefreshMatrix();
            e.Handled = true;
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T t) return t;
                try { child = VisualTreeHelper.GetParent(child); }
                catch { return null; }
            }
            return null;
        }

        private void RefreshMatrix()
        {
            if (_db == null) { MatrixGrid.ItemsSource = null; return; }
            try
            {
                _lastSpeakers = _db.GetAllSpeakers();
                int n = _lastSpeakers.Count;
                MatrixGrid.Columns.Clear();

                var nameCol = new DataGridTextColumn
                {
                    Header = "Голос",
                    Binding = new Binding("Name"),
                    Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                    MinWidth = 100
                };
                nameCol.ElementStyle = BuildNameStyle();
                MatrixGrid.Columns.Add(nameCol);

                for (int i = 0; i < n; i++)
                {
                    string baseHeader = ShortenName(_lastSpeakers[i].DisplayName);
                    string headerText = (i == _selectedColumnIndex) ? $"✓ {baseHeader}" : baseHeader;

                    var col = new DataGridTemplateColumn
                    {
                        Header = headerText,
                        Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                        MinWidth = 80,
                        CellTemplate = BuildCellTemplate(i)
                    };
                    MatrixGrid.Columns.Add(col);
                }

                var rowList = new List<(int speakerIdx, float simToSelected, MatrixRowData row)>();
                for (int i = 0; i < n; i++)
                {
                    var row = new MatrixRowData { Name = _lastSpeakers[i].DisplayName };
                    for (int j = 0; j < n; j++)
                    {
                        float sim = ComputeSimilarity(_lastSpeakers[i].VoiceEmbedding, _lastSpeakers[j].VoiceEmbedding);
                        row.Cells.Add(new MatrixCellData
                        {
                            Display = sim.ToString("F3"),
                            Background = GetSimilarityBrush(sim),
                            Foreground = GetSimilarityForeground(sim)
                        });
                    }

                    float simToSelected = (_selectedColumnIndex >= 0 && _selectedColumnIndex < n)
                        ? ComputeSimilarity(_lastSpeakers[i].VoiceEmbedding, _lastSpeakers[_selectedColumnIndex].VoiceEmbedding)
                        : 0;

                    rowList.Add((i, simToSelected, row));
                }

                if (_selectedColumnIndex >= 0 && _selectedColumnIndex < n)
                    rowList = rowList.OrderByDescending(x => x.simToSelected).ToList();

                var rows = new ObservableCollection<MatrixRowData>();
                foreach (var (speakerIdx, simToSelected, row) in rowList)
                {
                    if (_selectedColumnIndex >= 0 && _selectedColumnIndex < n)
                    {
                        if (speakerIdx == _selectedColumnIndex)
                            row.RowBackground = new SolidColorBrush(Color.FromRgb(0x60, 0x60, 0x60));
                        else if (simToSelected >= 0.75f)
                            row.RowBackground = new SolidColorBrush(Color.FromArgb(0x40, 0xC6, 0x28, 0x28));
                        else if (simToSelected >= 0.55f)
                            row.RowBackground = new SolidColorBrush(Color.FromArgb(0x40, 0xEF, 0x6C, 0x00));
                        else if (simToSelected >= 0.35f)
                            row.RowBackground = new SolidColorBrush(Color.FromArgb(0x40, 0xF9, 0xA8, 0x25));
                        else
                            row.RowBackground = new SolidColorBrush(Color.FromArgb(0x40, 0x2E, 0x7D, 0x32));
                    }
                    rows.Add(row);
                }

                MatrixGrid.ItemsSource = rows;
            }
            catch (Exception ex)
            {
                EventBus.Error("UI", $"Ошибка построения матрицы: {ex.Message}");
            }
        }

        private static Style BuildNameStyle()
        {
            var style = new Style(typeof(TextBlock));
            style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(8, 6, 8, 6)));
            style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center));
            style.Setters.Add(new Setter(TextBlock.FontWeightProperty, System.Windows.FontWeights.SemiBold));
            style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
            style.Setters.Add(new Setter(TextBlock.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Left));
            return style;
        }

        private DataTemplate BuildCellTemplate(int columnIndex)
        {
            var template = new DataTemplate();
            var borderFactory = new FrameworkElementFactory(typeof(Border));
            borderFactory.SetBinding(Border.BackgroundProperty, new Binding($"Cells[{columnIndex}].Background"));
            borderFactory.SetValue(Border.PaddingProperty, new Thickness(8, 6, 8, 6));
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding($"Cells[{columnIndex}].Display"));
            textFactory.SetBinding(TextBlock.ForegroundProperty, new Binding($"Cells[{columnIndex}].Foreground"));
            textFactory.SetValue(TextBlock.HorizontalAlignmentProperty, System.Windows.HorizontalAlignment.Center);
            textFactory.SetValue(TextBlock.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
            textFactory.SetValue(TextBlock.FontWeightProperty, System.Windows.FontWeights.SemiBold);
            borderFactory.AppendChild(textFactory);
            template.VisualTree = borderFactory;
            return template;
        }

        private static SolidColorBrush GetSimilarityBrush(float sim)
        {
            Color color;
            if (sim >= 0.999f) color = Color.FromRgb(0x9E, 0x9E, 0x9E);
            else if (sim >= 0.75f) color = Color.FromRgb(0xC6, 0x28, 0x28);
            else if (sim >= 0.55f) color = Color.FromRgb(0xEF, 0x6C, 0x00);
            else if (sim >= 0.35f) color = Color.FromRgb(0xF9, 0xA8, 0x25);
            else color = Color.FromRgb(0x2E, 0x7D, 0x32);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush GetSimilarityForeground(float sim)
        {
            if (sim >= 0.35f && sim < 0.55f)
                return new SolidColorBrush(Colors.Black);
            return new SolidColorBrush(Colors.White);
        }

        // ==========================================
        // ===== ГРАФИК СРАВНЕНИЯ 2 ГОЛОСОВ =====
        // ==========================================

        private void InitPlot()
        {
            _plotModel = new PlotModel
            {
                Title = "Сравнение эмбеддингов",
                Background = OxyColors.Transparent,
                PlotAreaBackground = OxyColors.Transparent
            };

            _xAxis = new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Индекс элемента вектора",
                MinimumPadding = 0.02,
                MaximumPadding = 0.02,
                AbsoluteMinimum = 0
            };
            _yAxis = new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Значение эмбеддинга",
                MinimumPadding = 0.05,
                MaximumPadding = 0.05
            };
            _plotModel.Axes.Add(_xAxis);
            _plotModel.Axes.Add(_yAxis);

            _legend = new Legend
            {
                LegendPosition = LegendPosition.TopRight,
                LegendPlacement = LegendPlacement.Inside
            };
            _plotModel.Legends.Add(_legend);

            _seriesA = new LineSeries { Title = "Голос A", Color = OxyColor.FromRgb(0x2E, 0x7D, 0xFF), StrokeThickness = 1.2 };
            _seriesB = new LineSeries { Title = "Голос B", Color = OxyColor.FromRgb(0xE5, 0x39, 0x35), StrokeThickness = 1.2 };
            _plotModel.Series.Add(_seriesA);
            _plotModel.Series.Add(_seriesB);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    EmbeddingPlot.Model = null;
                    EmbeddingPlot.Model = _plotModel;
                    ApplyPlotTheme();
                    EmbeddingPlot.InvalidatePlot(true);
                }
                catch (Exception ex) { EventBus.Error("UI", $"Ошибка инициализации графика: {ex.Message}"); }
            }), DispatcherPriority.Background);
        }

        private void ApplyPlotTheme()
        {
            if (_plotModel == null) return;
            var visibleTextColor = _currentDarkTheme ? OxyColor.FromRgb(0xEA, 0xEA, 0xEA) : OxyColor.FromRgb(0x2C, 0x2C, 0x2C);
            _plotModel.TitleColor = visibleTextColor;
            _plotModel.PlotAreaBorderColor = _currentDarkTheme ? OxyColor.FromRgb(0x3A, 0x3A, 0x3A) : OxyColor.FromRgb(0xC0, 0xC0, 0xC0);
            foreach (var axis in _plotModel.Axes)
            {
                axis.TextColor = visibleTextColor;
                axis.TicklineColor = visibleTextColor;
                axis.AxislineColor = visibleTextColor;
                axis.TitleColor = visibleTextColor;
            }
            foreach (var legend in _plotModel.Legends)
                legend.LegendTextColor = visibleTextColor;
            _plotModel.InvalidatePlot(false);
        }

        // ==========================================
        // ===== КАРТА ГОЛОСОВ =====
        // ==========================================

        private void InitVoiceMap()
        {
            _voiceMapModel = new PlotModel
            {
                Title = "Карта голосов",
                Background = OxyColors.Transparent,
                PlotAreaBackground = OxyColors.Transparent
            };

            _voiceMapModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Title = "Компонента 1",
                MinimumPadding = 0.15,
                MaximumPadding = 0.15
            });
            _voiceMapModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Title = "Компонента 2",
                MinimumPadding = 0.15,
                MaximumPadding = 0.15
            });

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    VoiceMapPlot.Model = null;
                    VoiceMapPlot.Model = _voiceMapModel;
                    ApplyVoiceMapTheme();
                    DrawVoiceMap();
                    VoiceMapPlot.InvalidatePlot(true);
                    VoiceMapPlot.PreviewMouseLeftButtonUp += VoiceMapPlot_PreviewMouseLeftButtonUp;
                }
                catch (Exception ex) { EventBus.Error("UI", $"Ошибка инициализации карты: {ex.Message}"); }
            }), DispatcherPriority.Background);
        }

        private void ApplyVoiceMapTheme()
        {
            if (_voiceMapModel == null) return;
            var visibleTextColor = _currentDarkTheme ? OxyColor.FromRgb(0xEA, 0xEA, 0xEA) : OxyColor.FromRgb(0x2C, 0x2C, 0x2C);
            _voiceMapModel.TitleColor = visibleTextColor;
            _voiceMapModel.PlotAreaBorderColor = _currentDarkTheme ? OxyColor.FromRgb(0x3A, 0x3A, 0x3A) : OxyColor.FromRgb(0xC0, 0xC0, 0xC0);
            foreach (var axis in _voiceMapModel.Axes)
            {
                axis.TextColor = visibleTextColor;
                axis.TicklineColor = visibleTextColor;
                axis.AxislineColor = visibleTextColor;
                axis.TitleColor = visibleTextColor;
            }
            foreach (var legend in _voiceMapModel.Legends)
                legend.LegendTextColor = visibleTextColor;
            _voiceMapModel.InvalidatePlot(false);
        }

        private void UpdateAnnotationsTheme()
        {
            if (_voiceMapModel == null) return;
            var textColor = _currentDarkTheme ? OxyColor.FromRgb(0xD0, 0xD0, 0xD0) : OxyColor.FromRgb(0x50, 0x50, 0x50);
            foreach (var annotation in _voiceMapModel.Annotations)
            {
                if (annotation is TextAnnotation ta) ta.TextColor = textColor;
            }
            _voiceMapModel.InvalidatePlot(false);
        }

        private void RefreshVoiceMap_Click(object sender, RoutedEventArgs e) => DrawVoiceMap();

        // ==========================================
        // ===== ПОРОГ ЛИНИЙ =====
        // ==========================================

        private void InitializeLineThresholdCombo()
        {
            if (LineThresholdCombo == null) return;
            LineThresholdCombo.Items.Clear();

            for (int i = 0; i <= 20; i++)
            {
                float value = i * 0.05f;
                var item = new ComboBoxItem
                {
                    Content = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                    Tag = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                };
                LineThresholdCombo.Items.Add(item);
            }

            SelectThresholdInCombo(_lineThreshold);
        }

        private void SelectThresholdInCombo(float value)
        {
            if (LineThresholdCombo == null) return;
            for (int i = 0; i < LineThresholdCombo.Items.Count; i++)
            {
                if (LineThresholdCombo.Items[i] is ComboBoxItem item &&
                    item.Tag is string tag &&
                    float.TryParse(tag, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v))
                {
                    if (Math.Abs(v - value) < 0.001f)
                    {
                        LineThresholdCombo.SelectedIndex = i;
                        return;
                    }
                }
            }
        }

        private void LineThreshold_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (LineThresholdCombo == null) return;
            if (LineThresholdCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag) return;
            if (!float.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float th)) return;

            _lineThreshold = th;
            App.Settings.SpeakerThreshold = th;
            App.SaveSettings();

            if (_voiceMapModel != null && _highlightSpeakerIndex < 0)
                DrawSimilarityLinesOnly();
        }

        private void ThresholdUp_Click(object sender, RoutedEventArgs e) => ChangeThreshold(+0.05f);
        private void ThresholdDown_Click(object sender, RoutedEventArgs e) => ChangeThreshold(-0.05f);

        private void ChangeThreshold(float delta)
        {
            float newValue = MathF.Round((_lineThreshold + delta) * 100f) / 100f;
            newValue = MathF.Max(0.0f, MathF.Min(1.0f, newValue));
            SelectThresholdInCombo(newValue);
        }

        private void DrawVoiceMap()
        {
            if (_voiceMapModel == null || _db == null) return;
            try
            {
                var speakers = _db.GetAllSpeakers();
                _voiceMapModel.Series.Clear();
                _voiceMapModel.Annotations.Clear();
                _highlightSpeakerIndex = -1;

                if (speakers.Count == 0)
                {
                    _voiceMapModel.Title = "Карта голосов (база пуста)";
                    _voiceMapModel.InvalidatePlot(true);
                    ClusterStatsPanel.Children.Clear();
                    return;
                }

                var embeddings = speakers.Select(s => s.VoiceEmbedding).Where(e => e.Length > 0).ToArray();
                if (embeddings.Length == 0)
                {
                    _voiceMapModel.Title = "Карта голосов (нет эмбеддингов)";
                    _voiceMapModel.InvalidatePlot(true);
                    return;
                }

                var pca = PcaProjector.ProjectTo2D(embeddings);
                var points = pca.Points;

                int clusterCount = Math.Max(2, Math.Min(5, embeddings.Length / 3));
                if (embeddings.Length < 3) clusterCount = 1;
                var labels = clusterCount > 1 ? SimpleKMeans.Cluster(points, clusterCount) : new int[points.Length];

                _voiceMapPoints = points;
                _voiceMapClusters = labels;
                _voiceMapSpeakers = speakers.ToArray();

                if (_voiceMapModel.Axes.Count >= 2)
                {
                    if (_voiceMapModel.Axes[0] is LinearAxis xAx) xAx.Title = $"Компонента 1 ({pca.Variance1 * 100:F1}%)";
                    if (_voiceMapModel.Axes[1] is LinearAxis yAx) yAx.Title = $"Компонента 2 ({pca.Variance2 * 100:F1}%)";
                }

                int actualClusters = labels.Length > 0 ? labels.Max() + 1 : 1;
                DrawConvexHulls(points, labels, actualClusters);

                var seriesByCluster = new ScatterSeries[actualClusters];
                for (int c = 0; c < actualClusters; c++)
                {
                    var color = ClusterColors[c % ClusterColors.Length];
                    var series = new ScatterSeries
                    {
                        Title = $"Кластер {c + 1}",
                        MarkerType = MarkerType.Circle,
                        MarkerSize = 8,
                        MarkerFill = color,
                        MarkerStroke = OxyColors.White,
                        MarkerStrokeThickness = 1.2,
                        TrackerFormatString = "{Tag}"
                    };
                    seriesByCluster[c] = series;
                    _voiceMapModel.Series.Add(series);
                }

                for (int i = 0; i < points.Length && i < speakers.Count; i++)
                {
                    int cluster = i < labels.Length ? labels[i] : 0;
                    var sp = new ScatterPoint(points[i].X, points[i].Y) { Tag = speakers[i].DisplayName };
                    seriesByCluster[cluster].Points.Add(sp);

                    var annotation = new TextAnnotation
                    {
                        Text = ShortenName(speakers[i].DisplayName),
                        TextPosition = new DataPoint(points[i].X, points[i].Y),
                        TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Left,
                        TextVerticalAlignment = OxyPlot.VerticalAlignment.Bottom,
                        FontSize = 11,
                        Stroke = OxyColors.Transparent,
                        TextColor = _currentDarkTheme ? OxyColor.FromRgb(0xD0, 0xD0, 0xD0) : OxyColor.FromRgb(0x50, 0x50, 0x50)
                    };
                    _voiceMapModel.Annotations.Add(annotation);
                }

                DrawSimilarityLinesOnly();
                _voiceMapModel.Title = $"Карта голосов ({speakers.Count} голосов, {actualClusters} кластеров)";
                ApplyVoiceMapTheme();
                _voiceMapModel.InvalidatePlot(true);
                UpdateClusterStats(speakers, labels, actualClusters);

                EventBus.Ok("UI", $"Карта голосов построена: {speakers.Count} голосов, {actualClusters} кластеров, variance 1={pca.Variance1 * 100:F1}%, variance 2={pca.Variance2 * 100:F1}%");
            }
            catch (Exception ex) { EventBus.Error("UI", $"Ошибка построения карты голосов: {ex.Message}"); }
        }

        private void DrawSimilarityLinesOnly()
        {
            if (_voiceMapModel == null) return;

            var linesToRemove = _voiceMapModel.Series.OfType<LineSeries>().ToList();
            foreach (var ls in linesToRemove)
                _voiceMapModel.Series.Remove(ls);

            int n = _voiceMapSpeakers.Length;
            if (n < 2 || _voiceMapPoints.Length < n) return;

            int firstScatterIndex = -1;
            for (int k = 0; k < _voiceMapModel.Series.Count; k++)
            {
                if (_voiceMapModel.Series[k] is ScatterSeries) { firstScatterIndex = k; break; }
            }

            if (_highlightSpeakerIndex >= 0 && _highlightSpeakerIndex < n)
            {
                var sims = new List<(int idx, float sim)>();
                for (int i = 0; i < n; i++)
                {
                    if (i == _highlightSpeakerIndex) continue;
                    if (_voiceMapSpeakers[i].VoiceEmbedding.Length == 0) continue;

                    float sim = ComputeSimilarity(
                        _voiceMapSpeakers[_highlightSpeakerIndex].VoiceEmbedding,
                        _voiceMapSpeakers[i].VoiceEmbedding);
                    sims.Add((i, sim));
                }

                int takeCount = Math.Min(3, sims.Count);
                var top3 = sims.OrderByDescending(x => x.sim).Take(takeCount).ToList();

                foreach (var (idx, sim) in top3)
                {
                    var color = GetLineColor(sim);
                    var series = new LineSeries
                    {
                        Color = OxyColor.FromArgb(0xFF, color.R, color.G, color.B),
                        StrokeThickness = 3.0,
                        LineStyle = LineStyle.Solid,
                        TrackerFormatString = ""
                    };
                    series.Points.Add(new DataPoint(
                        _voiceMapPoints[_highlightSpeakerIndex].X,
                        _voiceMapPoints[_highlightSpeakerIndex].Y));
                    series.Points.Add(new DataPoint(
                        _voiceMapPoints[idx].X,
                        _voiceMapPoints[idx].Y));

                    if (firstScatterIndex >= 0)
                        _voiceMapModel.Series.Insert(firstScatterIndex, series);
                    else
                        _voiceMapModel.Series.Add(series);
                }

                _voiceMapModel.InvalidatePlot(false);
                return;
            }

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    float sim = ComputeSimilarity(_voiceMapSpeakers[i].VoiceEmbedding, _voiceMapSpeakers[j].VoiceEmbedding);
                    if (sim < _lineThreshold) continue;

                    var color = GetLineColor(sim);
                    var series = new LineSeries
                    {
                        Color = OxyColor.FromArgb(0xA0, color.R, color.G, color.B),
                        StrokeThickness = 1.0,
                        LineStyle = LineStyle.Solid,
                        TrackerFormatString = ""
                    };
                    series.Points.Add(new DataPoint(_voiceMapPoints[i].X, _voiceMapPoints[i].Y));
                    series.Points.Add(new DataPoint(_voiceMapPoints[j].X, _voiceMapPoints[j].Y));

                    if (firstScatterIndex >= 0)
                        _voiceMapModel.Series.Insert(firstScatterIndex, series);
                    else
                        _voiceMapModel.Series.Add(series);
                }
            }

            _voiceMapModel.InvalidatePlot(false);
        }

        private static OxyColor GetLineColor(float sim)
        {
            if (sim >= 0.70f) return OxyColor.FromRgb(0xC6, 0x28, 0x28);
            if (sim >= 0.60f) return OxyColor.FromRgb(0xEF, 0x6C, 0x00);
            if (sim >= 0.50f) return OxyColor.FromRgb(0xFB, 0xC0, 0x2D);
            return OxyColor.FromRgb(0x43, 0xA0, 0x47);
        }

        private void DrawConvexHulls((double X, double Y)[] points, int[] labels, int clusterCount)
        {
            if (_voiceMapModel == null) return;
            for (int c = 0; c < clusterCount; c++)
            {
                var clusterPoints = new List<(double X, double Y)>();
                for (int i = 0; i < points.Length && i < labels.Length; i++)
                    if (labels[i] == c) clusterPoints.Add(points[i]);

                if (clusterPoints.Count < 3) continue;
                var hull = ConvexHull.Compute(clusterPoints);
                if (hull.Count < 3) continue;

                var color = ClusterColors[c % ClusterColors.Length];
                var annotation = new PolygonAnnotation
                {
                    Fill = OxyColor.FromArgb(0x18, color.R, color.G, color.B),
                    Stroke = OxyColor.FromArgb(0x60, color.R, color.G, color.B),
                    StrokeThickness = 1.5,
                    LineStyle = LineStyle.Dash
                };
                foreach (var p in hull)
                    annotation.Points.Add(new DataPoint(p.X, p.Y));
                _voiceMapModel.Annotations.Add(annotation);
            }
        }

        private void UpdateClusterStats(List<Speaker> speakers, int[] labels, int clusterCount)
        {
            ClusterStatsPanel.Children.Clear();
            for (int c = 0; c < clusterCount; c++)
            {
                var names = new List<string>();
                var embs = new List<float[]>();
                for (int i = 0; i < speakers.Count && i < labels.Length; i++)
                {
                    if (labels[i] != c) continue;
                    names.Add(speakers[i].DisplayName);
                    embs.Add(speakers[i].VoiceEmbedding);
                }

                float avgSim = 0; int pairs = 0;
                for (int i = 0; i < embs.Count; i++)
                    for (int j = i + 1; j < embs.Count; j++)
                    {
                        avgSim += ComputeSimilarity(embs[i], embs[j]);
                        pairs++;
                    }
                float avg = pairs > 0 ? avgSim / pairs : 0;

                var color = ClusterColors[c % ClusterColors.Length];
                var colorBrush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
                colorBrush.Freeze();

                var block = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(0x20, color.R, color.G, color.B)),
                    BorderBrush = colorBrush,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var sp = new StackPanel();
                var titleBlock = new StackPanel { Orientation = Orientation.Horizontal };
                titleBlock.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 12,
                    Height = 12,
                    Fill = colorBrush,
                    Margin = new Thickness(0, 0, 6, 0),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                });
                var titleText = new TextBlock
                {
                    Text = $"Кластер {c + 1} - {names.Count} голосов",
                    FontSize = 13,
                    FontWeight = System.Windows.FontWeights.SemiBold
                };
                titleText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextBrush");
                titleBlock.Children.Add(titleText);
                sp.Children.Add(titleBlock);

                var avgText = new TextBlock
                {
                    Text = $"Среднее сходство: {avg:F3}",
                    FontSize = 11,
                    Opacity = 0.85,
                    Margin = new Thickness(0, 4, 0, 4)
                };
                avgText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextBrush");
                sp.Children.Add(avgText);

                var namesText = new TextBlock
                {
                    Text = string.Join(", ", names.Select(ShortenName)),
                    FontSize = 11,
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap
                };
                namesText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextBrush");
                sp.Children.Add(namesText);

                block.Child = sp;
                ClusterStatsPanel.Children.Add(block);
            }
        }

        private void VoiceMapPlot_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_voiceMapModel == null || _db == null) return;
            if (_voiceMapPoints.Length == 0 || _voiceMapSpeakers.Length == 0) return;

            try
            {
                var pos = e.GetPosition(VoiceMapPlot);

                if (_voiceMapModel.Axes.Count < 2) return;
                if (_voiceMapModel.Axes[0] is not LinearAxis xAxis) return;
                if (_voiceMapModel.Axes[1] is not LinearAxis yAxis) return;

                int idx = -1;
                double minDist = double.MaxValue;
                for (int i = 0; i < _voiceMapPoints.Length; i++)
                {
                    double screenX = xAxis.Transform(_voiceMapPoints[i].X);
                    double screenY = yAxis.Transform(_voiceMapPoints[i].Y);
                    double dx = screenX - pos.X;
                    double dy = screenY - pos.Y;
                    double d = dx * dx + dy * dy;
                    if (d < minDist) { minDist = d; idx = i; }
                }

                const double thresholdPixels = 30.0;
                bool clickedOnPoint = idx >= 0 && minDist <= thresholdPixels * thresholdPixels;

                if (!clickedOnPoint)
                {
                    if (_highlightSpeakerIndex >= 0)
                    {
                        _highlightSpeakerIndex = -1;
                        DrawSimilarityLinesOnly();
                    }
                    _lastClickIndex = -1;
                    _lastClickTime = DateTime.MinValue;
                    return;
                }

                var now = DateTime.Now;
                bool isDoubleClick = (idx == _lastClickIndex) && ((now - _lastClickTime).TotalMilliseconds < 500);

                if (isDoubleClick)
                {
                    int clusterNum = idx < _voiceMapClusters.Length ? _voiceMapClusters[idx] : 0;
                    var detailsWindow = new SpeakerDetailsWindow(
                        _voiceMapSpeakers[idx], _db,
                        _voiceMapSpeakers, _voiceMapClusters, clusterNum)
                    {
                        Owner = this
                    };
                    detailsWindow.ShowDialog();
                    _lastClickIndex = -1;
                    _lastClickTime = DateTime.MinValue;
                }
                else
                {
                    _highlightSpeakerIndex = idx;
                    DrawSimilarityLinesOnly();
                    _lastClickIndex = idx;
                    _lastClickTime = now;
                    EventBus.Info("UI", $"Подсвечены связи для: {_voiceMapSpeakers[idx].DisplayName}");
                }
            }
            catch (Exception ex) { EventBus.Error("UI", $"Ошибка обработки клика: {ex.Message}"); }
        }

        private void ShowProblematicPairs_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) return;
            try
            {
                var speakers = _db.GetAllSpeakers().ToArray();
                if (speakers.Length < 2 || _voiceMapClusters.Length < speakers.Length)
                {
                    MessageBox.Show("Недостаточно данных. Сначала обновите карту голосов.",
                        "Проблемные пары", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var window = new ProblematicPairsWindow(speakers, _voiceMapClusters, this)
                {
                    Owner = this
                };
                window.ShowDialog();
            }
            catch (Exception ex) { EventBus.Error("UI", $"Ошибка поиска проблемных пар: {ex.Message}"); }
        }

        private void ExportVoiceMap_Click(object sender, RoutedEventArgs e)
        {
            if (_voiceMapModel == null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG (*.png)|*.png",
                FileName = $"TVE_voicemap_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var exporter = new OxyPlot.Wpf.PngExporter { Width = 1400, Height = 900 };
                using var stream = File.Create(dialog.FileName);
                exporter.Export(_voiceMapModel, stream);
                EventBus.Ok("UI", $"Карта голосов сохранена: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить карту:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ==========================================
        // ===== СРАВНЕНИЕ 2 ГОЛОСОВ =====
        // ==========================================

        private static Speaker CreatePlaceholder() => new Speaker
        {
            Id = PlaceholderSpeakerId,
            DisplayName = "-",
            Description = "Не выбран",
            VoiceEmbedding = Array.Empty<float>()
        };

        private void LoadSpeakersIntoCombos()
        {
            if (_db == null) return;
            try
            {
                var speakers = _db.GetAllSpeakers();
                var prevA = SpeakerACombo.SelectedItem as Speaker;
                var prevB = SpeakerBCombo.SelectedItem as Speaker;
                var items = new List<Speaker> { CreatePlaceholder() };
                items.AddRange(speakers);

                SpeakerACombo.ItemsSource = items;
                SpeakerBCombo.ItemsSource = items;
                SpeakerACombo.DisplayMemberPath = "DisplayName";
                SpeakerBCombo.DisplayMemberPath = "DisplayName";

                int idxA = prevA != null ? items.FindIndex(s => s.Id == prevA.Id) : -1;
                int idxB = prevB != null ? items.FindIndex(s => s.Id == prevB.Id) : -1;
                if (idxA < 0) idxA = speakers.Count >= 1 ? 1 : 0;
                if (idxB < 0) idxB = speakers.Count >= 2 ? 2 : 0;
                SpeakerACombo.SelectedIndex = idxA;
                SpeakerBCombo.SelectedIndex = idxB;
            }
            catch { }
        }

        private void SpeakerCombo_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_db == null || _seriesA == null || _seriesB == null) return;
            var spkA = SpeakerACombo.SelectedItem as Speaker;
            var spkB = SpeakerBCombo.SelectedItem as Speaker;
            if (spkA == null || spkB == null) return;
            DrawPlot(spkA, spkB);
        }

        private void ResetPlot_Click(object sender, RoutedEventArgs e)
        {
            if (_plotModel == null) return;
            _plotModel.ResetAllAxes();
            _plotModel.InvalidatePlot(false);
        }

        private void DrawPlot(Speaker spkA, Speaker spkB)
        {
            if (_plotModel == null || _seriesA == null || _seriesB == null) return;
            try
            {
                _seriesA.Points.Clear();
                _seriesB.Points.Clear();
                bool hasA = spkA.Id != PlaceholderSpeakerId;
                bool hasB = spkB.Id != PlaceholderSpeakerId;
                var embA = hasA ? spkA.VoiceEmbedding : Array.Empty<float>();
                var embB = hasB ? spkB.VoiceEmbedding : Array.Empty<float>();
                for (int i = 0; i < embA.Length; i++) _seriesA.Points.Add(new DataPoint(i, embA[i]));
                for (int i = 0; i < embB.Length; i++) _seriesB.Points.Add(new DataPoint(i, embB[i]));
                _seriesA.IsVisible = hasA;
                _seriesB.IsVisible = hasB;
                int maxLen = Math.Max(embA.Length, embB.Length);
                if (maxLen > 0 && _xAxis != null) { _xAxis.Minimum = 0; _xAxis.Maximum = maxLen - 1; }
                _seriesA.Title = hasA ? $"A: {spkA.DisplayName}" : "A: -";
                _seriesB.Title = hasB ? $"B: {spkB.DisplayName}" : "B: -";

                if (hasA && hasB)
                {
                    float sim = ComputeSimilarity(embA, embB);
                    _plotModel.Title = $"Сравнение эмбеддингов: {spkA.DisplayName} - {spkB.DisplayName}";
                    SimilarityDot.Visibility = Visibility.Visible;
                    SimilarityDot.Fill = GetSimilarityBrush(sim);
                    SimilarityText.Text = GetSimilarityLabel(sim, sim.ToString("F4"));
                }
                else if (hasA)
                {
                    _plotModel.Title = $"Эмбеддинг: {spkA.DisplayName}";
                    SimilarityDot.Visibility = Visibility.Collapsed;
                    SimilarityText.Text = "Показан эмбеддинг голоса A.";
                }
                else if (hasB)
                {
                    _plotModel.Title = $"Эмбеддинг: {spkB.DisplayName}";
                    SimilarityDot.Visibility = Visibility.Collapsed;
                    SimilarityText.Text = "Показан эмбеддинг голоса B.";
                }
                else
                {
                    _plotModel.Title = "Сравнение эмбеддингов";
                    SimilarityDot.Visibility = Visibility.Collapsed;
                    SimilarityText.Text = "Выберите хотя бы один голос.";
                }
                _plotModel.InvalidatePlot(true);
            }
            catch (Exception ex) { EventBus.Error("UI", $"Ошибка построения графика: {ex.Message}"); }
        }

        private static string GetSimilarityLabel(float sim, string value)
        {
            if (sim >= 0.75f) return $"Косинусное сходство: {value} - очень похожи";
            if (sim >= 0.55f) return $"Косинусное сходство: {value} - похожи";
            if (sim >= 0.35f) return $"Косинусное сходство: {value} - умеренно схожи";
            return $"Косинусное сходство: {value} - разные голоса";
        }

        private void ExportPlot_Click(object sender, RoutedEventArgs e)
        {
            if (_plotModel == null) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG (*.png)|*.png",
                FileName = $"TVE_embedding_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png"
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var exporter = new OxyPlot.Wpf.PngExporter { Width = 1200, Height = 600 };
                using var stream = File.Create(dialog.FileName);
                exporter.Export(_plotModel, stream);
                EventBus.Ok("UI", $"График сохранён: {dialog.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить график:\n{ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ==========================================
        // ===== ХЕЛПЕРЫ =====
        // ==========================================

        private static bool IsDarkTheme()
        {
            try
            {
                var theme = App.Settings.Theme;
                if (theme == "Dark") return true;
                if (theme == "Light") return false;
                return ThemeManager.GetSystemTheme() == "Dark";
            }
            catch { return false; }
        }

        private static float ComputeSimilarity(float[] a, float[] b)
        {
            return EmbeddingMath.CosineSimilarity(a, b);
        }

        private static string ShortenName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "-";
            if (name.Length <= 15) return name;
            return name.Substring(0, 14) + "…";
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "-";
            if (bytes < 1024) return $"{bytes} Б";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} КБ";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} МБ";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} ГБ";
        }

        private static string ShortenPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == "-") return "-";
            if (path.Length <= 70) return path;
            var fileName = Path.GetFileName(path);
            var folder = Path.GetDirectoryName(path) ?? "";
            if (folder.Length > 40) folder = "..." + folder.Substring(folder.Length - 40);
            return Path.Combine(folder, fileName);
        }

        private static string TranslateTheme(string theme) => theme switch
        {
            "Light" => "Светлая",
            "Dark" => "Тёмная",
            "System" => "Системная",
            _ => theme
        };

        private static string FormatUptime(TimeSpan span)
        {
            return $"{(int)span.TotalHours:D2}:{span.Minutes:D2}:{span.Seconds:D2}";
        }

        // ==========================================
        // ===== ГАРМОНИКИ =====
        // ==========================================

        private async void BtnHarmonics_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите аудиофайл для анализа гармоник",
                Filter = "Аудиофайлы (*.mp3;*.wav;*.m4a;*.flac;*.ogg)|" +
                         "*.mp3;*.wav;*.m4a;*.flac;*.ogg|" +
                         "Все файлы (*.*)|*.*"
            };

            if (dlg.ShowDialog() != true) return;

            string filePath = dlg.FileName;

            try
            {
                float[] samples = await Task.Run(
                    () => Mp3Processor.DecodeToMono16k(filePath));

                if (samples.Length < 1024)
                {
                    MessageBox.Show(
                        "Файл слишком короткий для анализа (минимум 1024 сэмпла ≈ 64 мс).",
                        "Анализ гармоник",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                var result = await Task.Run(() => HarmonicAnalyzer.Analyze(samples));

                var win = new HarmonicAnalysisWindow(filePath, samples, result)
                {
                    Owner = this
                };
                win.Show();

                EventBus.Info("Harmonic",
                    $"Открыт анализ гармоник: {Path.GetFileName(filePath)}");
            }
            catch (FileNotFoundException)
            {
                MessageBox.Show(
                    "Не удалось прочитать файл - возможно, он повреждён или занят.",
                    "Анализ гармоник",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Ошибка анализа:\n{ex.Message}",
                    "Анализ гармоник",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ==========================================
        // ===== ПАКЕТНЫЙ АНАЛИЗ =====
        // ==========================================

        private void BtnBatchAnalysis_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите аудиофайл для пакетного анализа",
                Filter = "Аудиофайлы (*.mp3;*.wav;*.m4a;*.flac;*.ogg)|" +
                         "*.mp3;*.wav;*.m4a;*.flac;*.ogg|" +
                         "Все файлы (*.*)|*.*"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                var win = new HarmonicBatchWindow(dlg.FileName)
                {
                    Owner = this
                };
                win.Show();

                EventBus.Info("HarmonicBatch",
                    $"Открыт пакетный анализ: {Path.GetFileName(dlg.FileName)}");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось открыть окно пакетного анализа:\n{ex.Message}",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        // ==========================================
        // ===== ВКЛАДКА «СЛИЯНИЕ ЭМБЕДДИНГОВ» =====
        // ==========================================

        private void InitMergeTab()
        {
            if (_db == null) return;

            try
            {
                var speakers = _db.GetAllSpeakers();
                var placeholder = new Speaker
                {
                    Id = -1,
                    DisplayName = "- выберите говорящего -",
                    VoiceEmbedding = Array.Empty<float>()
                };

                var items = new List<Speaker> { placeholder };
                items.AddRange(speakers);

                MergeSpeakerCombo.ItemsSource = items;
                MergeSpeakerCombo.DisplayMemberPath = "DisplayName";
                MergeSpeakerCombo.SelectedIndex = 0;

                MergeStatusText.Text =
                    "Выберите говорящего для визуализации последнего слияния эмбеддинга.";
            }
            catch (Exception ex)
            {
                EventBus.Error("UI", $"Ошибка инициализации вкладки слияния: {ex.Message}");
            }
        }

        private void MergeSpeakerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MergeSpeakerCombo.SelectedItem is not Speaker spk) return;
            if (spk.Id < 0) return;

            _mergeSelectedSpeaker = spk;
            UpdateMergeView();
        }

        private void MergeRefresh_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) return;

            if (MergeSpeakerCombo.SelectedItem is Speaker spk && spk.Id >= 0)
            {
                var fresh = _db.GetSpeaker(spk.Id);
                if (fresh != null) _mergeSelectedSpeaker = fresh;
            }
            UpdateMergeView();
        }

        private void UpdateMergeView()
        {
            if (_mergeSelectedSpeaker == null)
            {
                ClearMergeView();
                MergeStatusText.Text = "Выберите говорящего.";
                return;
            }

            if (!_mergeSelectedSpeaker.HasMergeHistory)
            {
                ClearMergeView();

                string reason = _mergeSelectedSpeaker.VoiceEmbedding.Length == 0
                    ? "У этого говорящего ещё нет эмбеддинга."
                    : "Для этого говорящего ещё не было ни одного слияния. " +
                      "Запишите или загрузите ещё один файл от того же человека.";

                MergeStatusText.Text = reason;
                return;
            }

            var oldEmb = _mergeSelectedSpeaker.LastMergeOld;
            var newEmb = _mergeSelectedSpeaker.LastMergeNew;
            var merged = _mergeSelectedSpeaker.LastMergeMerged;

            // Адаптивный масштаб: максимальная |компонента| по всем трём векторам.
            float maxAbs = 0f;
            foreach (var v in new[] { oldEmb, newEmb, merged })
            {
                for (int i = 0; i < v.Length; i++)
                {
                    float a = MathF.Abs(v[i]);
                    if (a > maxAbs) maxAbs = a;
                }
            }
            if (maxAbs < 1e-6f) maxAbs = 1f;
            float scale = maxAbs * 1.15f;

            MergeImageOld.Source = BuildStripeBitmap(oldEmb, scale);
            MergeImageNew.Source = BuildStripeBitmap(newEmb, scale);
            MergeImageMerged.Source = BuildStripeBitmap(merged, scale);

            float cosOldNew = EmbeddingMath.CosineSimilarity(oldEmb, newEmb);
            float cosOldMerged = EmbeddingMath.CosineSimilarity(oldEmb, merged);
            float cosNewMerged = EmbeddingMath.CosineSimilarity(newEmb, merged);

            MergeCosOldNew.Text = cosOldNew.ToString("F4");
            MergeCosOldMerged.Text = cosOldMerged.ToString("F4");
            MergeCosNewMerged.Text = cosNewMerged.ToString("F4");

            float theta = MathF.Acos(Math.Clamp(cosOldNew, -1f, 1f));
            float thetaDeg = theta * 180f / MathF.PI;

            MergeThetaT.Text =
                $"θ = {thetaDeg:F1}°, t = {_mergeSelectedSpeaker.LastMergeT:F3}, " +
                $"слияние {_mergeSelectedSpeaker.LastMergeAt.ToLocalTime():dd.MM.yyyy HH:mm:ss}";

            MergeStatusText.Text =
                $"Показано последнее слияние: {_mergeSelectedSpeaker.DisplayName}. " +
                $"Всего реплик: {_db?.GetUtteranceCount(_mergeSelectedSpeaker.Id) ?? 0}.";

            BuildMergeSpherePlot(oldEmb, newEmb, merged, thetaDeg, _mergeSelectedSpeaker.LastMergeT);
        }

        private void ClearMergeView()
        {
            MergeImageOld.Source = null;
            MergeImageNew.Source = null;
            MergeImageMerged.Source = null;
            MergeCosOldNew.Text = "-";
            MergeCosOldMerged.Text = "-";
            MergeCosNewMerged.Text = "-";
            MergeThetaT.Text = "-";

            _mergePlotModel = null;
            MergeSpherePlot.Model = null;
        }

        /// <summary>
        /// Строит bitmap-полосу 512×1, где каждая ячейка покрашена
        /// по значению компоненты эмбеддинга.
        ///
        /// Цвет: синий (−1) - белый (0) - красный (+1).
        /// Значение делится на <paramref name="scale"/> перед отображением,
        /// чтобы даже маленькие компоненты (типичные для эмбеддингов)
        /// давали видимый цвет, а не сливались в белое.
        /// </summary>
        private static WriteableBitmap BuildStripeBitmap(float[] vector, float scale)
        {
            int w = vector.Length;
            if (w == 0)
            {
                return new WriteableBitmap(1, 1, 96, 96, PixelFormats.Bgra32, null);
            }

            if (scale < 1e-9f) scale = 1f;

            var bmp = new WriteableBitmap(w, 1, 96, 96, PixelFormats.Bgra32, null);
            byte[] pixels = new byte[w * 4];

            for (int i = 0; i < w; i++)
            {
                float v = Math.Clamp(vector[i] / scale, -1f, 1f);

                byte r, g, b;
                if (v >= 0)
                {
                    r = (byte)(255 - (255 - 220) * v);
                    g = (byte)(255 - (255 - 40) * v);
                    b = (byte)(255 - (255 - 40) * v);
                }
                else
                {
                    float a = -v;
                    r = (byte)(255 - (255 - 40) * a);
                    g = (byte)(255 - (255 - 100) * a);
                    b = (byte)(255 - (255 - 220) * a);
                }

                int idx = i * 4;
                pixels[idx + 0] = b;
                pixels[idx + 1] = g;
                pixels[idx + 2] = r;
                pixels[idx + 3] = 255;
            }

            bmp.WritePixels(new Int32Rect(0, 0, w, 1), pixels, w * 4, 0);
            bmp.Freeze();
            return bmp;
        }

        private void BuildMergeSpherePlot(
            float[] oldEmb, float[] newEmb, float[] merged,
            float thetaDeg, float t)
        {
            var (_, textColor, borderColor) = GetMergePlotColors();

            _mergePlotModel = new PlotModel
            {
                Title = $"Векторы на единичной сфере (θ = {thetaDeg:F1}°, t = {t:F3})",
                TitleColor = textColor,
                TitleFontSize = 12,
                Background = OxyColors.Transparent,
                PlotAreaBackground = OxyColors.Transparent,
                PlotAreaBorderColor = borderColor,
                DefaultFontSize = 11
            };

            _mergePlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Bottom,
                Minimum = -1.15,
                Maximum = 1.15,
                TextColor = textColor,
                AxislineColor = textColor,
                TicklineColor = textColor,
                MajorGridlineStyle = LineStyle.Dot,
                MinorGridlineStyle = LineStyle.None
            });

            _mergePlotModel.Axes.Add(new LinearAxis
            {
                Position = AxisPosition.Left,
                Minimum = -1.15,
                Maximum = 1.15,
                TextColor = textColor,
                AxislineColor = textColor,
                TicklineColor = textColor,
                MajorGridlineStyle = LineStyle.Dot,
                MinorGridlineStyle = LineStyle.None
            });

            var pOld = ProjectTo2D(oldEmb, oldEmb, newEmb);
            var pNew = ProjectTo2D(newEmb, oldEmb, newEmb);
            var pMerged = ProjectTo2D(merged, oldEmb, newEmb);

            var circle = new LineSeries
            {
                Color = OxyColor.FromRgb(0x80, 0x80, 0x80),
                StrokeThickness = 1.0,
                LineStyle = LineStyle.Dash
            };
            for (int i = 0; i <= 100; i++)
            {
                double a = 2 * Math.PI * i / 100;
                circle.Points.Add(new DataPoint(Math.Cos(a), Math.Sin(a)));
            }
            _mergePlotModel.Series.Add(circle);

            var arc = new LineSeries
            {
                Color = OxyColor.FromRgb(0xFF, 0xC1, 0x07),
                StrokeThickness = 2.0,
                LineStyle = LineStyle.Solid
            };
            for (int i = 0; i <= 40; i++)
            {
                float tt = i / 40f;
                var interp = EmbeddingMath.Slerp(oldEmb, newEmb, tt);
                var p = ProjectTo2D(interp, oldEmb, newEmb);
                arc.Points.Add(new DataPoint(p.X, p.Y));
            }
            _mergePlotModel.Series.Add(arc);

            AddVectorLine(_mergePlotModel, pOld, OxyColor.FromRgb(0x2E, 0x7D, 0xFF));
            AddVectorLine(_mergePlotModel, pNew, OxyColor.FromRgb(0x43, 0xA0, 0x47));
            AddVectorLine(_mergePlotModel, pMerged, OxyColor.FromRgb(0xE5, 0x39, 0x35));

            AddVectorPoint(_mergePlotModel, pOld, "old", OxyColor.FromRgb(0x2E, 0x7D, 0xFF));
            AddVectorPoint(_mergePlotModel, pNew, "new", OxyColor.FromRgb(0x43, 0xA0, 0x47));
            AddVectorPoint(_mergePlotModel, pMerged, "merged", OxyColor.FromRgb(0xE5, 0x39, 0x35));

            MergeSpherePlot.Model = _mergePlotModel;
            MergeSpherePlot.InvalidatePlot(true);
        }

        private static (double X, double Y) ProjectTo2D(
            float[] v, float[] a, float[] b)
        {
            var e1 = EmbeddingMath.L2Normalize(a);

            var proj = new float[e1.Length];
            float dotE1B = 0;
            for (int i = 0; i < e1.Length; i++)
                dotE1B += e1[i] * b[i];
            for (int i = 0; i < e1.Length; i++)
                proj[i] = b[i] - dotE1B * e1[i];

            var e2 = EmbeddingMath.L2Normalize(proj);

            float x = 0, y = 0;
            for (int i = 0; i < e1.Length; i++)
            {
                x += e1[i] * v[i];
                y += e2[i] * v[i];
            }

            return (x, y);
        }

        private static void AddVectorLine(PlotModel model, (double X, double Y) p, OxyColor color)
        {
            var line = new LineSeries
            {
                Color = color,
                StrokeThickness = 2.0,
                LineStyle = LineStyle.Solid
            };
            line.Points.Add(new DataPoint(0, 0));
            line.Points.Add(new DataPoint(p.X, p.Y));
            model.Series.Add(line);
        }

        private static void AddVectorPoint(
            PlotModel model, (double X, double Y) p, string label, OxyColor color)
        {
            var scatter = new ScatterSeries
            {
                MarkerType = MarkerType.Circle,
                MarkerSize = 6,
                MarkerFill = color,
                MarkerStroke = OxyColors.White,
                MarkerStrokeThickness = 1.0
            };
            scatter.Points.Add(new ScatterPoint(p.X, p.Y) { Tag = label });
            model.Series.Add(scatter);

            model.Annotations.Add(new TextAnnotation
            {
                Text = label,
                TextPosition = new DataPoint(p.X, p.Y + 0.08),
                TextHorizontalAlignment = OxyPlot.HorizontalAlignment.Center,
                TextVerticalAlignment = OxyPlot.VerticalAlignment.Bottom,
                TextColor = color,
                FontSize = 11,
                FontWeight = OxyPlot.FontWeights.Bold,
                Stroke = OxyColors.Transparent
            });
        }

        private static (bool isDark, OxyColor text, OxyColor border) GetMergePlotColors()
        {
            bool dark = IsDarkTheme();
            return dark
                ? (true,
                   OxyColor.FromRgb(0xEA, 0xEA, 0xEA),
                   OxyColor.FromRgb(0x3A, 0x3A, 0x3A))
                : (false,
                   OxyColor.FromRgb(0x1A, 0x1A, 0x1A),
                   OxyColor.FromRgb(0xA0, 0xA0, 0xA0));
        }

        private void MergeExportPng_Click(object sender, RoutedEventArgs e)
        {
            if (_mergePlotModel == null)
            {
                MessageBox.Show("Нет данных для экспорта. Выберите говорящего с историей слияния.",
                    "Экспорт PNG", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить визуализацию слияния",
                Filter = "PNG (*.png)|*.png",
                FileName = $"TVE_merge_{_mergeSelectedSpeaker?.DisplayName ?? "unknown"}_" +
                           $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                var exporter = new OxyPlot.Wpf.PngExporter { Width = 1200, Height = 800 };
                using var stream = File.Create(dlg.FileName);
                exporter.Export(_mergePlotModel, stream);

                EventBus.Ok("UI", $"PNG слияния сохранён: {dlg.FileName}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить PNG:\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}