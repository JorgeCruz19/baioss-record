using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// N5: si FFmpeg NO se puede lanzar (binario ausente/bloqueado, ruta mala), el supervisor debe AVISAR en vez de
/// faultar en silencio el bucle y el watchdog (dejando al canal creyendo que graba). En grabación
/// (<c>RestartInternally=false</c>) el aviso es el evento <c>Crashed</c>; el motor lo recupera en una pieza nueva.
/// </summary>
public class FfmpegProcessSupervisorTests
{
    [Fact]
    public async Task FiresCrashed_WhenFfmpegBinaryCannotLaunch()
    {
        var badPath = Path.Combine(Path.GetTempPath(), $"no-such-ffmpeg-{Guid.NewGuid():N}.exe");
        await using var sup = new FfmpegProcessSupervisor(badPath, NullLogger.Instance) { RestartInternally = false };

        var crashed = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.Crashed += (_, code) => crashed.TrySetResult(code);

        await sup.StartAsync(new[] { "-version" });

        var done = await Task.WhenAny(crashed.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(done == crashed.Task,
            "El supervisor debe emitir Crashed si FFmpeg no se puede lanzar (no faultar en silencio).");
        Assert.NotEqual(0, await crashed.Task); // código ≠ 0 → el motor reconstruirá
    }
}

/// <summary>
/// #55: detector de crecimiento del archivo. Aunque FFmpeg reporte progreso, si los bytes en disco NO crecen
/// durante el margen, la grabación está muerta (encoder colgado / escritura bloqueada) y el watchdog la reinicia
/// en una pieza nueva. Una lectura negativa (pausa / sonda no evaluable) reinicia el reloj sin marcar estancado.
/// </summary>
public class FileGrowthTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public void NotStalled_WhileFileGrows()
    {
        var t = new FileGrowthTracker(T0);
        Assert.False(t.IsStalled(0, T0, Timeout));                        // línea base
        Assert.False(t.IsStalled(1_000_000, T0.AddSeconds(10), Timeout));
        Assert.False(t.IsStalled(2_000_000, T0.AddSeconds(50), Timeout)); // creció (aunque pasaron 40 s) → sano
    }

    [Fact]
    public void Stalled_WhenBytesFlatBeyondTimeout()
    {
        var t = new FileGrowthTracker(T0);
        Assert.False(t.IsStalled(5_000_000, T0.AddSeconds(2), Timeout));  // fija la base a los 2 s
        Assert.False(t.IsStalled(5_000_000, T0.AddSeconds(20), Timeout)); // sin crecer, dentro del margen (18 s)
        Assert.True(t.IsStalled(5_000_000, T0.AddSeconds(40), Timeout));  // sin crecer 38 s > 30 s → ESTANCADO
    }

    // --- Disco colgado vs FFmpeg colgado (incidente 2026-09-06) ---

    [Fact]
    public async Task VolumeProbe_RespondsTrue_OnAWritableFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "baioss-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.True(await VolumeProbe.IsResponsiveAsync(dir, TimeSpan.FromSeconds(10)));
            // La sonda no deja rastro: el archivo se borra al cerrarse.
            Assert.False(File.Exists(Path.Combine(dir, VolumeProbe.ProbeFileName)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task VolumeProbe_RespondsFalse_WhenTheFolderCannotBeWritten()
    {
        // Carpeta inexistente / ruta inválida: no se puede escribir ahí ahora mismo. Es la misma respuesta que
        // un disco colgado, y la reacción correcta es la misma: esperar y alarmar, no matar a FFmpeg.
        Assert.False(await VolumeProbe.IsResponsiveAsync(Path.Combine(Path.GetTempPath(), "no-existe-" + Guid.NewGuid()), TimeSpan.FromSeconds(5)));
        Assert.False(await VolumeProbe.IsResponsiveAsync("", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Arbiter_WithoutProbe_KillsOnAnyStall_AsBefore()
    {
        var arbiter = new StallArbiter(null);
        Assert.Equal(StallVerdict.Healthy, await arbiter.DecideAsync(false, false));
        Assert.Equal(StallVerdict.KillProcess, await arbiter.DecideAsync(true, false));
        Assert.Equal(StallVerdict.KillProcess, await arbiter.DecideAsync(false, true));
    }

    [Fact]
    public async Task Arbiter_WaitsWhileTheDiskIsDown_AndKillsOnlyIfFfmpegStaysStuckAfterItReturns()
    {
        // Reproduce el 6/9/2026: el disco deja de responder → NO se mata; vuelve → ventana nueva; si FFmpeg
        // sigue parado con el disco ya operativo, entonces sí es FFmpeg y se mata.
        bool diskOk = true;
        var arbiter = new StallArbiter(() => Task.FromResult(diskOk));

        diskOk = false;
        Assert.Equal(StallVerdict.VolumeStalled, await arbiter.DecideAsync(true, true));   // se detecta: avisar
        Assert.True(arbiter.VolumeStalled);
        Assert.Equal(StallVerdict.StillStalled, await arbiter.DecideAsync(false, false));  // los relojes se resetean:
        Assert.Equal(StallVerdict.StillStalled, await arbiter.DecideAsync(true, false));   // …parezca sano o no, se pregunta AL DISCO
        Assert.True(arbiter.VolumeStalled);

        diskOk = true;
        Assert.Equal(StallVerdict.VolumeResumed, await arbiter.DecideAsync(false, false)); // vuelve: ventana nueva
        Assert.False(arbiter.VolumeStalled);
        Assert.Equal(StallVerdict.Healthy, await arbiter.DecideAsync(false, false));       // FFmpeg reanudó: nada
        Assert.Equal(StallVerdict.KillProcess, await arbiter.DecideAsync(true, false));    // sigue parado con disco OK: es FFmpeg
    }

    [Fact]
    public async Task Arbiter_AFailingProbe_CountsAsDiskDown_NeverKills()
    {
        // Ante la duda no se mata: una sonda que lanza se trata como «el disco no responde».
        var arbiter = new StallArbiter(() => throw new IOException("boom"));
        Assert.Equal(StallVerdict.VolumeStalled, await arbiter.DecideAsync(true, true));
        Assert.Equal(StallVerdict.StillStalled, await arbiter.DecideAsync(true, true));
    }

    [Fact]
    public void GrowthTracker_Reset_GivesAFreshWindow_AfterADiskStall()
    {
        // El disco estuvo colgado 2 min: ese tiempo NO cuenta contra FFmpeg. Tras Reset, hace falta OTRO margen
        // completo sin crecimiento para volver a considerarlo estancado.
        var t0 = new DateTimeOffset(2026, 9, 6, 11, 22, 0, TimeSpan.Zero);
        var tracker = new FileGrowthTracker(t0);
        tracker.IsStalled(1000, t0, TimeSpan.FromSeconds(30));
        Assert.True(tracker.IsStalled(1000, t0.AddMinutes(2), TimeSpan.FromSeconds(30)));   // estancado

        tracker.Reset(t0.AddMinutes(2));
        Assert.False(tracker.IsStalled(1000, t0.AddMinutes(2).AddSeconds(20), TimeSpan.FromSeconds(30))); // ventana nueva
        Assert.True(tracker.IsStalled(1000, t0.AddMinutes(2).AddSeconds(40), TimeSpan.FromSeconds(30)));  // agotada otra vez
    }

    [Fact]
    public void NegativeReading_ResetsClock_NotStalled()
    {
        var t = new FileGrowthTracker(T0);
        Assert.False(t.IsStalled(5_000_000, T0.AddSeconds(2), Timeout));  // base a los 2 s
        Assert.False(t.IsStalled(-1, T0.AddSeconds(40), Timeout));        // pausa: reinicia el reloj a 40 s
        Assert.False(t.IsStalled(5_000_000, T0.AddSeconds(60), Timeout)); // 60-40 = 20 s < 30 s → NO estancado
    }
}
