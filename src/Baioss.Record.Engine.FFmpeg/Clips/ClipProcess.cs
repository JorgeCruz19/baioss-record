using System.Diagnostics;
using System.Text;

namespace Baioss.Record.Engine.FFmpeg.Clips;

/// <summary>
/// Lanza un ffmpeg/ffprobe de la revisión o de un clip y espera a que termine, con la salida y los errores por separado (la de
/// ffprobe es JSON: cualquier aviso en medio lo rompería). Prioridad por debajo de lo normal y en el Job de la aplicación: un
/// corte o una revisión nunca deben quitarle CPU a las grabaciones en curso, ni sobrevivir a la aplicación. Lo que no cuesta
/// casi nada y no puede esperar (el audio del reproductor, que es su reloj) va con prioridad normal (<c>belowNormal: false</c>).
/// </summary>
internal static class ClipProcess
{
    /// <summary>Código de salida cuando se mató el proceso por pasar de <c>timeout</c>.</summary>
    public const int TimedOut = -110;

    public static async Task<(string Out, string Err, int Exit)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout,
        CancellationToken ct, Action<string>? onOutputLine = null, bool belowNormal = true)
    {
        using var p = Start(exe, args, redirectInput: false, belowNormal);
        using var timer = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var output = new StringBuilder();
        var stdout = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line)
            {
                output.Append(line).Append('\n');
                onOutputLine?.Invoke(line);
            }
        }, CancellationToken.None);
        var stderr = p.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            await p.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return (output.ToString(), await stderr.ConfigureAwait(false), p.ExitCode);
        }
        catch (OperationCanceledException)
        {
            Kill(p);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { /* lecturas canceladas */ }
            if (timer.IsCancellationRequested && !ct.IsCancellationRequested) return (output.ToString(), "", TimedOut);
            throw;
        }
    }

    /// <summary>Arranca el proceso (salida y errores redirigidos) ya metido en el Job y, salvo <paramref name="belowNormal"/> =
    /// false, con prioridad baja.</summary>
    public static Process Start(string exe, IReadOnlyList<string> args, bool redirectInput, bool belowNormal = true)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectInput,
            // Rutas con acentos (carpetas de clips, nombres de grabación): FFmpeg habla UTF-8.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar " + Path.GetFileName(exe));
        ChildProcessTracker.Track(p);
        if (belowNormal)
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { /* ya terminó */ }
        return p;
    }

    public static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* ya salió */ }
    }

    /// <summary>Las últimas líneas con algo de un registro de FFmpeg (lo que se cuenta cuando falla), sin el prefijo «[x @ 0x…]».</summary>
    public static string Tail(string log, int lines = 3)
    {
        var useful = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.StartsWith('[') && l.IndexOf("] ", StringComparison.Ordinal) is var i and > 0 ? l[(i + 2)..] : l)
            .Where(l => l.Length > 0)
            .ToList();
        return string.Join(" | ", useful.Skip(Math.Max(0, useful.Count - lines)));
    }
}
