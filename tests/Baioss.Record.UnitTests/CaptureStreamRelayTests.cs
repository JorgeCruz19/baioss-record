using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Dimensionado del PRE-ROLL del relé de captura (modo dispositivo persistente). El pre-roll es lo que permite
/// que la grabación arranque en el fotograma clave ANTERIOR: sin él, el grabador esperaría al siguiente keyframe
/// y se PERDERÍA hasta un GOP de contenido. Debe medirse en SEGUNDOS equivalentes, no en bytes fijos: con un
/// tamaño fijo, un perfil de bitrate bajo daba una ventana de muchos segundos (el archivo empezaba mucho antes).
/// </summary>
public sealed class CaptureStreamRelayTests
{
    [Fact]
    public void PrerollBytes_EqualsRequestedSecondsOfTheProfileBitrate()
    {
        // 16 Mbps · 2,5 s = 5 MB
        Assert.Equal(5_000_000, CaptureStreamRelay.PrerollBytesFor(16_000_000, 2.5));
        // 8 Mbps · 2,5 s = 2,5 MB  → la MITAD que a 16 Mbps: los segundos se mantienen, los bytes no.
        Assert.Equal(2_500_000, CaptureStreamRelay.PrerollBytesFor(8_000_000, 2.5));
    }

    [Fact]
    public void PrerollBytes_HasFloor_SoLowBitratesStillCoverAGop()
    {
        // Un bitrate muy bajo no debe dejar una ventana ridícula: piso de 1 MB.
        Assert.Equal(1024 * 1024, CaptureStreamRelay.PrerollBytesFor(100_000, 2.5));
    }

    [Fact]
    public void PrerollBytes_HasCeiling_SoHighBitratesDoNotBlowMemory()
    {
        // 400 Mbps · 2,5 s serían 125 MB por canal: se acota a 32 MB.
        Assert.Equal(32 * 1024 * 1024, CaptureStreamRelay.PrerollBytesFor(400_000_000, 2.5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PrerollBytes_InvalidBitrate_FallsBackToFloor(long bps)
        => Assert.Equal(1024 * 1024, CaptureStreamRelay.PrerollBytesFor(bps));
}
