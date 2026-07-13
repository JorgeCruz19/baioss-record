using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Baioss.Record.App.Storage;

/// <summary>
/// Comportamiento adjunto que restringe un <see cref="TextBox"/> a ENTRADA NUMÉRICA ≥ 0 (entero o decimal):
/// bloquea teclear letras/símbolos/espacios y pegar texto no numérico. La validación de RANGO y orden se hace
/// al guardar (<c>StorageSettings.Sanitized</c>). Uso en XAML: <c>local:NumericInput.Kind="Integer"</c>.
/// (Fase 4c — validaciones de la ventana de Almacenamiento.)
/// </summary>
public static class NumericInput
{
    public enum NumberKind { Integer, Decimal }

    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(NumberKind?), typeof(NumericInput), new PropertyMetadata(null, OnKindChanged));

    public static void SetKind(DependencyObject o, NumberKind? v) => o.SetValue(KindProperty, v);
    public static NumberKind? GetKind(DependencyObject o) => (NumberKind?)o.GetValue(KindProperty);

    // Enteros: solo dígitos. Decimales: dígitos con un único separador (. o ,). Vacío es válido MIENTRAS se escribe.
    private static readonly Regex IntPattern = new(@"^\d*$", RegexOptions.Compiled);
    private static readonly Regex DecPattern = new(@"^\d*([.,]\d*)?$", RegexOptions.Compiled);

    private static void OnKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb) return;
        tb.PreviewTextInput -= OnPreviewTextInput;
        DataObject.RemovePastingHandler(tb, OnPaste);
        if (e.NewValue is NumberKind)
        {
            tb.PreviewTextInput += OnPreviewTextInput;
            DataObject.AddPastingHandler(tb, OnPaste);
        }
    }

    /// <summary>¿Sería válido el texto RESULTANTE si se insertara <paramref name="insert"/> (reemplazando la selección)?</summary>
    private static bool WouldBeValid(TextBox tb, string insert)
    {
        string proposed = tb.Text.Remove(tb.SelectionStart, tb.SelectionLength).Insert(tb.SelectionStart, insert);
        return GetKind(tb) == NumberKind.Decimal ? DecPattern.IsMatch(proposed) : IntPattern.IsMatch(proposed);
    }

    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is TextBox tb) e.Handled = !WouldBeValid(tb, e.Text); // rechaza letras, símbolos, 2º separador, espacio…
    }

    private static void OnPaste(object sender, DataObjectPastingEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var text = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? e.DataObject.GetData(DataFormats.Text) as string;
        if (text is null || !WouldBeValid(tb, text)) e.CancelCommand(); // cancela el pegado no numérico
    }
}
