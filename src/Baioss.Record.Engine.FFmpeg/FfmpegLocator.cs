using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// Sondea un archivo con ffprobe para VERIFICAR su integridad tras cerrarlo: lee las pistas
    /// (codec_type/codec_name) y la duración del contenedor en JSON. Un archivo corrupto o sin índice
    /// (p. ej. un MP4 al que le falta el <c>moov</c>) devuelve <see cref="MediaProbe.Unreadable"/> o sin
    /// pistas/duración, de modo que el llamador puede alarmar en vez de descubrirlo días después.
    /// </summary>
    public async Task<MediaProbe> ProbeMediaAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return MediaProbe.Unreadable;
        var args = new[]
        {
            "-v", "error", "-of", "json",
            "-show_entries", "format=duration:stream=codec_type,codec_name",
            filePath
        };
        var (output, exit) = await RunAsync(FfprobePath, args, ct).ConfigureAwait(false);
        if (exit != 0 || string.IsNullOrWhiteSpace(output)) return MediaProbe.Unreadable;

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            bool hasVideo = false, hasAudio = false;
            string? videoCodec = null;
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                foreach (var s in streams.EnumerateArray())
                {
                    var type = s.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                    if (type == "video") { hasVideo = true; videoCodec = s.TryGetProperty("codec_name", out var c) ? c.GetString() : videoCodec; }
                    else if (type == "audio") hasAudio = true;
                }

            double duration = 0;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var d) &&
                double.TryParse(d.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                duration = parsed;

            return new MediaProbe(hasVideo, hasAudio, duration, videoCodec);
        }
        catch (JsonException) { return MediaProbe.Unreadable; }
    }

    /// <summary>
    /// Remuxea un MP4/MOV a <c>+faststart</c> (índice al inicio) SIN recodificar. Atómico: escribe a un temporal
    /// en la MISMA carpeta y solo si FFmpeg termina bien y el resultado no está vacío sustituye al original (un
    /// <c>File.Move</c> de renombrado, instantáneo); ante cualquier fallo borra el temporal y deja el original
    /// intacto, de modo que nunca se pierde la grabación. Solo actúa sobre .mp4/.mov (otros contenedores → false).
    /// </summary>
    public async Task<bool> RemuxFaststartAsync(string filePath, CancellationToken ct = default)
    {
        if (!File.Exists(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        if (!ext.Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".mov", StringComparison.OrdinalIgnoreCase))
            return false; // faststart solo aplica a ISO-BMFF (MP4/MOV); TS/MKV no lo necesitan

        var dir = Path.GetDirectoryName(filePath) ?? ".";
        // Temporal en la misma carpeta (mismo volumen → el Move final es un renombrado atómico) y CON la
        // extensión real, para que FFmpeg elija el muxer por ella.
        var tmp = Path.Combine(dir, Path.GetFileNameWithoutExtension(filePath) + ".faststart" + ext);
        try
        {
            // -map 0 copia TODAS las pistas; -c copy no recodifica (rápido, sin pérdida); +faststart mueve el moov
            // al inicio (de paso des-fragmenta el fMP4). -y sobrescribe un temporal previo.
            var args = new[] { "-hide_banner", "-loglevel", "error", "-i", filePath, "-map", "0", "-c", "copy", "-movflags", "+faststart", "-y", tmp };
            var (_, exit) = await RunAsync(FfmpegPath, args, ct).ConfigureAwait(false);
            if (exit != 0 || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
            {
                TryDelete(tmp);
                return false;
            }
            File.Move(tmp, filePath, overwrite: true); // sustitución atómica del original por el optimizado
            return true;
        }
        catch
        {
            TryDelete(tmp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best-effort */ }
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
            // FFmpeg/ffprobe emiten en UTF-8; leerlo así preserva acentos en nombres de dispositivo y rutas
            // (sin esto, la codepage de consola los corrompería y, p. ej., dshow no hallaría el dispositivo).
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
