using System.Globalization;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Recording;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Implementación de <see cref="IRecorderEngine"/> sobre FFmpeg. Une el builder de
/// argumentos, el supervisor de proceso (resiliencia) y el parser de progreso (telemetría),
/// y materializa entidades <see cref="Segment"/> a partir del log del muxer.
/// </summary>
public sealed class FfmpegRecorderEngine : IRecorderEngine
{
    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfmpegRecorderEngine> _log;
    private readonly FfmpegProgressParser _parser = new();

    private FfmpegProcessSupervisor? _supervisor;
    private RecordingState _state = RecordingState.Idle;
    private Guid _sessionId;
    private int _segmentIndex;
    private string? _currentSegmentPath;
    private DateTimeOffset _currentSegmentStart;
    private bool _finalized;

    public FfmpegRecorderEngine(IFfmpegLocator locator, ILogger<FfmpegRecorderEngine> log)
    {
        _locator = locator;
        _log = log;
    }

    /// <summary>Raíz donde se escriben las grabaciones (se crea por canal una subcarpeta).</summary>
    public string OutputRoot { get; set; } = "recordings";
    public string ProxyRoot { get; set; } = "proxies";

    /// <summary>Archivo principal de la última sesión (solo en salida directa, sin segmentación).</summary>
    public string? LastOutputFile { get; private set; }

    public RecordingState State => _state;
    public RecorderStats Stats { get; private set; } = RecorderStats.Empty;

    public event EventHandler<RecordingState>? StateChanged;
    public event EventHandler<RecorderStats>? StatsUpdated;
    public event EventHandler<Segment>? SegmentClosed;

    /// <summary>Niveles true-peak L/R (dBFS) extraídos del filtro ebur128, para medidores VU.</summary>
    public event EventHandler<(double Left, double Right)>? AudioLevelsUpdated;

    public async Task StartAsync(RecordingSession session, RecordingProfile profile, ICaptureSource source, CancellationToken ct = default)
    {
        SetState(RecordingState.Starting);

        _sessionId = session.Id;
        _segmentIndex = 0;
        _finalized = false;
        _currentSegmentPath = null;

        var key = session.ChannelId.ToString("N")[..4];
        var dir = Path.Combine(OutputRoot, key);
        var proxyDir = Path.Combine(ProxyRoot, key);
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(proxyDir);

        var builder = new FfmpegArgumentBuilder()
            .From(source).Using(profile).ForChannel(key)
            .ToDirectory(dir).ProxyToDirectory(proxyDir);
        var args = builder.Build();
        LastOutputFile = string.IsNullOrEmpty(builder.OutputFilePath) ? null : builder.OutputFilePath;

        _log.LogInformation("FFmpeg argv: {Args}", string.Join(' ', args));

        _supervisor = new FfmpegProcessSupervisor(_locator.FfmpegPath, _log) { StallTimeout = TimeSpan.FromSeconds(10) };
        _supervisor.ProgressLine += OnProgress;
        _supervisor.LogLine += OnLog;
        _supervisor.Restarted += (_, _) => SetState(RecordingState.Recovering);
        _supervisor.Completed += OnCompleted;

        _currentSegmentStart = DateTimeOffset.UtcNow;
        await _supervisor.StartAsync(args, ct).ConfigureAwait(false);
        SetState(RecordingState.Recording);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        SetState(RecordingState.Stopping);
        if (_supervisor is not null) await _supervisor.DisposeAsync().ConfigureAwait(false);
        _supervisor = null;
        EmitFinalSegment();
        SetState(RecordingState.Idle);
    }

    // FFmpeg no soporta pausa nativa: en producción se cierra el segmento y se reanuda
    // con timecode continuo (ver docs/04-flujos). Aquí se refleja el estado.
    public Task PauseAsync(CancellationToken ct = default) { SetState(RecordingState.Paused); return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken ct = default) { SetState(RecordingState.Recording); return Task.CompletedTask; }

    private void OnProgress(object? sender, string line)
    {
        var stats = _parser.Feed(line);
        if (stats is null) return;
        Stats = stats;
        if (_state == RecordingState.Recovering) SetState(RecordingState.Recording);
        StatsUpdated?.Invoke(this, stats);
    }

    private void OnLog(object? sender, string line)
    {
        // Niveles de audio del filtro ebur128: "… FTPK: -16.6 dBFS …" (1 valor mono, 2 estéreo).
        int ftpk = line.IndexOf("FTPK:", StringComparison.Ordinal);
        if (ftpk >= 0)
        {
            var toks = line[(ftpk + 5)..].TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length >= 1 && double.TryParse(toks[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var l))
            {
                double r = toks.Length >= 2 && double.TryParse(toks[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var rr) ? rr : l;
                AudioLevelsUpdated?.Invoke(this, (l, r));
            }
            return;
        }

        // El muxer segment registra "Opening '<archivo>' for writing" al abrir cada segmento:
        // al abrir el siguiente, el anterior quedó cerrado.
        if (line.Contains("Opening '", StringComparison.Ordinal) &&
            line.Contains("' for writing", StringComparison.Ordinal))
        {
            var path = ExtractQuotedPath(line);
            if (path is not null)
            {
                if (_currentSegmentPath is not null) EmitSegment(_currentSegmentPath);
                _currentSegmentPath = path;
                _currentSegmentStart = DateTimeOffset.UtcNow;
            }
        }
        _log.LogTrace("ffmpeg: {Line}", line);
    }

    private void OnCompleted(object? sender, int code)
    {
        if (_state is RecordingState.Recording or RecordingState.Recovering or RecordingState.Paused)
        {
            EmitFinalSegment();
            SetState(code == 0 ? RecordingState.Idle : RecordingState.Error);
        }
    }

    private void EmitFinalSegment()
    {
        if (_finalized) return;
        _finalized = true;
        var path = _currentSegmentPath ?? LastOutputFile;
        if (path is not null) EmitSegment(path);
    }

    private void EmitSegment(string path)
    {
        var fi = new FileInfo(path);
        SegmentClosed?.Invoke(this, new Segment
        {
            SessionId = _sessionId,
            Index = _segmentIndex++,
            FilePath = path,
            Status = SegmentStatus.Completed,
            StartedAt = _currentSegmentStart,
            EndedAt = DateTimeOffset.UtcNow,
            SizeBytes = fi.Exists ? fi.Length : 0,
        });
    }

    private static string? ExtractQuotedPath(string line)
    {
        int a = line.IndexOf('\'');
        int b = line.IndexOf("' for writing", StringComparison.Ordinal);
        return a >= 0 && b > a ? line[(a + 1)..b] : null;
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
    }
}
