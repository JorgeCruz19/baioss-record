using Baioss.Record.Domain;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Regresión del ENCODING de la enumeración DirectShow: FFmpeg emite los nombres de dispositivo en UTF-8 y
/// el enumerador debe leerlos como UTF-8. Un nombre con acento que saliera con «Ã» delataría la lectura con
/// la codepage de consola (CP1252/850) — el bug que corrompía «Varios micrófonos» → «Varios micrÃ³fonos» y
/// dejaba a dshow sin encontrar el dispositivo (Could not find audio device / I/O error -5 al abrir).
/// </summary>
public sealed class DshowDeviceEncodingTests
{
    [SkippableFact]
    public async Task AudioDeviceNames_AreUtf8_WithoutMojibake()
    {
        Skip.IfNot(TestAssets.Available, "FFmpeg no disponible en tools/.");
        var enumerator = new FfmpegDeviceEnumerator(new FfmpegLocator(TestAssets.FfmpegDir!));

        var audio = await enumerator.DiscoverAudioDevicesAsync(InputType.DirectShow);
        Skip.If(audio.Count == 0, "No hay dispositivos de audio DirectShow en este equipo.");

        // Las secuencias «Ã»/«Â» son la firma del mojibake UTF-8 leído como CP1252; no deben aparecer.
        Assert.All(audio, name =>
            Assert.False(name.Contains('Ã') || name.Contains('Â'),
                $"Nombre de dispositivo con mojibake (lectura no-UTF8): «{name}»"));
    }
}
