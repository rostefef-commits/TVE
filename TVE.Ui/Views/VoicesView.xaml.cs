using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TVE.Core.Services;
using TVE.Core.Storage;

namespace TVE.Ui.Views
{
    public partial class VoicesView : UserControl
    {
        public event Action? BackRequested;

        private VoiceDatabase? _db;
        private ObservableCollection<SpeakerRowViewModel> _rows = new();
        private bool _sortComboInitialized;

        public VoicesView()
        {
            InitializeComponent();
        }

        public void SetDatabase(VoiceDatabase db)
        {
            _db = db;
            SpeakersList.ItemsSource = _rows;

            _sortComboInitialized = true;
            SortComboBox.SelectedIndex = 0;
            _sortComboInitialized = false;

            Refresh();
        }

        public void Refresh()
        {
            if (_db == null) return;

            var speakers = _db.GetAllSpeakers(GetSelectedSortOrder());
            _rows.Clear();
            foreach (var spk in speakers)
                _rows.Add(new SpeakerRowViewModel(spk, _db));

            if (_rows.Count == 0)
            {
                EmptyMessage.Visibility = Visibility.Visible;
                SpeakersList.Visibility = Visibility.Collapsed;
                ExportAllButton.IsEnabled = false;
            }
            else
            {
                EmptyMessage.Visibility = Visibility.Collapsed;
                SpeakersList.Visibility = Visibility.Visible;
                ExportAllButton.IsEnabled = true;
            }
        }

        // ==========================================
        // ===== СОРТИРОВКА =====
        // ==========================================

        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_sortComboInitialized) return;
            Refresh();
        }

        private SpeakerSortOrder GetSelectedSortOrder()
        {
            if (SortComboBox.SelectedItem is ComboBoxItem item
                && item.Tag is string tag
                && Enum.TryParse<SpeakerSortOrder>(tag, out var order))
            {
                return order;
            }
            return SpeakerSortOrder.DateNewFirst;
        }

        // ==========================================
        // ===== INLINE-РЕДАКТИРОВАНИЕ ИМЕНИ =====
        // ==========================================

        private void NameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && sender is TextBox tb)
            {
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        }

        // ==========================================
        // ===== ОКНО «ОПИСАНИЕ» =====
        // ==========================================

        private void Description_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.DataContext is not SpeakerRowViewModel row) return;

            var win = new DescriptionWindow(row.DisplayName, row.Description)
            {
                Owner = Window.GetWindow(this)
            };

            if (win.ShowDialog() == true)
                row.UpdateDescription(win.ResultDescription);
        }

        // ==========================================
        // ===== ОКНО «ИСТОРИЯ» =====
        // ==========================================

        private void History_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.DataContext is not SpeakerRowViewModel row) return;
            if (_db == null) return;

            var win = new HistoryWindow(row.DisplayName, row.Id, _db)
            {
                Owner = Window.GetWindow(this)
            };
            win.ShowDialog();
        }

        // ==========================================
        // ===== УДАЛЕНИЕ =====
        // ==========================================

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.DataContext is not SpeakerRowViewModel row) return;
            if (_db == null) return;

            var result = MessageBox.Show(
                $"Удалить говорящего «{row.DisplayName}» и всю его историю?\n\n" +
                "Это действие нельзя отменить.",
                "Подтверждение удаления",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            _db.DeleteSpeaker(row.Id);
            Refresh();
        }

        // ==========================================
        // ===== ЭКСПОРТ ЭМБЕДДИНГОВ =====
        // ==========================================

        private void ExportRowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.DataContext is not SpeakerRowViewModel row) return;
            if (btn.ContextMenu is not ContextMenu cm) return;

            cm.Tag = row;
            cm.PlacementTarget = btn;
            cm.Placement = PlacementMode.Bottom;
            cm.IsOpen = true;
        }

        private void ExportRowWide_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi) return;
            if (mi.Parent is not ContextMenu cm) return;
            if (cm.Tag is not SpeakerRowViewModel row) return;

            ExportSpeakers(
                new[] { row.Source },
                EmbeddingExportFormat.Wide,
                suggestedName: $"{row.DisplayName}_embedding_wide");
        }

        private void ExportRowLong_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi) return;
            if (mi.Parent is not ContextMenu cm) return;
            if (cm.Tag is not SpeakerRowViewModel row) return;

            ExportSpeakers(
                new[] { row.Source },
                EmbeddingExportFormat.Long,
                suggestedName: $"{row.DisplayName}_embedding_long");
        }

        /// <summary>
        /// Кнопка «Экспорт всех» в шапке. Открывает диалог,
        /// в котором можно выбрать конкретных говорящих и формат.
        /// </summary>
        private void ExportAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_db == null) return;

            var speakers = _db.GetAllSpeakers(GetSelectedSortOrder());
            if (speakers.Count == 0)
            {
                MessageBox.Show("В базе нет говорящих.",
                    "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string context = $"Экспорт эмбеддингов. Отметьте нужных говорящих и выберите формат файла:";

            var dialog = new ExportFormatDialog(context, speakers)
            {
                Owner = Window.GetWindow(this)
            };

            if (dialog.ShowDialog() != true) return;

            ExportSpeakers(
                dialog.SelectedSpeakers,
                dialog.SelectedFormat,
                suggestedName: "all_speakers");
        }

        /// <summary>
        /// Общая логика экспорта: проверка эмбеддингов, SaveFileDialog, запись.
        /// </summary>
        private void ExportSpeakers(
            IReadOnlyList<Speaker> speakers,
            EmbeddingExportFormat format,
            string suggestedName)
        {
            if (speakers.Count == 0)
            {
                MessageBox.Show("Не выбрано ни одного говорящего.",
                    "Экспорт", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (speakers.All(s => s.VoiceEmbedding.Length == 0))
            {
                MessageBox.Show(
                    "У выбранных говорящих нет сохранённых эмбеддингов (пустой вектор).\n" +
                    "Экспортировать нечего.",
                    "Экспорт", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить эмбеддинги в CSV",
                Filter = "CSV (*.csv)|*.csv|Все файлы (*.*)|*.*",
                FileName = $"{SanitizeFileName(suggestedName)}_" +
                           $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.csv"
            };
            if (dlg.ShowDialog() != true) return;

            try
            {
                if (format == EmbeddingExportFormat.Wide)
                    SpeakerExporter.ExportWide(dlg.FileName, speakers);
                else
                    SpeakerExporter.ExportLong(dlg.FileName, speakers);

                string who = speakers.Count == 1
                    ? $"говорящего «{speakers[0].DisplayName}»"
                    : $"{speakers.Count} говорящих";

                MessageBox.Show(
                    $"Экспорт завершён.\n\n" +
                    $"Формат: {(format == EmbeddingExportFormat.Wide ? "широкий" : "длинный")}\n" +
                    $"Экспортировано: {who}\n" +
                    $"Файл: {dlg.FileName}",
                    "Экспорт",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось сохранить файл:\n{ex.Message}",
                    "Ошибка экспорта",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                EventBus.Error("Export", $"Ошибка экспорта: {ex.Message}");
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "export";
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }

        // ==========================================
        // ===== НАЗАД =====
        // ==========================================

        private void BtnBack_Click(object sender, RoutedEventArgs e)
            => BackRequested?.Invoke();
    }
}