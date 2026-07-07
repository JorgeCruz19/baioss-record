using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// N12: el «%» debe quitarse del nombre base de grabación. El muxer `segment` lo interpreta como un
/// especificador de formato (como %d), así que un título como «Descuentos 50%» generaría un patrón inválido
/// y FFmpeg saldría con error EN BUCLE (grabación segmentada sin producir nada).
/// </summary>
public class RecordingNameSanitizeTests
{
    [Theory]
    [InlineData("Descuentos 50%", "Descuentos 50")]
    [InlineData("100%% seguro", "100 seguro")]
    [InlineData("Noticias", "Noticias")]                 // sin cambios cuando no hay %
    [InlineData("A/B:C*?", "ABC")]                        // caracteres inválidos de archivo también fuera
    public void SanitizeBaseName_StripsPercentAndInvalidChars(string raw, string expected)
        => Assert.Equal(expected, FfmpegChannelEngine.SanitizeBaseName(raw));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("%%%")]                                   // solo caracteres a quitar → null (usa el por defecto)
    public void SanitizeBaseName_ReturnsNull_WhenEmptyAfterCleaning(string raw)
        => Assert.Null(FfmpegChannelEngine.SanitizeBaseName(raw));
}
