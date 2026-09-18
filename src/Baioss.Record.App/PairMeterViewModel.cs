using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Baioss.Record.Application.Capture;
using Baioss.Record.App.Localization;

namespace Baioss.Record.App;

/// <summary>
/// Medidor compacto de UN PAR de canales (L/R) de una fuente con audio embebido multicanal (DeckLink de 8/16), con su
/// propio peak-hold. Lo crea y alimenta <see cref="ChannelViewModel"/> a partir de los true-peak por canal del preview;
/// <see cref="IsRecorded"/> distingue los pares que van al archivo de los que solo se miden.
/// </summary>
public sealed partial class PairMeterViewModel : ObservableObject
{
    private double _holdL = -60, _holdR = -60;

    public PairMeterViewModel(int pair, bool isRecorded)
    {
        Pair = pair;
        Label = AudioSelection.PairLabel(pair);
        _isRecorded = isRecorded;
    }

    /// <summary>Par 1-based (1 = canales 1-2).</summary>
    public int Pair { get; }

    /// <summary>«1-2», «3-4»…</summary>
    public string Label { get; }

    /// <summary>Se graba (según la selección de la entrada) o solo se mide.</summary>
    [ObservableProperty] private bool _isRecorded;

    [ObservableProperty] private double _leftLevel;
    [ObservableProperty] private double _rightLevel;
    [ObservableProperty] private double _leftPeak;
    [ObservableProperty] private double _rightPeak;

    /// <summary>Peak-hold mayor de los dos canales, en dBFS («-∞» en silencio).</summary>
    [ObservableProperty] private string _peakDb = "-∞";

    [ObservableProperty] private bool _clipping;

    /// <summary>«Canales 3-4: se graban» / «…: no se graban (solo se miden)», en el idioma del operador.</summary>
    public string ToolTipText => Loc.F(IsRecorded ? "Ch_PairRecorded" : "Ch_PairMetered", Label);

    partial void OnIsRecordedChanged(bool value) => OnPropertyChanged(nameof(ToolTipText));

    /// <summary>Cambió el idioma: el tooltip se compone en código y hay que reavisar.</summary>
    public void RefreshTexts() => OnPropertyChanged(nameof(ToolTipText));

    public void Apply(double l, double r)
    {
        _holdL = Math.Max(l, _holdL - 1.2); // peak-hold con decaimiento, como los medidores principales
        _holdR = Math.Max(r, _holdR - 1.2);
        LeftLevel = ChannelViewModel.Norm(l); LeftPeak = ChannelViewModel.Norm(_holdL);
        RightLevel = ChannelViewModel.Norm(r); RightPeak = ChannelViewModel.Norm(_holdR);
        PeakDb = ChannelViewModel.Fmt(Math.Max(_holdL, _holdR));
        Clipping = _holdL > -1 || _holdR > -1;
    }
}
