using System.Diagnostics;
using Baioss.Record.Infrastructure.Preview;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Colchón de preview: sin colchón es paso directo (el anillo de 3 de siempre); con colchón, una llegada a ráfagas sale a
/// cadencia constante, un hueco mantiene el último frame y se reanuda sin volver a llenar, una ráfaga mayor que el colchón
/// se acota descartando lo más viejo, y una fuente algo más lenta de lo declarado se corrige con repeticiones, no con
/// paradas. Mide con reloj real (decenas de ms): las tolerancias contemplan el jitter del planificador.
/// </summary>
public class PreviewPacerTests
{
    private const int FrameBytes = 64;

    private sealed class Sink
    {
        public readonly List<(double At, byte[] Buffer, int Thread)> Delivered = new();
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        public void Deliver(byte[] b) { lock (Delivered) Delivered.Add((_sw.Elapsed.TotalSeconds, b, Environment.CurrentManagedThreadId)); }
        public double Now => _sw.Elapsed.TotalSeconds;
        public (double At, byte[] Buffer, int Thread)[] Snapshot() { lock (Delivered) return Delivered.ToArray(); }
    }

    private static byte[] Arrive(PreviewPacer pacer)
    {
        var b = pacer.Rent();
        pacer.Enqueue(b);
        return b;
    }

    private static double MaxGap(IReadOnlyList<(double At, byte[] Buffer, int Thread)> d, double from, double to)
    {
        double max = 0;
        for (int i = 1; i < d.Count; i++)
            if (d[i].At >= from && d[i].At <= to) max = Math.Max(max, d[i].At - d[i - 1].At);
        return max;
    }

    [Fact]
    public void WithoutABuffer_ItIsAPassThrough_OnTheCallerThread_ThatReusesBuffersLikeTheOldRing()
    {
        var sink = new Sink();
        using var pacer = new PreviewPacer(FrameBytes, sink.Deliver, "t");
        pacer.Configure(TimeSpan.Zero, 30);

        var rented = new List<byte[]>();
        for (int i = 0; i < 12; i++) rented.Add(Arrive(pacer));

        var d = sink.Snapshot();
        Assert.Equal(12, d.Length);
        Assert.All(d, x => Assert.Equal(Environment.CurrentManagedThreadId, x.Thread)); // síncrono, en el hilo del lector
        // Un búfer recién entregado no se vuelve a prestar hasta 2 entregas después (la UI puede estar copiándolo): el
        // anillo de 3 de siempre (uno llenándose + dos entregados).
        for (int i = 0; i < rented.Count; i++)
            for (int j = i + 1; j < Math.Min(rented.Count, i + 3); j++)
                Assert.NotSame(rented[i], rented[j]);
        Assert.True(rented.Distinct().Count() <= 3, "el paso directo no debe alojar más de 3 búferes (2 en cuarentena + 1 en vuelo)");
        Assert.Equal(0, pacer.QueuedFrames);
    }

    [Fact]
    public async Task WithABuffer_ABurstyArrival_IsDeliveredAtTheNominalCadence()
    {
        var sink = new Sink();
        using var pacer = new PreviewPacer(FrameBytes, sink.Deliver, "t");
        pacer.Configure(TimeSpan.FromMilliseconds(1000), 30);

        // 30 fps de contenido que llega en ráfagas: 18 frames (600 ms) de golpe cada 600 ms, durante 4 s.
        for (int burst = 0; burst < 7; burst++)
        {
            for (int i = 0; i < 18; i++) Arrive(pacer);
            await Task.Delay(600);
        }
        await Task.Delay(300);

        var d = sink.Snapshot();
        // Ventana de régimen: del segundo 1,5 al 3,5 (llenado inicial ya hecho, ráfagas aún llegando).
        int inWindow = d.Count(x => x.At is >= 1.5 and <= 3.5);
        Assert.InRange(inWindow, 50, 70);                                 // ~30 fps × 2 s (±15 %)
        Assert.True(MaxGap(d, 1.5, 3.5) < 0.09, $"hueco máximo entre entregas {MaxGap(d, 1.5, 3.5):0.000} s: la ráfaga se coló");
        Assert.Equal(0, pacer.Dropped);
        Assert.All(d, x => Assert.NotEqual(Environment.CurrentManagedThreadId, x.Thread)); // en el hilo del colchón
    }

    [Fact]
    public async Task AGapInTheArrival_HoldsTheLastFrame_AndResumesAtOnce_WithoutRefilling()
    {
        var sink = new Sink();
        using var pacer = new PreviewPacer(FrameBytes, sink.Deliver, "t");
        pacer.Configure(TimeSpan.FromMilliseconds(500), 30);

        // 2 s a 30 fps regulares, un hueco de 1,5 s, y la ráfaga de recuperación (40 frames) seguida de flujo regular.
        for (int i = 0; i < 60; i++) { Arrive(pacer); await Task.Delay(33); }
        double gapStart = sink.Now;
        await Task.Delay(1500);
        double burstAt = sink.Now;
        for (int i = 0; i < 40; i++) Arrive(pacer);
        for (int i = 0; i < 30; i++) { Arrive(pacer); await Task.Delay(33); }
        await Task.Delay(200);

        var d = sink.Snapshot();
        // Durante el hueco el colchón (0,5 s) siguió entregando y luego se agotó: hubo repeticiones, no entregas.
        Assert.True(pacer.Repeated > 10, $"repetidos {pacer.Repeated}: el hueco no se notó en el colchón");
        Assert.DoesNotContain(d, x => x.At > gapStart + 0.8 && x.At < burstAt); // cola vacía: nada que entregar
        // Al volver el flujo se reanuda de inmediato (sin esperar a rellenar 0,5 s).
        var first = d.First(x => x.At >= burstAt);
        Assert.True(first.At - burstAt < 0.15, $"tardó {first.At - burstAt:0.000} s en reanudar tras la ráfaga");
        // La ráfaga de recuperación cupo en la cola (tope = 2×15 + 15 = 45) y se reproduce a cadencia, no de golpe: como
        // mucho un 25 % más deprisa (un descarte cada 5 entregas mientras la cola supere el doble del objetivo), que es
        // como el retardo vuelve al objetivo en vez de quedarse 1,3 s más atrás para siempre.
        int afterBurst = d.Count(x => x.At >= burstAt + 0.3 && x.At < burstAt + 1.3);
        Assert.True(afterBurst is >= 25 and <= 40, $"tras la ráfaga se entregaron {afterBurst} frames en 1 s (esperados ~30, ≤ 37)");
        Assert.InRange(pacer.Dropped, 0, 40 - pacer.TargetFrames);
    }

    [Fact]
    public void ABurstLargerThanTheBuffer_IsBounded_ByDroppingTheOldest()
    {
        var sink = new Sink();
        using var pacer = new PreviewPacer(FrameBytes, sink.Deliver, "t");
        pacer.Configure(TimeSpan.FromMilliseconds(500), 30);
        Assert.Equal(15, pacer.TargetFrames);
        Assert.Equal(45, pacer.MaxFrames);

        for (int i = 0; i < 200; i++) Arrive(pacer);

        Assert.True(pacer.QueuedFrames <= pacer.MaxFrames, $"cola {pacer.QueuedFrames} > tope {pacer.MaxFrames}");
        Assert.True(pacer.Dropped >= 200 - pacer.MaxFrames - 5, $"descartados {pacer.Dropped}");
    }

    [Fact]
    public async Task ASourceSlowerThanDeclared_IsCorrectedWithRepeats_NotWithStalls()
    {
        var sink = new Sink();
        using var pacer = new PreviewPacer(FrameBytes, sink.Deliver, "t");
        pacer.Configure(TimeSpan.FromMilliseconds(500), 30);

        // Declarada a 30, entrega ~26 fps regulares (pierde frames, como un servidor que descarta): 4 s.
        for (int i = 0; i < 104; i++) { Arrive(pacer); await Task.Delay(38); }
        await Task.Delay(100);

        var d = sink.Snapshot();
        // Nunca se queda sin frames: ningún hueco de entrega mayor que 3 intervalos (correcciones de 1 frame, no paradas).
        Assert.True(MaxGap(d, 1.0, 4.0) < 0.11, $"hueco máximo {MaxGap(d, 1.0, 4.0):0.000} s");
        Assert.Equal(0, pacer.Dropped);
        Assert.True(pacer.Repeated > 0, "una fuente más lenta se corrige repitiendo algún frame");
        Assert.InRange(pacer.DeliveryFps, 29.9, 30.1); // la cadencia es la declarada; la diferencia la absorben las repeticiones
    }

    [Fact]
    public async Task ReconfiguringToZero_FlushesTheQueue_AndGoesBackToDirectDelivery()
    {
        var sink = new Sink();
        using var pacer = new PreviewPacer(FrameBytes, sink.Deliver, "t");
        pacer.Configure(TimeSpan.FromMilliseconds(2000), 30);
        for (int i = 0; i < 10; i++) Arrive(pacer);        // llenando: nada entregado aún (objetivo 60)
        await Task.Delay(100);
        Assert.Empty(sink.Snapshot());

        pacer.Configure(TimeSpan.Zero, 30);
        Assert.Equal(0, pacer.QueuedFrames);
        Arrive(pacer);
        Assert.Single(sink.Snapshot());                     // directo otra vez
    }

    [Fact]
    public async Task WithABuffer_DeliveredBuffers_AreNotRentedAgain_UntilTwoMoreDeliveries()
    {
        var sink = new Sink();
        using var pacer = new PreviewPacer(FrameBytes, sink.Deliver, "t");
        pacer.Configure(TimeSpan.FromMilliseconds(200), 30);
        for (int i = 0; i < 60; i++) { Arrive(pacer); await Task.Delay(33); }
        await Task.Delay(300);

        var d = sink.Snapshot();
        Assert.True(d.Length > 40);
        for (int i = 0; i < d.Length; i++)
            for (int j = i + 1; j < Math.Min(d.Length, i + 3); j++)
                Assert.NotSame(d[i].Buffer, d[j].Buffer);
    }
}
