using System;
using System.Windows;
using System.Windows.Controls;

namespace Baioss.Record.App;

/// <summary>
/// Medidor de audio vertical. <see cref="Level"/> y <see cref="Peak"/> son fracciones
/// 0..1 (0 = -60 dBFS, 1 = 0 dBFS). El relleno se revela con un clip y la línea blanca
/// marca el peak-hold.
/// </summary>
public partial class VuMeter : UserControl
{
    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(VuMeter), new PropertyMetadata(0.0, OnChanged));

    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        nameof(Peak), typeof(double), typeof(VuMeter), new PropertyMetadata(0.0, OnChanged));

    public double Level { get => (double)GetValue(LevelProperty); set => SetValue(LevelProperty, value); }
    public double Peak { get => (double)GetValue(PeakProperty); set => SetValue(PeakProperty, value); }

    public VuMeter() => InitializeComponent();

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((VuMeter)d).Redraw();

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        double w = Root.ActualWidth, h = Root.ActualHeight;
        if (h <= 0 || w <= 0) return;

        double level = Math.Clamp(Level, 0, 1);
        double peak = Math.Clamp(Peak, 0, 1);

        // El clip revela el relleno desde abajo hasta la fracción de nivel.
        FillClip.Rect = new Rect(0, h * (1 - level), w, h * level);

        double y = h * (1 - peak);
        PeakLine.X1 = 0; PeakLine.X2 = w; PeakLine.Y1 = y; PeakLine.Y2 = y;
    }
}
