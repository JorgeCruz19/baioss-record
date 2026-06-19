using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Channels;
using Baioss.Record.App.Recording;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App;

/// <summary>
/// ViewModel de un canal (A/B). Enlaza el estado del <see cref="IChannelEngine"/> con la UI:
/// transporte, señal, telemetría, medidores VU y la CONFIGURACIÓN de grabación completa
/// (carpeta de destino, formato, tamaño, escaneo, fps, códec, control de tasa, burn-in y audio)
/// que el operador elige antes de grabar.
/// </summary>
public sealed partial class ChannelViewModel : ObservableObject, IDisposable
{
    private readonly IChannelEngine _engine;
    private readonly IConfigurableRecording? _config;
    private bool _initializing;

    public ChannelViewModel(IChannelEngine engine, FfmpegPreviewEngine? preview = null, bool gpuAvailable = false)
    {
        _engine = engine;
        _config = engine as IConfigurableRecording;
        IsConfigurable = _config is not null;
        Preview = preview;

        VideoCodecs = BuildVideoCodecs(gpuAvailable);
        InitSelectionsFromProfile();

        _engine.StatusChanged += OnStatusChanged;
        if (Preview is not null) Preview.AudioPeaksUpdated += OnPreviewAudio;
        Sync(engine.Status);
    }

    public FfmpegPreviewEngine? Preview { get; }
    public string Key => _engine.Status.Key;

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
    //  Configuración de grabación
    // ---------------------------------------------------------------------

    public bool IsConfigurable { get; }
    public bool CanConfigure => IsConfigurable && !IsRecording;

    /// <summary>Carpeta de destino elegida por el operador (se edita con el botón Examinar).</summary>
    [ObservableProperty] private string _outputDirectory = "";

    /// <summary>Quemar el timecode sobre la imagen grabada (overlay).</summary>
    [ObservableProperty] private bool _burnTimecode;

    public IReadOnlyList<NamedOption<ContainerFormat>> Containers { get; } = new NamedOption<ContainerFormat>[]
    {
        new("MP4", ContainerFormat.Mp4), new("MOV", ContainerFormat.Mov), new("MXF", ContainerFormat.Mxf),
        new("MKV", ContainerFormat.Mkv), new("MPEG-TS", ContainerFormat.Ts),
    };

    public IReadOnlyList<NamedOption<Resolution?>> Sizes { get; } = new NamedOption<Resolution?>[]
    {
        new("Fuente (nativa)", null), new("720p (1280×720)", Resolution.Hd720),
        new("1080p (1920×1080)", Resolution.Hd1080), new("4K UHD (3840×2160)", Resolution.Uhd4K),
    };

    public IReadOnlyList<NamedOption<ScanType>> ScanTypes { get; } = new NamedOption<ScanType>[]
    {
        new("Progresivo", ScanType.Progressive),
        new("Entrelazado (TFF)", ScanType.InterlacedTff),
        new("Entrelazado (BFF)", ScanType.InterlacedBff),
    };

    public IReadOnlyList<NamedOption<FrameRate?>> FrameRates { get; } = new NamedOption<FrameRate?>[]
    {
        new("Fuente", null), new("23.976", new FrameRate(24000, 1001)), new("24", FrameRate.P24),
        new("25", FrameRate.P25), new("29.97", FrameRate.P2997), new("30", FrameRate.P30),
        new("50", FrameRate.P50), new("59.94", FrameRate.P5994), new("60", FrameRate.P60),
    };

    public IReadOnlyList<NamedOption<RateControlMode>> RateControls { get; } = new NamedOption<RateControlMode>[]
    {
        new("CBR (tasa fija)", RateControlMode.ConstantBitrate),
        new("VBR (tasa media)", RateControlMode.VariableBitrate),
        new("Calidad constante", RateControlMode.ConstantQuality),
    };

    public IReadOnlyList<NamedOption<int>> Qualities { get; } = new NamedOption<int>[]
    {
        new("18 (alta)", 18), new("20", 20), new("23 (media)", 23), new("26", 26), new("30 (baja)", 30),
    };

    public IReadOnlyList<NamedOption<Bitrate>> Bitrates { get; } = new NamedOption<Bitrate>[]
    {
        new("8 Mbps", Bitrate.FromMbps(8)), new("16 Mbps", Bitrate.FromMbps(16)), new("25 Mbps", Bitrate.FromMbps(25)),
        new("50 Mbps", Bitrate.FromMbps(50)), new("100 Mbps", Bitrate.FromMbps(100)),
    };

    public IReadOnlyList<NamedOption<AudioCodec>> AudioCodecs { get; } = new NamedOption<AudioCodec>[]
    {
        new("AAC", AudioCodec.Aac), new("PCM 24-bit", AudioCodec.Pcm), new("Opus", AudioCodec.Opus), new("MP3", AudioCodec.Mp3),
    };

    public IReadOnlyList<NamedOption<AudioLayout>> AudioLayouts { get; } = new NamedOption<AudioLayout>[]
    {
        new("Mono", AudioLayout.Mono), new("Estéreo", AudioLayout.Stereo),
        new("5.1", AudioLayout.Surround51), new("7.1", AudioLayout.Surround71),
    };

    public IReadOnlyList<NamedOption<int>> SampleRates { get; } = new NamedOption<int>[]
    {
        new("48 kHz", 48_000), new("44.1 kHz", 44_100), new("96 kHz", 96_000),
    };

    public IReadOnlyList<NamedOption<Bitrate>> AudioBitrates { get; } = new NamedOption<Bitrate>[]
    {
        new("128 kbps", Bitrate.FromKbps(128)), new("192 kbps", Bitrate.FromKbps(192)),
        new("256 kbps", Bitrate.FromKbps(256)), new("384 kbps", Bitrate.FromKbps(384)),
    };

    public IReadOnlyList<NamedOption<VideoCodec>> VideoCodecs { get; }

    [ObservableProperty] private NamedOption<ContainerFormat>? _selectedContainer;
    [ObservableProperty] private NamedOption<Resolution?>? _selectedSize;
    [ObservableProperty] private NamedOption<ScanType>? _selectedScanType;
    [ObservableProperty] private NamedOption<FrameRate?>? _selectedFrameRate;
    [ObservableProperty] private NamedOption<VideoCodec>? _selectedVideoCodec;
    [ObservableProperty] private NamedOption<RateControlMode>? _selectedRateControl;
    [ObservableProperty] private NamedOption<int>? _selectedQuality;
    [ObservableProperty] private NamedOption<Bitrate>? _selectedBitrate;
    [ObservableProperty] private NamedOption<AudioCodec>? _selectedAudioCodec;
    [ObservableProperty] private NamedOption<AudioLayout>? _selectedAudioLayout;
    [ObservableProperty] private NamedOption<int>? _selectedSampleRate;
    [ObservableProperty] private NamedOption<Bitrate>? _selectedAudioBitrate;

    partial void OnSelectedContainerChanged(NamedOption<ContainerFormat>? value) => ApplyProfile();
    partial void OnSelectedSizeChanged(NamedOption<Resolution?>? value) => ApplyProfile();
    partial void OnSelectedScanTypeChanged(NamedOption<ScanType>? value) => ApplyProfile();
    partial void OnSelectedFrameRateChanged(NamedOption<FrameRate?>? value) => ApplyProfile();
    partial void OnSelectedVideoCodecChanged(NamedOption<VideoCodec>? value) => ApplyProfile();
    partial void OnSelectedRateControlChanged(NamedOption<RateControlMode>? value) => ApplyProfile();
    partial void OnSelectedQualityChanged(NamedOption<int>? value) => ApplyProfile();
    partial void OnSelectedBitrateChanged(NamedOption<Bitrate>? value) => ApplyProfile();
    partial void OnSelectedAudioCodecChanged(NamedOption<AudioCodec>? value) => ApplyProfile();
    partial void OnSelectedAudioLayoutChanged(NamedOption<AudioLayout>? value) => ApplyProfile();
    partial void OnSelectedSampleRateChanged(NamedOption<int>? value) => ApplyProfile();
    partial void OnSelectedAudioBitrateChanged(NamedOption<Bitrate>? value) => ApplyProfile();
    partial void OnBurnTimecodeChanged(bool value) => ApplyProfile();

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

    private static IReadOnlyList<NamedOption<VideoCodec>> BuildVideoCodecs(bool gpu)
    {
        var list = new List<NamedOption<VideoCodec>>();
        if (gpu)
        {
            list.Add(new("H.264 (NVENC)", VideoCodec.H264Nvenc));
            list.Add(new("HEVC (NVENC)", VideoCodec.HevcNvenc));
            list.Add(new("AV1 (NVENC)", VideoCodec.Av1Nvenc));
        }
        list.Add(new("H.264 (x264)", VideoCodec.H264x264));
        list.Add(new("H.265 (x265)", VideoCodec.H265x265));
        list.Add(new("ProRes", VideoCodec.ProRes));
        list.Add(new("DNxHR", VideoCodec.DnxHr));
        return list;
    }

    private void InitSelectionsFromProfile()
    {
        if (_config is null) return;
        _initializing = true;
        var p = _config.Profile;
        OutputDirectory = _config.OutputDirectory;
        BurnTimecode = p.BurnTimecode;
        SetSelectionsFrom(p);
        _initializing = false;
        ApplyProfile();
    }

    /// <summary>Posiciona los combos para reflejar un perfil dado (aproxima al subconjunto del editor).</summary>
    private void SetSelectionsFrom(RecordingProfile p)
    {
        SelectedContainer = Containers.FirstOrDefault(o => o.Value == p.Container) ?? Containers[0];
        SelectedSize = Sizes.FirstOrDefault(o => o.Value == p.TargetResolution) ?? Sizes[0];
        SelectedScanType = ScanTypes.FirstOrDefault(o => o.Value == p.ScanType) ?? ScanTypes[0];
        SelectedFrameRate = FrameRates.FirstOrDefault(o => o.Value == p.OutputFrameRate) ?? FrameRates[0];
        SelectedVideoCodec = VideoCodecs.FirstOrDefault(o => o.Value == p.VideoCodec) ?? VideoCodecs[0];
        SelectedRateControl = RateControls.FirstOrDefault(o => o.Value == p.RateControl) ?? RateControls[0];
        SelectedQuality = Qualities.FirstOrDefault(o => o.Value == p.Quality) ?? Qualities[2];
        SelectedBitrate = Bitrates.FirstOrDefault(o => o.Value.BitsPerSecond == p.VideoBitrate.BitsPerSecond) ?? Bitrates[0];
        SelectedAudioCodec = AudioCodecs.FirstOrDefault(o => o.Value == p.AudioCodec) ?? AudioCodecs[0];
        SelectedAudioLayout = AudioLayouts.FirstOrDefault(o => o.Value == p.AudioLayout) ?? AudioLayouts[1];
        SelectedSampleRate = SampleRates.FirstOrDefault(o => o.Value == p.AudioSampleRate) ?? SampleRates[0];
        SelectedAudioBitrate = AudioBitrates.FirstOrDefault(o => o.Value.BitsPerSecond == p.AudioBitrate.BitsPerSecond) ?? AudioBitrates[2];
    }

    /// <summary>
    /// Aplica un preset completo: fija el perfil exacto en el motor (incluidos campos que el editor
    /// inline no expone, como pixel format o max bitrate) y refresca los combos para reflejarlo.
    /// </summary>
    public void ApplyPreset(Baioss.Record.Application.Presets.EncodingPreset preset)
    {
        if (_config is null || IsRecording) return;
        var current = _config.Profile;
        var profile = preset.ToProfile(current.Id, current.Name); // conserva la identidad persistida
        _config.Profile = profile;

        _initializing = true;                 // refresca la UI sin reconstruir (no pisar el perfil)
        BurnTimecode = profile.BurnTimecode;
        SetSelectionsFrom(profile);
        _initializing = false;

        ProfileText = $"{profile.VideoCodec} · {profile.VideoBitrate} · " +
                      $"{(profile.TargetResolution?.ToString() ?? "nativa")} · {profile.Container}";
    }

    /// <summary>Reconstruye el <see cref="RecordingProfile"/> a partir de las selecciones y lo fija en el motor.</summary>
    private void ApplyProfile()
    {
        if (_initializing || _config is null) return;
        if (SelectedContainer is null || SelectedSize is null || SelectedScanType is null || SelectedFrameRate is null
            || SelectedVideoCodec is null || SelectedRateControl is null || SelectedQuality is null || SelectedBitrate is null
            || SelectedAudioCodec is null || SelectedAudioLayout is null || SelectedSampleRate is null || SelectedAudioBitrate is null)
            return;

        var current = _config.Profile;
        _config.Profile = new RecordingProfile
        {
            Id = current.Id,           // conserva la identidad (misma fila persistida del canal)
            Name = current.Name,
            VideoCodec = SelectedVideoCodec.Value,
            HwAccel = HwAccel.None,    // decode por software; el encoder (incl. NVENC) recibe frames de CPU
            VideoBitrate = SelectedBitrate.Value,
            TargetResolution = SelectedSize.Value,
            OutputFrameRate = SelectedFrameRate.Value,
            ScanType = SelectedScanType.Value,
            RateControl = SelectedRateControl.Value,
            Quality = SelectedQuality.Value,
            GopSize = current.GopSize,
            ClosedGop = current.ClosedGop,
            MaxBitrate = current.MaxBitrate,   // preserva lo no editable por combos
            PixelFormat = current.PixelFormat,
            AudioOnly = current.AudioOnly,
            BurnTimecode = BurnTimecode,
            AudioCodec = SelectedAudioCodec.Value,
            AudioLayout = SelectedAudioLayout.Value,
            AudioBitrate = SelectedAudioBitrate.Value,
            AudioSampleRate = SelectedSampleRate.Value,
            Container = SelectedContainer.Value,
        };

        string rateLabel = SelectedRateControl.Value == RateControlMode.ConstantQuality
            ? $"CRF {SelectedQuality.Value}"
            : SelectedBitrate.Label;
        ProfileText = $"{SelectedVideoCodec.Label} · {rateLabel} · " +
                      $"{(SelectedSize.Value?.ToString() ?? "nativa")} · {SelectedFrameRate.Label} · {SelectedContainer.Label}";
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

        Timecode = status.Stats.Timecode.ToString();
        FrameCount = status.Stats.FrameCount;
        OutputFps = status.Stats.OutputFps;
        DroppedFrames = status.Stats.DroppedFrames;
        BitrateText = status.Stats.Bitrate.BitsPerSecond > 0 ? status.Stats.Bitrate.ToString() : "—";
        IsRecording = status.RecordingState is RecordingState.Recording or RecordingState.Paused;

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
