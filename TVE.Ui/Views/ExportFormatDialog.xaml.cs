using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using TVE.Core.Storage;

namespace TVE.Ui.Views
{
    /// <summary>
    /// Формат экспорта эмбеддингов.
    /// </summary>
    public enum EmbeddingExportFormat
    {
        /// <summary>Имя;v1;v2;...;v512 - одна строка на говорящего.</summary>
        Wide,

        /// <summary>Имя;Номер;Значение - одна строка на значение.</summary>
        Long
    }

    /// <summary>
    /// Строка-обёртка для списка выбора говорящих.
    /// Поддерживает двустороннюю привязку IsSelected.
    /// </summary>
    public class SpeakerSelectionItem : INotifyPropertyChanged
    {
        private bool _isSelected = true;

        public Speaker Speaker { get; }

        public SpeakerSelectionItem(Speaker speaker)
        {
            Speaker = speaker;
        }

        public string DisplayName => Speaker.DisplayName;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Диалог выбора формата экспорта и (опционально) списка говорящих.
    /// </summary>
    public partial class ExportFormatDialog : Window
    {
        private readonly List<SpeakerSelectionItem> _items = new();
        private bool _hasSelectionSection;

        /// <summary>Выбранный формат. Читается после DialogResult == true.</summary>
        public EmbeddingExportFormat SelectedFormat { get; private set; }
            = EmbeddingExportFormat.Wide;

        /// <summary>Список выбранных говорящих. Читается после DialogResult == true.</summary>
        public List<Speaker> SelectedSpeakers { get; private set; } = new();

        // ==========================================
        // ===== КОНСТРУКТОРЫ =====
        // ==========================================

        /// <summary>
        /// Конструктор без выбора говорящих - только выбор формата.
        /// Используется для одиночного экспорта.
        /// </summary>
        public ExportFormatDialog(string contextText)
        {
            InitializeComponent();

            _hasSelectionSection = false;
            SelectionSection.Visibility = Visibility.Collapsed;

            TitleBarText.Text = "Экспорт эмбеддингов";
            ContextText.Text = contextText;

            UpdateExportButtonText();
        }

        /// <summary>
        /// Конструктор с выбором говорящих - показывается список с чекбоксами.
        /// Используется для экспорта всех.
        /// </summary>
        public ExportFormatDialog(string contextText, IReadOnlyList<Speaker> speakers)
        {
            InitializeComponent();

            _hasSelectionSection = true;

            TitleBarText.Text = "Экспорт эмбеддингов";
            ContextText.Text = contextText;

            foreach (var spk in speakers)
            {
                var item = new SpeakerSelectionItem(spk);
                item.PropertyChanged += (_, _) => UpdateExportButtonText();
                _items.Add(item);
            }

            SpeakersListBox.ItemsSource = _items;
            UpdateExportButtonText();
        }

        // ==========================================
        // ===== ОБРАБОТЧИКИ =====
        // ==========================================

        private void SelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
                item.IsSelected = true;
        }

        private void DeselectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in _items)
                item.IsSelected = false;
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            // 1. Собираем выбранных говорящих
            if (_hasSelectionSection)
            {
                SelectedSpeakers = _items
                    .Where(i => i.IsSelected)
                    .Select(i => i.Speaker)
                    .ToList();

                if (SelectedSpeakers.Count == 0)
                {
                    MessageBox.Show(
                        "Не выбрано ни одного говорящего.\nОтметьте галочками, кого экспортировать.",
                        "Экспорт",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }
            }

            // 2. Формат
            SelectedFormat = RadioWide.IsChecked == true
                ? EmbeddingExportFormat.Wide
                : EmbeddingExportFormat.Long;

            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        // ==========================================
        // ===== ОБНОВЛЕНИЕ КНОПКИ ЭКСПОРТА =====
        // ==========================================

        private void UpdateExportButtonText()
        {
            if (!_hasSelectionSection)
            {
                ExportButton.Content = "Экспортировать";
                ExportButton.IsEnabled = true;
                return;
            }

            int selected = _items.Count(i => i.IsSelected);
            ExportButton.Content = $"Экспортировать ({selected})";
            ExportButton.IsEnabled = selected > 0;
        }
    }
}