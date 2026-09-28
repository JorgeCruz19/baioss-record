using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Messaging;
using Baioss.Record.Infrastructure.Persistence;
using Baioss.Record.Infrastructure.Storage;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Pruebas de la auditoría de estabilidad (2026-09): cada una fija un fallo confirmado en el código para que no vuelva.
/// Supervisor de FFmpeg, sonda y guarda de disco, vigilancia de frames de la captura, registro de secretos, validación de
/// entradas de red y poda de la auditoría.
/// </summary>
public class StabilityAuditTests
{
    private static string Cmd => Environment.GetEnvironmentVariable("ComSpec") ?? @"C:\Windows\System32\cmd.exe";

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Esperando: " + what);
            await Task.Delay(25);
        }
    }

    // --- Supervisor ---

    [Fact]
    public async Task CancellingTheCallersToken_NoLongerStopsTheProcess()
    {
        // Antes el token de quien arrancaba (una petición HTTP, el programador) quedaba enlazado a la vida del proceso: un
        // cliente que abortaba la petición de «grabar» cerraba la grabación recién abierta.
        await using var sup = new FfmpegProcessSupervisor(Cmd, NullLogger.Instance) { FinalizeOnStop = false, RestartInternally = false };
        int completed = 0;
        sup.Completed += (_, _) => Interlocked.Increment(ref completed);
        using var cts = new CancellationTokenSource();
        await sup.StartAsync(new[] { "/c", "ping -n 6 127.0.0.1 > NUL" }, cts.Token);
        await WaitUntilAsync(() => sup.IsRunning, TimeSpan.FromSeconds(5), "proceso en marcha");
        cts.Cancel();
        await Task.Delay(1000);
        Assert.True(sup.IsRunning);
        Assert.Equal(0, Volatile.Read(ref completed));
    }

    [Fact]
    public async Task ACleanEndOfStreamAfterRunningAWhile_IsRelaunchedAtOnce_WithoutGrowingBackoff()
    {
        // Un emisor que cierra tras emitir (código 0) no es una caída: antes el backoff crecía 1, 2, 4… hasta 30 s con el
        // puerto de escucha CERRADO mientras la interfaz decía «esperando al emisor».
        await using var sup = new FfmpegProcessSupervisor(Cmd, NullLogger.Instance)
        {
            RestartOnCleanExit = true, FinalizeOnStop = false,
            CleanExitFastRestartAfter = TimeSpan.FromMilliseconds(500), CleanExitRestartDelay = TimeSpan.FromMilliseconds(50),
        };
        var restarts = new ConcurrentQueue<int>();
        sup.Restarted += (_, n) => restarts.Enqueue(n);
        await sup.StartAsync(new[] { "/c", "ping -n 2 127.0.0.1 > NUL" }); // ≈ 1 s y sale con 0
        await WaitUntilAsync(() => restarts.Count >= 3, TimeSpan.FromSeconds(15), "tres relanzamientos");
        Assert.All(restarts, n => Assert.Equal(1, n)); // no cuenta como reintento: el backoff no crece
    }

    [Fact]
    public async Task AbortCurrentProcess_IsTreatedAsACrash_AndTheProcessIsRelaunched()
    {
        await using var sup = new FfmpegProcessSupervisor(Cmd, NullLogger.Instance) { FinalizeOnStop = false };
        var restarted = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        sup.Restarted += (_, n) => restarted.TrySetResult(n);
        await sup.StartAsync(new[] { "/c", "ping -n 30 127.0.0.1 > NUL" });
        await WaitUntilAsync(() => sup.IsRunning, TimeSpan.FromSeconds(5), "proceso en marcha");
        Assert.True(sup.AbortCurrentProcess());
        Assert.Same(restarted.Task, await Task.WhenAny(restarted.Task, Task.Delay(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task ASubscriberThatThrows_DoesNotKillTheSupervision()
    {
        await using var sup = new FfmpegProcessSupervisor(Cmd, NullLogger.Instance)
            { FinalizeOnStop = false, RestartOnCleanExit = true, CleanExitFastRestartAfter = TimeSpan.Zero, CleanExitRestartDelay = TimeSpan.FromMilliseconds(50) };
        int restarts = 0;
        sup.Restarted += (_, _) => { Interlocked.Increment(ref restarts); throw new InvalidOperationException("suscriptor roto"); };
        await sup.StartAsync(new[] { "/c", "exit 0" });
        await WaitUntilAsync(() => Volatile.Read(ref restarts) >= 3, TimeSpan.FromSeconds(10), "sigue relanzando");
    }

    // --- Sonda del disco ---

    [Fact]
    public async Task ConcurrentVolumeProbesOnTheSameFolder_AllSeeTheDiskResponding()
    {
        // Antes cada sonda abría el MISMO archivo con FileShare.None: dos canales grabando en la misma carpeta se hacían
        // fallar entre sí («el disco no responde» falso). Ahora hay una sola escritura en vuelo por carpeta.
        var dir = Path.Combine(Path.GetTempPath(), $"baioss-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => VolumeProbe.IsResponsiveAsync(dir, TimeSpan.FromSeconds(5))));
            Assert.All(results, Assert.True);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // --- Vigilancia de frames de la captura (tarjeta muda sin decir «sin señal») ---

    [Fact]
    public void FrameFlowWatch_ReopensOnlyAfterFramesFlowedAndThenStopped()
    {
        var watch = new FrameFlowWatch();
        var t0 = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(30);
        Assert.False(watch.ShouldReopen(1, 0, judgeable: true, t0, timeout));             // conexión nueva, sin frames
        Assert.False(watch.ShouldReopen(1, 0, true, t0.AddSeconds(60), timeout));         // aún no fluyó: no se juzga
        Assert.False(watch.ShouldReopen(1, 50, true, t0.AddSeconds(61), timeout));        // fluye
        Assert.False(watch.ShouldReopen(1, 50, true, t0.AddSeconds(80), timeout));        // parado 19 s
        Assert.True(watch.ShouldReopen(1, 50, true, t0.AddSeconds(92), timeout));         // parado 31 s → reabrir
        Assert.False(watch.ShouldReopen(1, 50, true, t0.AddSeconds(200), timeout));       // una vez por episodio
    }

    [Fact]
    public void FrameFlowWatch_ABurstAlreadyReceivedWhenFirstSeen_CountsAsFlowing()
    {
        // La prueba con FFmpeg lo destapó: si todo lo que llegó en la conexión llegó antes de la primera observación, esa
        // conexión SÍ entregó frames; antes la línea base se los tragaba y la tarjeta muda no se detectaba nunca.
        var watch = new FrameFlowWatch();
        var t0 = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(3);
        Assert.False(watch.ShouldReopen(7, 75, true, t0, timeout));
        Assert.True(watch.ShouldReopen(7, 75, true, t0.AddSeconds(4), timeout));
    }

    [Fact]
    public void FrameFlowWatch_DoesNotJudgeWhenNotJudgeable_AndANewConnectionStartsOver()
    {
        var watch = new FrameFlowWatch();
        var t0 = DateTimeOffset.UtcNow;
        var timeout = TimeSpan.FromSeconds(30);
        watch.ShouldReopen(3, 10, true, t0, timeout);
        watch.ShouldReopen(3, 20, true, t0.AddSeconds(1), timeout);                        // fluyó
        Assert.False(watch.ShouldReopen(3, 20, judgeable: false, t0.AddSeconds(100), timeout)); // sin señal / reabriendo
        Assert.False(watch.ShouldReopen(3, 20, true, t0.AddSeconds(120), timeout));        // el reloj se reinició
        Assert.False(watch.ShouldReopen(4, 0, true, t0.AddSeconds(400), timeout));         // conexión nueva: de cero
        Assert.False(watch.ShouldReopen(4, 0, true, t0.AddSeconds(500), timeout));         // sin frames en ella: no insiste
    }


    // --- Secretos fuera del registro ---

    [Fact]
    public void TheSrtPassphraseAndStreamId_NeverReachTheLog()
    {
        var input = new NetworkInput
        {
            Protocol = NetworkProtocol.Srt, Role = NetworkRole.Connect, Host = "10.0.0.7", Port = 9000,
            Passphrase = "clave-muy-secreta-99", StreamId = "token=abc123",
        };
        var args = NetworkStreamReceiver.ArgumentsFor(input, 5000);
        Assert.Contains("clave-muy-secreta-99", args); // FFmpeg sí la necesita
        var logged = string.Join(' ', NetworkStreamReceiver.Redacted(args));
        Assert.DoesNotContain("clave-muy-secreta-99", logged);
        Assert.DoesNotContain("token=abc123", logged);
        Assert.Contains("-passphrase ***", logged);
        Assert.Contains("-srt_streamid ***", logged);
    }

    [Fact]
    public void AListenerWithoutHost_IsStillValidatedEntirely()
    {
        // Antes, sin host y en escucha, Validate() devolvía «válida» en el acto y se saltaba el resto de comprobaciones.
        Assert.Equal(NetworkInputError.InvalidPort,
            new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 70000 }.Validate());
        Assert.Equal(NetworkInputError.PassphraseLength,
            new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 9000, Passphrase = "corta" }.Validate());
        Assert.Equal(NetworkInputError.None,
            new NetworkInput { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = 9000 }.Validate());
    }

    // --- Guarda de disco ---

    private static string UnusedDriveRoot()
    {
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        for (char c = 'Z'; c >= 'G'; c--) if (!used.Contains(c)) return $"{c}:\baioss-no-existe";
        throw new InvalidOperationException("No hay letras de unidad libres para la prueba.");
    }

    [Fact]
    public async Task AVolumeThatCannotBeMeasured_NeverStopsTheRecordingAsDiskFull()
    {
        // Antes, (0, 0) de una medida fallida (NAS con un corte, USB reenumerándose) se evaluaba como «0 bytes libres» y
        // DETENÍA la grabación por disco lleno con el disco vacío.
        await using var guard = new DiskSpaceGuard(NullLogger.Instance) { PollInterval = TimeSpan.FromMilliseconds(30) };
        int updates = 0, autoStops = 0;
        guard.Updated += (_, e) => { Interlocked.Increment(ref updates); if (e.AutoStop) Interlocked.Increment(ref autoStops); };
        string dir = UnusedDriveRoot();
        guard.Start(() => dir, () => 1_000_000);
        await Task.Delay(400);
        await guard.StopAsync();
        Assert.Equal(0, Volatile.Read(ref autoStops));
        Assert.Equal(0, Volatile.Read(ref updates));
    }

    [Fact]
    public async Task AutoStop_NeedsTwoConsecutiveCriticalReadings()
    {
        var dir = Path.GetTempPath();
        await using var guard = new DiskSpaceGuard(NullLogger.Instance) { PollInterval = TimeSpan.FromMilliseconds(30) };
        var seen = new ConcurrentQueue<bool>();
        guard.Updated += (_, e) => seen.Enqueue(e.AutoStop);
        guard.Start(() => dir, () => 1_000_000, () => long.MaxValue); // piso imposible: siempre «crítico»
        await WaitUntilAsync(() => seen.Count >= 3, TimeSpan.FromSeconds(5), "tres lecturas");
        await guard.StopAsync();
        var readings = seen.ToArray();
        Assert.False(readings[0]); // una lectura suelta no para una grabación
        Assert.True(readings[1]);
    }

    // --- Poda de la auditoría ---

    [Fact]
    public async Task TheAuditLogIsPrunedInBatches_KeepingRecentEntries()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"baioss-prune-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection().AddBaiossInfrastructure(dbPath).BuildServiceProvider();
        try
        {
            services.EnsureBaiossDatabaseCreated();
            var factory = services.GetRequiredService<IDbContextFactory<BaiossDbContext>>();
            var cutoff = DateTimeOffset.UtcNow - TimeSpan.FromDays(30);
            await using (var db = await factory.CreateDbContextAsync())
            {
                for (int i = 0; i < 12; i++)
                    db.EventLog.Add(new EventLogEntry { Category = "vieja", Message = $"v{i}", Timestamp = cutoff - TimeSpan.FromHours(i + 1) });
                for (int i = 0; i < 3; i++)
                    db.EventLog.Add(new EventLogEntry { Category = "nueva", Message = $"n{i}", Timestamp = cutoff + TimeSpan.FromHours(i + 1) });
                await db.SaveChangesAsync();
            }
            int removed = await EventLogWriter.PruneAsync(factory, cutoff, batchSize: 5, TimeSpan.FromMilliseconds(1), CancellationToken.None);
            Assert.Equal(12, removed);
            await using (var db = await factory.CreateDbContextAsync())
                Assert.Equal(3, await db.EventLog.CountAsync());
        }
        finally
        {
            await services.DisposeAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" }) try { File.Delete(f); } catch { /* best effort */ }
        }
    }
}
