using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Events;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// Coordinador GLOBAL de emergencia de almacenamiento (Fase 3b + MULTI-DISCO). La detección por-canal
/// (<see cref="DiskSpaceGuard"/>) solo corre MIENTRAS se graba y su acción (auto-stop) es local; este servicio
/// vigila los volúmenes de grabación de forma CONTINUA —también en IDLE— y centraliza la ACCIÓN global.
/// <para>MULTI-DISCO: cada canal puede grabar a un disco distinto (o todos al mismo). El coordinador vigila el
/// CONJUNTO de discos de destino de los canales (los provee <see cref="RecordingRootsProvider"/>; si no hay,
/// vigila <see cref="RecordingsRoot"/>). El estado de emergencia se lleva POR DISCO (con histéresis por disco):</para>
/// <list type="number">
/// <item>Al entrar un disco en emergencia: AUDITA + (opt-in) auto-limpia ESE disco (borra las NO protegidas más
/// antiguas de ESE disco, no de otro).</item>
/// <item>El bloqueo de nuevas grabaciones (opt-in, <see cref="IStorageGate"/>) es POR DISCO: solo frena un canal
/// cuyo disco de destino está en emergencia; no frena un canal cuyo disco está bien.</item>
/// <item>El indicador de la UI (<see cref="IStorageStatusProvider"/>) refleja el disco MÁS crítico + cuántos hay.</item>
/// </list>
/// Sale de emergencia con HISTÉRESIS. Seguro por defecto: sin opt-in solo ALERTA. Si un volumen no se puede medir
/// (UNC/no listo), no actúa sobre él (no borra a ciegas).
/// </summary>
public sealed class StorageEmergencyCoordinator : BackgroundService, IStorageGate, IStorageStatusProvider
{
    private readonly IStorageManager _storage;
    private readonly IEventBus _bus;
    private readonly IStorageSettingsStore _settings;
    private readonly ILogger<StorageEmergencyCoordinator> _log;

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // Conjunto INMUTABLE de volúmenes en emergencia (clave = raíz del volumen, p. ej. «D:\»). Se REEMPLAZA entero
    // en cada sondeo (copy-on-write) y lo LEEN sin lock los pre-vuelos de grabación (IStorageGate, hilo de UI/API/
    // scheduler). volatile garantiza que los lectores vean la última referencia publicada.
    private volatile IReadOnlySet<string> _emergencyVolumes = EmptySet;
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

    /// <summary>Carpeta raíz de grabaciones por DEFECTO (…/recordings): el disco que se vigila si aún no hay
    /// destinos de canal conocidos (arranque) o no hay provider. Vacía + sin provider = sin vigilancia.</summary>
    public string RecordingsRoot { get; init; } = "";
    /// <summary>Histéresis (puntos porcentuales): una vez en emergencia, un disco solo SALE al bajar de
    /// EmergencyPercent−esto (anti-oscilación). Se aplica POR DISCO.</summary>
    public int HysteresisPoints { get; init; } = 2;
    /// <summary>Frecuencia de sondeo de los volúmenes.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Provee las carpetas de destino VIGENTES de los canales (para vigilar SUS discos). null o vacío ⇒
    /// se vigila <see cref="RecordingsRoot"/>. Se invoca en cada sondeo (PEREZOSO: no fuerza construir los canales
    /// al registrar el servicio; para cuando corre el primer sondeo real ya están compuestos).</summary>
    public Func<IReadOnlyCollection<string>>? RecordingRootsProvider { get; init; }

    // Los umbrales por % (Warn/Critical/Emergency) y los opt-in (auto-limpieza / bloqueo) se leen del
    // IStorageSettingsStore EN VIVO en cada sondeo/consulta (Fase 4c), así que un cambio surte efecto sin reiniciar.

    // --- IStorageGate: consultado en el pre-vuelo del canal para bloquear el inicio de grabaciones. ---
    /// <summary>Global: ¿hay ALGÚN disco de grabación en emergencia + bloqueo activado? (Para el bloqueo POR DISCO
    /// —el correcto con varios discos— usa <see cref="ShouldBlockRecordingTo"/>.)</summary>
    public bool ShouldBlockNewRecordings => _emergencyVolumes.Count > 0 && _settings.Current.StopNewRecordingsOnEmergency;
    public string? BlockReason => ShouldBlockNewRecordings ? _blockReason : null;

    /// <summary>POR DISCO: bloquea solo si el disco del destino <paramref name="destinationFolder"/> está en
    /// emergencia (y el opt-in está activo). No frena canales cuyo disco está bien.</summary>
    public bool ShouldBlockRecordingTo(string destinationFolder)
    {
        if (!_settings.Current.StopNewRecordingsOnEmergency) return false;
        var vol = NormalizeVolume(destinationFolder);
        return vol is not null && _emergencyVolumes.Contains(vol);
    }
    public string? BlockReasonFor(string destinationFolder)
        => ShouldBlockRecordingTo(destinationFolder)
            ? $"El disco de destino ({TrimVolume(NormalizeVolume(destinationFolder)!)}) está en EMERGENCIA de espacio. " +
              "Libera espacio o elige otra carpeta para poder grabar ahí."
            : null;

    // --- IStorageStatusProvider: alimenta el indicador GLOBAL de almacenamiento de la UI (Fase 4b). ---
    public StorageSnapshot Current => _snapshot;
    public event EventHandler<StorageSnapshot>? Changed;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(RecordingsRoot) && RecordingRootsProvider is null) return; // nada que vigilar

        // Medida inicial SOLO de la carpeta por defecto (NO invoca el provider → no fuerza construir los canales):
        // que el indicador tenga datos ya, sin esperar al arranque diferido.
        try { PublishSnapshot(SnapshotFrom(MeasureVolumes(new[] { RecordingsRoot }, _settings.Current))); } catch { /* best-effort */ }

        // Arranque diferido: deja componerse la BD y los canales antes de la primera medida/auto-limpieza reales.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        var s0 = _settings.Current;
        _log.LogInformation("Vigilancia de emergencia de almacenamiento activa (discos de destino de los canales; " +
            "por defecto «{Root}»): emergencia ≥ {Pct}% ocupado (auto-limpieza: {Clean}, bloquear nuevas grabaciones: {Block}, cada {Interval}).",
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

    /// <summary>Una pasada: mide los discos de destino, detecta ENTRADAS a emergencia por disco (audita + opt-in
    /// auto-limpia ESE disco), re-mide, recalcula el conjunto en emergencia con histéresis, audita SALIDAS y
    /// publica la instantánea del PEOR disco. Aislada para testear la lógica por separado.</summary>
    private async Task PollOnceAsync(CancellationToken ct)
    {
        var s = _settings.Current;                 // ajustes VIGENTES (umbrales + opt-in) — leídos en vivo (Fase 4c)
        var roots = WatchRoots();
        var prev = _emergencyVolumes;

        // 1) Mide y detecta ENTRADAS a emergencia (por disco). En cada entrada: audita y —opt-in— auto-limpia ese disco.
        var m1 = MeasureVolumes(roots, s);
        var justEntered = new List<VolMeasure>();
        foreach (var m in m1)
        {
            if (m.Total <= 0) continue; // no medible: no se actúa sobre él
            bool was = prev.Contains(m.Volume);
            if (IsEmergency(m.UsedPct, was, s.EmergencyPercent, HysteresisPoints) && !was)
            {
                justEntered.Add(m);
                _log.LogWarning("Almacenamiento en EMERGENCIA: {Used:0.#}% ocupado en «{Vol}» ({Free:N0} bytes libres).", m.UsedPct, m.Volume, m.Free);
                await _bus.PublishAsync(new StorageEmergencyEntered(m.Volume, m.Free, m.Total, m.UsedPct), ct).ConfigureAwait(false);
                if (s.AutoCleanupOnEmergency) await RunEmergencyCleanupAsync(m, s, ct).ConfigureAwait(false);
            }
        }

        // 2) Estado FINAL tras las limpiezas: re-mide y recalcula el conjunto en emergencia (histéresis vs prev).
        var m2 = MeasureVolumes(roots, s);
        var next = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in m2)
            if (m.Total > 0 && IsEmergency(m.UsedPct, prev.Contains(m.Volume), s.EmergencyPercent, HysteresisPoints))
                next.Add(m.Volume);

        // 3) Audita SALIDAS: discos que estaban en emergencia (prev ∪ recién entrados) y ya no lo están.
        var wasEmerg = new HashSet<string>(prev, StringComparer.OrdinalIgnoreCase);
        foreach (var e in justEntered) wasEmerg.Add(e.Volume);
        foreach (var vol in wasEmerg)
            if (!next.Contains(vol))
            {
                var m = m2.FirstOrDefault(x => string.Equals(x.Volume, vol, StringComparison.OrdinalIgnoreCase));
                long free = m?.Free ?? 0, total = m?.Total ?? 0; double used = m?.UsedPct ?? 0;
                _log.LogInformation("Almacenamiento fuera de emergencia: {Used:0.#}% ocupado en «{Vol}» ({Free:N0} bytes libres).", used, vol, free);
                await _bus.PublishAsync(new StorageEmergencyCleared(vol, free, total, used), ct).ConfigureAwait(false);
            }

        // 4) Publica el estado nuevo (para el gate por disco) y la instantánea del PEOR disco (para la UI).
        _emergencyVolumes = next;
        _blockReason = next.Count > 0
            ? $"Almacenamiento en EMERGENCIA en: {string.Join(", ", next.Select(TrimVolume))}. Libera espacio para iniciar nuevas grabaciones."
            : null;
        PublishSnapshot(SnapshotFrom(m2));
    }

    /// <summary>Carpetas de destino a vigilar: las de los canales (cada una en su disco) o, si no hay, la de por
    /// defecto. Se deduplican por VOLUMEN en <see cref="MeasureVolumes"/>.</summary>
    private IReadOnlyList<string> WatchRoots()
    {
        var fromChannels = RecordingRootsProvider?.Invoke();
        IEnumerable<string> roots = fromChannels is { Count: > 0 } ? fromChannels : new[] { RecordingsRoot };
        return roots.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
    }

    /// <summary>Medida por VOLUMEN (deduplicando las carpetas que caen en el mismo disco): libres/total/ocupado/
    /// salud + las carpetas de ese disco (para la limpieza). Lee cada disco UNA vez.</summary>
    private List<VolMeasure> MeasureVolumes(IEnumerable<string> roots, StorageSettings s)
    {
        var byVol = new Dictionary<string, VolMeasure>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var vol = NormalizeVolume(root);
            if (vol is null) continue;
            if (!byVol.TryGetValue(vol, out var m))
            {
                var (free, total) = DiskSpaceGuard.ReadDrive(root);
                double used = total > 0 ? (total - free) * 100.0 / total : 0;
                m = new VolMeasure
                {
                    Volume = vol,
                    Free = free,
                    Total = total,
                    UsedPct = used,
                    Health = total > 0 ? HealthFor(used, s.WarnPercent, s.CriticalPercent, s.EmergencyPercent) : StorageHealth.Unknown,
                };
                byVol[vol] = m;
            }
            m.Folders.Add(root);
        }
        return byVol.Values.ToList();
    }

    /// <summary>Instantánea para la UI: el disco MÁS crítico (mayor % ocupado) + cuántos discos medibles hay + su
    /// etiqueta. Si ninguno es medible, «—». (Fase 4b + multi-disco.)</summary>
    private StorageSnapshot SnapshotFrom(List<VolMeasure> measures)
    {
        var sorted = measures.Where(m => m.Total > 0).OrderByDescending(m => m.UsedPct).ToList();
        if (sorted.Count == 0) return StorageSnapshot.Unknown;
        var worst = sorted[0];
        var vols = sorted.Select(m => new VolumeStatus(TrimVolume(m.Volume), m.Free, m.Total, m.UsedPct, m.Health)).ToList();
        return new StorageSnapshot(worst.Free, worst.Total, worst.UsedPct, worst.Health,
            IsEmergency: _emergencyVolumes.Count > 0, VolumeCount: sorted.Count, WorstLabel: TrimVolume(worst.Volume), Volumes: vols);
    }

    /// <summary>Auto-limpieza de emergencia (opt-in) de UN disco: borra sus grabaciones NO protegidas más antiguas
    /// hasta dejarlo por debajo del umbral crítico. Acotada a ESE volumen (no toca otros discos). RE-MIDE en el
    /// próximo sondeo (la salida de emergencia se decide allí). Respeta protección + archivos en uso (vía StorageManager).</summary>
    private async Task RunEmergencyCleanupAsync(VolMeasure m, StorageSettings s, CancellationToken ct)
    {
        int targetFreePct = CleanupTargetFreePercent(s.CriticalPercent);
        _log.LogWarning("Emergencia: auto-limpieza en «{Vol}» para dejar ≥ {Pct}% libre (borra las NO protegidas más antiguas de ESE disco).", m.Volume, targetFreePct);
        try
        {
            int handled = await _storage.EnforceFreeSpaceAsync(m.Volume, 0, targetFreePct, RetentionAction.Delete, null, ct).ConfigureAwait(false);
            _log.LogInformation("Emergencia: auto-limpieza en «{Vol}» trató {N} archivo(s).", m.Volume, handled);
        }
        catch (Exception ex) { _log.LogError(ex, "Emergencia: la auto-limpieza en «{Vol}» falló.", m.Volume); }
    }

    private void PublishSnapshot(StorageSnapshot snapshot)
    {
        _snapshot = snapshot;
        Changed?.Invoke(this, snapshot);
    }

    /// <summary>Raíz del VOLUMEN de una ruta (p. ej. «D:\»), normalizada, para agrupar/clasificar por disco.
    /// null si la ruta es inválida. (Multi-disco.)</summary>
    internal static string? NormalizeVolume(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { var r = Path.GetPathRoot(Path.GetFullPath(path)); return string.IsNullOrEmpty(r) ? null : r; }
        catch { return null; }
    }

    /// <summary>«D:\» → «D:» para el texto de la UI/mensajes (más legible que la raíz con barra).</summary>
    private static string TrimVolume(string vol) => vol.TrimEnd('\\', '/');

    /// <summary>Nivel de salud del almacenamiento por % OCUPADO para el indicador de la UI (peor primero). 0 = umbral
    /// desactivado. Pura, testeable. (Fase 4b.)</summary>
    internal static StorageHealth HealthFor(double usedPercent, int warnPercent, int criticalPercent, int emergencyPercent)
    {
        if (emergencyPercent > 0 && usedPercent >= emergencyPercent) return StorageHealth.Emergency;
        if (criticalPercent > 0 && usedPercent >= criticalPercent) return StorageHealth.Critical;
        if (warnPercent > 0 && usedPercent >= warnPercent) return StorageHealth.Warning;
        return StorageHealth.Ok;
    }

    /// <summary>Decisión PURA (testeable) del estado de emergencia de UN disco con HISTÉRESIS: entra al alcanzar
    /// <paramref name="emergencyPercent"/>; una vez dentro, solo SALE al bajar de (emergencia − histéresis), para no
    /// oscilar en el borde. <paramref name="emergencyPercent"/> ≤ 0 = desactivado (nunca emergencia). (Fase 3b.)</summary>
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

    /// <summary>Medida de un disco en un sondeo: raíz del volumen, carpetas de destino en él, espacio y salud.</summary>
    private sealed class VolMeasure
    {
        public required string Volume { get; init; }
        public List<string> Folders { get; } = new();
        public long Free { get; set; }
        public long Total { get; set; }
        public double UsedPct { get; set; }
        public StorageHealth Health { get; set; }
    }
}
