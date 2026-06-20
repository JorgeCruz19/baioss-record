using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Presets;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App;

/// <summary>
/// ViewModel de un canal (A/B). Enlaza el estado del <see cref="IChannelEngine"/> con la UI:
/// transporte, señal, telemetría y medidores VU. La codificación (formato, tamaño, códec,
/// bitrate, audio…) se elige aplicando un <see cref="EncodingPreset"/> desde el gestor de
/// presets; aquí solo se fija la CARPETA DE DESTINO y se muestra el resumen del perfil vigente.
/// </summary>
public sealed partial class ChannelViewModel : ObservableObject, IDisposable
{
    private readonly IChannelEngine _engine;
    private readonly IConfigurableRecording? _config;

    public ChannelViewModel(IChannelEngine engine, IChannelPreviewSource? preview = null)
    {
        _engine = engine;
        _config = engine as IConfigurableRecording;
        IsConfigurable = _config is not null;
        Preview = preview;

        InitFromProfile();

        _engine.StatusChanged += OnStatusChanged;
        if (Preview is not null) Preview.AudioPeaksUpdated += OnPreviewAudio;
        Sync(engine.Status);
    }

    public IChannelPreviewSource? Preview { get; }
    public string Key => _engine.Status.Key;
    public Guid ChannelId => _engine.ChannelId;

    private double _peakHoldL = -60, _peakHoldR = -60;

    [ObservableProperty] private RecordingState _recordingState;
    [ObservableProperty] private SignalState _signalState;
    [ObservableProperty] private string _signalText = "SIN SEÑAL";
    [ObservableProperty] private string _formatText = "—";
    [ObservableProperty] private string _timecode = "00:00:00:00";
    [ObservableProperty] private long _frameCount;
    [ObservableProperty] private double _outputFps;
    [ObservableProperty] private long _droppedFrames;
    [ObservableProperty] private string _bitrateText = "—";
    [ObservableProperty][NotifyPropertyChangedFor(nameof(CanConfigure))] private bool _isRecording;
    [ObservableProperty] private bool _isLocked;
    [ObservableProperty] private string _profileText = "—";

    [ObservableProperty] private double _leftLevel;
    [ObservableProperty] private double _rightLevel;
    [ObservableProperty] private double _leftPeak;
    [ObservableProperty] private double _rightPeak;
    [ObservableProperty] private string _leftPeakDb = "-∞";
    [ObservableProperty] private string _rightPeakDb = "-∞";
    [ObservableProperty] private bool _clipping;

    // ---------------------------------------------------------------------
    //  Destino y perfil activo
    // ---------------------------------------------------------------------

    public bool IsConfigurable { get; }
    public bool CanConfigure => IsConfigurable && !IsRecording;

    /// <summary>Carpeta de destino elegida por el operador (se edita con el botón Examinar).</summary>
    [ObservableProperty] private string _outputDirectory = "";

    partial void OnOutputDirectoryChanged(string value)
    {
        if (_config is not null && !string.IsNullOrWhiteSpace(value)) _config.OutputDirectory = value;
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Carpeta de destino de las grabaciones" };
        if (Directory.Exists(OutputDirectory)) dialog.InitialDirectory = OutputDirectory;
        if (dialog.ShowDialog() == true) OutputDirectory = dialog.FolderName;
    }

    private void InitFromProfile()
    {
        if (_config is null) return;
        OutputDirectory = _config.OutputDirectory;
        RefreshProfileText();
    }

    /// <summary>
    /// Aplica un preset de encoding al canal: fija el perfil completo en el motor (conservando la
    /// identidad persistida del canal) y refresca el resumen. La carpeta de destino no la toca un
    /// preset; es una propiedad operativa por canal.
    /// </summary>
    public void ApplyPreset(EncodingPreset preset)
    {
        if (_config is null || IsRecording) return;
        var current = _config.Profile;
        _config.Profile = preset.ToProfile(current.Id, current.Name); // conserva Id/Name persistidos
        RefreshProfileText();
    }

    /// <summary>Resumen legible del perfil vigente (lo que se grabará): códec · tasa · tamaño · contenedor.</summary>
    private void RefreshProfileText()
    {
        if (_config is null) { ProfileText = "—"; return; }
        var p = _config.Profile;
        if (p.AudioOnly)
        {
            ProfileText = $"Solo audio · {p.AudioCodec} · {p.Container}";
            return;
        }
        string rate = p.RateControl == RateControlMode.ConstantQuality
            ? $"CRF {p.Quality}"
            : p.VideoBitrate.ToString();
        string res = p.TargetResolution?.ToString() ?? "nativa";
        ProfileText = $"{p.VideoCodec} · {rate} · {res} · {p.Container}";
    }

    // ---------------------------------------------------------------------
    //  Transporte
    // ---------------------------------------------------------------------

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

    private void OnPreviewAudio(object? sender, (double Left, double Right) lr)
        => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => ApplyAudio(lr.Left, lr.Right));

    private void ApplyAudio(double l, double r)
    {
        _peakHoldL = Math.Max(l, _peakHoldL - 1.2); // peak-hold con decaimiento
        _peakHoldR = Math.Max(r, _peakHoldR - 1.2);
        LeftLevel = Norm(l); LeftPeak = Norm(_peakHoldL);
        RightLevel = Norm(r); RightPeak = Norm(_peakHoldR);
        LeftPeakDb = Fmt(_peakHoldL); RightPeakDb = Fmt(_peakHoldR);
        Clipping = _peakHoldL > -1 || _peakHoldR > -1;
    }

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

        IsRecording = status.RecordingState is RecordingState.Recording or RecordingState.Paused;

        // FPS de salida es del preview en vivo (el motor unificado siempre tiene un FFmpeg de preview
        // activo): legítimo mostrarlo en cuanto hay señal.
        OutputFps = status.Stats.OutputFps;

        // Timer (timecode), bitrate, cuadros y dropped pertenecen a la GRABACIÓN. Como ese mismo
        // proceso de preview corre con -progress, avanzarían en reposo aunque no se grabe; se muestran
        // solo durante REC/Pausa y en reposo se dejan en valores idle (el timer arranca de 0 al grabar).
        if (IsRecording)
        {
            Timecode = status.Stats.Timecode.ToString();
            FrameCount = status.Stats.FrameCount;
            DroppedFrames = status.Stats.DroppedFrames;
            BitrateText = status.Stats.Bitrate.BitsPerSecond > 0 ? status.Stats.Bitrate.ToString() : "—";
        }
        else
        {
            Timecode = "00:00:00:00";
            FrameCount = 0;
            DroppedFrames = 0;
            BitrateText = "—";
        }

        // Medidores: en modo real los conduce el audio del preview (ver OnPreviewAudio); aquí solo
        // se actualizan desde el status cuando NO hay preview (canal simulado).
        if (Preview is null)
        {
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
        }

        StartCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    private static double Norm(double db) => Math.Clamp((db + 60) / 60.0, 0, 1);
    private static string Fmt(double db) => db <= -60 ? "-∞" : $"{db:0.0}";

    public void Dispose()
    {
        _engine.StatusChanged -= OnStatusChanged;
        if (Preview is not null) Preview.AudioPeaksUpdated -= OnPreviewAudio;
    }
}
