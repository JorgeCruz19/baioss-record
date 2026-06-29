using System.Collections.Concurrent;
using System.IO;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// Registro COMPARTIDO del ritmo de escritura (bytes/s) de los canales que están grabando, agrupado por
/// VOLUMEN. La guarda de disco de cada canal consulta el ritmo AGREGADO de su volumen (no solo el suyo):
/// con N canales escribiendo en el mismo disco, el espacio se consume ~N× más rápido y el «tiempo restante»
/// estimado por canal estaba inflado ~N× → el auto-stop por disco crítico llegaba demasiado tarde, sin
/// margen para el cierre ordenado de los N procesos. (Auditoría 24/7, A7/#10.)
/// </summary>
public sealed class DiskUsageRegistry
{
    private readonly record struct Entry(string Root, Func<long> Rate);
    private readonly ConcurrentDictionary<Guid, Entry> _active = new();

    /// <summary>Raíz de volumen normalizada (p. ej. «C:») para agrupar canales que comparten disco.</summary>
    private static string Volume(string outputDir)
        => (Path.GetPathRoot(Path.GetFullPath(outputDir)) ?? outputDir).TrimEnd('\\', '/').ToUpperInvariant();

    /// <summary>Registra (o actualiza) el ritmo de un canal que empieza a grabar.</summary>
    public void Register(Guid channelId, string outputDir, Func<long> bytesPerSecond)
        => _active[channelId] = new Entry(Volume(outputDir), bytesPerSecond);

    /// <summary>Quita el canal al detener su grabación.</summary>
    public void Unregister(Guid channelId) => _active.TryRemove(channelId, out _);

    /// <summary>Suma de bytes/s de todos los canales activos que escriben en el mismo volumen que <paramref name="outputDir"/>.</summary>
    public long TotalBytesPerSecond(string outputDir)
    {
        var vol = Volume(outputDir);
        long sum = 0;
        foreach (var e in _active.Values)
            if (e.Root == vol)
                try { sum += Math.Max(0, e.Rate()); } catch { /* un canal con ritmo no disponible no rompe la suma */ }
        return sum;
    }

    /// <summary>Número de canales activos que graban en el mismo volumen (≥1 si el llamante está grabando).</summary>
    public int ActiveCount(string outputDir)
    {
        var vol = Volume(outputDir);
        int n = 0;
        foreach (var e in _active.Values) if (e.Root == vol) n++;
        return n;
    }
}
