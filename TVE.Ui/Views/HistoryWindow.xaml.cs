using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using TVE.Core.Storage;

namespace TVE.Ui.Views
{
    /// <summary>
    /// Элемент истории для отображения в списке.
    /// </summary>
    public class HistoryItem
    {
        public string TimestampText { get; set; } = string.Empty;
        public string Text { get; set; } = string.Empty;
        public bool IsSystem { get; set; }
    }

    public partial class HistoryWindow : Window
    {
        private readonly int _speakerId;
        private readonly VoiceDatabase _db;
        private readonly ObservableCollection<HistoryItem> _items = new();

        public HistoryWindow(string speakerName, int speakerId, VoiceDatabase db)
        {
            InitializeComponent();

            _speakerId = speakerId;
            _db = db;

            Title = $"История чата — {speakerName}";
            TitleText.Text = $"История чата «{speakerName}»";

            HistoryList.ItemsSource = _items;

            LoadAll();
        }

        private void LoadAll()
        {
            var all = _db.GetUtterancesForSpeaker(_speakerId);
            Fill(all);
        }

        private void Fill(IEnumerable<Utterance> utterances)
        {
            _items.Clear();

            foreach (var u in utterances)
            {
                string text = u.IsSystem ? $"⚙ {u.Text}" : u.Text;

                _items.Add(new HistoryItem
                {
                    TimestampText = u.Timestamp.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"),
                    Text = text,
                    IsSystem = u.IsSystem
                });
            }
        }

        private void ApplyFilter_Click(object sender, RoutedEventArgs e)
        {
            DateTime? from = FromDate.SelectedDate;
            DateTime? to = ToDate.SelectedDate;

            if (from.HasValue && to.HasValue && from.Value > to.Value)
            {
                MessageBox.Show(
                    "Дата «С» не может быть позже даты «По».",
                    "Некорректный фильтр",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // Приводим к UTC (в БД храним UTC)
            DateTime? fromUtc = from?.ToUniversalTime();
            DateTime? toUtc = to?.ToUniversalTime();

            var filtered = _db.GetUtterancesForSpeaker(_speakerId, fromUtc, toUtc);
            Fill(filtered);
        }

        private void ResetFilter_Click(object sender, RoutedEventArgs e)
        {
            FromDate.SelectedDate = null;
            ToDate.SelectedDate = null;
            LoadAll();
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}