namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Comprueba si un volumen ACEPTA ESCRITURAS AHORA: escribe unos bytes en un archivo temporal de la carpeta con
/// <see cref="FileOptions.WriteThrough"/> (sin pasar por la caché: solo termina cuando el disco lo ha aceptado)
/// y lo borra. Es lo que distingue «FFmpeg no escribe» de «el disco no responde», que desde fuera se ven igual
/// (el archivo no crece) pero exigen reacciones opuestas. Incidente 2026-09-06.
///
/// En un disco colgado la escritura se queda bloqueada en el sistema hasta que el disco vuelva; por eso corre en
/// un hilo propio (no del pool, que se quedaría sin hilos) y el llamador la acota con su propio tiempo máximo.
/// </summary>
public static class VolumeProbe
{
    public const string ProbeFileName = ".baioss-probe";

    /// <summary>
    /// <c>true</c> si la carpeta aceptó una escritura sincronizada dentro de <paramref name="timeout"/>;
    /// <c>false</c> si tardó más, no existe o no se pudo escribir. Nunca lanza.
    /// </summary>
    public static async Task<bool> IsResponsiveAsync(string directory, TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(directory)) return false;
        var work = Task.Factory.StartNew(() => WriteThrough(directory), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var done = await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false);
        return done == work && work.Result;
    }

    private static bool WriteThrough(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var path = Path.Combine(directory, ProbeFileName);
            var payload = new byte[512];
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough | FileOptions.DeleteOnClose))
            {
                fs.Write(payload, 0, payload.Length);
                fs.Flush(flushToDisk: true);
            }
            return true;
        }
        catch { return false; }
    }
}
