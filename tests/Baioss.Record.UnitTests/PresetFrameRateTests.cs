using Baioss.Record.Application.Presets;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// La cadencia que escribe el operador en el editor de presets. Antes era un entero que iba al numerador: un preset nuevo no podía
/// ser 29.97, y en una copia de un preset 29.97 (30000/1001) el campo decía «30000» y escribir 25 dejaba 25/1001, un cuadro cada 40 s.
/// </summary>
public sealed class PresetFrameRateTests
{
    [Theory]
    [InlineData("25", 25, 1)]
    [InlineData("50", 50, 1)]
    [InlineData("29.97", 30000, 1001)]
    [InlineData("29,97", 30000, 1001)]     // con la coma decimal del español
    [InlineData("59.94", 60000, 1001)]
    [InlineData("23.976", 24000, 1001)]
    [InlineData("23.98", 24000, 1001)]
    [InlineData("47.952", 48000, 1001)]
    [InlineData("119.88", 120000, 1001)]
    [InlineData("30000/1001", 30000, 1001)]
    [InlineData(" 60 ", 60, 1)]
    [InlineData("12.5", 25, 2)]
    [InlineData("", 0, 1)]                 // vacío: la de la fuente
    [InlineData("0", 0, 1)]
    public void WhatTheOperatorTypes_IsKeptExact(string text, int numerator, int denominator)
    {
        Assert.True(PresetFrameRate.TryParse(text, out int n, out int d));
        Assert.Equal((numerator, denominator), (n, d));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-25")]
    [InlineData("25/0")]
    [InlineData("30000/")]
    [InlineData("1e3")]
    public void SomethingThatIsNotAFrameRate_IsRefused(string text) => Assert.False(PresetFrameRate.TryParse(text, out _, out _));

    [Theory]
    [InlineData(30000, 1001, "29.97")]
    [InlineData(60000, 1001, "59.94")]
    [InlineData(24000, 1001, "23.976")]
    [InlineData(25, 1, "25")]
    [InlineData(0, 1, "")]
    [InlineData(25, 2, "12.5")]
    [InlineData(25, 1001, "25/1001")] // la de una copia mal editada se ve tal cual
    public void TheFieldShowsTheRateAsItIsCalled(int numerator, int denominator, string shown)
        => Assert.Equal(shown, PresetFrameRate.Format(numerator, denominator));

    [Fact]
    public void EveryFactoryRate_GoesThereAndBack()
    {
        foreach (var p in PresetCatalog.CreateBuiltIns().Where(p => p.FrameRateNum > 0))
        {
            Assert.True(PresetFrameRate.TryParse(PresetFrameRate.Format(p.FrameRateNum, p.FrameRateDen), out int n, out int d), p.Name);
            Assert.Equal((p.FrameRateNum, p.FrameRateDen), (n, d));
        }
    }
}
