using Baioss.Record.Application.Licensing;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Composición de la huella del equipo. Lo crítico: que sea DETERMINISTA (el mismo PC da siempre la misma) y NO
/// AMBIGUA (dos equipos distintos nunca coinciden por cómo se concatenan los identificadores).
/// </summary>
public sealed class MachineFingerprintComposerTests
{
    [Fact]
    public void SameComponents_AlwaysProduceTheSameFingerprint()
        => Assert.Equal(
            MachineFingerprintComposer.Compose("guid-1", "vol-1", "board-1"),
            MachineFingerprintComposer.Compose("guid-1", "vol-1", "board-1"));

    [Fact]
    public void ShiftedComponents_DoNotCollide()
    {
        // REGRESIÓN: concatenando sin etiqueta ni longitud, estas cuatro combinaciones daban el MISMO hash, es
        // decir, equipos distintos con la misma huella (y por tanto una licencia válida en varios PCs).
        var a = MachineFingerprintComposer.Compose("ABC", null, "DEF");
        var b = MachineFingerprintComposer.Compose("ABC", "DEF", null);
        var c = MachineFingerprintComposer.Compose("AB", "CD", "EF");
        var d = MachineFingerprintComposer.Compose("ABC", "DE", "F");

        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
        Assert.NotEqual(b, c);
        Assert.NotEqual(c, d);
    }

    [Fact]
    public void MissingComponent_IsStable_NotOmitted()
    {
        // Un componente ausente NO se salta (eso desplazaría el resto): se emite vacío y la huella sigue estable.
        Assert.Equal(
            MachineFingerprintComposer.Compose("guid", null, "board"),
            MachineFingerprintComposer.Compose("guid", "", "board"));
    }

    [Theory]
    [InlineData("To Be Filled By O.E.M.")]
    [InlineData("  default string  ")]
    [InlineData("System Serial Number")]
    [InlineData("None")]
    [InlineData("N/A")]
    public void HardwarePlaceholders_AreTreatedAsAbsent(string junk)
    {
        // Si estos valores contaran como identificador, TODOS los equipos que los devuelven compartirían huella.
        Assert.Equal("", MachineFingerprintComposer.Normalize(junk));
        Assert.Equal(
            MachineFingerprintComposer.Compose("guid", junk),
            MachineFingerprintComposer.Compose("guid", null));
    }

    [Theory]
    [InlineData("abc-123", "ABC-123")]
    [InlineData("  ABC-123  ", "ABC-123")]
    [InlineData("ABC   123", "ABC 123")]
    public void Identifiers_AreNormalised(string raw, string expected)
        => Assert.Equal(expected, MachineFingerprintComposer.Normalize(raw));

    [Fact]
    public void DifferentMachines_ProduceDifferentFingerprints()
        => Assert.NotEqual(
            MachineFingerprintComposer.Compose("guid-A", "vol-A"),
            MachineFingerprintComposer.Compose("guid-B", "vol-B"));

    [Fact]
    public void Fingerprint_HasTheExpectedLengthAndReadableCode()
    {
        var fp = MachineFingerprintComposer.Compose("guid", "vol");
        Assert.Equal(MachineFingerprintComposer.Length, fp.Length);

        string code = MachineFingerprintComposer.ToCode(fp);
        Assert.Equal(16, code.Replace("-", "").Length); // 10 bytes → 16 caracteres
        foreach (char ch in code.Replace("-", ""))
            Assert.DoesNotContain(ch, "ILOU");           // sin caracteres confundibles al dictarlo
    }

    [Fact]
    public void MachineWithNoRealIdentifiers_IsDetected()
    {
        Assert.False(MachineFingerprintComposer.HasAnyRealIdentifier(null, "", "Default string"));
        Assert.True(MachineFingerprintComposer.HasAnyRealIdentifier(null, "c9ddc3ed-b96d-4940"));
    }
}
