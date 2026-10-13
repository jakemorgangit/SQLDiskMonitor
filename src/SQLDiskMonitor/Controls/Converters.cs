using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SQLDiskMonitor.Models;

namespace SQLDiskMonitor.Controls;

public class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => (value is Visibility.Visible) ^ Invert;
}

/// <summary>Visible when the value is non-null, non-empty and non-zero.</summary>
public class HasValueToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object? value, Type t, object p, CultureInfo c)
    {
        bool has = value switch
        {
            null => false,
            string s => !string.IsNullOrWhiteSpace(s),
            double d => d != 0,
            int i => i != 0,
            System.Collections.ICollection col => col.Count > 0,
            _ => true
        };
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class PressureToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Palette.ForPressure(value is double d ? d : 0);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class SeverityToBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Palette.Brush(value switch
    {
        Severity.Critical => "Crit",
        Severity.High => "High",
        Severity.Medium => "Warn",
        Severity.Low => "Info",
        _ => "TextSubtle"
    });
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public class EnumEquals : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value?.ToString() == p?.ToString();
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true ? Enum.Parse(t, p.ToString()!) : Binding.DoNothing;
}

public class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is not true;
}

/// <summary>Visible when the value is not null (zero counts as a value, e.g. LUN 0).</summary>
public class NotNullToVisibility : IValueConverter
{
    public object Convert(object? value, Type t, object p, CultureInfo c) => value != null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
