using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace CurePlease
{
    /// <summary>
    /// Maps a 0..100 percent value to a bar brush. ConverterParameter "hp" (the
    /// default) uses the legacy Cure Please HP thresholds (&gt;=75 green, &gt;50
    /// yellow, &gt;25 orange, otherwise red); "mp" is a flat blue.
    /// </summary>
    public sealed class PercentBarBrushConverter : IValueConverter
    {
        private static readonly Brush HpHigh = Freeze(0x66, 0xBB, 0x6A);
        private static readonly Brush HpMid = Freeze(0xFF, 0xEE, 0x58);
        private static readonly Brush HpLow = Freeze(0xFF, 0xA7, 0x26);
        private static readonly Brush HpCritical = Freeze(0xEF, 0x53, 0x50);
        private static readonly Brush Mp = Freeze(0x64, 0xB5, 0xF6);

        private static Brush Freeze(byte r, byte g, byte b)
        {
            var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            brush.Freeze();
            return brush;
        }

        /// <summary>Converts a 0..100 percent to the matching bar brush.</summary>
        /// <param name="value">The percent value (any numeric type).</param>
        /// <param name="targetType">Ignored.</param>
        /// <param name="parameter">"hp" (default) or "mp".</param>
        /// <param name="culture">Culture for numeric conversion.</param>
        /// <returns>The brush for the bar fill.</returns>
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (parameter is string kind && kind.Equals("mp", StringComparison.OrdinalIgnoreCase))
            {
                return Mp;
            }

            double pct = 0;
            try { pct = System.Convert.ToDouble(value, culture); }
            catch (FormatException) { }
            catch (InvalidCastException) { }
            catch (OverflowException) { }

            if (pct >= 75)
            {
                return HpHigh;
            }
            if (pct > 50)
            {
                return HpMid;
            }
            if (pct > 25)
            {
                return HpLow;
            }
            return HpCritical;
        }

        /// <summary>Not supported (one-way converter).</summary>
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
