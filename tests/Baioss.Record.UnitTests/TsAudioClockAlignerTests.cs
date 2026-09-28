using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Alineador del reloj de audio dentro del MPEG-TS del relé: con relojes coherentes no toca un byte; con el audio a horas
/// de distancia del vídeo (emisor roto) retiene el audio 2,5 s, mide en régimen cuánto va por delante del último vídeo
/// llegado y lo resta desde el primer PES retenido (NO por el primer par: el servidor real vuelca al conectar una ráfaga
/// de caché con audio anterior al vídeo, 1,2 s medidos); después un servo lento sigue derivas reales; el retardo manual
/// por fuente se suma siempre; reensambla paquetes partidos; no confunde la vuelta del contador de 33 bits con un desfase;
/// y vuelve a medir tras Reset. Paquetes TS sintéticos con cabeceras PES reales e instantes de llegada simulados.
/// </summary>
public class TsAudioClockAlignerTests
{
    private const int V = TsAudioClockAligner.VideoPid, A = TsAudioClockAligner.AudioPid;
    private const long Ticks = 90_000;
    private const long Skew = 1_108_648_800; // 12 318,32 s en ticks: el desfase EN RÉGIMEN medido en el servidor RTMP real
    private const long VideoStart = 100 * Ticks; // DTS del vídeo de contenido 0

    /// <summary>Paquete TS de 188 bytes; con <paramref name="pts"/> lleva una cabecera PES al inicio de la carga útil.
    /// <paramref name="tag"/> se guarda en los bytes 100-103 para reconocer el paquete a la salida.</summary>
    private static byte[] Packet(int pid, bool pusi, long? pts, long? dts = null, bool adaptation = false, byte filler = 0xAB, int tag = 0)
    {
        var p = new byte[TsAudioClockAligner.PacketSize];
        Array.Fill(p, filler);
        p[0] = 0x47;
        p[1] = (byte)((pusi ? 0x40 : 0x00) | (pid >> 8));
        p[2] = (byte)(pid & 0xFF);
        int payload = 4;
        if (adaptation) { p[3] = 0x30; p[4] = 7; p[5] = 0x10; payload = 4 + 1 + 7; } // campo de adaptación de 7 bytes (con PCR)
        else p[3] = 0x10;
        if (pusi && pts is not null)
        {
            p[payload] = 0; p[payload + 1] = 0; p[payload + 2] = 1;
            p[payload + 3] = (byte)(pid == V ? 0xE0 : 0xC0);
            p[payload + 4] = 0; p[payload + 5] = 0;
            p[payload + 6] = 0x80;
            p[payload + 7] = (byte)(dts is null ? 0x80 : 0xC0);
            p[payload + 8] = (byte)(dts is null ? 5 : 10);
            WriteTs(p, payload + 9, pts.Value, dts is null ? 0x2 : 0x3);
            if (dts is not null) WriteTs(p, payload + 14, dts.Value, 0x1);
        }
        BitConverter.GetBytes(tag).CopyTo(p, 100);
        return p;
    }

    private static void WriteTs(byte[] p, int i, long ts, int marker)
    {
        p[i] = (byte)((marker << 4) | (int)((ts >> 29) & 0x0E) | 1);
        p[i + 1] = (byte)((ts >> 22) & 0xFF);
        p[i + 2] = (byte)(((ts >> 14) & 0xFE) | 1);
        p[i + 3] = (byte)((ts >> 7) & 0xFF);
        p[i + 4] = (byte)(((ts << 1) & 0xFE) | 1);
    }

    private static (long Pts, long? Dts) ReadPes(byte[] p)
    {
        int payload = ((p[3] >> 4) & 0x3) == 3 ? 4 + 1 + p[4] : 4;
        long Read(int i) => ((long)(p[i] & 0x0E) << 29) | ((long)p[i + 1] << 22) | ((long)(p[i + 2] & 0xFE) << 14) | ((long)p[i + 3] << 7) | ((long)p[i + 4] >> 1);
        bool hasDts = (p[payload + 7] >> 6) == 0x3;
        return (Read(payload + 9), hasDts ? Read(payload + 14) : null);
    }

    private static int Pid(byte[] p) => ((p[1] & 0x1F) << 8) | p[2];
    private static int Tag(byte[] p) => BitConverter.ToInt32(p, 100);
    private static byte[] Concat(params byte[][] packets) => packets.SelectMany(p => p).ToArray();
    private static byte[][] Split(byte[] stream) => Enumerable.Range(0, stream.Length / 188).Select(i => stream[(i * 188)..((i + 1) * 188)]).ToArray();

    /// <summary>
    /// Flujo como lo entrega un servidor real, por LLEGADA: al conectar vuelca en 50 ms una ráfaga de caché (vídeo de los últimos
    /// <paramref name="videoCacheSeconds"/> y audio de los últimos <paramref name="audioCacheSeconds"/> de contenido) y luego emite
    /// en tiempo real desde el contenido 0: vídeo a 30 fps (DTS regular, PTS un frame por delante, como con B-frames) y audio a
    /// 46,875 PES/s cuyas marcas avanzan <paramref name="audioTicksPerPes"/> por PES (1920 = reloj coherente; 1843 = un 4 % más
    /// lento), desplazadas <paramref name="audioSkew"/>. El audio va etiquetado con su índice de contenido (j, 0 = contenido 0).
    /// </summary>
    private static List<(double At, byte[] Packet)> ServerStream(int seconds, long audioSkew, double videoCacheSeconds = 0, double audioCacheSeconds = 0, long audioTicksPerPes = 1920)
    {
        var packets = new List<(double, byte[])>();
        int burstV = (int)Math.Round(videoCacheSeconds * 30), burstA = (int)Math.Round(audioCacheSeconds * 46.875);
        // La ráfaga ocupa los primeros 50 ms; el directo (contenido 0) empieza cuando acaba.
        double live = burstV + burstA > 0 ? 0.05 : 0;
        for (int i = -burstV; i < seconds * 30; i++)
        {
            long dts = VideoStart + i * 3000L;
            double at = i < 0 ? 0.05 * (i + burstV) / Math.Max(1, burstV) : live + i / 30.0;
            packets.Add((at, Packet(V, true, pts: dts + 3000, dts: dts, tag: i)));
        }
        double lastVideoContent = (seconds * 30 - 1) / 30.0;
        for (int j = -burstA; j / 46.875 + 0.001 <= lastVideoContent; j++)
        {
            long pts = VideoStart + audioSkew + j * audioTicksPerPes;
            double at = j < 0 ? 0.05 * (j + burstA) / Math.Max(1, burstA) : live + j / 46.875 + 0.001;
            packets.Add((at, Packet(A, true, pts: pts, tag: j)));
        }
        return packets.OrderBy(p => p.Item1).ToList();
    }

    /// <summary>Alimenta el flujo paquete a paquete con su instante de llegada y devuelve la salida en orden.</summary>
    private static byte[][] Feed(TsAudioClockAligner aligner, List<(double At, byte[] Packet)> stream, Action<double>? probe = null)
    {
        var output = new List<byte>();
        foreach (var (at, packet) in stream)
        {
            output.AddRange(aligner.Process(packet, at));
            probe?.Invoke(at);
        }
        return Split(output.ToArray());
    }

    /// <summary>Para cada PES de audio de la salida, cuánto se aleja su PTS del vídeo con el que LLEGÓ (contenido j/46,875).</summary>
    private static (double Worst, int Count) AudioErrorAgainstContent(byte[][] output, long expectedDelayTicks = 0)
    {
        double worst = 0; int count = 0; long previous = long.MinValue;
        foreach (var p in output)
        {
            if (Pid(p) != A) continue;
            int j = Tag(p);
            long expected = VideoStart + (long)(j / 46.875 * Ticks) + expectedDelayTicks;
            var (pts, _) = ReadPes(p);
            Assert.True(pts > previous, "el audio nunca retrocede");
            previous = pts;
            worst = Math.Max(worst, Math.Abs(pts - expected) / (double)Ticks);
            count++;
        }
        return (worst, count);
    }

    // --- Relojes coherentes ---

    [Fact]
    public void CoherentClocks_PassUntouched_EvenWithAGopCacheLead()
    {
        var aligner = new TsAudioClockAligner();
        var input = Concat(
            Packet(V, true, pts: 1 * Ticks, dts: 1 * Ticks - 3600),
            Packet(V, false, null),
            Packet(A, true, pts: 5 * Ticks),           // el audio empieza 4 s después (caché de GOP del servidor): legítimo
            Packet(0x1000, true, null, filler: 0x00),  // PMT u otro PID: se reenvía tal cual
            Packet(A, true, pts: 5 * Ticks + 1920));
        double? corrected = null;
        aligner.SkewCorrected += s => corrected = s;

        var output = aligner.Process(input, 0);

        Assert.Equal(input, output);
        Assert.Equal(0, aligner.CorrectionSeconds);
        Assert.Null(aligner.FirstPairSkewSeconds);
        Assert.Null(corrected);
    }

    [Fact]
    public void HeldAudio_IsReleasedUntouched_WhenTheClocksTurnOutToBeCoherent()
    {
        var aligner = new TsAudioClockAligner();
        var a1 = Packet(A, true, pts: 3 * Ticks);
        var v1 = Packet(V, true, pts: 2 * Ticks);
        Assert.Empty(aligner.Process(a1, 0)); // sin vídeo aún: se retiene
        Assert.Equal(Concat(a1, v1), aligner.Process(v1, 0.01));
        Assert.Equal(0, aligner.CorrectionSeconds);
    }

    [Fact]
    public void CoherentClocks_WithDriftingTimestamps_AreRespected_TheServoStaysOff()
    {
        // Con relojes coherentes las marcas de tiempo son la verdad (una deriva así sería del emisor y hay que conservarla).
        var aligner = new TsAudioClockAligner();
        var stream = ServerStream(seconds: 20, audioSkew: Ticks / 2, audioTicksPerPes: 1843);
        var output = Feed(aligner, stream);
        Assert.Equal(Concat(stream.Select(s => s.Packet).ToArray()), Concat(output));
        Assert.Equal(0, aligner.CorrectionSeconds);
        Assert.Equal(0, aligner.DriftCompensatedSeconds);
    }

    // --- Relojes distintos (el servidor RTMP real) ---

    [Fact]
    public void SeparateClocks_TheAudioIsAlignedByArrivalInSteadyState_NotByTheFirstPair()
    {
        // Como el servidor real: audio 3,4 h «por delante» y, al conectar, una ráfaga de caché con 1,2 s de vídeo y 2,4 s de
        // audio ANTERIORES: el primer par se equivoca 1,2 s; medir en régimen (pasada la ráfaga) no.
        var aligner = new TsAudioClockAligner();
        double? corrected = null;
        aligner.SkewCorrected += s => corrected = s;
        var stream = ServerStream(seconds: 8, audioSkew: Skew, videoCacheSeconds: 1.2, audioCacheSeconds: 2.4);

        bool settlingAt2 = false, settledAt3 = false;
        var output = Feed(aligner, stream, at =>
        {
            if (at is > 1.9 and < 2.0) settlingAt2 = aligner.IsSettling;
            if (at is > 3.0 and < 3.1) settledAt3 = !aligner.IsSettling;
        });

        Assert.True(settlingAt2, "a los 2 s aún se retiene el audio (asentamiento)");
        Assert.True(settledAt3, "a los 3 s ya se decidió");
        var (worst, count) = AudioErrorAgainstContent(output);
        Assert.True(count > 350, $"se soltó todo el audio ({count} PES)");
        Assert.True(worst < 0.1, $"desviación máxima entre el audio y el vídeo con el que llegó: {worst:0.000} s");
        Assert.Equal(12318.32, corrected!.Value, 1);                       // el desfase en régimen…
        Assert.Equal(12318.32 - 1.222, aligner.FirstPairSkewSeconds!.Value, 1); // …no el del primer par (la caché lo desvía 1,2 s)
        // El vídeo sale intacto y en orden.
        var video = output.Where(p => Pid(p) == V).ToArray();
        Assert.Equal(stream.Count(s => Pid(s.Packet) == V), video.Length);
        Assert.Equal(stream.Where(s => Pid(s.Packet) == V).Select(s => Tag(s.Packet)), video.Select(Tag));
    }

    [Fact]
    public void SeparateClocks_TheServoFollowsAnAudioClockThatRunsSlow()
    {
        var aligner = new TsAudioClockAligner();
        var output = Feed(aligner, ServerStream(seconds: 60, audioSkew: Skew, audioTicksPerPes: 1843));
        // Sin el servo, al minuto el audio iría 2,4 s por detrás del vídeo con el que llegó; con él, se queda a menos de 0,1 s.
        var (worst, _) = AudioErrorAgainstContent(output);
        Assert.True(worst < 0.1, $"desviación máxima: {worst:0.000} s");
        Assert.InRange(aligner.DriftCompensatedSeconds, 1.9, 2.8);
    }

    [Fact]
    public void SeparateClocks_WithoutLiveAudioAfterTheBurst_TheSettlingExpires_AndTheFirstPairIsUsed()
    {
        var aligner = new TsAudioClockAligner();
        var stream = ServerStream(seconds: 10, audioSkew: Skew, videoCacheSeconds: 1.0, audioCacheSeconds: 1.0)
            .Where(s => Pid(s.Packet) == V || s.At < 0.05).ToList(); // tras la ráfaga solo llega vídeo
        var output = Feed(aligner, stream);
        Assert.False(aligner.IsSettling);
        Assert.NotNull(aligner.CorrectionSeconds);
        Assert.Equal(output.Count(p => Pid(p) == A), stream.Count(s => Pid(s.Packet) == A)); // el audio retenido se suelta igualmente
    }

    // --- Retardo manual ---

    [Fact]
    public void AManualAudioDelay_IsAdded_WithCoherentAndWithSeparateClocks()
    {
        var coherent = new TsAudioClockAligner { AudioDelayTicks = 27000 }; // +300 ms: el audio suena más tarde
        var stream = ServerStream(seconds: 3, audioSkew: Ticks / 4);
        var output = Feed(coherent, stream);
        foreach (var (input, produced) in stream.Select(s => s.Packet).Zip(output))
        {
            if (Pid(input) == V) { Assert.Equal(input, produced); continue; }
            Assert.Equal(ReadPes(input).Pts + 27000, ReadPes(produced).Pts);
        }
        Assert.Equal(0, coherent.CorrectionSeconds);

        var negative = new TsAudioClockAligner { AudioDelayTicks = -9000 }; // −100 ms: el audio se adelanta
        var single = Concat(Packet(V, true, pts: Ticks), Packet(A, true, pts: Ticks + 900));
        Assert.Equal(Ticks + 900 - 9000, ReadPes(Split(negative.Process(single, 0))[1]).Pts);

        var separate = new TsAudioClockAligner { AudioDelayTicks = 27000 };
        var (worst, _) = AudioErrorAgainstContent(Feed(separate, ServerStream(seconds: 8, audioSkew: Skew, videoCacheSeconds: 1.2, audioCacheSeconds: 2.4)), expectedDelayTicks: 27000);
        Assert.True(worst < 0.1, $"desviación máxima con retardo manual: {worst:0.000} s");
    }

    // --- Mecánica de paquetes ---

    [Fact]
    public void PacketsSplitAcrossReads_AreReassembled_AndTheResultIsTheSame()
    {
        var input = Concat(
            Packet(V, true, pts: 7 * Ticks),
            Packet(A, true, pts: 7 * Ticks + 900),
            Packet(A, true, pts: 7 * Ticks + 2820, dts: 7 * Ticks + 2820, adaptation: true),
            Packet(V, false, null, filler: 0x12));
        var whole = new TsAudioClockAligner { AudioDelayTicks = 4500 }.Process(input, 0);

        var pieces = new TsAudioClockAligner { AudioDelayTicks = 4500 };
        var output = new List<byte>();
        for (int i = 0; i < input.Length; i += 100) output.AddRange(pieces.Process(input.AsSpan(i, Math.Min(100, input.Length - i)), 0));

        Assert.Equal(whole, output.ToArray());
        Assert.Equal(4 * 188, whole.Length);
        Assert.Equal((7 * Ticks + 2820 + 4500, 7 * Ticks + 2820 + 4500), ReadPes(Split(whole)[2])); // PTS y DTS, tras un campo de adaptación
        Assert.Equal(0x31, Split(whole)[2][4 + 8 + 9] & 0xF1);  // las marcas '0011'/'0001' se conservan
        Assert.Equal(0x11, Split(whole)[2][4 + 8 + 14] & 0xF1);
    }

    [Fact]
    public void TheWrapOfThe33BitCounter_IsNotASkew()
    {
        var aligner = new TsAudioClockAligner();
        long wrap = 1L << 33;
        var input = Concat(
            Packet(V, true, pts: wrap - Ticks / 2),   // medio segundo antes de la vuelta
            Packet(A, true, pts: Ticks / 2));         // medio segundo después: 1 s de diferencia real
        Assert.Equal(input, aligner.Process(input, 0));
        Assert.Equal(0, aligner.CorrectionSeconds);
    }

    [Fact]
    public void Reset_MeasuresTheNextStreamAgain()
    {
        var aligner = new TsAudioClockAligner();
        Feed(aligner, ServerStream(seconds: 4, audioSkew: Skew));
        Assert.Equal(12318.32, aligner.CorrectionSeconds!.Value, 1);

        aligner.Reset();
        Assert.Null(aligner.CorrectionSeconds);
        Assert.False(aligner.IsSettling);
        var healthy = Concat(Packet(V, true, pts: 50 * Ticks), Packet(A, true, pts: 50 * Ticks + 900));
        Assert.Equal(healthy, aligner.Process(healthy, 100));
        Assert.Equal(0, aligner.CorrectionSeconds);
    }

    [Fact]
    public void BytesBeforeTheSyncByte_AreSkipped()
    {
        var aligner = new TsAudioClockAligner();
        var v1 = Packet(V, true, pts: Ticks);
        var input = new byte[] { 0x00, 0x11, 0x22 }.Concat(v1).ToArray();
        Assert.Equal(v1, aligner.Process(input, 0));
    }

    [Fact]
    public void WithoutVideo_HeldAudioIsReleasedUncorrected_OnceTheCapIsReached()
    {
        var aligner = new TsAudioClockAligner { MaxHeldBytes = 3 * 188 };
        var a = Packet(A, true, pts: Ticks);
        Assert.Empty(aligner.Process(a, 0));
        Assert.Empty(aligner.Process(a, 0));
        Assert.Empty(aligner.Process(a, 0));
        Assert.Equal(Concat(a, a, a, a), aligner.Process(a, 0)); // el cuarto supera el tope: se suelta todo tal cual
        Assert.Equal(0, aligner.CorrectionSeconds);
    }

    [Fact]
    public void SeparateClocks_TheVideoWaitsWithTheAudioWhileSettling_AndEverythingComesOutInOrder()
    {
        // Mientras se mide (asentamiento) no sale NADA: ni audio ni vídeo. Así un proceso del canal que conecte en ese
        // momento no ve un flujo solo-vídeo (con 2 s de análisis arrancaría sin audio). Al decidir sale todo, en su orden.
        var aligner = new TsAudioClockAligner();
        var stream = ServerStream(seconds: 6, audioSkew: Skew);
        var output = new List<byte>();
        int outputWhileSettling = 0, videoHeld = 0;
        foreach (var (at, packet) in stream)
        {
            var produced = aligner.Process(packet, at);
            if (aligner.IsSettling) { outputWhileSettling += produced.Length; if (Pid(packet) == V) videoHeld++; }
            output.AddRange(produced);
        }
        Assert.True(videoHeld > 60, $"llegó vídeo durante el asentamiento ({videoHeld} PES)");
        Assert.Equal(0, outputWhileSettling);
        Assert.False(aligner.IsSettling);

        var packets = Split(output.ToArray());
        Assert.Equal(stream.Select(s => Tag(s.Packet)), packets.Select(Tag)); // todo, en el orden de llegada
        Assert.Equal(stream.Where(s => Pid(s.Packet) == V).Select(s => ReadPes(s.Packet).Dts), packets.Where(p => Pid(p) == V).Select(p => ReadPes(p).Dts)); // el vídeo, intacto
        var (worst, count) = AudioErrorAgainstContent(packets);
        Assert.True(count > 250 && worst < 0.1, $"audio alineado: {count} PES, desviación máxima {worst:0.000} s");
        Assert.Equal(VideoStart + (6 * 30 - 1) * 3000L, aligner.LastVideoDts);
    }

    [Fact]
    public void PesTimestamps_AreReadWithAndWithoutDts()
    {
        Assert.True(TsAudioClockAligner.TryReadPesTimestamps(Packet(V, true, pts: 5000, dts: 2000, adaptation: true), out var pts, out var dts));
        Assert.Equal((5000, 2000), (pts, dts));
        Assert.True(TsAudioClockAligner.TryReadPesTimestamps(Packet(A, true, pts: 7000), out pts, out dts));
        Assert.Equal((7000, 7000), (pts, dts));
        Assert.False(TsAudioClockAligner.TryReadPesTimestamps(Packet(V, false, pts: null), out _, out _));
    }
}
