using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Recording;

namespace Baioss.Record.App.Demo;

/// <summary>
/// Motor de canal SIMULADO para desarrollo de UI y demo, sin hardware ni FFmpeg.
/// Emula lock de señal (~1 s), telemetría de grabación y medidores de audio L/R con
/// peak-hold. En producción se sustituye por el ChannelEngine real (FFmpeg) cambiando
/// solo el registro en DI — la UI no cambia porque ambos implementan <see cref="IChannelEngine"/>.
/// </summary>
public sealed class SimulatedChannelEngine : IChannelEngine, IConfigurableRecording
{
    /// <summary>Perfil editable desde la UI (en demo no afecta a la grabación simulada).</summary>
    public RecordingProfile Profile { get; set; } = new()
    {
        Name = "Demo", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(8), TargetResolution = Resolution.Hd1080,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    /// <summary>Carpeta de destino (en demo no se escribe nada).</summary>
    public string OutputDirectory { get; set; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Baioss");

    private readonly System.Timers.Timer _timer = new(100) { AutoReset = true };
    private readonly DateTimeOffset _bootedAt = DateTimeOffset.UtcNow;
    private readonly Random _rng = new();

    private SignalInfo _signal = SignalInfo.None;
    private RecordingState _state = RecordingState.Idle;
    private DateTimeOffset _recStartedAt;
    private Guid? _sessionId;
    private long _frame;
    private double _peakL = -60, _peakR = -60;
    private IReadOnlyList<AudioMeter> _audio = new[] { AudioMeter.Silent, AudioMeter.Silent };

    public SimulatedChannelEngine(string key)
    {
        ChannelId = Guid.NewGuid();
        Key = key;
        _timer.Elapsed += Tick;
        _timer.Start();
    }

    public Guid ChannelId { get; }
    public string Key { get; }

    public ChannelStatus Status => new(ChannelId, Key, _state, _signal, BuildStats(), _sessionId, _audio);

    public event EventHandler<ChannelStatus>? StatusChanged;

    private void Tick(object? sender, System.Timers.ElapsedEventArgs e)
    {
        // Tras ~1 s la señal hace lock a 1080p25 con audio estéreo.
        if (_signal.State != SignalState.Locked &&
            DateTimeOffset.UtcNow - _bootedAt > TimeSpan.FromSeconds(1))
        {
            _signal = new SignalInfo(SignalState.Locked, Resolution.Hd1080, FrameRate.P25,
                AudioLayout.Stereo, HasAudio: true, Timecode.Zero, Bitrate.FromMbps(50));
        }

        if (_state == RecordingState.Recording)
            _frame = (long)((DateTimeOffset.UtcNow - _recStartedAt).TotalSeconds * 25);

        _audio = ComputeAudio();
        StatusChanged?.Invoke(this, Status);
    }

    private RecorderStats BuildStats()
    {
        if (_state is not (RecordingState.Recording or RecordingState.Paused))
            return RecorderStats.Empty;

        var tc = Timecode.FromFrameNumber(_frame, nominalRate: 25);
        return new RecorderStats(
            InputFps: 25, OutputFps: 25, DroppedFrames: 0, DuplicatedFrames: 0,
            Bitrate: Bitrate.FromMbps(50), BufferHealth: 1.0, Timecode: tc, FrameCount: _frame);
    }

    /// <summary>Niveles oscilantes con leve diferencia L/R y peak-hold con decaimiento.</summary>
    private AudioMeter[] ComputeAudio()
    {
        if (_signal.State != SignalState.Locked)
        {
            _peakL = _peakR = -60;
            return new[] { AudioMeter.Silent, AudioMeter.Silent };
        }

        double t = (DateTimeOffset.UtcNow - _bootedAt).TotalSeconds;
        double bias = _state == RecordingState.Recording ? 0 : -8; // más caliente al grabar
        double l = Math.Clamp(-18 + 8 * Math.Sin(t * 3.1) + bias + _rng.NextDouble() * 4 - 2, -60, 0);
        double r = Math.Clamp(-18 + 8 * Math.Sin(t * 3.1 + 0.6) + bias + _rng.NextDouble() * 4 - 2, -60, 0);

        _peakL = Math.Max(l, _peakL - 1.2); // decaimiento de peak-hold por tick
        _peakR = Math.Max(r, _peakR - 1.2);

        return new[]
        {
            new AudioMeter(_peakL, l, _peakL > -1),
            new AudioMeter(_peakR, r, _peakR > -1),
        };
    }

    public Task BindSourceAsync(Guid sourceId, CancellationToken ct = default) => Task.CompletedTask;
    public Task StartPreviewAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task StartRecordingAsync(Guid profileId, string? @operator, CancellationToken ct = default)
    {
        _recStartedAt = DateTimeOffset.UtcNow;
        _sessionId = Guid.NewGuid();
        _frame = 0;
        _state = RecordingState.Recording;
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    public Task StopRecordingAsync(CancellationToken ct = default)
    {
        _state = RecordingState.Idle;
        _sessionId = null;
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    public Task PauseRecordingAsync(CancellationToken ct = default)
    {
        if (_state == RecordingState.Recording) _state = RecordingState.Paused;
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    public Task ResumeRecordingAsync(CancellationToken ct = default)
    {
        if (_state == RecordingState.Paused)
        {
            // Reanuda preservando continuidad: ajusta el origen al tiempo ya grabado.
            _recStartedAt = DateTimeOffset.UtcNow - TimeSpan.FromSeconds(_frame / 25.0);
            _state = RecordingState.Recording;
        }
        StatusChanged?.Invoke(this, Status);
        return Task.CompletedTask;
    }

    public Task EnableContinuousModeAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _timer.Stop();
        _timer.Dispose();
        return ValueTask.CompletedTask;
    }
}
