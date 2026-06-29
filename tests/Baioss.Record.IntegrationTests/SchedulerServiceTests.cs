using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Infrastructure.Scheduling;
using Baioss.Record.IntegrationTests.Fakes;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Prueba la LÓGICA DE DISPARO del scheduler (la parte con estado, más allá del cálculo puro de
/// ocurrencias de <c>ScheduleEvaluator</c>): arranque a la hora, auto-stop por duración, reanudación
/// tras reinicio y no interferir con una grabación manual. Usa un reloj controlable y dobles en memoria.
/// </summary>
public class SchedulerServiceTests
{
    private static readonly TimeSpan Off = TimeSpan.FromHours(-6);
    private static DateTimeOffset At(int h, int mi) => new(2026, 6, 20, h, mi, 0, Off);

    private static ScheduledJob OnceJob(Guid channelId, int durationMin, DateTimeOffset? lastRun = null) => new()
    {
        ChannelId = channelId,
        Action = ScheduledAction.StartRecording,
        RunAt = At(20, 0),
        Recurrence = RecurrenceKind.Once,
        Duration = TimeSpan.FromMinutes(durationMin),
        LastRunAt = lastRun,
    };

    [Fact]
    public async Task Tick_StartsDueRecording_ThenAutoStopsAfterDuration()
    {
        var channelId = Guid.NewGuid();
        var engine = new FakeChannelEngine(channelId, "A");
        var clock = new MutableClock { UtcNow = At(20, 0) };
        var repo = new InMemoryScheduledJobRepository();
        await repo.AddAsync(OnceJob(channelId, durationMin: 30));
        var svc = new SchedulerService(repo, new FakeChannelManager(engine), clock, NullLogger<SchedulerService>.Instance);

        await svc.TickAsync(default);                 // justo a la hora → arranca
        Assert.Equal(1, engine.StartCount);
        Assert.Equal(0, engine.StopCount);

        clock.UtcNow = At(20, 15);                    // a mitad → ni re-arranca ni para
        await svc.TickAsync(default);
        Assert.Equal(1, engine.StartCount);
        Assert.Equal(0, engine.StopCount);

        clock.UtcNow = At(20, 31);                    // pasada la duración → auto-stop
        await svc.TickAsync(default);
        Assert.Equal(1, engine.StartCount);
        Assert.Equal(1, engine.StopCount);
    }

    [Fact]
    public async Task Tick_DoesNotInterfereWithManualRecording()
    {
        var channelId = Guid.NewGuid();
        var engine = new FakeChannelEngine(channelId, "A");
        await engine.StartRecordingAsync(Guid.Empty, "manual"); // ya grabando manualmente
        Assert.Equal(1, engine.StartCount);

        var clock = new MutableClock { UtcNow = At(20, 0) };
        var repo = new InMemoryScheduledJobRepository();
        await repo.AddAsync(OnceJob(channelId, durationMin: 30));
        var svc = new SchedulerService(repo, new FakeChannelManager(engine), clock, NullLogger<SchedulerService>.Instance);

        await svc.TickAsync(default);
        // No arranca una segunda grabación ni detiene la manual.
        Assert.Equal(1, engine.StartCount);
        Assert.Equal(0, engine.StopCount);
    }

    [Fact]
    public async Task Tick_ResumesScheduledRecordingAfterRestart_WithinWindow()
    {
        // La franja ya se había disparado (LastRunAt puesto) pero el estado en memoria se perdió tras un
        // reinicio; dentro de la ventana de grabación debe REANUDAR.
        var channelId = Guid.NewGuid();
        var engine = new FakeChannelEngine(channelId, "A");
        var clock = new MutableClock { UtcNow = At(20, 10) };
        var repo = new InMemoryScheduledJobRepository();
        await repo.AddAsync(OnceJob(channelId, durationMin: 30, lastRun: At(20, 0)));
        var svc = new SchedulerService(repo, new FakeChannelManager(engine), clock, NullLogger<SchedulerService>.Instance);

        await svc.TickAsync(default);
        Assert.Equal(1, engine.StartCount); // reanuda dentro de la ventana

        clock.UtcNow = At(20, 31);          // y respeta el fin programado
        await svc.TickAsync(default);
        Assert.Equal(1, engine.StopCount);
    }

    [Fact]
    public async Task Tick_AppliesJobSegmentation_AndClearsAfterStop()
    {
        var channelId = Guid.NewGuid();
        var engine = new FakeChannelEngine(channelId, "A");
        var clock = new MutableClock { UtcNow = At(20, 0) };
        var repo = new InMemoryScheduledJobRepository();
        var job = OnceJob(channelId, durationMin: 30);
        job.SegmentMinutes = 10;                       // la segmentación es propia de la programación
        await repo.AddAsync(job);
        var svc = new SchedulerService(repo, new FakeChannelManager(engine), clock, NullLogger<SchedulerService>.Instance);

        await svc.TickAsync(default);
        Assert.NotNull(engine.Profile.Segmentation);   // aplicada al perfil del canal al arrancar
        Assert.Equal(TimeSpan.FromMinutes(10), engine.Profile.Segmentation!.Duration);

        clock.UtcNow = At(20, 31);
        await svc.TickAsync(default);
        Assert.Null(engine.Profile.Segmentation);      // retirada al terminar (no la hereda una grabación manual)
    }

    [Fact]
    public async Task Skip_StopsCurrentOccurrence_DoesNotResume_ButNextDayRuns()
    {
        var channelId = Guid.NewGuid();
        var engine = new FakeChannelEngine(channelId, "A");
        var clock = new MutableClock { UtcNow = At(20, 0) };
        var repo = new InMemoryScheduledJobRepository();
        var job = new ScheduledJob
        {
            ChannelId = channelId, Action = ScheduledAction.StartRecording,
            RunAt = At(20, 0), Recurrence = RecurrenceKind.Daily, Duration = TimeSpan.FromMinutes(30),
        };
        await repo.AddAsync(job);
        var svc = new SchedulerService(repo, new FakeChannelManager(engine), clock, NullLogger<SchedulerService>.Instance);

        await svc.TickAsync(default);                       // arranca la grabación de hoy
        Assert.Equal(1, engine.StartCount);
        Assert.Contains(channelId, svc.ActiveScheduledChannels);

        await svc.SkipCurrentAsync(channelId);              // el operador la salta
        Assert.Equal(1, engine.StopCount);                 // se detiene
        Assert.DoesNotContain(channelId, svc.ActiveScheduledChannels);

        clock.UtcNow = At(20, 10);                          // dentro de la misma ventana
        await svc.TickAsync(default);
        Assert.Equal(1, engine.StartCount);                // NO se reanuda

        clock.UtcNow = At(20, 0).AddDays(1);               // al día siguiente
        await svc.TickAsync(default);
        Assert.Equal(2, engine.StartCount);                // la siguiente ocurrencia SÍ se ejecuta
    }

    [Fact]
    public async Task Tick_PassesScheduledFileName_DateUnderscoreTitle()
    {
        var channelId = Guid.NewGuid();
        var engine = new FakeChannelEngine(channelId, "A");
        var clock = new MutableClock { UtcNow = At(20, 0) };
        var repo = new InMemoryScheduledJobRepository();
        var job = OnceJob(channelId, durationMin: 30);
        job.Title = "Noticias";                            // el nombre del archivo = dd-MM-yyyy_Título
        await repo.AddAsync(job);
        var svc = new SchedulerService(repo, new FakeChannelManager(engine), clock, NullLogger<SchedulerService>.Instance);

        await svc.TickAsync(default);
        Assert.Equal("20-06-2026_Noticias", engine.LastRecordingName); // la fecha es la de la ocurrencia
    }

    [Fact]
    public async Task Tick_DoesNotStopManualRecording_StartedAfterScheduledWasStopped()
    {
        // Escenario #20: el scheduler arranca la programada → el operador la detiene y arranca una grabación
        // MANUAL en el mismo canal → al vencer la duración de la programada, el auto-stop NO debe cortar la manual.
        var channelId = Guid.NewGuid();
        var engine = new FakeChannelEngine(channelId, "A");
        var clock = new MutableClock { UtcNow = At(20, 0) };
        var repo = new InMemoryScheduledJobRepository();
        await repo.AddAsync(OnceJob(channelId, durationMin: 30));
        var svc = new SchedulerService(repo, new FakeChannelManager(engine), clock, NullLogger<SchedulerService>.Instance);

        await svc.TickAsync(default);                       // 20:00 → arranca la programada (sesión #1)
        Assert.Equal(1, engine.StartCount);

        await engine.StopRecordingAsync();                 // el operador la detiene
        await engine.StartRecordingAsync(Guid.Empty, "manual", "Mi Grabación"); // y arranca una manual (sesión #2)
        Assert.Equal(2, engine.StartCount);
        Assert.Equal(1, engine.StopCount);

        clock.UtcNow = At(20, 31);                          // pasada la duración de la programada
        await svc.TickAsync(default);

        // La manual sigue grabando: el auto-stop vio otra sesión y no la tocó.
        Assert.Equal(1, engine.StopCount);
        Assert.Equal(RecordingState.Recording, engine.Status.RecordingState);
    }

    [Fact]
    public async Task Tick_DoesNothing_WhenNothingDue()
    {
        var channelId = Guid.NewGuid();
        var engine = new FakeChannelEngine(channelId, "A");
        var clock = new MutableClock { UtcNow = At(8, 0) };   // mucho antes de la franja de las 20:00
        var repo = new InMemoryScheduledJobRepository();
        await repo.AddAsync(OnceJob(channelId, durationMin: 30));
        var svc = new SchedulerService(repo, new FakeChannelManager(engine), clock, NullLogger<SchedulerService>.Instance);

        await svc.TickAsync(default);
        Assert.Equal(0, engine.StartCount);
        Assert.Equal(0, engine.StopCount);
    }

    // --- Dobles en memoria ---

    private sealed class MutableClock : IClock { public DateTimeOffset UtcNow { get; set; } }

    private sealed class FakeChannelManager : IChannelManager
    {
        private readonly Dictionary<Guid, IChannelEngine> _map;
        public FakeChannelManager(params IChannelEngine[] engines) => _map = engines.ToDictionary(e => e.ChannelId, e => e);
        public IReadOnlyCollection<IChannelEngine> Channels => _map.Values;
        public IChannelEngine Get(Guid id) => _map[id];
        public bool TryGet(Guid id, out IChannelEngine? engine) { var ok = _map.TryGetValue(id, out var e); engine = e; return ok; }
    }

    private sealed class InMemoryScheduledJobRepository : IScheduledJobRepository
    {
        private readonly List<ScheduledJob> _jobs = new();
        public Task<ScheduledJob?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_jobs.FirstOrDefault(j => j.Id == id));
        public Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken ct = default) => Task.FromResult((IReadOnlyList<ScheduledJob>)_jobs.ToList());
        public Task AddAsync(ScheduledJob entity, CancellationToken ct = default) { _jobs.Add(entity); return Task.CompletedTask; }
        public Task UpdateAsync(ScheduledJob entity, CancellationToken ct = default) => Task.CompletedTask; // mutación in-place sobre la misma referencia
        public Task RemoveAsync(Guid id, CancellationToken ct = default) { _jobs.RemoveAll(j => j.Id == id); return Task.CompletedTask; }
    }
}
