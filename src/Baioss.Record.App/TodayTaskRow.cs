using System.Windows.Media;

namespace Baioss.Record.App;

/// <summary>
/// Fila de la tabla «HOY» que aparece bajo el preview de un canal: una grabación programada del día.
/// Columnas Entrada (hora de inicio) · Salida (hora de fin) · Título · Segmento (vacío si no segmenta).
/// La fila EN EJECUCIÓN se resalta con otro color.
/// </summary>
public sealed class TodayTaskRow
{
    public required string EntradaText { get; init; }
    public required string SalidaText { get; init; }
    public required string Title { get; init; }
    public required string SegmentText { get; init; }
    public required bool IsRunning { get; init; }

    /// <summary>Fondo de la fila: verde "al aire" si está en ejecución; transparente si no.</summary>
    public Brush RowBackground => IsRunning ? RunningBg : Brushes.Transparent;

    /// <summary>Texto: oscuro sobre el verde (legible) si en ejecución; claro normal si no.</summary>
    public Brush RowForeground => IsRunning ? RunningFg : DefaultFg;

    private static readonly Brush DefaultFg = Frozen("#E6E9EF");
    private static readonly Brush RunningBg = Frozen("#30A46C");
    private static readonly Brush RunningFg = Frozen("#0C0F15");

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
