using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Baioss.Record.App;

/// <summary>Conversores de la ventana «Revisar» (<c>{x:Static app:ReviewConverters.NotEmpty}</c>).</summary>
public static class ReviewConverters
{
    /// <summary>Visible si el texto no está vacío.</summary>
    public static readonly IValueConverter NotEmpty = new Converter(v => v is string s && s.Length > 0);

    /// <summary>Visible si el booleano es falso.</summary>
    public static readonly IValueConverter Not = new Converter(v => v is false);

    private sealed class Converter(Func<object?, bool> visible) : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => visible(value) ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
