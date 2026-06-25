using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NetUsageMonitor.Common;

namespace NetUsageMonitor.Ui;

/// <summary>Formats a byte count (long/double) as e.g. "1.50 MB". Sorting still uses the numeric source.</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => ByteFormatter.Bytes(System.Convert.ToInt64(value));

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Formats a bytes/second rate as e.g. "1.50 MB/s" (or "—" when idle).</summary>
public sealed class RateConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => ByteFormatter.Rate(System.Convert.ToDouble(value));

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>True -> Visible, False -> Collapsed. Pass "invert" to reverse.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Null/empty -> Collapsed, otherwise Visible. Pass "invert" to reverse (show when null).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = !(value is null || (value is string s && string.IsNullOrWhiteSpace(s)));
        if (string.Equals(parameter as string, "invert", StringComparison.OrdinalIgnoreCase)) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Maps a row's status string to an accent brush for the status dot.</summary>
public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Blocked = new(Color.FromRgb(0xEF, 0x44, 0x44));   // red
    private static readonly SolidColorBrush Capped = new(Color.FromRgb(0xF9, 0x73, 0x16));    // orange
    private static readonly SolidColorBrush Kept = new(Color.FromRgb(0xF5, 0x9E, 0x0B));     // amber
    private static readonly SolidColorBrush Ignored = new(Color.FromRgb(0x6B, 0x72, 0x80));  // grey
    private static readonly SolidColorBrush Active = new(Color.FromRgb(0x10, 0xB9, 0x81));   // green
    private static readonly SolidColorBrush Idle = new(Color.FromRgb(0x37, 0x41, 0x51));     // dark

    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var s = value as string ?? "";
        if (s.Contains("Blocked", StringComparison.OrdinalIgnoreCase)) return Blocked;
        if (s.Contains("Capped", StringComparison.OrdinalIgnoreCase)) return Capped;
        if (s.Contains("Ignored", StringComparison.OrdinalIgnoreCase)) return Ignored;
        if (s.Contains("Kept", StringComparison.OrdinalIgnoreCase)) return Kept;
        if (s.Contains("Active", StringComparison.OrdinalIgnoreCase)) return Active;
        return Idle;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
