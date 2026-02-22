using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace DiskPeek.Converters;

/// <summary>Formats a byte count as a human-readable string.</summary>
public class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not long bytes) return "—";
        return FormatBytes(bytes);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0) return "—";
        if (bytes == 0) return "0 B";
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < suffixes.Length - 1) { d /= 1024; i++; }
        return i == 0 ? $"{(long)d} {suffixes[i]}" : $"{d:F1} {suffixes[i]}";
    }
}

/// <summary>Returns a Segoe MDL2 Assets glyph for file vs directory.</summary>
public class TypeIconConverter : IValueConverter
{
    // Folder = E8B7, File = E8A5 (Segoe MDL2 Assets)
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? "\uE8B7" : "\uE8A5";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Maps IsDirectory to a foreground colour.</summary>
public class TypeColorConverter : IValueConverter
{
    private static readonly SolidColorBrush FolderBrush = new(Color.FromRgb(0xF0, 0xC0, 0x50));
    private static readonly SolidColorBrush FileBrush   = new(Color.FromRgb(0x88, 0x99, 0xBB));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? FolderBrush : FileBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>bool → Visibility.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool invert = parameter is string s && s == "invert";
        bool visible = value is true;
        if (invert) visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

/// <summary>
/// [percent, trackWidth] → fill width scaled to the track's actual pixel width.
/// This ensures the bar always fills proportionally regardless of column width.
/// </summary>
public class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        double pct   = values.Length > 0 && values[0] is double d ? d : 0;
        double track = values.Length > 1 && values[1] is double w ? w : 0;
        return Math.Max(0, Math.Min(track, pct / 100.0 * track));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Formats a DateTime as a relative/absolute time label.</summary>
public class DateTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime dt) return "—";
        var span = DateTime.Now - dt;
        if (span.TotalSeconds < 90) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        return dt.ToString("yyyy-MM-dd HH:mm");
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Formats a DriveInfo as a human-readable combo-box label.</summary>
public class DriveInfoConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not System.IO.DriveInfo d) return "?";
        try
        {
            string letter = d.Name.TrimEnd('\\', '/');
            string label  = d.VolumeLabel is { Length: > 0 } v ? $"  [{v}]" : string.Empty;
            string free   = FileSizeConverter.FormatBytes(d.AvailableFreeSpace);
            string total  = FileSizeConverter.FormatBytes(d.TotalSize);
            return $"{letter}{label}  ·  {free} free / {total}";
        }
        catch { return d.Name; }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
