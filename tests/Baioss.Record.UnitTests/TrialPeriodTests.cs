using Baioss.Record.Application.Licensing;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Periodo de prueba (14 días). Lo importante aquí no es contar días, es que NINGÚN juego con el reloj del PC
/// alargue la prueba, y que a la vez un ajuste de hora legítimo no deje al cliente sin poder grabar.
/// </summary>
public sealed class TrialPeriodTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);
    private static TrialMark Fresh => TrialPeriod.Begin(Start);

    [Fact]
    public void FirstDay_HasAllDaysRemaining()
    {
        var (days, expired) = TrialPeriod.Evaluate(Fresh, Start);
        Assert.Equal(14, days);
        Assert.False(expired);
    }

    [Theory]
    [InlineData(1, 13)]
    [InlineData(7, 7)]
    [InlineData(13, 1)]
    public void DaysRemaining_CountDown(int elapsedDays, int expected)
    {
        var (days, expired) = TrialPeriod.Evaluate(Fresh, Start.AddDays(elapsedDays));
        Assert.Equal(expected, days);
        Assert.False(expired);
    }

    [Fact]
    public void AtExactlyFourteenDays_ItExpires()
    {
        var (days, expired) = TrialPeriod.Evaluate(Fresh, Start.AddDays(14));
        Assert.Equal(0, days);
        Assert.True(expired);
    }

    [Fact]
    public void LongAfterTheTrial_StaysExpired_WithoutNegativeDays()
    {
        var (days, expired) = TrialPeriod.Evaluate(Fresh, Start.AddDays(400));
        Assert.Equal(0, days);
        Assert.True(expired);
    }

    // --- Manipulación del reloj ---

    [Fact]
    public void TurningTheClockBack_DoesNotExtendTheTrial()
    {
        // Se llegó al día 13 (marca de agua); alguien atrasa el reloj al día 1 para «renovar» la prueba.
        var mark = TrialPeriod.Advance(Fresh, Start.AddDays(13), TimeSpan.Zero);
        var (days, _) = TrialPeriod.Evaluate(mark, Start.AddDays(1));
        Assert.Equal(1, days); // sigue contando desde la marca de agua, no desde el reloj falseado
    }

    [Fact]
    public void TurningTheClockBackEveryDay_DoesNotFreezeTheTrial()
    {
        // Ataque realista: usar la app y atrasar el reloj un día cada día. El calendario deja de avanzar, pero el
        // USO ACUMULADO (reloj monotónico) no se puede falsear y acaba consumiendo la prueba igual.
        var mark = Fresh;
        for (int i = 0; i < 14; i++)
            mark = TrialPeriod.Advance(mark, Start, TimeSpan.FromDays(1)); // el reloj «no avanza» nunca

        var (days, expired) = TrialPeriod.Evaluate(mark, Start);
        Assert.Equal(0, days);
        Assert.True(expired);
    }

    [Fact]
    public void ClockSetFarIntoTheFutureThenCorrected_DoesNotFreezeTheTrial()
    {
        // Reloj puesto en 2030 al instalar y devuelto a la fecha real: el calendario queda «congelado» porque la
        // marca de agua está en el futuro, pero el uso real sigue contando y la prueba termina.
        var far = Start.AddYears(4);
        var mark = TrialPeriod.Begin(far);
        for (int i = 0; i < 14; i++)
            mark = TrialPeriod.Advance(mark, Start, TimeSpan.FromDays(1)); // ya con la hora corregida

        Assert.True(TrialPeriod.Evaluate(mark, Start).Expired);
    }

    [Fact]
    public void LegitimateClockCorrection_DoesNotConsumeExtraDays()
    {
        // Corrección normal hacia atrás (NTP): la marca de agua manda, pero NO se saltan días de golpe.
        var mark = TrialPeriod.Advance(Fresh, Start.AddDays(5), TimeSpan.FromDays(5));
        var (days, _) = TrialPeriod.Evaluate(mark, Start.AddDays(5).AddHours(-3));
        Assert.Equal(9, days); // 5 días consumidos, ni uno más por el ajuste
    }

    // --- Acumulación de uso ---

    [Fact]
    public void Advance_AccumulatesUsage_AndNeverLowersTheHighWater()
    {
        var mark = TrialPeriod.Advance(Fresh, Start.AddDays(3), TimeSpan.FromHours(2));
        Assert.Equal(Start.AddDays(3), mark.HighWater);
        Assert.Equal(7200, mark.UsedSeconds);

        var back = TrialPeriod.Advance(mark, Start.AddDays(1), TimeSpan.FromHours(1)); // reloj atrasado
        Assert.Equal(Start.AddDays(3), back.HighWater); // no baja
        Assert.Equal(10800, back.UsedSeconds);          // el uso sigue sumando
    }

    [Fact]
    public void Advance_IgnoresNegativeElapsed()
    {
        var mark = TrialPeriod.Advance(Fresh, Start, TimeSpan.FromHours(-5));
        Assert.Equal(0, mark.UsedSeconds);
    }

    [Fact]
    public void ZeroOrNegativeTrialLength_IsExpired()
    {
        Assert.True(TrialPeriod.Evaluate(Fresh, Start, trialDays: 0).Expired);
        Assert.True(TrialPeriod.Evaluate(Fresh, Start, trialDays: -5).Expired);
    }
}
