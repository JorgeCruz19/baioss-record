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

    [Fact]
    public void NegativeReading_ResetsClock_NotStalled()
    {
        var t = new FileGrowthTracker(T0);
        Assert.False(t.IsStalled(5_000_000, T0.AddSeconds(2), Timeout));  // base a los 2 s
        Assert.False(t.IsStalled(-1, T0.AddSeconds(40), Timeout));        // pausa: reinicia el reloj a 40 s
        Assert.False(t.IsStalled(5_000_000, T0.AddSeconds(60), Timeout)); // 60-40 = 20 s < 30 s → NO estancado
    }
}
