using Baioss.Record.Application.Abstractions;

namespace Baioss.Record.Infrastructure.Time;

/// <summary>Reloj real del sistema. Implementación por defecto de <see cref="IClock"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
