using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiteDB;
using TVE.Core.Audio;
using TVE.Core.Services;
using TVE.Core.Storage;
using TVE.Ui.Services;

namespace TVE.Ui.Views
{
    public partial class SettingsView : UserControl
    {
        public event Action? BackRequested;

        private bool _loading;

        public SettingsView()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Обновляет экран: список устройств, тему, путь к БД, диагностику.
        /// Вызывается из MainWindow при каждом переходе на экран «Настройки».
        /// </summary>
        public void Refresh()
        {
            LoadInputDevices();
            LoadThemeSelection();
            LoadDatabasePath();
            LoadDiagnosticSetting();
        }

        // ===== Устройство ввода =====
        private void LoadInputDevices()
        {
            _loading = true;

            var devices = AudioDeviceService.GetInputDevices();
            InputDeviceCombo.Items.Clear();

            if (devices.Count == 0)
            {
                InputDeviceCombo.Items.Add(new ComboBoxItem
                {
                    Content = "Устройства не найдены",
                    IsEnabled = false,
                    Tag = null
                });
                InputDeviceCombo.SelectedIndex = 0;

                App.Settings.InputDeviceNumber = null;
                App.SaveSettings();

                _loading = false;
                return;
            }

            foreach (var d in devices)
            {
                InputDeviceCombo.Items.Add(new ComboBoxItem
                {
                    Content = d.Name,
                    Tag = d.Number
                });
            }

            int? saved = App.Settings.InputDeviceNumber;
            int selectedIndex = 0;
            for (int i = 0; i < devices.Count; i++)
            {
                if (devices[i].Number == saved)
                {
                    selectedIndex = i;
                    break;
                }
            }
            InputDeviceCombo.SelectedIndex = selectedIndex;

            if (saved != devices[selectedIndex].Number)
            {
                App.Settings.InputDeviceNumber = devices[selectedIndex].Number;
                App.SaveSettings();
            }

            _loading = false;
        }

        private void InputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            if (InputDeviceCombo.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not int number) return;

            App.Settings.InputDeviceNumber = number;
            App.SaveSettings();
        }

        // ===== Тема =====
        private void LoadThemeSelection()
        {
            _loading = true;

            switch (App.Settings.Theme)
            {
                case ThemeManager.Light:
                    ThemeLight.IsChecked = true;
                    break;
                case ThemeManager.Dark:
                    ThemeDark.IsChecked = true;
                    break;
                default:
                    ThemeSystem.IsChecked = true;
                    break;
            }

            _loading = false;
        }

        private void Theme_Checked(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            if (sender is not RadioButton rb) return;
            if (rb.Tag is not string themeName) return;

            App.Settings.Theme = themeName;
            App.SaveSettings();
            ThemeManager.Apply(themeName);
            ModuleStatus.SetTheme(themeName);
        }

        // ===== Путь к базе данных =====
        private void LoadDatabasePath()
        {
            string path = !string.IsNullOrWhiteSpace(App.Settings.DatabasePath)
                ? App.Settings.DatabasePath
                : VoiceDatabase.DefaultPath;

            DbPathText.Text = Path.GetDirectoryName(path) ?? path;
            DbChangeHint.Visibility = Visibility.Collapsed;
        }

        private void ChangeDbFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Выберите папку для хранения базы данных"
            };

            string currentPath = !string.IsNullOrWhiteSpace(App.Settings.DatabasePath)
                ? App.Settings.DatabasePath
                : VoiceDatabase.DefaultPath;
            string? currentFolder = Path.GetDirectoryName(currentPath);
            if (!string.IsNullOrEmpty(currentFolder) && Directory.Exists(currentFolder))
                dialog.InitialDirectory = currentFolder;

            if (dialog.ShowDialog() != true) return;

            string newFolder = dialog.FolderName;
            string newDbPath = Path.Combine(newFolder, "voices.db");

            // Проверка прав на запись
            try
            {
                string testFile = Path.Combine(newFolder, ".tve_write_test");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch
            {
                MessageBox.Show(
                    "В выбранной папке нет прав на запись.\nВыберите другую папку.",
                    "Ошибка",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            string currentDbPath = !string.IsNullOrWhiteSpace(App.Settings.DatabasePath)
                ? App.Settings.DatabasePath
                : VoiceDatabase.DefaultPath;

            bool copyCurrent = false;
            if (File.Exists(currentDbPath))
            {
                var result = MessageBox.Show(
                    $"Скопировать текущую базу данных в новое место?\n\n" +
                    $"Откуда: {currentDbPath}\nКуда: {newDbPath}\n\n" +
                    $"Да — перенести все голоса и историю.\n" +
                    $"Нет — при следующем запуске база создастся пустой.\n" +
                    $"Отмена — не менять путь.",
                    "Копирование базы данных",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel) return;
                copyCurrent = result == MessageBoxResult.Yes;
            }

            if (copyCurrent)
            {
                try
                {
                    File.Copy(currentDbPath, newDbPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        $"Не удалось скопировать базу данных:\n{ex.Message}",
                        "Ошибка",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }
            }

            App.Settings.DatabasePath = newDbPath;
            App.SaveSettings();

            LoadDatabasePath();
            DbChangeHint.Text = "⚠ Новый путь вступит в силу при следующем запуске программы.";
            DbChangeHint.Visibility = Visibility.Visible;
        }

        private void OpenDbFolder_Click(object sender, RoutedEventArgs e)
        {
            string currentPath = !string.IsNullOrWhiteSpace(App.Settings.DatabasePath)
                ? App.Settings.DatabasePath
                : VoiceDatabase.DefaultPath;

            string? folder = Path.GetDirectoryName(currentPath);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                MessageBox.Show("Папка не найдена.", "Ошибка",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = folder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть папку:\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ===== Экспорт базы =====
        private void ExportDatabase_Click(object sender, RoutedEventArgs e)
        {
            string currentPath = GetCurrentDbPath();

            if (!File.Exists(currentPath))
            {
                MessageBox.Show("Файл базы данных не найден.",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Сохранить резервную копию базы",
                Filter = "База данных TVE (*.db)|*.db",
                FileName = $"voices_backup_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.db"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                File.Copy(currentPath, dialog.FileName, overwrite: true);
                MessageBox.Show(
                    $"Резервная копия сохранена:\n{dialog.FileName}",
                    "Готово",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось сохранить копию:\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ===== Импорт базы =====
        private void ImportDatabase_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите файл базы данных для импорта",
                Filter = "База данных TVE (*.db)|*.db|Все файлы (*.*)|*.*"
            };

            if (dialog.ShowDialog() != true) return;

            string sourceFile = dialog.FileName;

            // 1. Проверяем, что это валидный LiteDB с коллекцией speakers
            if (!IsValidTveDatabase(sourceFile, out string validationError))
            {
                MessageBox.Show(
                    $"Выбранный файл не является базой данных TVE.\n\n{validationError}",
                    "Ошибка импорта",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // 2. Спрашиваем про бэкап текущей базы
            string currentPath = GetCurrentDbPath();
            bool makeBackup = false;

            if (File.Exists(currentPath))
            {
                var result = MessageBox.Show(
                    $"Текущая база будет заменена.\n\n" +
                    $"Сохранить текущую базу как резервную копию?\n" +
                    $"(Копия будет сохранена рядом: {Path.GetDirectoryName(currentPath)})\n\n" +
                    $"Да — сохранить копию перед заменой.\n" +
                    $"Нет — заменить без копии.\n" +
                    $"Отмена — ничего не менять.",
                    "Импорт базы данных",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel) return;
                makeBackup = result == MessageBoxResult.Yes;
            }

            // 3. Делаем бэкап, если нужно
            if (makeBackup)
            {
                try
                {
                    string backupFolder = Path.GetDirectoryName(currentPath) ?? AppDomain.CurrentDomain.BaseDirectory;
                    string backupName = $"voices_backup_before_import_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.db";
                    string backupPath = Path.Combine(backupFolder, backupName);
                    File.Copy(currentPath, backupPath, overwrite: true);
                }
                catch (Exception ex)
                {
                    var result = MessageBox.Show(
                        $"Не удалось сохранить резервную копию:\n{ex.Message}\n\n" +
                        $"Продолжить импорт без копии?",
                        "Ошибка бэкапа",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (result != MessageBoxResult.Yes) return;
                }
            }

            // 4. Копируем файл поверх текущей базы
            try
            {
                // Если папки нет — создаём
                string? targetFolder = Path.GetDirectoryName(currentPath);
                if (!string.IsNullOrEmpty(targetFolder))
                    Directory.CreateDirectory(targetFolder);

                File.Copy(sourceFile, currentPath, overwrite: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Не удалось импортировать базу данных:\n{ex.Message}",
                    "Ошибка импорта",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            // 5. Предлагаем перезапустить
            var restartResult = MessageBox.Show(
                "✅ Импорт завершён.\n\n" +
                "Чтобы изменения вступили в силу, программу нужно перезапустить.\n\n" +
                "Перезапустить сейчас?",
                "Импорт выполнен",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (restartResult == MessageBoxResult.Yes)
            {
                RestartApplication();
            }
            else
            {
                DbChangeHint.Text = "⚠ База данных импортирована. Перезапустите программу для применения.";
                DbChangeHint.Visibility = Visibility.Visible;
            }
        }

        /// <summary>
        /// Проверяет, что выбранный файл — валидный LiteDB с коллекцией speakers.
        /// </summary>
        private static bool IsValidTveDatabase(string path, out string error)
        {
            error = string.Empty;
            try
            {
                using var db = new LiteDatabase($"Filename={path};ReadOnly=true");
                var collectionNames = db.GetCollectionNames().ToList();
                if (!collectionNames.Contains("speakers"))
                {
                    error = "В файле нет коллекции 'speakers'.";
                    return false;
                }
                // Пробуем прочитать хотя бы одну запись
                var speakers = db.GetCollection("speakers");
                speakers.Count();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Не удалось открыть файл как базу LiteDB:\n{ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Перезапускает текущее приложение.
        /// </summary>
        private static void RestartApplication()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (string.IsNullOrEmpty(exePath))
                {
                    MessageBox.Show("Не удалось определить путь к .exe. Перезапустите вручную.",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = exePath,
                    UseShellExecute = true
                });

                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось перезапустить программу:\n{ex.Message}",
                    "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string GetCurrentDbPath()
        {
            return !string.IsNullOrWhiteSpace(App.Settings.DatabasePath)
                ? App.Settings.DatabasePath
                : VoiceDatabase.DefaultPath;
        }

        // ===== Диагностика =====
        private void LoadDiagnosticSetting()
        {
            _loading = true;
            CreateDiagnosticFilesCheck.IsChecked = App.Settings.CreateDiagnosticFiles;
            _loading = false;
        }

        private void CreateDiagnosticFiles_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            App.Settings.CreateDiagnosticFiles = CreateDiagnosticFilesCheck.IsChecked == true;
            App.SaveSettings();
        }

        // ===== Назад =====
        private void BtnBack_Click(object sender, RoutedEventArgs e)
        {
            BackRequested?.Invoke();
        }
    }
}