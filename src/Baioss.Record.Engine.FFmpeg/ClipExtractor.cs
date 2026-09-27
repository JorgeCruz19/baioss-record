using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Localization;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>Lo que salió del corte: el archivo, su duración real, su tamaño y el plan que se ejecutó.</summary>
public sealed record ClipOutcome(string Path, TimeSpan Duration, long SizeBytes, ClipPlan Plan);

/// <summary>
/// Saca un clip de los últimos N segundos de una grabación que AÚN SE ESTÁ ESCRIBIENDO, con procesos ffprobe/ffmpeg
/// aparte (el grabador no se entera): mide la duración legible de cada archivo, planifica el tramo
/// (<see cref="ClipPlanner"/>), busca el fotograma clave anterior al inicio con <c>ffprobe -read_intervals</c> (solo lee
/// esa ventana, no el archivo entero), copia sin recodificar en prioridad baja (disco compartido con las grabaciones) y
/// verifica que el resultado se reproduce.
/// </summary>
public sealed class ClipExtractor(IFfmpegLocator locator, ILogger log)
{
    /// <summary>Tope de la copia (un clip de 10 min a 50 Mbps son 3,7 GB: segundos en SSD, más en red).</summary>
    public TimeSpan CopyTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public async Task<ClipOutcome> ExtractAsync(IReadOnlyList<string> files, ClipContainer container, double wantedSeconds, string outputPath, CancellationToken ct = default)
    {
        // 1) Duración legible AHORA de cada archivo (el último crece; ffprobe lee su moov/fragmentos o el final del TS).
        var sources = new List<ClipSourceFile>(files.Count);
        foreach (var f in files)
        {
            var probe = await locator.ProbeMediaAsync(f, ct).ConfigureAwait(false);
            long size = 0; try { size = new FileInfo(f).Length; } catch { /* sin tamaño: la estimación de espacio ya se hizo */ }
            if (probe.DurationSeconds > 0) sources.Add(new ClipSourceFile(f, probe.DurationSeconds, size));
            else log.LogDebug("Clip: {File} aún no tiene duración legible; se omite.", Path.GetFileName(f));
        }
        var plan = ClipPlanner.Plan(sources, wantedSeconds, container)
                   ?? throw new ClipExtractionException(ClipError.TooShort, Localizer.F("Clip_Err_TooShort", ClipPlanner.MinClipSeconds));

        // 2) Fotograma clave anterior al inicio (ventana de 12 s por delante: cubre GOP de hasta 10 s; si no aparece, una de 60 s).
        var keyframes = await KeyframesNearAsync(plan.Files[0].Path, plan.StartSeconds, 12, ct).ConfigureAwait(false);
        if (keyframes.Count == 0 && plan.StartSeconds > 0) keyframes = await KeyframesNearAsync(plan.Files[0].Path, plan.StartSeconds, 60, ct).ConfigureAwait(false);
        plan = ClipPlanner.SnapToKeyframe(plan, keyframes);

        // 3) Copia sin recodificar (prioridad baja: el disco es de las grabaciones). Un archivo: directo. Varios (grabación
        //    segmentada): la cola del primero a una pieza temporal y luego pieza + segmentos siguientes con el demuxer
        //    concat (ver ClipPlanner.BuildTailArguments).
        string tempTag = Guid.NewGuid().ToString("N");
        string? list = null, piece = null;
        try
        {
            IReadOnlyList<string> args;
            if (plan.Files.Count == 1) args = ClipPlanner.BuildArguments(plan, container, outputPath);
            else
            {
                piece = Path.Combine(Path.GetTempPath(), $"baioss-clip-{tempTag}-cola.{(container == ClipContainer.FragmentedMp4 ? "mp4" : "ts")}");
                await CopyAsync(ClipPlanner.BuildTailArguments(plan, container, piece), piece, ct).ConfigureAwait(false);
                var pieceProbe = await locator.ProbeMediaAsync(piece, ct).ConfigureAwait(false);
                if (!pieceProbe.IsPlayable) throw new ClipExtractionException(ClipError.Failed, Localizer.F("Clip_Err_Failed", "tail"));
                var parts = new List<ClipSourceFile>(plan.Files.Count) { new(piece, pieceProbe.DurationSeconds, 0) };
                for (int i = 1; i < plan.Files.Count; i++) parts.Add(plan.Files[i]);
                list = Path.Combine(Path.GetTempPath(), $"baioss-clip-{tempTag}.txt");
                await File.WriteAllTextAsync(list, ClipPlanner.ConcatList(parts), ct).ConfigureAwait(false);
                args = ClipPlanner.BuildConcatArguments(plan, container, list, outputPath);
            }
            await CopyAsync(args, outputPath, ct).ConfigureAwait(false);
        }
        finally
        {
            if (list is not null) TryDelete(list);
            if (piece is not null) TryDelete(piece);
        }

        // 4) Verificación: que se reproduzca y tenga duración.
        var result = await locator.ProbeMediaAsync(outputPath, ct).ConfigureAwait(false);
        if (!result.IsPlayable)
        {
            TryDelete(outputPath);
            throw new ClipExtractionException(ClipError.Failed, Localizer.F("Clip_Err_Failed", "unverified"));
        }
        long bytes = 0; try { bytes = new FileInfo(outputPath).Length; } catch { /* best-effort */ }
        return new ClipOutcome(outputPath, TimeSpan.FromSeconds(result.DurationSeconds), bytes, plan);
    }

    /// <summary>Una pasada de ffmpeg; si falla, borra lo que dejó y explica con la última línea de su salida.</summary>
    private async Task CopyAsync(IReadOnlyList<string> args, string produced, CancellationToken ct)
    {
        log.LogDebug("Clip: ffmpeg {Args}", string.Join(' ', args));
        var (output, code) = await RunAsync(locator.FfmpegPath, args, CopyTimeout, ct).ConfigureAwait(false);
        if (code != 0)
        {
            TryDelete(produced);
            throw new ClipExtractionException(ClipError.Failed, Localizer.F("Clip_Err_Failed", LastLine(output, code)));
        }
    }

    /// <summary>Instantes (s) de los fotogramas clave de vídeo entre <c>at − window</c> y <c>at + 0.5</c>, leyendo SOLO esa ventana.</summary>
    private async Task<IReadOnlyList<double>> KeyframesNearAsync(string file, double at, double window, CancellationToken ct)
    {
        double from = Math.Max(0, at - window), to = at + 0.5;
        var args = new[]
        {
            "-v", "error", "-select_streams", "v:0",
            "-read_intervals", string.Create(CultureInfo.InvariantCulture, $"{from:0.000}%{to:0.000}"),
            "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", file,
        };
        var (output, code) = await RunAsync(locator.FfprobePath, args, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        var result = new List<double>();
        if (code != 0) return result;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            int comma = line.IndexOf(',');
            if (comma <= 0 || line.IndexOf('K', comma) < 0) continue;
            if (double.TryParse(line[..comma], NumberStyles.Float, CultureInfo.InvariantCulture, out double pts)) result.Add(pts);
        }
        return result;
    }

    /// <summary>Ejecuta ffmpeg/ffprobe con la salida leída en hilos propios (no del pool) y un tope de tiempo.</summary>
    private static async Task<(string Output, int ExitCode)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar " + Path.GetFileName(exe));
        try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* ya terminó o sin permisos */ }
        var sb = new StringBuilder();
        var stdout = ProcessOutput.ReadLines(p.StandardOutput, l => { lock (sb) sb.Append(l).Append('\n'); }, "clip-stdout");
        var stderr = ProcessOutput.ReadLines(p.StandardError, l => { lock (sb) sb.Append(l).Append('\n'); }, "clip-stderr");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ya salió */ }
            if (ct.IsCancellationRequested) throw;
            return ("timeout", -110);
        }
        ProcessOutput.Join(TimeSpan.FromSeconds(2), stdout, stderr);
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static string LastLine(string output, int code)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[^1] : string.Create(CultureInfo.InvariantCulture, $"exit {code}");
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
    }
}
