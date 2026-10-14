using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Las líneas con las que FFmpeg dice que el DISPOSITIVO tiró frames porque el proceso no los recogía a tiempo (literales
/// del FFmpeg empaquetado): pérdida segura que enciende la alarma de frames perdidos aunque su contador no la vea.
/// </summary>
public sealed class FfmpegInputOverrunTests
{
    [Theory]
    [InlineData("[decklink @ 000001d2c3a4b5c0] Decklink input buffer overrun!")]
    [InlineData("[dshow @ 000002a1b2c3d4e0] real-time buffer [Magewell USB Capture HDMI] [video input] too full or near too full (87% of size: 3041280 [rtbufsize parameter])! frame dropped!")]
    [InlineData("[dshow @ 000002a1b2c3d4e0] real-time buffer 85% full! frame dropped!")]
    public void DeviceOverruns_AreRecognised(string line) => Assert.True(FfmpegLogLines.IsInputOverrun(line));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[vf#2:0 @ 0000020f] *** dropping frame 12 at ts 11")]          // conversión de cadencia (-v verbose): no es el dispositivo
    [InlineData("[rawvideo @ 00000233] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 3 >= 3")]
    [InlineData("Input #0, decklink, from 'DeckLink Duo (1)':")]
    [InlineData("frame dropped")]
    public void OtherLines_AreNot(string? line) => Assert.False(FfmpegLogLines.IsInputOverrun(line));
}
