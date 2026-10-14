using Baioss.Record.Application.Channels;
using Baioss.Record.Domain.ValueObjects;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// La regla de la alarma de frames perdidos: los descartes que pide la conversión de cadencia del preset no son saturación;
/// los que sobran, y el atraso sostenido con una fuente a ritmo de tiempo real, sí.
/// </summary>
public sealed class SaturationDetectorTests
{
    private static readonly FrameRateConversion NoConversion = FrameRateConversion.For(FrameRate.P30, output: null);
    private static readonly FrameRateConversion ThirtyToTwentyFive = FrameRateConversion.For(FrameRate.P30, FrameRate.P25);

    /// <summary>Descartes acumulados de FFmpeg al convertir de 30 a 25 fps tras <paramref name="inputFrames"/> frames de
    /// entrada: tira uno de cada seis.</summary>
    private static long ConversionDrops(long inputFrames) => inputFrames - (long)Math.Floor(inputFrames * 25.0 / 30.0);

    /// <summary>Muestra «a la FFmpeg»: el contador de frames es el de la primera salida (el preview: frames de entrada).</summary>
    private static SaturationSample At(double seconds, long frames, long drops, double mediaSeconds = -1)
        => new(drops, frames, mediaSeconds < 0 ? -1 : (long)(mediaSeconds * 1_000_000), TimeSpan.FromSeconds(seconds));

    private static SaturationDetector NewDetector(bool realTime = false)
    {
        var d = new SaturationDetector(onAfter: 3, offAfter: 5);
        d.Reset(realTime);
        return d;
    }

    [Fact]
    public void ConversionDrops_DoNotAlarm_ButTheOldRuleDid()
    {
        // Lo que se vio: una fuente a 30 fps grabada con un preset a 25 da un drop_frames que crece 5 por segundo con la CPU
        // sobrada (medido con el FFmpeg empaquetado: 28 descartes en 6 s a 20,9x). La regla antigua lo tomaba por saturación.
        var oldRule = new DropAlarmTracker(onAfter: 3, offAfter: 5);
        var detector = NewDetector(realTime: true);
        bool oldAlarm = false, newAlarm = false;
        // Muestras a ~1 s con la fase irregular de la vida real (31, 60, 92… frames de entrada) y velocidad 1x.
        long frames = 0;
        for (int i = 1; i <= 120; i++)
        {
            frames += i % 3 == 0 ? 32 : 29;
            double t = frames / 30.0;
            oldAlarm |= oldRule.Update(ConversionDrops(frames));
            newAlarm |= detector.Update(At(t, frames, ConversionDrops(frames), t), ThirtyToTwentyFive);
        }
        Assert.True(oldAlarm, "la regla antigua debía encender la alarma con la conversión (el falso positivo que se corrige)");
        Assert.False(newAlarm, "la conversión de cadencia pedida no es saturación");
        Assert.Equal(0, detector.UnexplainedDrops);
    }

    [Fact]
    public void ConversionPlusRealLoss_StillAlarms()
    {
        var detector = NewDetector();
        long frames = 0, extra = 0;
        bool alarm = false;
        for (int i = 1; i <= 20; i++)
        {
            frames += 30;
            if (i > 5) extra += 4; // a partir del 5.º segundo el proceso tira 4 frames más de los que pide la conversión
            alarm = detector.Update(At(i, frames, ConversionDrops(frames) + extra), ThirtyToTwentyFive);
        }
        Assert.True(alarm, "los descartes que la conversión no explica siguen siendo saturación");
        Assert.Equal(SaturationSymptom.UnexplainedDrops, detector.LastSymptom);
        // Lo que se enseña como «perdidos» son los que sobran (menos el margen de fase), no los de la conversión.
        Assert.InRange(detector.UnexplainedDrops, extra - 3, extra);
    }

    [Fact]
    public void WithoutConversion_AnyNewDropCounts_AsBefore()
    {
        // Sin conversión se conserva el comportamiento de siempre: tres muestras seguidas con descartes nuevos → alarma.
        var detector = NewDetector();
        Assert.False(detector.Update(At(1, 30, 0), NoConversion)); // línea base
        Assert.False(detector.Update(At(2, 60, 2), NoConversion));
        Assert.False(detector.Update(At(3, 90, 5), NoConversion));
        Assert.True(detector.Update(At(4, 120, 9), NoConversion));
        Assert.Equal(9, detector.UnexplainedDrops);
    }

    [Fact]
    public void IsolatedDrop_DoesNotAlarm()
    {
        var detector = NewDetector();
        detector.Update(At(1, 30, 0), NoConversion);
        detector.Update(At(2, 60, 1), NoConversion);
        for (int i = 3; i < 15; i++) Assert.False(detector.Update(At(i, i * 30, 1), NoConversion));
    }

    [Fact]
    public void UnknownSourceRate_WithFixedOutput_DropsAreNotJudged()
    {
        // Preset a 25 y una fuente que no dice su tasa: no hay forma de separar la conversión de la pérdida.
        var unknown = FrameRateConversion.For(source: null, FrameRate.P25);
        var detector = NewDetector();
        long frames = 0;
        for (int i = 1; i <= 30; i++)
        {
            frames += 30;
            Assert.False(detector.Update(At(i, frames, ConversionDrops(frames)), unknown));
        }
        Assert.Equal(0, detector.UnexplainedDrops);
    }

    [Fact]
    public void RealTimeSource_SustainedlyBehind_Alarms()
    {
        // Lo que se midió con FFmpeg (lavfi 1080p60 en tiempo real + x265): speed 0,55x y CERO descartes. Solo el atraso lo delata.
        var detector = NewDetector(realTime: true);
        bool alarm = false;
        int samples = 0;
        for (int i = 1; i <= 20 && !alarm; i++)
        {
            samples = i;
            alarm = detector.Update(At(i, i * 33, 0, mediaSeconds: i * 0.55), NoConversion);
        }
        Assert.True(alarm, "un proceso al 55 % del tiempo real con una fuente a ritmo constante no da abasto");
        Assert.InRange(samples, 9, 12); // 1 de referencia + 8 de ventana + la racha de 3, con margen
        Assert.Equal(SaturationSymptom.BehindRealTime, detector.LastSymptom);
        Assert.Equal(0.55, detector.Speed!.Value, 2);
    }

    [Fact]
    public void RealTimeSource_WithConversion_AtFullSpeed_DoesNotAlarm()
    {
        // La conversión no frena el tiempo de medios: de 30 a 25 a velocidad 1x no hay atraso que valga.
        var detector = NewDetector(realTime: true);
        for (int i = 1; i <= 60; i++)
            Assert.False(detector.Update(At(i, i * 30, ConversionDrops(i * 30), mediaSeconds: i), ThirtyToTwentyFive));
    }

    [Fact]
    public void ShortDip_DoesNotAlarm()
    {
        // Dos segundos al 60 % (un pico ajeno de CPU o disco) y vuelta al día: no es saturación sostenida.
        var detector = NewDetector(realTime: true);
        double media = 0;
        for (int i = 1; i <= 40; i++)
        {
            media += i is 20 or 21 ? 0.6 : i is 22 or 23 ? 1.2 : 1.0;
            Assert.False(detector.Update(At(i, i * 30, 0, media), NoConversion));
        }
    }

    [Fact]
    public void NonRealTimeSource_Behind_IsNotJudged()
    {
        // Una entrada de red llega a trompicones por su lado: que el proceso «vaya lento» no dice nada del equipo.
        var detector = NewDetector(realTime: false);
        for (int i = 1; i <= 40; i++)
            Assert.False(detector.Update(At(i, i * 15, 0, mediaSeconds: i * 0.5), NoConversion));
        Assert.Null(detector.Speed);
    }

    [Fact]
    public void FrozenSamples_AreNotSlowness()
    {
        // La fuente calla (relé reabriéndose, disco que no acepta escrituras): el tiempo de medios no avanza. Eso no es que el
        // equipo no dé abasto —tiene sus propias alarmas— y no debe encender esta.
        var detector = NewDetector(realTime: true);
        for (int i = 1; i <= 10; i++) detector.Update(At(i, i * 30, 0, mediaSeconds: i), NoConversion);
        for (int i = 11; i <= 50; i++) Assert.False(detector.Update(At(i, 300, 0, mediaSeconds: 10), NoConversion));
        // Y cuando vuelve, al día, sigue apagada.
        for (int i = 51; i <= 60; i++) Assert.False(detector.Update(At(i, 300 + (i - 50) * 30, 0, mediaSeconds: 10 + i - 50), NoConversion));
    }

    [Fact]
    public void Saturation_ClearsWhenTheProcessCatchesUp()
    {
        var detector = NewDetector(realTime: true);
        double media = 0;
        int i = 0;
        while (!detector.Active && i < 30) { i++; media += 0.5; detector.Update(At(i, i * 30, 0, media), NoConversion); }
        Assert.True(detector.Active);
        int cleared = 0;
        while (detector.Active && cleared < 30) { i++; cleared++; media += 1.0; detector.Update(At(i, i * 30, 0, media), NoConversion); }
        Assert.False(detector.Active, "al volver a ir al día la alarma se apaga");
        Assert.InRange(cleared, 5, 15);
    }

    [Fact]
    public void NewProcess_RestartsTheBaseline_AndKeepsWhatWasLost()
    {
        // Pieza nueva tras una caída (o carta de ajuste): los contadores y el tiempo de medios empiezan de cero. No es un salto
        // de descartes ni de velocidad, y lo ya perdido en la sesión se conserva.
        var detector = NewDetector(realTime: true);
        detector.Update(At(1, 30, 0, 1), NoConversion);
        detector.Update(At(2, 60, 4, 2), NoConversion);
        Assert.Equal(4, detector.UnexplainedDrops);
        for (int i = 3; i <= 12; i++) detector.Update(At(i, i * 30, 4, i), NoConversion);
        // Proceso nuevo: 25 frames, 0 descartes, 0,9 s de medios.
        Assert.False(detector.Update(At(13, 25, 0, 0.9), NoConversion));
        for (int i = 14; i <= 30; i++) Assert.False(detector.Update(At(i, 25 + (i - 13) * 30, 0, 0.9 + (i - 13)), NoConversion));
        Assert.Equal(4, detector.UnexplainedDrops);
    }

    [Fact]
    public void MediaTimeNotYetKnown_IsIgnored()
    {
        var detector = NewDetector(realTime: true);
        for (int i = 1; i <= 20; i++) Assert.False(detector.Update(At(i, i * 30, 0, mediaSeconds: -1), NoConversion));
        Assert.Null(detector.Speed);
    }

    [Fact]
    public void BlocksReadTogether_AreMeasuredWithTheNextSample()
    {
        // Dos bloques de progreso leídos de golpe (el lector se atrasó): el segundo, a 50 ms del primero, daría una velocidad
        // absurda (0,55 s de medios en 50 ms: 11x). Se mide junto con el siguiente.
        var detector = NewDetector(realTime: true);
        detector.Update(At(1, 30, 0, 1), NoConversion);
        detector.Update(At(2.95, 88, 0, 2.95), NoConversion);
        detector.Update(At(3.0, 90, 0, 3.5), NoConversion); // a 50 ms del anterior
        detector.Update(At(4.0, 120, 0, 4.0), NoConversion);
        Assert.Equal(1.0, detector.Speed!.Value, 3);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var detector = NewDetector();
        detector.Update(At(1, 30, 0), NoConversion);
        detector.Update(At(2, 60, 3), NoConversion);
        detector.Update(At(3, 90, 6), NoConversion);
        detector.Update(At(4, 120, 9), NoConversion);
        Assert.True(detector.Active);
        detector.Reset(realTimeSource: false);
        Assert.False(detector.Active);
        Assert.Equal(0, detector.UnexplainedDrops);
        Assert.False(detector.Update(At(5, 150, 100), NoConversion)); // la primera muestra vuelve a ser línea base
    }
}
