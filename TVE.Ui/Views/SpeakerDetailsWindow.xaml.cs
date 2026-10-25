using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TVE.Core.Storage;

namespace TVE.Ui.Views
{
    public partial class SpeakerDetailsWindow : Window
    {
        public SpeakerDetailsWindow(
            Speaker speaker,
            VoiceDatabase db,
            Speaker[] allSpeakers,
            int[] clusterLabels,
            int clusterNumber)
        {
            InitializeComponent();

            Title = $"Детали: {speaker.DisplayName}";
            TitleText.Text = speaker.DisplayName;
            DescriptionText.Text = string.IsNullOrWhiteSpace(speaker.Description)
                ? "Описание не задано"
                : speaker.Description;

            ClusterText.Text = $"Кластер {clusterNumber + 1}";

            try
            {
                var utterances = db.GetUtterancesForSpeaker(speaker.Id);
                UtterancesText.Text = utterances.Count.ToString();
            }
            catch { UtterancesText.Text = "-"; }

            try
            {
                var sameCluster = new List<float[]>();
                for (int i = 0; i < allSpeakers.Length && i < clusterLabels.Length; i++)
                {
                    if (clusterLabels[i] == clusterNumber && allSpeakers[i].VoiceEmbedding.Length > 0)
                        sameCluster.Add(allSpeakers[i].VoiceEmbedding);
                }

                if (sameCluster.Count >= 2)
                {
                    float sum = 0;
                    int count = 0;
                    for (int i = 0; i < sameCluster.Count; i++)
                        for (int j = i + 1; j < sameCluster.Count; j++)
                        {
                            sum += Cosine(sameCluster[i], sameCluster[j]);
                            count++;
                        }
                    float avg = count > 0 ? sum / count : 0;
                    AvgSimText.Text = avg.ToString("F3");
                }
                else AvgSimText.Text = "мало данных";
            }
            catch { AvgSimText.Text = "-"; }

            FirstSeenText.Text = speaker.FirstSeen.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
            LastSeenText.Text = speaker.LastSeen.ToLocalTime().ToString("dd.MM.yyyy HH:mm");

            // Топ-3 ближайших соседей
            try
            {
                var neighbors = new List<(string Name, float Sim)>();
                foreach (var other in allSpeakers)
                {
                    if (other.Id == speaker.Id) continue;
                    if (other.VoiceEmbedding.Length == 0) continue;

                    float sim = Cosine(speaker.VoiceEmbedding, other.VoiceEmbedding);
                    neighbors.Add((other.DisplayName, sim));
                }

                var top3 = neighbors.OrderByDescending(n => n.Sim).Take(3).ToList();

                if (top3.Count == 0)
                {
                    var empty = new TextBlock
                    {
                        Text = "Нет других голосов в базе",
                        FontSize = 12,
                        Opacity = 0.6
                    };
                    empty.SetResourceReference(TextBlock.ForegroundProperty, "AppTextBrush");
                    NeighborsPanel.Children.Add(empty);
                }
                else
                {
                    int rank = 1;
                    foreach (var (name, sim) in top3)
                    {
                        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                        var rankText = new TextBlock
                        {
                            Text = $"{rank}.",
                            FontSize = 12,
                            Opacity = 0.6
                        };
                        rankText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextBrush");
                        Grid.SetColumn(rankText, 0);
                        row.Children.Add(rankText);

                        var nameText = new TextBlock
                        {
                            Text = name,
                            FontSize = 12,
                            TextTrimming = TextTrimming.CharacterEllipsis
                        };
                        nameText.SetResourceReference(TextBlock.ForegroundProperty, "AppTextBrush");
                        Grid.SetColumn(nameText, 1);
                        row.Children.Add(nameText);

                        var simText = new TextBlock
                        {
                            Text = sim.ToString("F3"),
                            FontSize = 12,
                            FontWeight = System.Windows.FontWeights.SemiBold,
                            Foreground = GetSimBrush(sim)
                        };
                        Grid.SetColumn(simText, 2);
                        row.Children.Add(simText);

                        NeighborsPanel.Children.Add(row);
                        rank++;
                    }
                }
            }
            catch { }
        }

        private static SolidColorBrush GetSimBrush(float sim)
        {
            Color c;
            if (sim >= 0.75f) c = Color.FromRgb(0xC6, 0x28, 0x28);
            else if (sim >= 0.55f) c = Color.FromRgb(0xEF, 0x6C, 0x00);
            else if (sim >= 0.35f) c = Color.FromRgb(0xF9, 0xA8, 0x25);
            else c = Color.FromRgb(0x2E, 0x7D, 0x32);
            return new SolidColorBrush(c);
        }

        private static float Cosine(float[] a, float[] b)
        {
            if (a.Length != b.Length) return 0;
            float dot = 0, na = 0, nb = 0;
            for (int i = 0; i < a.Length; i++)
            {
                dot += a[i] * b[i];
                na += a[i] * a[i];
                nb += b[i] * b[i];
            }
            if (na == 0 || nb == 0) return 0;
            return dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}