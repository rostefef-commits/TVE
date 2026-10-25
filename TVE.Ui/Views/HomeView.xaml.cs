using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TVE.Core.Analysis;
using TVE.Core.Audio;
using TVE.Core.Command;
using TVE.Core.Recognition;
using TVE.Core.Speakers;
using TVE.Core.Storage;

namespace TVE.Ui.Views
{
    public partial class HomeView : UserControl
    {
        private enum DialogState
        {
            Recording,
            ExpectingName
        }

        // ===== События =====
        public event Action<string>? NavigateRequested;
        public event Action? AnalysisRequested;

        private MicrophoneCapture? _capture;
        private WhisperRecognizer? _recognizer;
        private VoiceEmbedder? _embedder;
        private VoiceDatabase? _db;

        private readonly CommandDetector _commandDetector = new();

        private bool _isRecording;
        private bool _isProcessingFile;
        private DialogState _state = DialogState.Recording;
        private DateTime _commandTime;
        private int _pendingSpeakerId = -1;
        private int _currentSpeakerId = -1;
        private string _currentTranscription = "";
        private int _consecutiveBadCount;

        // ===== Настройки идентификации =====
        private const float LiveThreshold = 0.55f;
        private const float LiveMargin = 0.05f;
        private const float LiveWeight = 0.25f;
        private const float FileClusterThreshold = 0.6f;
        private const float FileThreshold = 0.55f;
        private const float FileMargin = 0.05f;
        private const float FileWeight = 0.15f;

        public HomeView()
        {
            InitializeComponent();
        }

        public void Initialize(
            MicrophoneCapture capture,
            WhisperRecognizer recognizer,
            VoiceEmbedder embedder,
            VoiceDatabase db)
        {
            _capture = capture;
            _recognizer = recognizer;
            _embedder = embedder;
            _db = db;

            _capture.SpeechSegmentReady += OnSpeechSegmentReady;
            _capture.MicrophoneLost += OnMicrophoneLost;
            _capture.MicrophoneRestored += OnMicrophoneRestored;

            if (!_capture.IsDeviceAvailable)
                ShowNoMicrophoneState();
        }

        // ==========================================
        // ===== КЛИК ПО КРУГУ =====
        // ==========================================

        private void MicrophoneCircle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isProcessingFile) return;
            if (_capture == null || !_capture.IsDeviceAvailable) return;

            if (_isRecording) StopRecording();
            else StartRecording();
        }

        private void StartRecording()
        {
            if (_capture == null) return;

            _isRecording = true;
            _currentTranscription = "";
            _state = DialogState.Recording;
            _currentSpeakerId = -1;
            _consecutiveBadCount = 0;
            _commandDetector.Reset();

            MicrophoneCircle.Fill = (Brush)FindResource("MicRecordingBrush");
            MicIconText.Visibility = Visibility.Visible;
            DropIconText.Visibility = Visibility.Collapsed;
            CircleStatusText.Visibility = Visibility.Collapsed;

            LeftPanel.Visibility = Visibility.Visible;
            RightPanel.Visibility = Visibility.Visible;
            RightPanelHeader.Text = "Говорите...";
            RightPanelHeader.Foreground = (Brush)FindResource("AppTextBrush");
            TranscriptionText.Text = "";
            StopButton.Visibility = Visibility.Visible;
            SpeakerNameText.Text = "...";
            SpeakerDescriptionText.Text = "...";

            _capture.Start(App.Settings.InputDeviceNumber);
        }

        private void StopRecording()
        {
            _isRecording = false;
            _state = DialogState.Recording;
            _capture?.Stop();

            MicrophoneCircle.Fill = (Brush)FindResource("MicIdleBrush");
            LeftPanel.Visibility = Visibility.Collapsed;
            RightPanel.Visibility = Visibility.Collapsed;
            StopButton.Visibility = Visibility.Collapsed;
        }

        private void StopButton_Click(object sender, RoutedEventArgs e) => StopRecording();

        // ==========================================
        // ===== DRAG-AND-DROP =====
        // ==========================================

        private void MicrophoneCircle_DragEnter(object sender, DragEventArgs e)
        {
            if (!IsValidDrop(e)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }

            e.Effects = DragDropEffects.Copy;
            MicrophoneCircle.Fill = (Brush)FindResource("MicDropFileBrush");
            MicIconText.Visibility = Visibility.Collapsed;
            DropIconText.Visibility = Visibility.Visible;
            e.Handled = true;
        }

        private void MicrophoneCircle_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = IsValidDrop(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void MicrophoneCircle_DragLeave(object sender, DragEventArgs e)
        {
            ResetDropVisual();
            e.Handled = true;
        }

        private async void MicrophoneCircle_Drop(object sender, DragEventArgs e)
        {
            ResetDropVisual();
            e.Handled = true;

            if (!IsValidDrop(e)) return;

            if (_isRecording)
            {
                LeftPanel.Visibility = Visibility.Visible;
                RightPanel.Visibility = Visibility.Visible;
                RightPanelHeader.Text = "⚠ Сначала остановите запись";
                RightPanelHeader.Foreground = (Brush)FindResource("WarningBrush");
                return;
            }

            if (_isProcessingFile) return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return;

            string filePath = files[0];

            if (_state == DialogState.ExpectingName)
            {
                _state = DialogState.Recording;
                _pendingSpeakerId = -1;
                _commandDetector.Reset();
            }

            await ProcessMp3FileAsync(filePath);
        }

        private bool IsValidDrop(DragEventArgs e)
        {
            if (_isProcessingFile) return false;
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files == null || files.Length == 0) return false;

            string ext = Path.GetExtension(files[0]).ToLowerInvariant();
            return ext == ".mp3" || ext == ".wav" || ext == ".m4a" || ext == ".flac"
                || ext == ".aac" || ext == ".ogg" || ext == ".oga";
        }

        private void ResetDropVisual()
        {
            if (_isProcessingFile) return;
            MicrophoneCircle.Fill = (Brush)FindResource("MicIdleBrush");
            MicIconText.Visibility = Visibility.Visible;
            DropIconText.Visibility = Visibility.Collapsed;
        }

        // ==========================================
        // ===== ОБРАБОТКА ФАЙЛА =====
        // ==========================================

        private async Task ProcessMp3FileAsync(string filePath)
        {
            _isProcessingFile = true;

            MicrophoneCircle.Fill = (Brush)FindResource("MicDropFileBrush");
            MicIconText.Visibility = Visibility.Visible;
            DropIconText.Visibility = Visibility.Collapsed;
            CircleStatusText.Text = "Обработка...";
            CircleStatusText.Visibility = Visibility.Visible;

            LeftPanel.Visibility = Visibility.Visible;
            RightPanel.Visibility = Visibility.Visible;
            RightPanelHeader.Text = "Обработка файла...";
            RightPanelHeader.Foreground = (Brush)FindResource("AppTextBrush");
            SpeakerNameText.Text = "...";
            SpeakerDescriptionText.Text = "...";
            StopButton.Visibility = Visibility.Collapsed;

            string fileName = Path.GetFileName(filePath);
            _currentTranscription = $"[{fileName}] ";
            TranscriptionText.Text = _currentTranscription;

            try
            {
                // === ПРОХОД 1: Декодирование и нарезка ===
                var segments = await Task.Run(() => Mp3Processor.ExtractSegments(filePath));

                if (segments.Count == 0)
                {
                    SetFileMessage("⚠ Файл не содержит распознаваемой речи");
                    return;
                }

                // === ПРОХОД 2: Сбор эмбеддингов и текстов (без записи в БД) ===
                var collected = new List<(float[] emb, string text)>();
                foreach (var seg in segments)
                {
                    var item = await CollectSegmentDataAsync(seg);
                    if (item.HasValue)
                        collected.Add(item.Value);
                }

                if (collected.Count == 0)
                {
                    SetFileMessage("⚠ Не удалось распознать речь в файле");
                    return;
                }

                // === ПРОХОД 3: Кластеризация эмбеддингов ===
                var embeddings = collected.Select(c => c.emb).ToArray();
                var labels = SpeakerClusterer.Cluster(embeddings, FileClusterThreshold);

                int clusterCount = labels.Max() + 1;
                LogMp3($"Clusters found: {clusterCount} (from {collected.Count} segments)");

                // === ПРОХОД 4: Для каждого кластера — назначение говорящего ===
                var clusterToSpeakerId = new int[clusterCount];
                var clusterToName = new string[clusterCount];

                for (int cluster = 0; cluster < clusterCount; cluster++)
                {
                    var clusterEmbs = new List<float[]>();
                    for (int i = 0; i < collected.Count; i++)
                    {
                        if (labels[i] == cluster)
                            clusterEmbs.Add(collected[i].emb);
                    }

                    var centroid = SpeakerClusterer.ComputeCentroid(clusterEmbs);

                    var (speaker, similarity) = _db!.FindClosestSpeakerWithScore(centroid, FileThreshold, FileMargin);

                    if (speaker == null)
                    {
                        speaker = new Speaker
                        {
                            DisplayName = _db.NextUnknownName(),
                            Description = "пока тут пусто",
                            VoiceEmbedding = EmbeddingMath.L2Normalize(centroid)
                        };
                        speaker.Id = _db.InsertSpeaker(speaker);
                        LogMp3($"Cluster {cluster}: NEW speaker {speaker.DisplayName} " +
                               $"(sim={similarity:F3})");
                    }
                    else
                    {
                        _db.UpdateEmbeddingSmart(speaker, centroid, similarity);
                        LogMp3($"Cluster {cluster}: MATCHED {speaker.DisplayName} " +
                               $"(sim={similarity:F3})");
                    }

                    clusterToSpeakerId[cluster] = speaker.Id;
                    clusterToName[cluster] = speaker.DisplayName;
                }

                // === ПРОХОД 5: Запись реплик в БД ===
                var earliestTs = DateTime.UtcNow;
                var uniqueSpeakers = clusterToSpeakerId.Distinct().ToList();

                foreach (var spkId in uniqueSpeakers)
                {
                    _db.AddUtterance(new Utterance
                    {
                        SpeakerId = spkId,
                        Text = $"вставлен .mp3: {fileName}",
                        Timestamp = earliestTs.AddMilliseconds(-1),
                        IsSystem = true
                    });
                }

                for (int i = 0; i < collected.Count; i++)
                {
                    int spkId = clusterToSpeakerId[labels[i]];
                    _db.AddUtterance(new Utterance
                    {
                        SpeakerId = spkId,
                        Text = collected[i].text,
                        Timestamp = earliestTs.AddMilliseconds(i),
                        IsSystem = false
                    });

                    var spk = _db.GetSpeaker(spkId);
                    if (spk != null)
                    {
                        spk.LastSeen = DateTime.UtcNow;
                        _db.UpdateSpeaker(spk);
                    }
                }

                // === UI: показываем результат ===
                Dispatcher.Invoke(() =>
                {
                    for (int i = 0; i < collected.Count; i++)
                    {
                        int cl = labels[i];
                        _currentTranscription += $"[{clusterToName[cl]}] {collected[i].text} ";
                    }
                    TranscriptionText.Text = _currentTranscription;

                    int[] countPerCluster = new int[clusterCount];
                    for (int i = 0; i < collected.Count; i++)
                        countPerCluster[labels[i]]++;

                    int mainCluster = 0;
                    for (int c = 1; c < clusterCount; c++)
                        if (countPerCluster[c] > countPerCluster[mainCluster])
                            mainCluster = c;

                    int mainSpeakerId = clusterToSpeakerId[mainCluster];
                    var mainSpeaker = _db?.GetSpeaker(mainSpeakerId);

                    if (mainSpeaker != null)
                    {
                        SpeakerNameText.Text = mainSpeaker.DisplayName;
                        SpeakerDescriptionText.Text = mainSpeaker.Description;
                        SpeakerNameText.Foreground = (Brush)FindResource("AppTextBrush");
                    }
                });

                string summary = clusterCount == 1
                    ? $"✅ Обработано: {collected.Count} реплик, говорящих: 1"
                    : $"✅ Обработано: {collected.Count} реплик, говорящих: {clusterCount} " +
                      $"({string.Join(", ", clusterToName.Distinct())})";

                SetFileMessage(summary);
            }
            catch (FileNotFoundException)
            {
                SetFileMessage("⚠ Не удалось прочитать файл");
            }
            catch (Exception ex)
            {
                SetFileMessage($"⚠ Ошибка: {ex.Message}");
            }
            finally
            {
                _isProcessingFile = false;
                MicrophoneCircle.Fill = (Brush)FindResource("MicIdleBrush");
                MicIconText.Visibility = Visibility.Visible;
                DropIconText.Visibility = Visibility.Collapsed;
                CircleStatusText.Visibility = Visibility.Collapsed;
                StopButton.Visibility = Visibility.Collapsed;
            }
        }

        private async Task<(float[] emb, string text)?> CollectSegmentDataAsync(float[] samples)
        {
            if (_embedder == null || _recognizer == null) return null;

            if (SegmentQualityChecker.CheckBasicQuality(samples) != SegmentBadReason.None)
                return null;

            float[] embedding;
            try { embedding = _embedder.Extract(samples); }
            catch { return null; }
            if (embedding.Length == 0) return null;

            string text;
            try { text = await _recognizer.TranscribeAsync(samples); }
            catch { return null; }
            if (SegmentQualityChecker.IsLikelyGarbage(text)) return null;

            return (embedding, text);
        }

        private void SetFileMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                RightPanelHeader.Text = message;
                RightPanelHeader.Foreground = (Brush)FindResource("AppTextBrush");
            });
        }

        // ==========================================
        // ===== ЖИВАЯ ЗАПИСЬ =====
        // ==========================================

        private async void OnSpeechSegmentReady(float[] samples)
        {
            if (!_isRecording) return;

            // Диагностика — только если включена в настройках
            if (App.Settings.CreateDiagnosticFiles)
                SaveDebugWav(samples);

            var bad = SegmentQualityChecker.CheckBasicQuality(samples);
            if (bad != SegmentBadReason.None)
            {
                ShowBadWarning(bad);
                return;
            }

            if (_embedder == null || _recognizer == null || _db == null) return;

            float[] embedding;
            try { embedding = _embedder.Extract(samples); }
            catch { ShowBadWarning(SegmentBadReason.TooShort); return; }
            if (embedding.Length == 0)
            {
                ShowBadWarning(SegmentBadReason.TooShort);
                return;
            }

            if (samples.Length >= SegmentQualityChecker.MinSamplesForSplitCheck)
            {
                int half = samples.Length / 2;
                var emb1 = _embedder.Extract(samples[..half]);
                var emb2 = _embedder.Extract(samples[half..]);
                if (SegmentQualityChecker.LooksLikeMultipleSpeakers(samples, emb1, emb2))
                {
                    ShowBadWarning(SegmentBadReason.MultipleSpeakers);
                    return;
                }
            }

            string text;
            try { text = await _recognizer.TranscribeAsync(samples); }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                    RightPanelHeader.Text = $"⚠ Ошибка: {ex.Message}");
                return;
            }

            if (SegmentQualityChecker.IsLikelyGarbage(text))
            {
                ShowBadWarning(SegmentBadReason.TooShort);
                return;
            }

            ResetBadWarning();

            var (speaker, similarity) = _db.FindClosestSpeakerWithScore(
                embedding, LiveThreshold, LiveMargin);
            bool isNew = false;

            if (speaker == null)
            {
                speaker = new Speaker
                {
                    DisplayName = _db.NextUnknownName(),
                    Description = "пока тут пусто",
                    VoiceEmbedding = EmbeddingMath.L2Normalize(embedding)
                };
                speaker.Id = _db.InsertSpeaker(speaker);
                isNew = true;
            }
            else
            {
                _db.UpdateEmbeddingSmart(speaker, embedding, similarity);
            }

            _currentSpeakerId = speaker.Id;
            Dispatcher.Invoke(() => ProcessRecognizedText(text, speaker, isNew));
        }

        private void ProcessRecognizedText(string text, Speaker speaker, bool isNew)
        {
            if (_state == DialogState.ExpectingName)
            {
                if ((DateTime.Now - _commandTime).TotalSeconds > 10)
                {
                    BackToRecording("⌛ Время ожидания истекло.");
                    SaveUtterance(speaker.Id, text);
                    return;
                }

                if (speaker.Id != _pendingSpeakerId)
                {
                    BackToRecording("⚠ Ожидание отменено: заговорил другой человек.");
                    SaveUtterance(speaker.Id, text);
                    return;
                }

                string fio = NameExtractor.Extract(text);
                if (string.IsNullOrWhiteSpace(fio))
                {
                    _commandTime = DateTime.Now;
                    RightPanelHeader.Text = "Не удалось распознать ФИО. Повторите, пожалуйста.";
                    return;
                }

                if (_db != null && _db.NameExists(fio, excludeSpeakerId: _pendingSpeakerId))
                {
                    _commandTime = DateTime.Now;
                    RightPanelHeader.Text = $"⚠ Такое имя уже есть в базе: {fio}. Назовите другое.";
                    return;
                }

                RenameSpeaker(_pendingSpeakerId, fio);
                _currentTranscription += $"[Имя: {fio}] ";
                TranscriptionText.Text = _currentTranscription;
                BackToRecording($"✅ Имя изменено на «{fio}»");
                return;
            }

            if (_commandDetector.FeedAndCheck(text))
            {
                _state = DialogState.ExpectingName;
                _commandTime = DateTime.Now;
                _pendingSpeakerId = speaker.Id;

                MicrophoneCircle.Fill = (Brush)FindResource("MicExpectingNameBrush");
                RightPanelHeader.Text = "Назовите ваше ФИО";
                RightPanelHeader.Foreground = (Brush)FindResource("AppTextBrush");
                SpeakerDescriptionText.Text = "ожидание нового ФИО";

                _currentTranscription += "[Код принят] ";
                TranscriptionText.Text = _currentTranscription;
                return;
            }

            _currentTranscription += text + " ";
            TranscriptionText.Text = _currentTranscription;

            SpeakerNameText.Text = speaker.DisplayName;
            SpeakerDescriptionText.Text = speaker.Description;
            SpeakerNameText.Foreground = isNew
                ? (Brush)FindResource("WarningBrush")
                : (Brush)FindResource("AppTextBrush");

            SaveUtterance(speaker.Id, text);
        }

        private void ShowBadWarning(SegmentBadReason reason)
        {
            _consecutiveBadCount++;
            string message = _consecutiveBadCount >= SegmentQualityChecker.MaxConsecutiveBad
                ? SegmentQualityChecker.MsgCheckMic
                : SegmentQualityChecker.GetMessage(reason);

            Dispatcher.Invoke(() =>
            {
                RightPanelHeader.Text = "⚠ " + message;
                RightPanelHeader.Foreground = (Brush)FindResource("WarningBrush");
                TranscriptionText.Text = "";
            });
        }

        private void ResetBadWarning()
        {
            if (_consecutiveBadCount == 0) return;
            _consecutiveBadCount = 0;
            Dispatcher.Invoke(RestorePanelHeader);
        }

        private void RestorePanelHeader()
        {
            RightPanelHeader.Foreground = (Brush)FindResource("AppTextBrush");
            RightPanelHeader.Text = _state == DialogState.ExpectingName
                ? "Назовите ваше ФИО"
                : "Говорите...";
            TranscriptionText.Text = _currentTranscription;
        }

        private void BackToRecording(string message)
        {
            _state = DialogState.Recording;
            _pendingSpeakerId = -1;
            _commandDetector.Reset();

            MicrophoneCircle.Fill = (Brush)FindResource("MicRecordingBrush");
            RightPanelHeader.Text = "Говорите...";
            RightPanelHeader.Foreground = (Brush)FindResource("AppTextBrush");
            _currentTranscription += message + " ";
            TranscriptionText.Text = _currentTranscription;

            if (_currentSpeakerId >= 0 && _db != null)
            {
                var spk = _db.GetSpeaker(_currentSpeakerId);
                if (spk != null)
                {
                    SpeakerNameText.Text = spk.DisplayName;
                    SpeakerDescriptionText.Text = spk.Description;
                    SpeakerNameText.Foreground = (Brush)FindResource("AppTextBrush");
                }
            }
        }

        private void RenameSpeaker(int speakerId, string newName)
        {
            if (_db == null || speakerId < 0) return;
            var spk = _db.GetSpeaker(speakerId);
            if (spk == null) return;

            string oldName = spk.DisplayName;
            spk.DisplayName = newName;
            _db.UpdateSpeaker(spk);

            _db.AddUtterance(new Utterance
            {
                SpeakerId = speakerId,
                Text = $"Имя изменено: {oldName} → {newName}",
                Timestamp = DateTime.UtcNow,
                IsSystem = true
            });

            SpeakerNameText.Text = newName;
        }

        private void SaveUtterance(int speakerId, string text)
        {
            if (_db == null || speakerId < 0) return;

            _db.AddUtterance(new Utterance
            {
                SpeakerId = speakerId,
                Text = text,
                Timestamp = DateTime.UtcNow,
                IsSystem = false
            });

            var spk = _db.GetSpeaker(speakerId);
            if (spk != null)
            {
                spk.LastSeen = DateTime.UtcNow;
                _db.UpdateSpeaker(spk);
            }
        }

        private void OnMicrophoneLost(Exception? ex)
        {
            Dispatcher.Invoke(() =>
            {
                _isRecording = false;
                _state = DialogState.Recording;
                _pendingSpeakerId = -1;
                _commandDetector.Reset();

                ShowNoMicrophoneState();

                LeftPanel.Visibility = Visibility.Collapsed;
                RightPanel.Visibility = Visibility.Collapsed;
                StopButton.Visibility = Visibility.Collapsed;
            });
        }

        private void OnMicrophoneRestored()
        {
            Dispatcher.Invoke(() =>
            {
                if (_isRecording || _isProcessingFile) return;
                MicrophoneCircle.Fill = (Brush)FindResource("MicIdleBrush");
                CircleStatusText.Visibility = Visibility.Collapsed;
            });
        }

        private void ShowNoMicrophoneState()
        {
            MicrophoneCircle.Fill = (Brush)FindResource("MicNoDeviceBrush");
            CircleStatusText.Text = "Вставьте устройство ввода";
            CircleStatusText.Visibility = Visibility.Visible;
        }

        // ==========================================
        // ===== ДИАГНОСТИКА (только если включена) =====
        // ==========================================

        private void SaveDebugWav(float[] samples)
        {
            try
            {
                if (!App.Settings.CreateDiagnosticFiles) return;

                string debugFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "debug_segments");
                Directory.CreateDirectory(debugFolder);
                string filePath = Path.Combine(debugFolder,
                    $"segment_{DateTime.Now:yyyy-MM-dd_HH-mm-ss-fff}.wav");

                using var writer = new WaveFileWriter(filePath, new WaveFormat(16000, 1));
                writer.WriteSamples(samples, 0, samples.Length);
            }
            catch { }
        }

        private static void LogMp3(string message)
        {
            try
            {
                if (!App.Settings.CreateDiagnosticFiles) return;

                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "debug_decoded");
                Directory.CreateDirectory(folder);
                string logPath = Path.Combine(folder, "mp3_debug.log");
                File.AppendAllText(logPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] [Segment] {message}{Environment.NewLine}");
            }
            catch { }
        }

        // ==========================================
        // ===== НИЖНИЕ КНОПКИ =====
        // ==========================================

        private void BtnVoices_Click(object sender, RoutedEventArgs e)
            => NavigateRequested?.Invoke("Voices");

        private void BtnAnalysis_Click(object sender, RoutedEventArgs e)
            => AnalysisRequested?.Invoke();

        private void BtnSettings_Click(object sender, RoutedEventArgs e)
            => NavigateRequested?.Invoke("Settings");
    }
}