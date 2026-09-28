using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace R5Flowstate.Shell.Linux;

/// <summary>Patch-note lines: headers render bold (Windows used a DataTrigger).</summary>
public sealed class HeaderWeightConverter : IValueConverter
{
    public static readonly HeaderWeightConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? FontWeight.Bold : FontWeight.Normal;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}