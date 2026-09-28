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
        var work = WorkFor(directory);
        var done = await Task.WhenAny(work, Task.Delay(timeout)).ConfigureAwait(false);
        return done == work && work.Result;
    }

    // UNA escritura en vuelo por carpeta. Con el disco colgado la escritura se queda bloqueada en el sistema minutos, y el
    // vigilante pregunta cada 2 s por canal: antes cada pregunta abría un hilo nuevo (≈ 60 por canal en un cuelgue de
    // 2 min, todos bloqueados en el disco). Mientras la anterior no termine, las preguntas nuevas esperan a esa misma.
    // Además, dos canales que graban en la misma carpeta ya no compiten por el mismo archivo de sonda (FileShare.None):
    // antes uno de los dos veía «no responde» por la colisión.
    private static readonly Dictionary<string, Task<bool>> InFlight = new(StringComparer.OrdinalIgnoreCase);

    private static Task<bool> WorkFor(string directory)
    {
        string key;
        try { key = Path.GetFullPath(directory); } catch { key = directory; }
        lock (InFlight)
        {
            if (InFlight.TryGetValue(key, out var pending) && !pending.IsCompleted) return pending;
            var work = Task.Factory.StartNew(() => WriteThrough(directory), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            InFlight[key] = work;
            return work;
        }
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
