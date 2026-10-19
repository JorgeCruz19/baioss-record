using System.Buffers;
using System.Runtime.InteropServices;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// El audio de cada proceso de FFmpeg empieza en el instante de la fuente de su primer cuadro de vídeo: con lo guardado (recortando el
/// bloque que lo contiene), esperando a que llegue (descartando lo de antes) o con silencio delante si es anterior a lo guardado. Sin
/// marcas, o si no cuadran, nada.
/// </summary>
public sealed class NdiAudioAlignerTests
{
    private const int Rate = 48000, Channels = 2, BlockSamples = 960; // bloques de 20 ms

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Un bloque estéreo de 20 ms cuyas muestras valen su índice absoluto desde el instante 0 de la fuente (para saber dónde
    /// empieza lo que se entrega).</summary>
    private static NdiFrame Block(int index)
    {
        int bytes = BlockSamples * Channels * sizeof(float);
        var buf = ArrayPool<byte>.Shared.Rent(bytes);
        var samples = MemoryMarshal.Cast<byte, float>(buf.AsSpan(0, bytes));
        for (int i = 0; i < BlockSamples; i++) samples[2 * i] = samples[2 * i + 1] = index * BlockSamples + i;
        return new NdiFrame(buf, bytes);
    }

    private static float FirstSample(NdiFrame f) => MemoryMarshal.Cast<byte, float>(f.Buffer.AsSpan(0, f.Length))[0];

    private static int Samples(NdiFrame f) => f.Length / (Channels * sizeof(float));

    /// <summary>Un alineador con los bloques 0–4 (0–100 ms) guardados.</summary>
    private static NdiAudioAligner WithHistory(int blocks = 5)
    {
        var aligner = new NdiAudioAligner();
        for (int i = 0; i < blocks; i++)
        {
            var b = Block(i);
            aligner.Remember(Ms(20 * i), b, BlockSamples, Rate, Channels);
            b.Release(); // el llamador suelta el suyo: el alineador guarda una referencia propia
        }
        return aligner;
    }

    [Fact]
    public void AVideoStartWithinTheHistory_StartsTheAudioThere_TrimmingTheBlockThatHoldsIt()
    {
        var (blocks, outcome, amount) = WithHistory().StartAt(Ms(30));
        Assert.Equal(NdiAudioAligner.Outcome.FromHistory, outcome);
        Assert.Equal(10, amount.TotalMilliseconds, 3);
        Assert.Equal(1440f, FirstSample(blocks[0]));                    // la muestra de los 30 ms
        Assert.Equal(480, Samples(blocks[0]));                           // lo que queda del bloque 1
        Assert.Equal(new[] { 1920f, 2880f, 3840f }, blocks.Skip(1).Select(FirstSample)); // y los bloques 2–4 enteros
        Assert.Equal(480 + 3 * BlockSamples, blocks.Sum(Samples));       // de los 30 ms a los 100 ms
        foreach (var b in blocks) b.Release();
    }

    [Fact]
    public void AVideoStartBeforeTheHistory_IsPaddedWithSilence_ThenTheWholeHistory()
    {
        var (blocks, outcome, amount) = WithHistory().StartAt(Ms(-50));
        Assert.Equal(NdiAudioAligner.Outcome.Padded, outcome);
        Assert.Equal(50, amount.TotalMilliseconds, 3);
        Assert.Equal(2400, Samples(blocks[0]));                          // 50 ms de silencio
        Assert.All(MemoryMarshal.Cast<byte, float>(blocks[0].Buffer.AsSpan(0, blocks[0].Length)).ToArray(), s => Assert.Equal(0f, s));
        Assert.Equal(0f, FirstSample(blocks[1]));                        // y luego todo lo guardado
        Assert.Equal(6, blocks.Count);
        foreach (var b in blocks) b.Release();
    }

    [Fact]
    public void AVideoStartTheAudioHasNotReachedYet_WaitsForIt_DroppingWhatComesBefore()
    {
        var aligner = WithHistory();                                     // audio hasta los 100 ms
        var (blocks, outcome, amount) = aligner.StartAt(Ms(130));        // el vídeo empieza a los 130 ms
        Assert.Equal(NdiAudioAligner.Outcome.Waiting, outcome);
        Assert.Equal(30, amount.TotalMilliseconds, 3);
        Assert.Empty(blocks);

        var b5 = Block(5);                                               // 100–120 ms: todo antes del arranque
        Assert.Null(aligner.Admit(Ms(100), b5, BlockSamples));
        var b6 = Block(6);                                               // 120–140 ms: contiene el arranque
        var first = aligner.Admit(Ms(120), b6, BlockSamples);
        Assert.NotNull(first);
        Assert.NotSame(b6, first);
        Assert.Equal(6240f, FirstSample(first!));                        // la muestra de los 130 ms
        Assert.Equal(480, Samples(first!));
        var b7 = Block(7);                                               // lo siguiente pasa tal cual
        Assert.Same(b7, aligner.Admit(Ms(140), b7, BlockSamples));
        foreach (var b in new[] { b5, b6, first!, b7 }) b.Release();
    }

    [Fact]
    public void WithoutMarks_OrWithoutHistory_OrWhenTheClocksDoNotMatch_NothingIsAligned()
    {
        Assert.Equal(NdiAudioAligner.Outcome.NotAligned, WithHistory().StartAt(null).Outcome);
        Assert.Equal(NdiAudioAligner.Outcome.NotAligned, new NdiAudioAligner().StartAt(Ms(30)).Outcome);
        Assert.Equal(NdiAudioAligner.Outcome.NotAligned, WithHistory().StartAt(TimeSpan.FromSeconds(10)).Outcome);
        Assert.Equal(NdiAudioAligner.Outcome.NotAligned, WithHistory().StartAt(TimeSpan.FromSeconds(-10)).Outcome);

        // Un bloque sin marca olvida lo guardado: el siguiente proceso empieza como siempre.
        var aligner = WithHistory();
        var unmarked = Block(5);
        aligner.Remember(null, unmarked, BlockSamples, Rate, Channels);
        Assert.Equal(NdiAudioAligner.Outcome.NotAligned, aligner.StartAt(Ms(30)).Outcome);
        unmarked.Release();
    }

    [Fact]
    public void WithoutAPendingStart_LiveAudioPassesThrough()
    {
        var aligner = WithHistory();
        var live = Block(5);
        Assert.Same(live, aligner.Admit(Ms(100), live, BlockSamples));
        live.Release();
    }

    [Fact]
    public void ANewSampleRate_ForgetsWhatWasKept()
    {
        var aligner = WithHistory();
        var b = Block(5);
        aligner.Remember(Ms(100), b, BlockSamples, 44100, Channels);
        b.Release();
        var (blocks, outcome, _) = aligner.StartAt(Ms(30));               // los 30 ms ya no están: lo guardado empieza a los 100
        Assert.Equal(NdiAudioAligner.Outcome.Padded, outcome);
        foreach (var x in blocks) x.Release();
    }
}
