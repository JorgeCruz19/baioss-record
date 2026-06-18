using System.Diagnostics;
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
    {
        var args = new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-f", "lavfi", "-i", "nullsrc=s=128x128:r=25",
            "-frames:v", "1", "-c:v", encoder, "-f", "null", "-"
        };
        var (_, exit) = await RunAsync(FfmpegPath, args, ct).ConfigureAwait(false);
        return exit == 0;
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
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return (stdout + stderr, p.ExitCode);
    }
}
