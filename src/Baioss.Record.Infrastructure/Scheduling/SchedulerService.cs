using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Scheduling;

namespace Baioss.Record.Infrastructure.Scheduling;

/// <summary>
/// Servicio de programación (grabación automática por hora/calendario). Corre como
/// <see cref="BackgroundService"/>: cada <see cref="TickInterval"/> revisa los trabajos y dispara los
/// que tocan, invocando start/stop sobre el canal correspondiente. Una grabación programada es un
/// <see cref="ScheduledAction.StartRecording"/> con <see cref="ScheduledJob.Duration"/> (auto-stop al
/// cumplirse). Es resiliente: si la app se reinicia dentro de la ventana de una grabación, la reanuda;
/// no interfiere con una grabación manual en curso; y no dispara franjas demasiado viejas
/// (<see cref="StartGrace"/>). Sin duración, el start no se auto-detiene (queda para un stop manual o un
/// trabajo <see cref="ScheduledAction.StopRecording"/>).
/// </summary>
public sealed class SchedulerService : BackgroundService, ISchedulerService
{
    private readonly IScheduledJobRepository _repo;
    private readonly IChannelManager _channels;
    private readonly IClock _clock;
    private readonly ILogger<SchedulerService> _log;

    // Grabaciones programadas que ESTA instancia inició: jobId → (cuándo parar, canal). Concurrente: lo
    // escribe el bucle del BackgroundService y lo lee/modifica la UI (saltar / indicador de activo).
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset StopAt, Guid ChannelId)> _active = new();
    private readonly HashSet<Guid> _warnedBusy = new();

    // 1 s: una grabación programada arranca con ≤1 s de margen respecto a su hora exacta (hh:mm:ss).
    // El chequeo es muy barato (una consulta a SQLite local); el log de EF se silencia en el host.
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>Tolerancia para disparar un start "tarde" (p. ej. la app arrancó justo después de la hora).</summary>
    public TimeSpan StartGrace { get; init; } = TimeSpan.FromMinutes(2);

    public IReadOnlySet<Guid> ActiveScheduledChannels => _active.Values.Select(v => v.ChannelId).ToHashSet();
    public event EventHandler? ActiveChanged;

    private void RaiseActiveChanged() => ActiveChanged?.Invoke(this, EventArgs.Empty);

    public SchedulerService(IScheduledJobRepository repo, IChannelManager channels, IClock clock, ILogger<SchedulerService> log)
    {
        _repo = repo;
        _channels = channels;
        _clock = clock;
        _log = log;
    }

    // --- ISchedulerService: CRUD que usan la UI y la API ---

    public async Task<ScheduledJob> ScheduleAsync(ScheduledJob job, CancellationToken ct = default)
    {
        await _repo.AddAsync(job, ct).ConfigureAwait(false);
        _log.LogInformation("Programado «{Title}»: canal {Channel}, {RunAt} ({Recurrence}).",
            job.Title, job.ChannelId, job.RunAt, job.Recurrence);
        return job;
    }

    public Task CancelAsync(Guid jobId, CancellationToken ct = default) => _repo.RemoveAsync(jobId, ct);

    public Task<IReadOnlyList<ScheduledJob>> GetAllAsync(CancellationToken ct = default) => _repo.ListAsync(ct);

    public async Task SetEnabledAsync(Guid jobId, bool enabled, CancellationToken ct = default)
    {
        if (await _repo.GetAsync(jobId, ct).ConfigureAwait(false) is { } job)
        {
            job.Enabled = enabled;
            await _repo.UpdateAsync(job, ct).ConfigureAwait(false);
        }
    }

    public async Task UpdateAsync(ScheduledJob job, CancellationToken ct = default)
    {
        await _repo.UpdateAsync(job, ct).ConfigureAwait(false);
        _log.LogInformation("Editada «{Title}»: canal {Channel}, {RunAt} ({Recurrence}).",
            job.Title, job.ChannelId, job.RunAt, job.Recurrence);
    }

    public async Task<IReadOnlyList<ScheduledJob>> GetUpcomingAsync(DateTimeOffset until, CancellationToken ct = default)
    {
        var now = _clock.UtcNow;
        var jobs = await _repo.ListAsync(ct).ConfigureAwait(false);
        return jobs
            .Where(j => j.Enabled && ScheduleEvaluator.NextSlotAfter(j, now) is { } next && next <= until)
            .OrderBy(j => ScheduleEvaluator.NextSlotAfter(j, now))
            .ToList();
    }

    // --- Bucle de disparo ---

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Espera inicial: deja que la UI y los canales terminen de componerse antes del primer disparo.
        try { await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Avisa de las franjas que ya pasaron sin ejecutarse (p. ej. app apagada en su horario).
        try { await WarnMissedOccurrencesAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.LogError(ex, "Scheduler: fallo al revisar ocurrencias perdidas."); }

        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "Scheduler: fallo en el tick."); }

            try { await Task.Delay(TickInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Al arrancar, avisa (log) de las grabaciones programadas cuya franja YA pasó por completo y nunca se
    /// ejecutó —típicamente porque la app estuvo apagada en ese horario—. No las dispara (la hora pasó); solo
    /// deja constancia para el operador. Una franja aún dentro de su ventana la reanuda el Tick normal.
    /// </summary>
    private async Task WarnMissedOccurrencesAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        foreach (var job in await _repo.ListAsync(ct).ConfigureAwait(false))
        {
            if (!job.Enabled || job.Action != ScheduledAction.StartRecording) continue;
            if (ScheduleEvaluator.LatestSlotAtOrBefore(job, now) is not { } occ) continue;

            bool fired = job.LastRunAt is { } lr && lr >= occ;
            var windowEnd = job.Duration is { } d ? occ + d : occ + StartGrace; // fin de la ventana grabable
            if (!fired && now > windowEnd)
                _log.LogWarning("Scheduler: «{Title}» del {Occ:dd-MM-yyyy HH:mm} no se ejecutó (¿app apagada en su horario?).", job.Title, occ);
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;

        // 1) Auto-stop de las grabaciones programadas cuya duración expiró.
        foreach (var (jobId, info) in _active.ToList())
        {
            if (now < info.StopAt) continue;
            if (_channels.TryGet(info.ChannelId, out var ch) && ch is not null)
            {
                try
                {
                    await ch.StopRecordingAsync(ct).ConfigureAwait(false);
                    // La segmentación es propia de la grabación programada: se retira al terminar para no
                    // arrastrarla a una grabación manual posterior.
                    if (ch is IConfigurableRecording cfg) cfg.Profile.Segmentation = null;
                    _log.LogInformation("Scheduler: fin de grabación programada en canal {Channel}.", info.ChannelId);
                }
                catch (Exception ex) { _log.LogError(ex, "Scheduler: error al detener canal {Channel}.", info.ChannelId); }
            }
            if (_active.TryRemove(jobId, out _)) RaiseActiveChanged();
        }

        // 2) Disparo de starts/stops según la franja vigente de cada trabajo.
        var jobs = await _repo.ListAsync(ct).ConfigureAwait(false);
        foreach (var job in jobs)
        {
            if (!job.Enabled) continue;
            if (ScheduleEvaluator.LatestSlotAtOrBefore(job, now) is not { } occ) continue;

            if (job.Action == ScheduledAction.StartRecording) await TryStartAsync(job, occ, now, ct).ConfigureAwait(false);
            else if (job.Action == ScheduledAction.StopRecording) await TryStopAsync(job, occ, now, ct).ConfigureAwait(false);
            // SwitchProfile / SwitchSource: pendientes (Fase 2).
        }
    }

    private async Task TryStartAsync(ScheduledJob job, DateTimeOffset occ, DateTimeOffset now, CancellationToken ct)
    {
        if (_active.ContainsKey(job.Id)) return; // ya activa (la lleva esta instancia)

        // El operador saltó ESTA ocurrencia: no se inicia ni se reanuda (las siguientes sí).
        if (job.SkippedOccurrence is { } skipped && skipped == occ) return;

        DateTimeOffset? stopAt = job.Duration is { } d ? occ + d : null;
        // Con duración: vigente mientras no se llegue al fin (permite reanudar tras un reinicio dentro de
        // la ventana). Sin duración: solo dentro de la gracia inicial.
        bool within = stopAt is { } s ? now < s : now - occ <= StartGrace;
        if (!within) return;

        // Sin duración, evita re-disparar la misma franja dentro de la gracia.
        if (stopAt is null && job.LastRunAt is { } lr && lr >= occ) return;

        if (!_channels.TryGet(job.ChannelId, out var ch) || ch is null)
        {
            _log.LogWarning("Scheduler: canal {Channel} no disponible para «{Title}».", job.ChannelId, job.Title);
            return;
        }

        // No interferir con una grabación manual (u otra) en curso en ese canal.
        if (ch.Status.RecordingState is RecordingState.Recording or RecordingState.Paused)
        {
            if (_warnedBusy.Add(job.Id))
                _log.LogWarning("Scheduler: «{Title}» saltada; el canal {Channel} ya está grabando.", job.Title, job.ChannelId);
            return;
        }
        _warnedBusy.Remove(job.Id);

        // Aplica la segmentación PROPIA de esta grabación programada al perfil del canal (la grabación
        // usa el perfil activo). Se fija siempre (política o null) para no heredar restos de otra.
        if (ch is IConfigurableRecording cfg) cfg.Profile.Segmentation = SegPolicy(job);

        try
        {
            await ch.StartRecordingAsync(job.ProfileId ?? Guid.Empty, "Programación", ScheduledName(job, occ), ct).ConfigureAwait(false);
            job.LastRunAt = occ;
            await _repo.UpdateAsync(job, ct).ConfigureAwait(false);
            if (stopAt is { } st) { _active[job.Id] = (st, job.ChannelId); RaiseActiveChanged(); }
            _log.LogInformation("Scheduler: inicio de «{Title}» en canal {Channel}{Until}.",
                job.Title, job.ChannelId, stopAt is { } e ? $" (hasta {e:HH:mm})" : "");
        }
        catch (Exception ex) { _log.LogError(ex, "Scheduler: error al iniciar «{Title}».", job.Title); }
    }

    private async Task TryStopAsync(ScheduledJob job, DateTimeOffset occ, DateTimeOffset now, CancellationToken ct)
    {
        if (now - occ > StartGrace) return;                 // solo dentro de la ventana de gracia
        if (job.LastRunAt is { } lr && lr >= occ) return;   // ya disparada esta franja

        if (_channels.TryGet(job.ChannelId, out var ch) && ch is not null &&
            ch.Status.RecordingState is RecordingState.Recording or RecordingState.Paused)
        {
            try { await ch.StopRecordingAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "Scheduler: error al detener «{Title}».", job.Title); }
        }
        job.LastRunAt = occ;
        await _repo.UpdateAsync(job, ct).ConfigureAwait(false);
        bool removed = false;
        foreach (var kv in _active.Where(k => k.Value.ChannelId == job.ChannelId).ToList()) removed |= _active.TryRemove(kv.Key, out _);
        if (removed) RaiseActiveChanged();
    }

    /// <summary>
    /// Salta la grabación programada en curso de un canal: marca SOLO la ocurrencia actual como saltada
    /// (no se reanuda) y detiene la grabación ya. Las siguientes ocurrencias se ejecutan con normalidad.
    /// </summary>
    public async Task SkipCurrentAsync(Guid channelId, CancellationToken ct = default)
    {
        // Busca el trabajo activo de ese canal.
        Guid jobId = default; bool found = false;
        foreach (var kv in _active)
            if (kv.Value.ChannelId == channelId) { jobId = kv.Key; found = true; break; }
        if (!found) return;

        // Marca la ocurrencia actual como saltada (persistente: sobrevive a un reinicio dentro de la ventana).
        var now = _clock.UtcNow;
        if (await _repo.GetAsync(jobId, ct).ConfigureAwait(false) is { } job &&
            ScheduleEvaluator.LatestSlotAtOrBefore(job, now) is { } occ)
        {
            job.SkippedOccurrence = occ;
            await _repo.UpdateAsync(job, ct).ConfigureAwait(false);
            _log.LogInformation("Scheduler: «{Title}» SALTADA por el operador en canal {Channel} (solo esta ocurrencia).", job.Title, channelId);
        }

        // Detén la grabación ya y retira su segmentación.
        if (_channels.TryGet(channelId, out var ch) && ch is not null)
        {
            try { await ch.StopRecordingAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "Scheduler: error al saltar la grabación del canal {Channel}.", channelId); }
            if (ch is IConfigurableRecording cfg) cfg.Profile.Segmentation = null;
        }
        if (_active.TryRemove(jobId, out _)) RaiseActiveChanged();
    }

    /// <summary>
    /// Nombre de archivo de una grabación programada: «dd-MM-yyyy_Título» con la fecha de la OCURRENCIA.
    /// Si es segmentada, el motor añade «_1, _2…» al final de cada segmento.
    /// </summary>
    private static string ScheduledName(ScheduledJob job, DateTimeOffset occ)
    {
        string date = occ.ToString("dd-MM-yyyy", CultureInfo.InvariantCulture);
        string title = string.IsNullOrWhiteSpace(job.Title) ? "Grabación" : job.Title.Trim();
        return $"{date}_{title}";
    }

    /// <summary>Política de segmentación del trabajo (corte por duración cada N minutos), o null si no segmenta.</summary>
    private static SegmentationPolicy? SegPolicy(ScheduledJob job)
        => job.SegmentMinutes is { } m && m > 0
            ? new SegmentationPolicy { Trigger = SegmentTrigger.Duration, Duration = TimeSpan.FromMinutes(m) }
            : null;
}
