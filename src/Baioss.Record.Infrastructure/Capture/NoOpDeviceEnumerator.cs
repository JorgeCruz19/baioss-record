using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Enumerador vacío usado cuando no hay FFmpeg disponible (modo simulado): permite que la UI
/// inyecte siempre un <see cref="IDeviceEnumerator"/> sin condicionar el resto del cableado.
/// </summary>
public sealed class NoOpDeviceEnumerator : IDeviceEnumerator
{
    public Task<IReadOnlyList<InputSource>> DiscoverAsync(InputType type, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<InputSource>>(Array.Empty<InputSource>());

    public Task<IReadOnlyList<string>> DiscoverAudioDevicesAsync(InputType type, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

    public Task<IReadOnlyList<DeviceFormat>> DiscoverFormatsAsync(InputType type, string deviceId, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DeviceFormat>>(Array.Empty<DeviceFormat>());
}
