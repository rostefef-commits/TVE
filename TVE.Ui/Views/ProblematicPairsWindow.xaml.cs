using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TVE.Core.Analysis;
using TVE.Core.Storage;

namespace TVE.Ui.Views
{
    public partial class ProblematicPairsWindow : Window
    {
        private readonly Speaker[] _speakers;
        private readonly int[] _clusters;
        private float _threshold = 0.50f;
        private bool _initialized;

        public ProblematicPairsWindow(Speaker[] speakers, int[] clusters, Window owner)
        {
            InitializeComponent();

            _speakers = speakers ?? Array.Empty<Speaker>();
            _clusters = clusters ?? Array.Empty<int>();
            Owner = owner;

            // Загружаем сохранённый порог
            _threshold = App.Settings.SpeakerThreshold;
            InitializeThresholdCombo();

            _initialized = true;
            RefreshList();
        }

        private void InitializeThresholdCombo()
        {
            ThresholdCombo.Items.Clear();
            for (int i = 0; i <= 20; i++)
            {
                float value = i * 0.05f;
                var item = new ComboBoxItem
                {
                    Content = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                    Tag = value.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                };
                ThresholdCombo.Items.Add(item);
            }
            SelectThresholdInCombo(_threshold);
        }

        private void SelectThresholdInCombo(float value)
        {
            for (int i = 0; i < ThresholdCombo.Items.Count; i++)
            {
                if (ThresholdCombo.Items[i] is ComboBoxItem item &&
                    item.Tag is string tag &&
                    float.TryParse(tag, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out float v))
                {
                    if (Math.Abs(v - value) < 0.001f)
                    {
                        ThresholdCombo.SelectedIndex = i;
                        return;
                    }
                }
            }
        }

        private void Threshold_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (!_initialized) return;
            if (_speakers.Length == 0) return;
            if (ThresholdCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag) return;
            if (!float.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float th)) return;

            _threshold = th;

            // Синхронизация с общей настройкой - чтобы карта подхватила то же значение
            App.Settings.SpeakerThreshold = th;
            App.SaveSettings();

            RefreshList();
        }

        private void ThresholdUp_Click(object sender, RoutedEventArgs e)
        {
            ChangeThreshold(+0.05f);
        }

        private void ThresholdDown_Click(object sender, RoutedEventArgs e)
        {
            ChangeThreshold(-0.05f);
        }

        private void ChangeThreshold(float delta)
        {
            float newValue = MathF.Round((_threshold + delta) * 100f) / 100f;
            newValue = MathF.Max(0.0f, MathF.Min(1.0f, newValue));
            SelectThresholdInCombo(newValue);
        }

        private void RefreshList()
        {
            if (_speakers.Length == 0 || _clusters.Length == 0)
            {
                PairsList.ItemsSource = null;
                ListContainer.Visibility = Visibility.Collapsed;
                EmptyText.Text = "Недостаточно данных для поиска проблемных пар.";
                EmptyText.Visibility = Visibility.Visible;
                CountText.Text = "Найдено пар: 0";
                HeaderText.Text = "Проблемные пары голосов";
                TitleBarText.Text = "Проблемные пары";
                Title = "Проблемные пары";
                return;
            }

            List<ProblematicPair> pairs;
            try
            {
                pairs = ProblematicPairFinder.Find(_speakers, _clusters, _threshold);
            }
            catch
            {
                pairs = new List<ProblematicPair>();
            }

            CountText.Text = $"Найдено пар: {pairs.Count}";

            if (pairs.Count == 0)
            {
                ListContainer.Visibility = Visibility.Collapsed;
                EmptyText.Text = "Проблемных пар не найдено. При текущем пороге все голоса хорошо разделены.";
                EmptyText.Visibility = Visibility.Visible;
                HeaderText.Text = "Проблемные пары голосов - не найдено";
                TitleBarText.Text = "Проблемные пары - не найдено";
                Title = "Проблемные пары - не найдено";
            }
            else
            {
                PairsList.ItemsSource = pairs;
                ListContainer.Visibility = Visibility.Visible;
                EmptyText.Visibility = Visibility.Collapsed;
                HeaderText.Text = $"Проблемные пары голосов ({pairs.Count})";
                TitleBarText.Text = $"Проблемные пары ({pairs.Count})";
                Title = $"Проблемные пары ({pairs.Count})";
            }
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void BtnMaximize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}