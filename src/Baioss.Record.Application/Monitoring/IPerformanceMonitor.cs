namespace Baioss.Record.Application.Monitoring;

/// <summary>Muestra de telemetría del sistema (host) para el dashboard técnico.</summary>
public sealed record PerformanceSnapshot(
    double CpuPercent,
    double RamPercent,
    double GpuPercent,
    double VramPercent,
    double DiskPercent,
    double NetworkMbps,
    DateTimeOffset At);

/// <summary>
/// Monitor de rendimiento del host. Lee contadores de CPU/RAM/Disco/Red y,
/// vía NVML/ADL/IGCL, métricas de GPU/VRAM. Publica muestras periódicas.
/// </summary>
public interface IPerformanceMonitor
{
    PerformanceSnapshot Current { get; }
    event EventHandler<PerformanceSnapshot>? Updated;
    Task StartAsync(TimeSpan interval, CancellationToken ct = default);
}
