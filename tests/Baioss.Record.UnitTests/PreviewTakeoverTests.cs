using Baioss.Record.Infrastructure.Preview;
using Xunit;
using static Baioss.Record.Infrastructure.Preview.PreviewTakeover;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Cuándo pasa el preview del proceso viejo al nuevo en un relevo: cuando el nuevo enseña un frame de la fuente posterior al último
/// que se vio (con el mismo, sin repetirlo); antes no, salvo que no se sepa qué frame enseña cada uno, que el viejo ya no pinte o
/// que el nuevo no lo alcance a tiempo. Con el mando en el primer frame del nuevo, el preview saltaba 4–10 frames atrás.
/// </summary>
public class PreviewTakeoverTests
{
    [Fact]
    public void ANewProcessBehindTheOldOne_WaitsWhileTheOldOneKeepsPainting()
        => Assert.Equal(Verdict.NotYet, Decide(position: 103, lastShown: 107, oldStillPainting: true, waitedTooLong: false));

    [Fact]
    public void ANewProcessPastTheOldOne_TakesOverWithThatFrame()
        => Assert.Equal(Verdict.TakeOver, Decide(position: 108, lastShown: 107, oldStillPainting: true, waitedTooLong: false));

    [Fact]
    public void ANewProcessOnTheFrameJustShown_TakesOverWithoutShowingItTwice()
        => Assert.Equal(Verdict.TakeOverAfterThisFrame, Decide(position: 107, lastShown: 107, oldStillPainting: true, waitedTooLong: false));

    [Fact]
    public void WithAFieldRatePreview_HalfFramesCount()
    {
        // Un preview a un frame por campo saca dos por frame de la fuente: el segundo campo de 107 va después del primero.
        Assert.Equal(Verdict.TakeOver, Decide(position: 107.5, lastShown: 107, oldStillPainting: true, waitedTooLong: false));
        Assert.Equal(Verdict.NotYet, Decide(position: 106.5, lastShown: 107, oldStillPainting: true, waitedTooLong: false));
    }

    [Theory]
    [InlineData(null, 107.0)]  // la fuente no dice qué frame enseña el nuevo (la carta de ajuste, un relé de red)
    [InlineData(103.0, null)]  // ni el viejo (era la carta de ajuste, o el primer proceso del canal)
    public void WithoutKnowingWhichFrameEachOneShows_TheNewOneTakesOverWithItsFirstFrame(double? position, double? lastShown)
        => Assert.Equal(Verdict.TakeOver, Decide(position, lastShown, oldStillPainting: true, waitedTooLong: false));

    [Fact]
    public void WhenTheOldOneNoLongerPaints_TheNewOneTakesOverAtOnce()
        => Assert.Equal(Verdict.TakeOver, Decide(position: 103, lastShown: 107, oldStillPainting: false, waitedTooLong: false)); // mejor imagen que congelado

    [Fact]
    public void ANewProcessThatNeverCatchesUp_TakesOverAfterTheTimeout()
        => Assert.Equal(Verdict.TakeOver, Decide(position: 103, lastShown: 107, oldStillPainting: true, waitedTooLong: true));
}
