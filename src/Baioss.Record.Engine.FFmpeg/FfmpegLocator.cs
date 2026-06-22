using System.Diagnostics;
using System.Linq;
using Baioss.Record.Application.Abstractions;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Localiza ffmpeg/ffprobe y consulta capacidades. Acepta tanto una carpeta (que
/// contenga ffmpeg.exe) como la ruta directa al ejecutable.
/// </summary>
public sealed class FfmpegLocator : IFfmpegLocator
{
    public FfmpegLocator(string ffmpegDirectoryOrExe)
    {
        if (Directory.Exists(ffmpegDirectoryOrExe))
        {
            FfmpegPath = Path.Combine(ffmpegDirectoryOrExe, "ffmpeg.exe");
            FfprobePath = Path.Combine(ffmpegDirectoryOrExe, "ffprobe.exe");
        }
        else
        {
            FfmpegPath = ffmpegDirectoryOrExe;
            var dir = Path.GetDirectoryName(ffmpegDirectoryOrExe) ?? ".";
            FfprobePath = Path.Combine(dir, "ffprobe.exe");
        }

        if (!File.Exists(FfmpegPath))
            throw new FileNotFoundException("No se encontró ffmpeg.exe.", FfmpegPath);
    }

    public string FfmpegPath { get; }
    public string FfprobePath { get; }

    public async Task<IReadOnlyCollection<string>> GetAvailableEncodersAsync(CancellationToken ct = default)
    {
        var (output, _) = await RunAsync(FfmpegPath, new[] { "-hide_banner", "-encoders" }, ct).ConfigureAwait(false);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in output.Split('\n'))
        {
            // " V....D h264_nvenc   NVIDIA NVENC H.264 encoder"
            var line = raw.TrimStart();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6 && parts[0][0] is 'V' or 'A' or 'S')
                set.Add(parts[1]);
        }
        return set;
    }

    /// <summary>
    /// Comprueba en runtime si un encoder de video abre realmente (NVENC requiere
    /// nvcuda.dll; estar listado en -encoders no garantiza que el driver esté presente).
    /// </summary>
    public async Task<bool> IsVideoEncoderUsableAsync(string encoder, CancellationToken ct = default)
        => (await ProbeVideoEncoderAsync(encoder, ct).ConfigureAwait(false)).Ok;

    /// <summary>
    /// Sondea si un encoder de vídeo abre realmente (codifica 1 frame; estar listado en <c>-encoders</c>
    /// no garantiza que el driver lo soporte). Devuelve también, si falla, una RAZÓN concisa — p. ej.
    /// «Driver does not support the required nvenc API version. Required: 13.1 Found: 13.0» — para poder
    /// explicar en el log por qué se descarta una GPU y se pasa a la siguiente de la cascada.
    /// </summary>
    public async Task<(bool Ok, string Detail)> ProbeVideoEncoderAsync(string encoder, CancellationToken ct = default)
    {
        var args = new[]
        {
            "-hide_banner", "-loglevel", "warning", // «warning» basta para la razón (driver/versión) sin tanta salida
            "-f", "lavfi", "-i", "nullsrc=s=128x128:r=25",
            // nv12 es el formato nativo de los encoders por hardware (NVENC/QSV/AMF) y también lo acepta
            // libx264, así que la prueba es representativa para todos.
            "-frames:v", "1", "-c:v", encoder, "-pix_fmt", "nv12", "-f", "null", "-"
        };
        var (output, exit) = await RunAsync(FfmpegPath, args, ct).ConfigureAwait(false);
        if (exit == 0) return (true, "");

        // Razón legible: la línea de log más informativa (driver/versión/soporte), sin el prefijo «[enc @ …]».
        var lines = output.Split('\n').Select(CleanLogLine).Where(l => l.Length > 0).ToArray();
        string? reason = lines.LastOrDefault(l =>
            l.Contains("driver", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("API version", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("not support", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("Cannot load", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("capable", StringComparison.OrdinalIgnoreCase) ||
            l.Contains("No NVENC", StringComparison.OrdinalIgnoreCase));
        return (false, reason ?? lines.LastOrDefault() ?? $"exit {exit}");
    }

    /// <summary>Quita el prefijo «[componente @ dirección] » que FFmpeg antepone a cada línea de log.</summary>
    private static string CleanLogLine(string line)
    {
        var l = line.Trim();
        if (l.StartsWith('['))
        {
            int close = l.IndexOf("] ", StringComparison.Ordinal);
            if (close >= 0) l = l[(close + 2)..].Trim();
        }
        return l;
    }

    private static async Task<(string Output, int ExitCode)> RunAsync(string exe, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar FFmpeg.");
        // Lee AMBOS flujos en paralelo: si se leyeran en serie y uno llenara su buffer de tubería mientras
        // se espera el otro, FFmpeg se bloquearía al escribir → deadlock (visible con salida grande, p. ej.
        // `-loglevel verbose`). Leer concurrentemente lo evita siempre.
        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);
        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (await stdoutTask + await stderrTask, p.ExitCode);
    }
}
