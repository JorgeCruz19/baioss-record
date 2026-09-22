using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Api;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Scheduling;
using Baioss.Record.Application.Storage;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Infrastructure;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Messaging;
using Baioss.Record.Infrastructure.Scheduling;
using Baioss.Record.Infrastructure.Storage;
using Baioss.Record.IntegrationTests.Fakes;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// La programación (tareas automáticas de cada canal) gestionada por la API, como hace el panel web: con el scheduler de
/// verdad, un repositorio en memoria y un reloj controlable. Sábado 19-09-2026, 12:00, en una zona fija UTC−6.
/// </summary>
public sealed class ScheduleApiTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.CreateCustomTimeZone("prueba-6", TimeSpan.FromHours(-6), "prueba", "prueba");
    private static DateTimeOffset At(int day, int h, int mi = 0, int s = 0) => new(2026, 9, day, h, mi, s, TimeSpan.FromHours(-6));

    private sealed class Rig : IAsyncDisposable
    {
        public required WebApplication App { get; init; }
        public required FakeChannelEngine ChannelA { get; init; }
        public required FakeChannelEngine ChannelB { get; init; }
        public required MutableClock Clock { get; init; }
        public required SchedulerService Scheduler { get; init; }
        public List<IDomainEvent> Events { get; } = new();
        public HttpClient Client => App.GetTestClient();
        public async ValueTask DisposeAsync() { await App.StopAsync(); await App.DisposeAsync(); }
    }

    private static async Task<Rig> BuildAsync()
    {
        var a = new FakeChannelEngine(Guid.NewGuid(), "A");
        var b = new FakeChannelEngine(Guid.NewGuid(), "B");
        var clock = new MutableClock { UtcNow = At(19, 12) };

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IChannelEngine>(a);
        builder.Services.AddSingleton<IChannelEngine>(b);
        builder.Services.AddSingleton<IChannelManager, ChannelManager>();
        builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
        builder.Services.AddSingleton<IStorageManager, StorageManager>();
        builder.Services.AddSingleton<IClock>(clock);
        builder.Services.AddSingleton(Zone);
        builder.Services.AddSingleton<IScheduledJobRepository, InMemoryJobs>();
        builder.Services.AddSingleton(sp => new SchedulerService(sp.GetRequiredService<IScheduledJobRepository>(),
            sp.GetRequiredService<IChannelManager>(), clock, NullLogger<SchedulerService>.Instance, sp.GetRequiredService<IEventBus>()));
        builder.Services.AddSingleton<ISchedulerService>(sp => sp.GetRequiredService<SchedulerService>());
        builder.Services.AddBaiossCqrs();

        var app = builder.Build();
        app.UseWebSockets();
        app.MapBaiossApi();
        await app.StartAsync();

        var rig = new Rig { App = app, ChannelA = a, ChannelB = b, Clock = clock, Scheduler = app.Services.GetRequiredService<SchedulerService>() };
        app.Services.GetRequiredService<IEventBus>().Subscribe<IDomainEvent>((e, _) => { lock (rig.Events) rig.Events.Add(e); return Task.CompletedTask; });
        return rig;
    }

    private static object Daily(Guid channel, string title, string start = "20:00", string end = "21:00", string? op = "jcruz")
        => new { channelId = channel, title, recurrence = "Daily", startTime = start, endTime = end, @operator = op };

    private static async Task<JsonElement> Json(HttpResponseMessage r) => await r.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Crear_Una_Tarea_La_Devuelve_Con_Todo_Calculado_Y_Deja_Rastro_De_Quien_La_Creo()
    {
        await using var rig = await BuildAsync();

        var response = await rig.Client.PostAsJsonAsync("/api/v1/schedule", new
        {
            channelId = rig.ChannelA.ChannelId, title = "Noticias", recurrence = "Weekly", startTime = "23:30", endTime = "00:15:30",
            weekdays = new[] { "Mon", "wednesday" }, segmentMinutes = 10, @operator = "jcruz",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var job = (await Json(response)).GetProperty("job");
        Assert.Equal("A", job.GetProperty("channelKey").GetString());
        Assert.Equal("Weekly", job.GetProperty("recurrence").GetString());
        Assert.Equal(new[] { "Mon", "Wed" }, job.GetProperty("weekdays").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("23:30:00", job.GetProperty("startTime").GetString());
        Assert.Equal("00:15:30", job.GetProperty("endTime").GetString());   // hora de pared del equipo que graba, sin zona
        Assert.Equal(45 * 60 + 30, job.GetProperty("durationSeconds").GetInt32());
        Assert.True(job.GetProperty("endsNextDay").GetBoolean());
        Assert.Equal(10, job.GetProperty("segmentMinutes").GetInt32());
        Assert.Equal("scheduled", job.GetProperty("state").GetString());
        Assert.Equal("2026-09-21T23:30:00", job.GetProperty("nextRun").GetString()); // el lunes siguiente al sábado 19

        var changed = Assert.Single(rig.Events.OfType<ScheduleChanged>());
        Assert.Equal(ScheduleChangeKind.Created, changed.Change);
        Assert.Equal("Noticias", changed.ScheduledJobTitle);
        Assert.Equal("jcruz", changed.Operator);
    }

    [Fact]
    public async Task La_Lista_Agrupa_Por_Canal_Da_La_Ocurrencia_De_Hoy_Y_Se_Puede_Filtrar()
    {
        await using var rig = await BuildAsync();
        (await rig.Client.PostAsJsonAsync("/api/v1/schedule", Daily(rig.ChannelB.ChannelId, "Pleno", "08:00", "09:00"))).EnsureSuccessStatusCode();
        (await rig.Client.PostAsJsonAsync("/api/v1/schedule", Daily(rig.ChannelA.ChannelId, "Noticias"))).EnsureSuccessStatusCode();

        var all = await rig.Client.GetFromJsonAsync<JsonElement>("/api/v1/schedule");

        Assert.Equal("2026-09-19T12:00:00", all.GetProperty("now").GetString());
        Assert.Equal(-360, all.GetProperty("utcOffsetMinutes").GetInt32());
        var jobs = all.GetProperty("jobs").EnumerateArray().ToList();
        Assert.Equal(new[] { "Noticias", "Pleno" }, jobs.Select(j => j.GetProperty("title").GetString()).ToArray()); // A antes que B
        // «Noticias» le toca hoy a las 20:00 (aún no); «Pleno» es de las 08:00 y se creó a las 12:00 → hoy ya pasó.
        Assert.Equal("scheduled", jobs[0].GetProperty("today").GetProperty("status").GetString());
        Assert.Equal("2026-09-19T20:00:00", jobs[0].GetProperty("today").GetProperty("start").GetString());
        Assert.Equal("2026-09-20T08:00:00", jobs[1].GetProperty("nextRun").GetString());

        var onlyB = await rig.Client.GetFromJsonAsync<JsonElement>($"/api/v1/schedule?channel={rig.ChannelB.ChannelId}");
        Assert.Equal("Pleno", Assert.Single(onlyB.GetProperty("jobs").EnumerateArray().ToList()).GetProperty("title").GetString());
    }

    [Theory]
    [InlineData("{\"recurrence\":\"Daily\",\"startTime\":\"20:00\",\"endTime\":\"20:00\"}", HttpStatusCode.BadRequest, "end-equals-start")]
    [InlineData("{\"recurrence\":\"Weekly\",\"startTime\":\"20:00\",\"endTime\":\"21:00\"}", HttpStatusCode.BadRequest, "pick-weekday")]
    [InlineData("{\"recurrence\":\"Once\",\"startTime\":\"20:00\",\"endTime\":\"21:00\"}", HttpStatusCode.BadRequest, "pick-date")]
    [InlineData("{\"recurrence\":\"Once\",\"date\":\"2026-09-19\",\"startTime\":\"11:00\",\"endTime\":\"11:30\"}", HttpStatusCode.BadRequest, "past-date")]
    [InlineData("{\"recurrence\":\"Once\",\"date\":\"19/09/2026\",\"startTime\":\"20:00\",\"endTime\":\"21:00\"}", HttpStatusCode.BadRequest, "invalid-date")]
    [InlineData("{\"recurrence\":\"Cada tanto\",\"startTime\":\"20:00\",\"endTime\":\"21:00\"}", HttpStatusCode.BadRequest, "invalid-recurrence")]
    [InlineData("{\"recurrence\":\"Daily\",\"startTime\":\"25:00\",\"endTime\":\"21:00\"}", HttpStatusCode.BadRequest, "invalid-time")]
    [InlineData("{\"recurrence\":\"Weekly\",\"weekdays\":[\"Lunes\"],\"startTime\":\"20:00\",\"endTime\":\"21:00\"}", HttpStatusCode.BadRequest, "invalid-weekdays")]
    [InlineData("{\"recurrence\":\"Daily\",\"startTime\":\"20:00\",\"endTime\":\"21:00\",\"segmentMinutes\":0}", HttpStatusCode.BadRequest, "segment-minutes-invalid")]
    public async Task Un_Borrador_Mal_Formado_Se_Rechaza_Con_Su_Codigo_Y_Su_Explicacion(string json, HttpStatusCode status, string code)
    {
        await using var rig = await BuildAsync();
        var body = json.Insert(1, $"\"channelId\":\"{rig.ChannelA.ChannelId}\",\"title\":\"x\",");

        var response = await rig.Client.PostAsync("/api/v1/schedule", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(status, response.StatusCode);
        var error = await Json(response);
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(error.GetProperty("error").GetString()));
        Assert.Empty(rig.Events.OfType<ScheduleChanged>());                 // nada guardado, nada auditado
    }

    [Fact]
    public async Task Un_Canal_Que_No_Existe_Un_Titulo_Repetido_Y_Un_Solape_Se_Rechazan()
    {
        await using var rig = await BuildAsync();
        (await rig.Client.PostAsJsonAsync("/api/v1/schedule", Daily(rig.ChannelA.ChannelId, "Noticias"))).EnsureSuccessStatusCode();

        var noChannel = await rig.Client.PostAsJsonAsync("/api/v1/schedule", Daily(Guid.NewGuid(), "Otra"));
        Assert.Equal(HttpStatusCode.BadRequest, noChannel.StatusCode);
        Assert.Equal("unknown-channel", (await Json(noChannel)).GetProperty("code").GetString());

        var duplicate = await rig.Client.PostAsJsonAsync("/api/v1/schedule", Daily(rig.ChannelB.ChannelId, "NOTICIAS", "06:00", "07:00"));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("duplicate-title", (await Json(duplicate)).GetProperty("code").GetString());

        var clash = await rig.Client.PostAsJsonAsync("/api/v1/schedule", Daily(rig.ChannelA.ChannelId, "Deportes", "20:30", "22:00"));
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        var why = await Json(clash);
        Assert.Equal("clash", why.GetProperty("code").GetString());
        Assert.Equal("Noticias", why.GetProperty("clashWith").GetString());

        // El mismo horario en OTRO canal no choca.
        Assert.Equal(HttpStatusCode.Created, (await rig.Client.PostAsJsonAsync("/api/v1/schedule", Daily(rig.ChannelB.ChannelId, "Deportes", "20:30", "22:00"))).StatusCode);
    }

    [Fact]
    public async Task Editar_Pausar_Reanudar_Y_Borrar_Una_Tarea()
    {
        await using var rig = await BuildAsync();
        var created = (await Json(await rig.Client.PostAsJsonAsync("/api/v1/schedule", Daily(rig.ChannelA.ChannelId, "Noticias")))).GetProperty("job");
        var id = created.GetProperty("id").GetGuid();

        // Editar: mismo id, nueva hora de fin.
        var edited = await rig.Client.PutAsJsonAsync($"/api/v1/schedule/{id}", Daily(rig.ChannelA.ChannelId, "Noticias", "20:00", "22:30", "ana"));
        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);
        var job = (await Json(edited)).GetProperty("job");
        Assert.Equal(id, job.GetProperty("id").GetGuid());
        Assert.Equal("22:30:00", job.GetProperty("endTime").GetString());

        // Pausar: deja de tener próxima ejecución y de reservar su franja.
        var paused = await Json(await rig.Client.PostAsJsonAsync($"/api/v1/schedule/{id}/enabled", new { enabled = false, @operator = "ana" }));
        Assert.Equal("paused", paused.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, paused.GetProperty("nextRun").ValueKind);
        (await rig.Client.PostAsJsonAsync("/api/v1/schedule", Daily(rig.ChannelA.ChannelId, "Deportes", "21:00", "22:00"))).EnsureSuccessStatusCode();

        // Reanudarla ahora chocaría con «Deportes», que ocupó su franja mientras estaba en pausa.
        var resume = await rig.Client.PostAsJsonAsync($"/api/v1/schedule/{id}/enabled", new { enabled = true });
        Assert.Equal(HttpStatusCode.Conflict, resume.StatusCode);
        Assert.Equal("clash", (await Json(resume)).GetProperty("code").GetString());

        // Borrar; la segunda vez ya no existe.
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Client.DeleteAsync($"/api/v1/schedule/{id}?operator=ana")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Client.DeleteAsync($"/api/v1/schedule/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await rig.Client.PutAsJsonAsync($"/api/v1/schedule/{id}", Daily(rig.ChannelA.ChannelId, "Noticias"))).StatusCode);

        var kinds = rig.Events.OfType<ScheduleChanged>().Where(e => e.ScheduledJobId == id).Select(e => (e.Change, e.Operator)).ToList();
        Assert.Equal(new (ScheduleChangeKind, string?)[]
        {
            (ScheduleChangeKind.Created, "jcruz"), (ScheduleChangeKind.Updated, "ana"), (ScheduleChangeKind.Paused, "ana"), (ScheduleChangeKind.Deleted, "ana"),
        }, kinds);
    }

    [Fact]
    public async Task La_Tarea_En_Marcha_Se_Ve_En_Su_Canal_Con_Lo_Que_Le_Queda_Y_Se_Puede_Saltar()
    {
        await using var rig = await BuildAsync();
        (await rig.Client.PostAsJsonAsync("/api/v1/schedule", new
        {
            channelId = rig.ChannelA.ChannelId, title = "Final de copa", recurrence = "Once", date = "2026-09-19", startTime = "20:00", endTime = "21:30",
        })).EnsureSuccessStatusCode();
        Assert.Empty((await rig.Client.GetFromJsonAsync<JsonElement>("/api/v1/schedule/active")).EnumerateArray());

        rig.Clock.UtcNow = At(19, 20, 0, 5);
        await rig.Scheduler.TickAsync(default);                           // llega su hora: el scheduler la arranca
        rig.Clock.UtcNow = At(19, 20, 30);

        var active = Assert.Single((await rig.Client.GetFromJsonAsync<JsonElement>("/api/v1/schedule/active")).EnumerateArray().ToList());
        Assert.Equal(rig.ChannelA.ChannelId, active.GetProperty("channelId").GetGuid());
        Assert.Equal("Final de copa", active.GetProperty("title").GetString());
        Assert.Equal("2026-09-19T21:30:00", active.GetProperty("endsAt").GetString());
        Assert.Equal(3600, active.GetProperty("remainingSeconds").GetInt32());   // con el reloj del equipo que graba
        var listed = (await rig.Client.GetFromJsonAsync<JsonElement>("/api/v1/schedule")).GetProperty("jobs")[0];
        Assert.Equal("running", listed.GetProperty("state").GetString());
        Assert.Equal("2026-09-19T21:30:00", listed.GetProperty("runningUntil").GetString());
        Assert.Equal(RecordingTrigger.Scheduled, rig.ChannelA.LastOrigin!.Trigger);

        // Saltarla: se detiene YA, con su motivo, y deja de estar en marcha. Saltar otra vez no tiene qué saltar.
        Assert.Equal(HttpStatusCode.NoContent, (await rig.Client.PostAsync($"/api/v1/channels/{rig.ChannelA.ChannelId}/schedule/skip", null)).StatusCode);
        Assert.Equal(RecordingStopReason.ScheduledSkip, rig.ChannelA.LastStopReason);
        Assert.Empty((await rig.Client.GetFromJsonAsync<JsonElement>("/api/v1/schedule/active")).EnumerateArray());
        var again = await rig.Client.PostAsync($"/api/v1/channels/{rig.ChannelA.ChannelId}/schedule/skip", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("no-active-task", (await Json(again)).GetProperty("code").GetString());

        // …y en la ocurrencia de hoy queda como saltada, no como «en curso».
        var today = (await rig.Client.GetFromJsonAsync<JsonElement>("/api/v1/schedule")).GetProperty("jobs")[0].GetProperty("today");
        Assert.Equal("skipped", today.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Un_Equipo_Sin_Programador_Lo_Dice_Sin_Romper_El_Resto_De_La_Api()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IChannelEngine>(new FakeChannelEngine(Guid.NewGuid(), "A"));
        builder.Services.AddSingleton<IChannelManager, ChannelManager>();
        builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
        builder.Services.AddSingleton<IStorageManager, StorageManager>();
        builder.Services.AddBaiossCqrs();
        await using var app = builder.Build();
        app.MapBaiossApi();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync("/api/v1/schedule");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("no-scheduler", (await Json(response)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await app.GetTestClient().GetAsync("/api/v1/channels")).StatusCode);
        await app.StopAsync();
    }

    // ------------------------------------------------------------------ dobles de prueba

    private sealed class MutableClock : IClock { public DateTimeOffset UtcNow { get; set; } }

    /// <summary>Repositorio en memoria que, como el de verdad, SUSTITUYE la tarea al actualizar (editar crea otra instancia).</summary>
    private sealed class InMemoryJobs : IScheduledJobRepository
    {
        private readonly List<ScheduledJob> _jobs = new();
        public Task<ScheduledJob?> GetAsync(Guid id, CancellationToken ct = default) { lock (_jobs) return Task.FromResult(_jobs.FirstOrDefault(j => j.Id == id)); }
        public Task<IReadOnlyList<ScheduledJob>> ListAsync(CancellationToken ct = default) { lock (_jobs) return Task.FromResult((IReadOnlyList<ScheduledJob>)_jobs.ToList()); }
        public Task AddAsync(ScheduledJob entity, CancellationToken ct = default) { lock (_jobs) _jobs.Add(entity); return Task.CompletedTask; }
        public Task UpdateAsync(ScheduledJob entity, CancellationToken ct = default)
        {
            lock (_jobs) { int i = _jobs.FindIndex(j => j.Id == entity.Id); if (i >= 0) _jobs[i] = entity; }
            return Task.CompletedTask;
        }
        public Task RemoveAsync(Guid id, CancellationToken ct = default) { lock (_jobs) _jobs.RemoveAll(j => j.Id == id); return Task.CompletedTask; }
    }
}
