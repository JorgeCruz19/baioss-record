using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Silencio para una fuente NDI sin audio: sin datos en la entrada de audio, FFmpeg no arrancaba (ni preview ni grabación).
/// El silencio sigue al contador de cuadros —como las marcas de tiempo que FFmpeg pone a estas entradas— y el audio real manda.
/// </summary>
public sealed class NdiAudioGapFillerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);
    private const int Rate = 48000, N = 30000, D = 1001;              // 1080i59.94 / 29.97: 1601,6 muestras por cuadro
    private static DateTimeOffset Frame(int n) => T0 + TimeSpan.FromSeconds(n * (double)D / N);

    [Fact]
    public void WithAudioFlowing_NoSilenceIsSent()
    {
        var filler = new NdiAudioGapFiller();
        for (int n = 0; n < 300; n++)
        {
            filler.OnAudio(Frame(n) - TimeSpan.FromMilliseconds(5)); // un bloque de audio por cuadro, poco antes
            Assert.Equal(0, filler.OnVideo(Frame(n), Rate, N, D));
        }
        Assert.False(filler.Filling);
    }

    [Fact]
    public void WithoutAudio_AfterTheGap_TheFramesAlreadySentGetTheirSilence_AndThenEachFrameItsOwn()
    {
        var filler = new NdiAudioGapFiller(); // medio segundo
        long total = 0;
        int firstFilled = -1;
        for (int n = 0; n < 300; n++)
        {
            int samples = filler.OnVideo(Frame(n), Rate, N, D);
            if (samples > 0 && firstFilled < 0) firstFilled = n;
            total += samples;
        }
        // 0,5 s a 29.97 → el cuadro 15 (0,5005 s) es el primero tras el hueco, y recibe el silencio de los 16 que ya pasaron.
        Assert.Equal(15, firstFilled);
        Assert.True(filler.Filling);
        // En total, exactamente la duración de los 300 cuadros: 300 × 1601,6 = 480480 muestras (10,01 s).
        Assert.Equal(480480, total);
    }

    [Fact]
    public void EachFrame_GetsItsDuration_WithoutDrift()
    {
        var filler = new NdiAudioGapFiller { Gap = TimeSpan.Zero };
        var perFrame = Enumerable.Range(0, 5).Select(n => filler.OnVideo(Frame(n), Rate, N, D)).ToArray();
        Assert.All(perFrame, s => Assert.InRange(s, 1601, 1602));   // 1601,6: unos cuadros 1601 y otros 1602
        Assert.Equal(8008, perFrame.Sum());                          // y cada 5, exacto
    }

    [Fact]
    public void WhenAudioStopsMidway_SilenceCoversTheGap_AndStopsWhenAudioReturns()
    {
        var filler = new NdiAudioGapFiller();
        for (int n = 0; n < 60; n++)
        {
            Assert.Equal(0, filler.OnVideo(Frame(n), Rate, N, D));
            filler.OnAudio(Frame(n) + TimeSpan.FromMilliseconds(10)); // el audio de cada cuadro llega tras él
        }

        // El audio se corta tras el cuadro 59: rellena en cuanto pasa medio segundo, con lo que ya faltaba.
        long silence = 0;
        for (int n = 60; n < 120; n++) silence += filler.OnVideo(Frame(n), Rate, N, D);
        Assert.True(filler.Filling);
        Assert.Equal(96096, silence);                               // 60 cuadros sin audio × 1601,6: ni uno más ni uno menos

        filler.OnAudio(Frame(120));
        Assert.False(filler.Filling);
        Assert.Equal(0, filler.OnVideo(Frame(120), Rate, N, D));
    }

    [Fact]
    public void ASourceThatSendsFewFrames_IsJudgedByTime_NotByFrameCount()
    {
        // Una salida de grafismo que declara 60 cuadros por segundo y manda tres: el hueco se mide en tiempo, y el silencio
        // va a la tasa declarada (la que FFmpeg usa para las marcas del vídeo), cuadro a cuadro.
        var filler = new NdiAudioGapFiller();
        Assert.Equal(0, filler.OnVideo(T0, Rate, 60, 1));
        Assert.Equal(0, filler.OnVideo(T0 + TimeSpan.FromMilliseconds(333), Rate, 60, 1));
        Assert.Equal(3 * 800, filler.OnVideo(T0 + TimeSpan.FromMilliseconds(667), Rate, 60, 1)); // los 3 cuadros, 800 muestras cada uno
        Assert.Equal(800, filler.OnVideo(T0 + TimeSpan.FromMilliseconds(1000), Rate, 60, 1));
    }
}
