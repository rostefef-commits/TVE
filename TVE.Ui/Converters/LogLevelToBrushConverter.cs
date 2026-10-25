using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using TVE.Core.Services;

namespace TVE.Ui.Converters
{
    public class LogLevelToBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush InfoBrush = CreateBrush(0x77, 0x77, 0x77);
        private static readonly SolidColorBrush OkBrush = CreateBrush(0x2E, 0x7D, 0x32);
        private static readonly SolidColorBrush WarnBrush = CreateBrush(0xEF, 0x6C, 0x00);
        private static readonly SolidColorBrush ErrorBrush = CreateBrush(0xC6, 0x28, 0x28);

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is LogLevel level)
            {
                return level switch
                {
                    LogLevel.Ok => OkBrush,
                    LogLevel.Warn => WarnBrush,
                    LogLevel.Error => ErrorBrush,
                    _ => InfoBrush
                };
            }
            return InfoBrush;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();

        private static SolidColorBrush CreateBrush(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }
    }
}