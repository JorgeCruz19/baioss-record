using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Preview;

namespace Baioss.Record.Infrastructure.Preview;

/// <summary>Un frame de preview en BGRA (8 bits/canal, top-down), listo para subir a textura/bitmap.</summary>
public sealed record PreviewFrame(byte[] Bgra, int Width, int Height, int Stride);

/// <summary>
/// Motor de preview de baja latencia basado en FFmpeg: decodifica la fuente a una resolución
/// reducida y emite frames BGRA crudos por una tubería, en un proceso independiente del de
/// grabación (la cadencia del preview no acopla a la del encoder). El render por GPU
/// (textura D3D11 → D3DImage) vive en la capa de presentación, que consume
/// <see cref="FrameReady"/>; si el interop D3D falla, la misma fuente de frames alimenta un
/// WriteableBitmap (fallback). La ruta de cero copias NVDEC→D3D11 es una optimización futura
/// sobre este mismo contrato.
/// </summary>
public sealed class FfmpegPreviewEngine : IPreviewEngine
{
    private readonly IFfmpegLocator _locator;
    private readonly ILogger<FfmpegPreviewEngine> _log;

    private Process? _process;
    private CancellationTokenSource? _cts;
    private Task? _readLoop;

    public FfmpegPreviewEngine(IFfmpegLocator locator, ILogger<FfmpegPreviewEngine> log)
    {
        _locator = locator;
        _log = log;
    }

    /// <summary>Resolución del preview (fija, 16:9 por defecto). Determina el tamaño de cada frame.</summary>
    public int FrameWidth { get; init; } = 640;
    public int FrameHeight { get; init; } = 360;
    public int FrameRate { get; init; } = 25;

    // --- IPreviewEngine ---
    public PreviewMode Mode { get; set; } = PreviewMode.Preview;
    public ScopeKind ActiveScopes { get; set; } = ScopeKind.None;
    public bool ShowSafeArea { get; set; }
    public bool ShowTimecode { get; set; }

    /// <summary>0: en esta build la textura D3D11 la posee el render de presentación (ver PreviewSurface).</summary>
    public nint SharedTextureHandle => 0;

    /// <summary>Niveles true-peak L/R (dBFS) del audio de la fuente EN VIVO, para medidores VU.</summary>
    /// <remarks>El metering se mide sobre la señal de entrada (la misma del preview), de forma
    /// continua e independiente de si se está grabando — el monitoreo es de la fuente, no del encoder.</remarks>
    public event EventHandler<(double Left, double Right)>? AudioPeaksUpdated;

    /// <summary>Niveles de audio (contrato <see cref="IPreviewEngine"/>); se eleva junto con <see cref="AudioPeaksUpdated"/>.</summary>
    public event EventHandler<AudioLevels>? AudioLevelsUpdated;

    /// <summary>Se eleva por cada frame decodificado (en un hilo de fondo; el consumidor marshalea a UI).</summary>
    public event EventHandler<PreviewFrame>? FrameReady;

    public Task StartAsync(ICaptureSource source, CancellationToken ct = default)
    {
        Stop(); // idempotente: reinicia si ya había un preview activo
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var args = BuildArgs(source);
        var psi = new ProcessStartInfo
        {
            FileName = _locator.FfmpegPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) OnStderr(e.Data); };

        _log.LogInformation("Preview FFmpeg argv: {Args}", string.Join(' ', args));
        _process.Start();
        _process.BeginErrorReadLine();

        _readLoop = Task.Run(() => ReadFramesAsync(_process, _cts.Token));
        return Task.CompletedTask;
    }

    private IReadOnlyList<string> BuildArgs(ICaptureSource source)
    {
        // -loglevel info: necesario para que ebur128 emita sus líneas FTPK por stderr.
        // -nostats: silencia la línea de estadísticas del encoder (ruido innecesario en stderr).
        var args = new List<string> { "-hide_banner", "-nostats", "-loglevel", "info" };
        args.AddRange(source.BuildInputArguments());           // -stream_loop -1 -re -i <fuente>

        // Salida 1 — video BGRA escalado por la tubería (lo consume el render).
        args.Add("-map"); args.Add("0:v:0");
        args.Add("-vf");
        args.Add(string.Create(CultureInfo.InvariantCulture, $"scale={FrameWidth}:{FrameHeight},format=bgra"));
        args.Add("-r"); args.Add(FrameRate.ToString(CultureInfo.InvariantCulture));
        args.Add("-f"); args.Add("rawvideo");
        args.Add("pipe:1");

        // Salida 2 — medición de loudness/true-peak de la señal en vivo (FTPK por stderr),
        // descartada al dispositivo nulo. Alimenta los medidores VU de forma continua.
        args.Add("-map"); args.Add("0:a:0?");
        args.Add("-af"); args.Add("ebur128=peak=true");
        args.Add("-f"); args.Add("null");
        args.Add("NUL");
        return args;
    }

    private void OnStderr(string line)
    {
        // Líneas de ebur128: "… FTPK: -16.6 -16.9 dBFS …" (1 valor mono, 2 estéreo).
        int ftpk = line.IndexOf("FTPK:", StringComparison.Ordinal);
        if (ftpk >= 0)
        {
            var toks = line[(ftpk + 5)..].TrimStart().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (toks.Length >= 1 && double.TryParse(toks[0], NumberStyles.Any, CultureInfo.InvariantCulture, out var l))
            {
                double r = toks.Length >= 2 && double.TryParse(toks[1], NumberStyles.Any, CultureInfo.InvariantCulture, out var rr) ? rr : l;
                AudioPeaksUpdated?.Invoke(this, (l, r));
                double peak = Math.Max(l, r);
                AudioLevelsUpdated?.Invoke(this, new AudioLevels(peak, peak, peak > -1));
            }
            return;
        }
        _log.LogTrace("preview ffmpeg: {Line}", line);
    }

    private async Task ReadFramesAsync(Process process, CancellationToken ct)
    {
        int stride = FrameWidth * 4;          // BGRA = 4 bytes/píxel
        int frameSize = stride * FrameHeight;
        var stream = process.StandardOutput.BaseStream;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var buffer = new byte[frameSize];
                int read = 0;
                while (read < frameSize)      // la tubería puede entregar el frame en varios trozos
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(read, frameSize - read), ct).ConfigureAwait(false);
                    if (n == 0) return;       // EOF: el proceso terminó
                    read += n;
                }
                FrameReady?.Invoke(this, new PreviewFrame(buffer, FrameWidth, FrameHeight, stride));
            }
        }
        catch (OperationCanceledException) { /* stop */ }
        catch (Exception ex) { _log.LogError(ex, "Error leyendo frames de preview."); }
    }

    public Task StopAsync(CancellationToken ct = default) { Stop(); return Task.CompletedTask; }

    private void Stop()
    {
        try { _cts?.Cancel(); } catch { /* noop */ }
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
        _process?.Dispose();
        _process = null;
        _cts?.Dispose();
        _cts = null;
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (_readLoop is not null)
        {
            try { await _readLoop.ConfigureAwait(false); } catch { /* noop */ }
            _readLoop = null;
        }
    }
}
