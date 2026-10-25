using System.Windows;

namespace TVE.Ui.Views
{
    public partial class DescriptionWindow : Window
    {
        public string ResultDescription { get; private set; } = string.Empty;

        public DescriptionWindow(string speakerName, string currentDescription)
        {
            InitializeComponent();

            Title = $"Описание — {speakerName}";
            TitleText.Text = $"Описание для «{speakerName}»";
            DescriptionBox.Text = currentDescription ?? string.Empty;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ResultDescription = DescriptionBox.Text?.Trim() ?? string.Empty;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}