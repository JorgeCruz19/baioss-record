using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Recording;
using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.Infrastructure.Preview;

/// <summary>
/// Motor de captura UNIFICADO de un canal: un único proceso FFmpeg abre la fuente UNA sola vez y la
/// bifurca en preview (frames BGRA por TCP loopback) + medidores (ebur128) y, al grabar, también la
/// salida a archivo — todo a la vez. Así un dispositivo en vivo (DeckLink/cámara), que no admite dos
/// aperturas, puede previsualizarse y grabarse simultáneamente. Reutiliza el
/// <see cref="FfmpegProcessSupervisor"/> (watchdog/respawn 24/7), el <see cref="FfmpegProgressParser"/>
/// (telemetría) y el <see cref="FfmpegArgumentBuilder"/>. Alternar grabación reconstruye el argv y
/// reinicia el proceso (breve reconexión del preview); el cierre ordenado del supervisor finaliza el
/// archivo correctamente.
/// </summary>
public sealed class FfmpegChannelEngine : IChannelPreviewSource, IAsyncDisposable
{
    private readonly IFfmpegLocator _locator;
    private readonly ILogger _log;
    private readonly FfmpegProgressParser _parser = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    private ICaptureSource? _source;
    private RecordingProfile? _baseProfile;
    private RecordingProfile? _recordProfile;
    private string _channelKey = "A";

    private FfmpegProcessSupervisor? _supervisor;
    private TcpListener? _listener;
    private CancellationTokenSource? _acceptCts;
    private Task? _acceptLoop;
    private int _port;

    private RecordingState _state = RecordingState.Idle;
    private Guid _sessionId;
    private int _segmentIndex;
    private string? _recordFile;
    private DateTimeOffset _recordStart;
    private bool _finalized;

    public FfmpegChannelEngine(IFfmpegLocator locator, ILogger log)
    {
        _locator = locator;
        _log = log;
    }

    public int FrameWidth { get; init; } = 640;
    public int FrameHeight { get; init; } = 360;

    /// <summary>Raíz de grabación; se crea una subcarpeta por canal.</summary>
    public string OutputRoot { get; set; } = "recordings";

    public RecordingState State => _state;
    public RecorderStats Stats { get; private set; } = RecorderStats.Empty;
    public string? LastOutputFile { get; private set; }

    public event EventHandler<PreviewFrame>? FrameReady;
    public event EventHandler<(double Left, double Right)>? AudioPeaksUpdated;
    public event EventHandler<RecordingState>? StateChanged;
    public event EventHandler<RecorderStats>? StatsUpdated;
    public event EventHandler<Segment>? SegmentClosed;

    /// <summary>Arranca la captura SIEMPRE-ACTIVA (preview + medidores) sobre la fuente del canal.</summary>
    public async Task StartPreviewAsync(ICaptureSource source, RecordingProfile baseProfile, string channelKey, CancellationToken ct = default)
    {
        _source = source;
        _baseProfile = baseProfile;
        _channelKey = channelKey;

        // Servidor TCP loopback para recibir los frames de preview del proceso FFmpeg.
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptCts = new CancellationTokenSource();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_acceptCts.Token), _acceptCts.Token);

        await ReplaceProcessAsync(recording: false, ct).ConfigureAwait(false);
    }

    /// <summary>Arranca la grabación a archivo SIN interrumpir el preview (mismo proceso, salida extra).</summary>
    public async Task StartRecordingAsync(Guid sessionId, RecordingProfile profile, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _recordProfile = profile;
            _sessionId = sessionId;
            _segmentIndex = 0;
            _finalized = false;
            _recordStart = DateTimeOffset.UtcNow;
            SetState(RecordingState.Starting);
            await ReplaceProcessAsync(recording: true, ct).ConfigureAwait(false);
            SetState(RecordingState.Recording);
        }
        finally { _gate.Release(); }
    }

    /// <summary>Detiene la grabación y vuelve a preview-only; el cierre ordenado finaliza el archivo.</summary>
    public async Task StopRecordingAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            SetState(RecordingState.Stopping);
            await ReplaceProcessAsync(recording: false, ct).ConfigureAwait(false); // dispone el de grabación → flush/moov
            EmitFinalSegment();
            _recordProfile = null;
            Stats = RecorderStats.Empty;
            SetState(RecordingState.Idle);
        }
        finally { _gate.Release(); }
    }

    public Task PauseAsync(CancellationToken ct = default) { SetState(RecordingState.Paused); return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken ct = default) { SetState(RecordingState.Recording); return Task.CompletedTask; }

    private async Task ReplaceProcessAsync(bool recording, CancellationToken ct)
    {
        // Dispone el proceso anterior (su cierre ordenado envía 'q' y finaliza el archivo si grababa).
        if (_supervisor is not null)
        {
            await _supervisor.DisposeAsync().ConfigureAwait(false);
            _supervisor = null;
        }

        var profile = recording ? _recordProfile! : (_baseProfile ?? _recordProfile!);
        var dir = Path.Combine(OutputRoot, _channelKey);
        if (recording) Directory.CreateDirectory(dir);

        var builder = new FfmpegArgumentBuilder()
            .From(_source!).Using(profile).ForChannel(_channelKey)
            .ToDirectory(dir).WithPreviewSink($"tcp://127.0.0.1:{_port}");
        var args = builder.BuildLive(recording, FrameWidth, FrameHeight);

        if (recording)
        {
            LastOutputFile = string.IsNullOrEmpty(builder.OutputFilePath) ? null : builder.OutputFilePath;
            _recordFile = LastOutputFile;
        }

        _log.LogInformation("Pipeline canal {Key} ({Mode}): {Args}",
            _channelKey, recording ? "grabación+preview" : "preview", string.Join(' ', args));

        _supervisor = new FfmpegProcessSupervisor(_locator.FfmpegPath, _log) { StallTimeout = TimeSpan.FromSeconds(15) };
        _supervisor.ProgressLine += OnProgress;
        _supervisor.LogLine += OnLog;
        await _supervisor.StartAsync(args, ct).ConfigureAwait(false);
    }

    // El proceso FFmpeg se conecta como cliente al servidor TCP; aquí leemos los frames BGRA y, al
    // reiniciar el proceso (alternar grabación / respawn), re-aceptamos la nueva conexión.
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener!.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                await using var stream = client.GetStream();
                await ReadFramesAsync(stream, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Preview TCP: reintentando aceptación."); }
        }
    }

    private async Task ReadFramesAsync(NetworkStream stream, CancellationToken ct)
    {
        int stride = FrameWidth * 4;
        int frameSize = stride * FrameHeight;
        var buffer = new byte[frameSize];
        while (!ct.IsCancellationRequested)
        {
            int read = 0;
            while (read < frameSize)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(read, frameSize - read), ct).ConfigureAwait(false);
                if (n == 0) return; // el proceso cerró la conexión (reinicio) → volver a aceptar
                read += n;
            }
            FrameReady?.Invoke(this, new PreviewFrame((byte[])buffer.Clone(), FrameWidth, FrameHeight, stride));
        }
    }

    private void OnProgress(object? sender, string line)
    {
        var stats = _parser.Feed(line);
        if (stats is null) return;
        Stats = stats;
        StatsUpdated?.Invoke(this, stats);
    }

    private void OnLog(object? sender, string line)
    {
        // Niveles de audio del filtro ebur128: "… FTPK: -16.6 -16.9 dBFS …" (1 mono, 2 estéreo).
        int ftpk = line.IndexOf("FTPK:", StringComparison.Ordinal);
        if (ftpk < 0) return;

        var toks = line[(ftpk + 5)..].TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (toks.Length >= 1 && double.TryParse(toks[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var l))
        {
            double r = toks.Length >= 2 && double.TryParse(toks[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var rr) ? rr : l;
            AudioPeaksUpdated?.Invoke(this, (l, r));
        }
    }

    private void EmitFinalSegment()
    {
        if (_finalized) return;
        _finalized = true;
        if (_recordFile is null) return;

        var fi = new FileInfo(_recordFile);
        SegmentClosed?.Invoke(this, new Segment
        {
            SessionId = _sessionId,
            Index = _segmentIndex++,
            FilePath = _recordFile,
            Status = SegmentStatus.Completed,
            StartedAt = _recordStart,
            EndedAt = DateTimeOffset.UtcNow,
            SizeBytes = fi.Exists ? fi.Length : 0,
        });
        _recordFile = null;
    }

    private void SetState(RecordingState state)
    {
        if (_state == state) return;
        _state = state;
        StateChanged?.Invoke(this, state);
    }

    public async ValueTask DisposeAsync()
    {
        if (_supervisor is not null) await _supervisor.DisposeAsync().ConfigureAwait(false);
        if (_acceptCts is not null) await _acceptCts.CancelAsync().ConfigureAwait(false);
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.ConfigureAwait(false); } catch { /* cancelación esperada */ }
        }
        try { _listener?.Stop(); } catch { /* noop */ }
        _acceptCts?.Dispose();
        _gate.Dispose();
    }
}
