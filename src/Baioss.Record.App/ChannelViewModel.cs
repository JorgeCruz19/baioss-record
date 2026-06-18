using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain;
using Baioss.Record.Application.Channels;

namespace Baioss.Record.App;

/// <summary>
/// ViewModel de un canal (A/B). Enlaza el estado del <see cref="IChannelEngine"/>
/// con la UI: transporte, badge de señal, telemetría y medidores de audio VU/Peak.
/// </summary>
public sealed partial class ChannelViewModel : ObservableObject, IDisposable
{
    private readonly IChannelEngine _engine;

    public ChannelViewModel(IChannelEngine engine)
    {
        _engine = engine;
        _engine.StatusChanged += OnStatusChanged;
        Sync(engine.Status);
    }

    public string Key => _engine.Status.Key;

    // Demo: en producción provienen del InputSource y el RecordingProfile vinculados.
    public string SourceText => "Entrada simulada · SDI 1080i";
    public string ProfileText => "HEVC NVENC · 50 Mbps · MXF · seg 15 min";

    [ObservableProperty] private RecordingState _recordingState;
    [ObservableProperty] private SignalState _signalState;
    [ObservableProperty] private string _signalText = "SIN SEÑAL";
    [ObservableProperty] private string _formatText = "—";
    [ObservableProperty] private string _timecode = "00:00:00:00";
    [ObservableProperty] private long _frameCount;
    [ObservableProperty] private double _outputFps;
    [ObservableProperty] private long _droppedFrames;
    [ObservableProperty] private string _bitrateText = "—";
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private bool _isLocked;

    // Medidores de audio normalizados a 0..1 (rango -60..0 dBFS) + peak en dBFS.
    [ObservableProperty] private double _leftLevel;
    [ObservableProperty] private double _rightLevel;
    [ObservableProperty] private double _leftPeak;
    [ObservableProperty] private double _rightPeak;
    [ObservableProperty] private string _leftPeakDb = "-∞";
    [ObservableProperty] private string _rightPeakDb = "-∞";
    [ObservableProperty] private bool _clipping;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => _engine.StartRecordingAsync(Guid.Empty, Environment.UserName);
    private bool CanStart() => !IsRecording && IsLocked;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopAsync() => _engine.StopRecordingAsync();
    private bool CanStop() => IsRecording;

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task PauseAsync() => _engine.PauseRecordingAsync();
    private bool CanPause() => RecordingState == RecordingState.Recording;

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => _engine.ResumeRecordingAsync();
    private bool CanResume() => RecordingState == RecordingState.Paused;

    private void OnStatusChanged(object? sender, ChannelStatus status)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => Sync(status));

    private void Sync(ChannelStatus status)
    {
        RecordingState = status.RecordingState;
        SignalState = status.Signal.State;
        IsLocked = status.Signal.State == SignalState.Locked;
        SignalText = status.Signal.State switch
        {
            SignalState.Locked => "SEÑAL OK",
            SignalState.Unstable => "INESTABLE",
            _ => "SIN SEÑAL"
        };
        FormatText = status.Signal is { Resolution: { } r, FrameRate: { } f } ? $"{r} · {f}" : "—";

        Timecode = status.Stats.Timecode.ToString();
        FrameCount = status.Stats.FrameCount;
        OutputFps = status.Stats.OutputFps;
        DroppedFrames = status.Stats.DroppedFrames;
        BitrateText = status.Stats.Bitrate.BitsPerSecond > 0 ? status.Stats.Bitrate.ToString() : "—";
        IsRecording = status.RecordingState is RecordingState.Recording or RecordingState.Paused;

        var audio = status.Audio;
        if (audio is { Count: >= 2 })
        {
            LeftLevel = Norm(audio[0].RmsDb); LeftPeak = Norm(audio[0].PeakDb);
            RightLevel = Norm(audio[1].RmsDb); RightPeak = Norm(audio[1].PeakDb);
            LeftPeakDb = Fmt(audio[0].PeakDb); RightPeakDb = Fmt(audio[1].PeakDb);
            Clipping = audio[0].Clipping || audio[1].Clipping;
        }
        else
        {
            LeftLevel = RightLevel = LeftPeak = RightPeak = 0;
            LeftPeakDb = RightPeakDb = "-∞";
            Clipping = false;
        }

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    private static double Norm(double db) => Math.Clamp((db + 60) / 60.0, 0, 1);
    private static string Fmt(double db) => db <= -60 ? "-∞" : $"{db:0.0}";

    public void Dispose() => _engine.StatusChanged -= OnStatusChanged;
}
