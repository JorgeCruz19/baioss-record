using System.Globalization;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Recording;
using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

public class FfmpegProgressParserTests
{
    [Fact]
    public void Feed_ReturnsNull_UntilProgressLineClosesBlock()
    {
        var parser = new FfmpegProgressParser();
        Assert.Null(parser.Feed("frame=100"));
        Assert.Null(parser.Feed("fps=25.0"));
        Assert.Null(parser.Feed("bitrate=2046.7kbits/s"));
        Assert.NotNull(parser.Feed("progress=continue"));
    }

    [Fact]
    public void Feed_ParsesAFullProgressBlock()
    {
        var parser = new FfmpegProgressParser();
        parser.Feed("frame=100");
        parser.Feed("fps=25.0");
        parser.Feed("drop_frames=2");
        parser.Feed("dup_frames=1");
        parser.Feed("bitrate=2046.7kbits/s");
        parser.Feed("out_time_us=4000000");
        var stats = parser.Feed("progress=continue");

        Assert.NotNull(stats);
        Assert.Equal(100, stats!.FrameCount);
        Assert.Equal(25.0, stats.OutputFps);
        Assert.Equal(2, stats.DroppedFrames);
        Assert.Equal(1, stats.DuplicatedFrames);
        Assert.Equal(2_046_700, stats.Bitrate.BitsPerSecond);   // "2046.7kbits/s" → bps
        Assert.Equal("00:00:04:00", stats.Timecode.ToString());  // out_time_us=4 s → 00:00:04:00
    }

    [Fact]
    public void Feed_Timecode_FollowsRealTimeNotFrameCount_AtHighFps()
    {
        // Regresión: a 50 fps el contador subía de dos en dos porque dividía 'frame' entre 25 fijos.
        // Ahora el HH:MM:SS viene de out_time (tiempo real): 100 cuadros a 50 fps = 2 s reales, no 4.
        var parser = new FfmpegProgressParser { NominalRate = 50 };
        parser.Feed("frame=100");
        parser.Feed("fps=50.0");
        parser.Feed("out_time_us=2000000");                      // 2 s reales
        var stats = parser.Feed("progress=continue");

        Assert.Equal("00:00:02:00", stats!.Timecode.ToString());  // 2 s, NO 4 s
        Assert.Equal(100, stats.FrameCount);                      // el conteo de cuadros sí es 100
    }

    [Fact]
    public void Feed_Timecode_UsesOutTimeMs_WhenUsAbsent()
    {
        // Algunos builds emiten solo 'out_time_ms' (que, por histórico de FFmpeg, está en microsegundos).
        var parser = new FfmpegProgressParser();
        parser.Feed("frame=75");
        parser.Feed("out_time_ms=3000000");                      // 3 s
        var stats = parser.Feed("progress=continue");

        Assert.Equal("00:00:03:00", stats!.Timecode.ToString());
    }

    [Fact]
    public void Feed_ExposesTheMediaTime_AndMinusOneWhileFfmpegDoesNotKnowIt()
    {
        // Al arrancar FFmpeg escribe «N/A»: para el timecode vale 0, pero quien mide si el proceso va al día debe saber que
        // aún no hay dato (no que el proceso esté parado en el segundo 0).
        var parser = new FfmpegProgressParser();
        parser.Feed("frame=0");
        parser.Feed("out_time_us=N/A");
        parser.Feed("out_time_ms=N/A");
        var start = parser.Feed("progress=continue");
        Assert.Equal(-1, start!.MediaTimeUs);
        Assert.Equal("00:00:00:00", start.Timecode.ToString());

        parser.Feed("frame=50");
        parser.Feed("out_time_us=2000000");
        Assert.Equal(2_000_000, parser.Feed("progress=continue")!.MediaTimeUs);

        parser.Feed("frame=75");
        parser.Feed("out_time_ms=3000000"); // builds que solo dan out_time_ms (también en µs)
        Assert.Equal(3_000_000, parser.Feed("progress=continue")!.MediaTimeUs);
    }

    [Fact]
    public void Feed_IgnoresLinesWithoutKeyValue()
    {
        var parser = new FfmpegProgressParser();
        Assert.Null(parser.Feed("=novalue"));
        Assert.Null(parser.Feed("sinIgual"));
    }

    // --- FPS OUT: el ritmo de ahora, no la media de FFmpeg desde el arranque ---

    /// <summary>Un proceso simulado: bloques de progreso a la cadencia de -stats_period con el contador acumulado y el «fps» que
    /// FFmpeg imprimiría (frames ÷ tiempo desde el arranque), con un reloj que decide el test.</summary>
    private sealed class FakeProcess
    {
        public TimeSpan Now;
        public long Frames;
        public double MediaSeconds;
        public double ProcessSeconds;
        public readonly FfmpegProgressParser Parser;
        public FakeProcess() => Parser = new FfmpegProgressParser(() => Now);

        /// <summary>Avanza un segundo de pared en el que salen <paramref name="frames"/> frames de una fuente a 29,97.</summary>
        public RecorderStats Second(double frames)
        {
            Now += TimeSpan.FromSeconds(1);
            ProcessSeconds += 1;
            Frames += (long)Math.Round(frames);
            MediaSeconds = Frames / 29.97;
            Parser.Feed($"frame={Frames}");
            Parser.Feed(string.Create(CultureInfo.InvariantCulture, $"fps={Frames / ProcessSeconds:0.00}"));
            Parser.Feed("drop_frames=0");
            Parser.Feed($"out_time_us={(long)(MediaSeconds * 1_000_000)}");
            return Parser.Feed("progress=continue")!;
        }
    }

    [Fact]
    public void OutputFps_AfterABacklogDrains_ShowsTheCurrentRate_NotTheAverageSinceStart()
    {
        // Medido con una DeckLink: tras vaciarse un atraso, FPS OUT marcó 37,9–44 durante un minuto con una fuente de 29,97,
        // porque el «fps» de FFmpeg es la media desde que arrancó el proceso.
        var p = new FakeProcess();
        for (int s = 0; s < 10; s++) p.Second(60);             // diez segundos vaciando el atraso al doble
        RecorderStats last = RecorderStats.Empty;
        for (int s = 0; s < 10; s++) last = p.Second(29.97);

        Assert.InRange(last.InputFps, 44, 46);                  // la media de FFmpeg aún arrastra el atraso…
        Assert.InRange(last.OutputFps, 29.5, 30.5);             // …el panel enseña lo que sale ahora
        Assert.Equal(p.Frames, last.FrameCount);                // y el contador sigue siendo el acumulado
    }

    [Fact]
    public void OutputFps_FollowsTheRateWithinTheWindow()
    {
        var p = new FakeProcess();
        for (int s = 0; s < 5; s++) p.Second(25);
        Assert.InRange(p.Second(25).OutputFps, 24.5, 25.5);

        // El proceso se atasca: sale la mitad. En cuanto el tramo de medida lo cubre, FPS OUT lo dice.
        p.Second(12); p.Second(13);
        Assert.InRange(p.Second(12).OutputFps, 11.5, 13);
    }

    [Fact]
    public void OutputFps_UsesFfmpegsFigure_UntilTwoBlocksAreFarEnoughApart()
    {
        var now = TimeSpan.Zero;
        var parser = new FfmpegProgressParser(() => now);
        parser.Feed("frame=12");
        parser.Feed("fps=24.00");
        Assert.Equal(24.0, parser.Feed("progress=continue")!.OutputFps); // primer bloque: no hay con qué comparar

        now += TimeSpan.FromMilliseconds(30);                              // dos bloques de golpe (la tubería se atascó)
        parser.Feed("frame=13");
        parser.Feed("fps=24.50");
        Assert.Equal(24.5, parser.Feed("progress=continue")!.OutputFps);  // 1 frame en 30 ms no es un ritmo

        now += TimeSpan.FromSeconds(1);
        parser.Feed("frame=38");
        parser.Feed("fps=25.00");
        Assert.InRange(parser.Feed("progress=continue")!.OutputFps, 24.5, 25.5);
    }

    [Fact]
    public void OutputFps_StartsOverWithANewProcess()
    {
        // Grabar/Detener o una pieza nueva relanzan FFmpeg: su contador vuelve a cero y no debe salir un ritmo negativo.
        var now = TimeSpan.Zero;
        var parser = new FfmpegProgressParser(() => now);
        foreach (var frame in new[] { 25, 50, 75, 100 })
        {
            now += TimeSpan.FromSeconds(1);
            parser.Feed($"frame={frame}");
            parser.Feed("fps=25.00");
            parser.Feed("progress=continue");
        }

        now += TimeSpan.FromSeconds(1);
        parser.Feed("frame=20");
        parser.Feed("fps=20.00");
        Assert.Equal(20.0, parser.Feed("progress=continue")!.OutputFps);  // proceso nuevo: su propia cifra

        now += TimeSpan.FromSeconds(1);
        parser.Feed("frame=45");
        parser.Feed("fps=22.50");
        Assert.InRange(parser.Feed("progress=continue")!.OutputFps, 24.5, 25.5);
    }

    [Fact]
    public void TheFramesDroppedAlarm_StillSeesTheProcessFallingBehind_AndNotTheDrain()
    {
        // La alarma de frames perdidos va por el contador ACUMULADO y el tiempo de medios, no por FPS OUT: vaciar un atraso no
        // la enciende, y un proceso que se queda atrás de verdad sí.
        var p = new FakeProcess();
        var detector = new SaturationDetector();
        detector.Reset(realTimeSource: true);
        var cadence = FrameRateConversion.For(FrameRate.P2997, null);
        RecorderStats last = RecorderStats.Empty;
        bool Feed(double frames)
        {
            last = p.Second(frames);
            return detector.Update(new SaturationSample(last.DroppedFrames, last.FrameCount, last.MediaTimeUs, p.Now), cadence);
        }

        for (int s = 0; s < 10; s++) Assert.False(Feed(60), "vaciar un atraso no es saturación");
        for (int s = 0; s < 20; s++) Assert.False(Feed(29.97), "a ritmo real no hay alarma");

        bool alarm = false;
        for (int s = 0; s < 20 && !alarm; s++) alarm = Feed(21);   // al 70 % del tiempo real, sostenido
        Assert.True(alarm, "un proceso que se queda atrás debe seguir encendiendo la alarma");
        Assert.InRange(last.OutputFps, 20, 22);                    // y FPS OUT lo dice a la vez
    }
}
