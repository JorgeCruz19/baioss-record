using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Licensing;

namespace Baioss.Record.Infrastructure.Licensing;

/// <summary>
/// Estado de licencia del equipo: periodo de prueba de 14 días por defecto y licencia PERPETUA atada a esta
/// máquina. Es también la <see cref="ILicenseGate"/> que consulta el pre-vuelo de grabación.
///
/// <para><b>Sin E/S en la ruta crítica.</b> El estado se recalcula en un bucle de fondo y se publica en un campo
/// <c>volatile</c>. Consultar <see cref="Current"/> o <see cref="ShouldBlockNewRecordings"/> es leer un campo: ni
/// toca disco ni registro ni puede lanzar, así que jamás congela el hilo de UI ni el arranque de una grabación.</para>
///
/// <para><b>A prueba de fallos propios.</b> Si no se puede leer el almacén (permisos, archivo corrupto, error
/// interno), el estado es <see cref="LicenseState.Unknown"/> y <b>NO se bloquea nada</b>: en un grabador 24/7,
/// dejar sin grabar por un fallo del sistema de licencias sería mucho peor que un impago. Solo se bloquea cuando
/// consta con certeza que la prueba terminó y no hay licencia válida.</para>
///
/// <para><b>Nunca se persiste «activado».</b> Del disco solo sale el TEXTO de la clave; que la licencia sea válida
/// se decide verificando la firma contra la huella del equipo en cada recálculo.</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LicenseService : BackgroundService, ILicenseService, ILicenseGate
{
    private readonly IMachineFingerprint _fingerprint;
    private readonly LicenseStore _store;
    private readonly ILogger<LicenseService> _log;
    private readonly object _sync = new();

    // Publicado por el bucle de fondo y leído sin lock por el pre-vuelo y la UI.
    private volatile LicenseInfo _current;
    private long _lastTickTicks;   // reloj MONOTÓNICO: mide el uso real, inmune a que cambien la hora del sistema

    public LicenseService(IMachineFingerprint fingerprint, LicenseStore store, ILogger<LicenseService> log)
    {
        _fingerprint = fingerprint;
        _store = store;
        _log = log;
        _current = new LicenseInfo(LicenseState.Unknown, 0, null, fingerprint.Code);
        _lastTickTicks = Stopwatch.GetTimestamp();
        Refresh(TimeSpan.Zero); // primer cálculo inmediato: la UI ya arranca con el estado real
    }

    /// <summary>Días del periodo de prueba (configurable solo para pruebas internas).</summary>
    public int TrialDays { get; init; } = TrialPeriod.DefaultDays;

    /// <summary>Frecuencia de recálculo: basta con vigilar el paso de los días y acumular uso.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    public LicenseInfo Current => _current;
    public string MachineCode => _fingerprint.Code;
    public event EventHandler<LicenseInfo>? Changed;

    // --- ILicenseGate: lectura de campo, sin E/S, sin excepciones posibles ---
    public bool ShouldBlockNewRecordings => _current.State is LicenseState.Expired;
    public string? BlockReason => ShouldBlockNewRecordings
        ? $"El periodo de prueba ha terminado. Activa la licencia para volver a grabar (código de equipo: {MachineCode})."
        : null;

    public LicenseActivationResult Activate(string licenseKey)
    {
        try
        {
            var rejection = LicenseKey.Verify(licenseKey, _fingerprint.Raw.Span, LicensePublicKey.ProviderPublicKeyBase64);
            if (rejection != LicenseRejection.None)
                return new LicenseActivationResult(false, rejection, MessageFor(rejection));

            lock (_sync)
            {
                var record = _store.Read() ?? NewTrialRecord();
                _store.Write(record with { LicenseKey = licenseKey.Trim() });
            }
            Refresh(TimeSpan.Zero);
            _log.LogInformation("Licencia ACTIVADA para el equipo {Code}.", MachineCode);
            return new LicenseActivationResult(true, LicenseRejection.None, "Licencia activada. Gracias.");
        }
        catch (Exception ex)
        {
            // Ni el botón «Activar» ni el endpoint HTTP pueden reventar por una clave rara.
            _log.LogError(ex, "Licencias: fallo inesperado al activar.");
            return new LicenseActivationResult(false, LicenseRejection.Malformed,
                "No se pudo activar la licencia. Revisa que esté copiada completa.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            // Tiempo REALMENTE transcurrido según un reloj que no se puede manipular desde el panel de control.
            long now = Stopwatch.GetTimestamp();
            var elapsed = Stopwatch.GetElapsedTime(Interlocked.Exchange(ref _lastTickTicks, now), now);
            try { Refresh(elapsed); }
            catch (Exception ex) { _log.LogError(ex, "Licencias: fallo al recalcular el estado; se mantiene el anterior."); }
        }
    }

    /// <summary>Recalcula el estado y, si cambió, lo publica. Suma <paramref name="elapsed"/> al uso acumulado.</summary>
    private void Refresh(TimeSpan elapsed)
    {
        LicenseInfo next;
        lock (_sync)
        {
            LicenseRecord? record;
            try { record = _store.Read(); }
            catch (Exception ex)
            {
                // No se pudo leer: NO se bloquea. Se avisa y se sigue grabando.
                _log.LogError(ex, "Licencias: no se pudo leer el estado; no se bloqueará la grabación.");
                Publish(new LicenseInfo(LicenseState.Unknown, 0, null, MachineCode));
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (record is null)
            {
                record = NewTrialRecord();     // primer arranque: empieza la prueba
                _store.Write(record);
                _log.LogInformation("Periodo de prueba iniciado ({Days} días). Código de equipo: {Code}.", TrialDays, MachineCode);
            }

            // Licencia: se VERIFICA siempre, nunca se confía en un estado guardado.
            if (!string.IsNullOrWhiteSpace(record.LicenseKey) &&
                LicenseKey.Verify(record.LicenseKey, _fingerprint.Raw.Span, LicensePublicKey.ProviderPublicKeyBase64) == LicenseRejection.None)
            {
                next = new LicenseInfo(LicenseState.Licensed, 0, record.StartedAt, MachineCode);
            }
            else
            {
                var advanced = TrialPeriod.Advance(record.ToMark(), now, elapsed);
                if (advanced != record.ToMark()) _store.Write(record with
                {
                    HighWater = advanced.HighWater,
                    UsedSeconds = advanced.UsedSeconds,
                });

                var (days, expired) = TrialPeriod.Evaluate(advanced, now, TrialDays);
                next = new LicenseInfo(expired ? LicenseState.Expired : LicenseState.Trial, days, record.StartedAt, MachineCode);
            }
        }
        Publish(next);
    }

    private LicenseRecord NewTrialRecord()
    {
        var mark = TrialPeriod.Begin(DateTimeOffset.UtcNow);
        return new LicenseRecord(mark.StartedAt, mark.HighWater, mark.UsedSeconds, null);
    }

    private void Publish(LicenseInfo info)
    {
        var previous = _current;
        _current = info;
        if (previous.State != info.State || previous.DaysRemaining != info.DaysRemaining)
        {
            if (previous.State != info.State)
                _log.LogInformation("Estado de licencia: {Previous} → {State} ({Summary}).", previous.State, info.State, info.Summary);
            Changed?.Invoke(this, info);
        }
    }

    private static string MessageFor(LicenseRejection rejection) => rejection switch
    {
        LicenseRejection.Malformed => "La licencia no tiene un formato válido. Cópiala completa, tal como te la enviaron.",
        LicenseRejection.OtherMachine => "Esta licencia no es válida para este equipo. Comprueba que el código de equipo coincide con el que enviaste.",
        LicenseRejection.UnsupportedVersion => "Esta licencia requiere una versión más reciente de Baioss Record.",
        _ => "La licencia no es válida.",
    };
}
