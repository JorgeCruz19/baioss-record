using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// Coordinador GLOBAL de emergencia de almacenamiento (Fase 3b). La detección de disco POR-CANAL
/// (<see cref="DiskSpaceGuard"/>) solo corre MIENTRAS se graba y su acción (auto-stop) es local; este servicio
/// vigila el volumen de grabación de forma CONTINUA —también en IDLE— y centraliza la ACCIÓN global al entrar en
/// emergencia (ocupado ≥ el umbral de emergencia de los ajustes). Los umbrales y los opt-in se leen del
/// <see cref="IStorageSettingsStore"/> EN VIVO en cada sondeo (editables sin reiniciar, Fase 4c):
/// <list type="number">
/// <item>AUDITA la transición al bus (<see cref="StorageEmergencyEntered"/>/<see cref="StorageEmergencyCleared"/>).</item>
/// <item>Opt-in auto-limpieza: dispara la limpieza por espacio (borra las grabaciones NO protegidas más antiguas
/// hasta dejar el disco bajo el umbral crítico).</item>
/// <item>Opt-in bloqueo: implementa <see cref="IStorageGate"/> para BLOQUEAR el inicio de nuevas grabaciones
/// (lo consulta el pre-vuelo del canal) mientras dure la emergencia.</item>
/// </list>
/// Sale de la emergencia con HISTÉRESIS (para no oscilar en el borde). Seguro por defecto: sin opt-in solo ALERTA
/// (no borra ni bloquea). Si no se puede medir el volumen (p. ej. UNC no soportada), no actúa (no borra a ciegas).
/// </summary>
public sealed class StorageEmergencyCoordinator : BackgroundService, IStorageGate, IStorageStatusProvider
{
    private readonly IStorageManager _storage;
    private readonly IEventBus _bus;
    private readonly IStorageSettingsStore _settings;
    private readonly ILogger<StorageEmergencyCoordinator> _log;

    // volatile: lo escribe el bucle de vigilancia (hilo de fondo) y lo LEEN sin lock los pre-vuelos de grabación
    // (hilo de UI/API/scheduler) a través de IStorageGate. volatile garantiza que vean el valor más reciente.
    private volatile bool _emergency;
    private volatile string? _blockReason;
    private volatile StorageSnapshot _snapshot = StorageSnapshot.Unknown; // último estado publicado a la UI (Fase 4b)

    public StorageEmergencyCoordinator(IStorageManager storage, IEventBus bus, IStorageSettingsStore settings,
        ILogger<StorageEmergencyCoordinator> log)
    {
        _storage = storage;
        _bus = bus;
        _settings = settings;
        _log = log;
    }

    /// <summary>Carpeta raíz de grabaciones (…/recordings): el volumen que se vigila. Vacía = sin vigilancia (desactivado).</summary>
    public string RecordingsRoot { get; init; } = "";
    /// <summary>Histéresis (puntos porcentuales): una vez en emergencia, solo SALE al bajar de EmergencyPercent−esto (anti-oscilación).</summary>
    public int HysteresisPoints { get; init; } = 2;
    /// <summary>Frecuencia de sondeo del volumen.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);

    // Los umbrales por % (Warn/Critical/Emergency) y los opt-in (auto-limpieza / bloqueo) se leen del
    // IStorageSettingsStore EN VIVO en cada sondeo/consulta (Fase 4c), así que un cambio surte efecto sin reiniciar.

    // --- IStorageGate: consultado en el pre-vuelo del canal para bloquear el inicio de grabaciones. ---
    public bool ShouldBlockNewRecordings => _emergency && _settings.Current.StopNewRecordingsOnEmergency;
    public string? BlockReason => ShouldBlockNewRecordings ? _blockReason : null;

    // --- IStorageStatusProvider: alimenta el indicador GLOBAL de almacenamiento de la UI (Fase 4b). ---
    public StorageSnapshot Current => _snapshot;
    public event EventHandler<StorageSnapshot>? Changed;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(RecordingsRoot)) return; // sin volumen que vigilar: desactivado

        // Medida inicial SOLO para el indicador de la UI (sin acciones): que el indicador tenga datos ya, sin
        // esperar al arranque diferido. Las ACCIONES (auto-limpieza) sí esperan a que la BD esté lista.
        try { PublishSnapshot(Measure()); } catch { /* best-effort */ }

        // Arranque diferido: deja componerse la BD y los canales antes de la primera medida/auto-limpieza.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var s0 = _settings.Current; // ajustes vigentes al arrancar (se releen en vivo en cada sondeo)
        _log.LogInformation("Vigilancia de emergencia de almacenamiento activa en «{Root}»: emergencia ≥ {Pct}% ocupado " +
            "(auto-limpieza: {Clean}, bloquear nuevas grabaciones: {Block}, cada {Interval}).",
            RecordingsRoot, s0.EmergencyPercent, s0.AutoCleanupOnEmergency, s0.StopNewRecordingsOnEmergency, PollInterval);

        while (!ct.IsCancellationRequested)
        {
            try { await PollOnceAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogError(ex, "Emergencia de almacenamiento: fallo en la vigilancia."); }

            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Una pasada de vigilancia: mide el volumen, decide el estado con histéresis y, en las TRANSICIONES,
    /// audita + (opt-in) auto-limpia / bloquea. Aislada para testear la lógica de estado por separado.</summary>
    private async Task PollOnceAsync(CancellationToken ct)
    {
        var s = _settings.Current; // ajustes VIGENTES (umbrales + opt-in) — leídos en vivo (Fase 4c)
        var snap = Measure();
        if (snap.TotalBytes <= 0) { PublishSnapshot(snap); return; } // no medible (UNC/no lista): publica «—» y no actúa
        double usedPct = snap.UsedPercent;
        long free = snap.FreeBytes, total = snap.TotalBytes;

        bool nowEmergency = IsEmergency(usedPct, _emergency, s.EmergencyPercent, HysteresisPoints);

        if (nowEmergency && !_emergency)
        {
            // TRANSICIÓN a emergencia: audita, marca el bloqueo (si opt-in) y —si opt-in— auto-limpia.
            _emergency = true;
            _blockReason = $"Almacenamiento en EMERGENCIA ({usedPct:0.#}% ocupado, {free / 1_073_741_824d:0.#} GB libres). " +
                           "Libera espacio para poder iniciar nuevas grabaciones.";
            _log.LogWarning("Almacenamiento en EMERGENCIA: {Used:0.#}% ocupado en «{Root}» ({Free:N0} bytes libres).", usedPct, RecordingsRoot, free);
            await _bus.PublishAsync(new StorageEmergencyEntered(RecordingsRoot, free, total, usedPct), ct).ConfigureAwait(false);

            if (s.AutoCleanupOnEmergency)
                await RunEmergencyCleanupAsync(ct).ConfigureAwait(false);
        }
        else if (!nowEmergency && _emergency)
        {
            // TRANSICIÓN fuera de emergencia (ya bajo el umbral con histéresis): audita y libera el bloqueo.
            await ClearEmergencyAsync(free, total, usedPct, ct).ConfigureAwait(false);
        }

        // Publica el estado FINAL para el indicador de la UI: IsEmergency ya actualizado y el espacio libre
        // reflejando una posible auto-limpieza. (Fase 4b.)
        PublishSnapshot(Measure());
    }

    /// <summary>Mide el volumen y construye la instantánea para la UI (Health por % ocupado + flag de emergencia
    /// vigente). Si no se puede medir (UNC/no lista), devuelve <see cref="StorageSnapshot.Unknown"/>. (Fase 4b.)</summary>
    private StorageSnapshot Measure()
    {
        var (free, total) = DiskSpaceGuard.ReadDrive(RecordingsRoot);
        if (total <= 0) return StorageSnapshot.Unknown;
        double usedPct = (total - free) * 100.0 / total;
        var s = _settings.Current; // umbrales VIGENTES (Fase 4c)
        return new StorageSnapshot(free, total, usedPct, HealthFor(usedPct, s.WarnPercent, s.CriticalPercent, s.EmergencyPercent), _emergency);
    }

    private void PublishSnapshot(StorageSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(this, snapshot);
    }

    /// <summary>Nivel de salud del almacenamiento por % OCUPADO para el indicador de la UI (peor primero). 0 = umbral
    /// desactivado. Pura, testeable. (Fase 4b.)</summary>
    internal static StorageHealth HealthFor(double usedPercent, int warnPercent, int criticalPercent, int emergencyPercent)
    {
        if (emergencyPercent > 0 && usedPercent >= emergencyPercent) return StorageHealth.Emergency;
        if (criticalPercent > 0 && usedPercent >= criticalPercent) return StorageHealth.Critical;
        if (warnPercent > 0 && usedPercent >= warnPercent) return StorageHealth.Warning;
        return StorageHealth.Ok;
    }

    /// <summary>Auto-limpieza de emergencia (opt-in): borra las grabaciones NO protegidas más antiguas hasta dejar
    /// el disco por debajo del umbral crítico. Tras limpiar, RE-MIDE y sale de la emergencia de inmediato si se
    /// recuperó (no espera al próximo sondeo). Respeta protección + archivos en uso + audita (vía StorageManager).</summary>
    private async Task RunEmergencyCleanupAsync(CancellationToken ct)
    {
        int targetFreePct = CleanupTargetFreePercent(_settings.Current.CriticalPercent);
        _log.LogWarning("Emergencia: auto-limpieza para dejar ≥ {Pct}% libre (borra las NO protegidas más antiguas).", targetFreePct);
        int handled;
        try
        {
            handled = await _storage.EnforceFreeSpaceAsync(RecordingsRoot, 0, targetFreePct,
                RetentionAction.Delete, null, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Emergencia: la auto-limpieza falló.");
            return;
        }
        _log.LogInformation("Emergencia: auto-limpieza trató {N} archivo(s).", handled);

        var (free, total) = DiskSpaceGuard.ReadDrive(RecordingsRoot);
        if (total <= 0) return;
        double usedPct = (total - free) * 100.0 / total;
        if (!IsEmergency(usedPct, wasEmergency: true, _settings.Current.EmergencyPercent, HysteresisPoints))
            await ClearEmergencyAsync(free, total, usedPct, ct).ConfigureAwait(false);
    }

    private async Task ClearEmergencyAsync(long free, long total, double usedPct, CancellationToken ct)
    {
        _emergency = false;
        _blockReason = null;
        _log.LogInformation("Almacenamiento fuera de emergencia: {Used:0.#}% ocupado en «{Root}» ({Free:N0} bytes libres).", usedPct, RecordingsRoot, free);
        await _bus.PublishAsync(new StorageEmergencyCleared(RecordingsRoot, free, total, usedPct), ct).ConfigureAwait(false);
    }

    /// <summary>Decisión PURA (testeable) del estado de emergencia con HISTÉRESIS: entra al alcanzar
    /// <paramref name="emergencyPercent"/>; una vez dentro, solo SALE al bajar de (emergencia − histéresis),
    /// para no oscilar en el borde. <paramref name="emergencyPercent"/> ≤ 0 = desactivado (nunca emergencia). (Fase 3b.)</summary>
    internal static bool IsEmergency(double usedPercent, bool wasEmergency, int emergencyPercent, int hysteresisPoints)
    {
        if (emergencyPercent <= 0) return false;
        double exit = emergencyPercent - Math.Max(0, hysteresisPoints);
        return wasEmergency ? usedPercent >= exit : usedPercent >= emergencyPercent;
    }

    /// <summary>Objetivo de % libre para la auto-limpieza de emergencia: dejar el disco por debajo del umbral
    /// CRÍTICO (100 − crítico). Fuera de rango (0 o ≥100) → 10% por defecto. Pura, testeable. (Fase 3b.)</summary>
    internal static int CleanupTargetFreePercent(int criticalPercent)
        => criticalPercent is > 0 and < 100 ? 100 - criticalPercent : 10;
}
