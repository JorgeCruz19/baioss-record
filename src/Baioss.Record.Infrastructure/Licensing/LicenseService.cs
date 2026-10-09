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
///
/// <para><b>Manipulación evidente ≠ fallo propio.</b> La autocomprobación de los binarios
/// (<see cref="AssemblyIntegrity"/>) distingue dos cosas que hasta ahora se confundían bajo «no verificable»: un
/// fallo GENUINO al comprobar (sin manifiesto, firma ilegible, archivo bloqueado por el antivirus, resumen no
/// calculable) sigue siendo fail-open y no cambia nada; una evidencia POSITIVA (manifiesto auténtico y un archivo
/// leído con éxito que no coincide) hace que la clave de licencia guardada NO se aplique: la instalación se trata
/// como no licenciada y manda el periodo de prueba. Ni aun así se interrumpe nada en curso, y el operador recibe
/// el motivo y la salida (reiniciar o reinstalar).</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LicenseService : BackgroundService, ILicenseService, ILicenseGate
{
    private static readonly IntegrityReport NotChecked = IntegrityReport.Unverified("sin autocomprobación de integridad");

    private readonly IMachineFingerprint _fingerprint;
    private readonly LicenseStore _store;
    private readonly AssemblyIntegrity? _integrity;
    private readonly ILogger<LicenseService> _log;
    private readonly object _sync = new();

    // Publicado por el bucle de fondo y leído sin lock por el pre-vuelo y la UI.
    private volatile LicenseInfo _current;
    private long _lastTickTicks;   // reloj MONOTÓNICO: mide el uso real, inmune a que cambien la hora del sistema
    private bool _tamperIgnoredLogged; // el aviso «clave válida pero no aplicada» se escribe una vez, no cada minuto

    /// <summary>Composición de producción. La autocomprobación de los binarios es opcional: sin ella (o si no se
    /// registró) todo queda «sin verificar», que no cambia nada.</summary>
    public LicenseService(IMachineFingerprint fingerprint, LicenseStore store, ILogger<LicenseService> log, AssemblyIntegrity? integrity = null)
        : this(fingerprint, store, log, LicensePublicKey.ProviderPublicKeyBase64, integrity) { }

    /// <summary>Para las pruebas: verificar contra una clave pública de usar y tirar (la privada del proveedor no está en
    /// el repositorio, así que con la pública real no se puede fabricar ninguna licencia válida en una prueba).</summary>
    internal LicenseService(IMachineFingerprint fingerprint, LicenseStore store, ILogger<LicenseService> log, string publicKeyBase64,
        AssemblyIntegrity? integrity = null)
    {
        _fingerprint = fingerprint;
        _store = store;
        _integrity = integrity;
        _log = log;
        _publicKeyBase64 = publicKeyBase64;
        _current = new LicenseInfo(LicenseState.Unknown, 0, null, fingerprint.Code);
        _lastTickTicks = Stopwatch.GetTimestamp();
        Refresh(TimeSpan.Zero); // primer cálculo inmediato: la UI ya arranca con el estado real
    }

    private readonly string _publicKeyBase64;

    /// <summary>Un ciclo del bucle de fondo, a demanda (solo para las pruebas).</summary>
    internal void RefreshForTests(TimeSpan elapsed) => Refresh(elapsed);

    /// <summary>Días del periodo de prueba (configurable solo para pruebas internas).</summary>
    public int TrialDays { get; init; } = TrialPeriod.DefaultDays;

    /// <summary>Frecuencia de recálculo: basta con vigilar el paso de los días y acumular uso.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMinutes(1);

    public LicenseInfo Current => _current;
    public string MachineCode => _fingerprint.Code;
    public event EventHandler<LicenseInfo>? Changed;

    // --- ILicenseGate: lectura de campo, sin E/S, sin excepciones posibles ---
    public bool ShouldBlockNewRecordings => _current.State is LicenseState.Expired;
    public string? BlockReason
    {
        get
        {
            var current = _current; // una sola lectura del campo volatile: estado y veredicto de la MISMA instantánea
            if (current.State is not LicenseState.Expired) return null;
            // Con la instalación modificada, el motivo dice la verdad útil: la licencia (si la hay) no se aplica
            // y cómo recuperarse; «activa la licencia» a secas despistaría a un cliente que ya la tiene.
            return current.IsTampered
                ? Application.Localization.Localizer.F("Lic_Blocked_Tampered", MachineCode)
                : Application.Localization.Localizer.F("Lic_Blocked", MachineCode);
        }
    }

    public LicenseActivationResult Activate(string licenseKey)
    {
        try
        {
            var rejection = LicenseKey.Verify(licenseKey, _fingerprint.Raw.Span, _publicKeyBase64);
            if (rejection != LicenseRejection.None)
                return new LicenseActivationResult(false, rejection, MessageFor(rejection));

            bool saved;
            lock (_sync)
            {
                var read = _store.Read();
                var record = read.Record ?? (NewTrialRecord() with { LicenseKey = read.SalvagedKey });
                saved = _store.Write(record with { LicenseKey = licenseKey.Trim() });
            }
            Refresh(TimeSpan.Zero);

            if (!saved)
            {
                // La clave ES válida pero no quedó guardada en NINGUNA ubicación: decir «activada» sería mentir
                // — al caducar la prueba el equipo se bloquearía pese a la confirmación. Rejection.None con
                // Success=false distingue «válida pero sin persistir» de una clave rechazada (quien deja la
                // licencia pendiente del instalador usa esa distinción para reintentar en el próximo arranque).
                _log.LogError("Licencias: la clave es válida pero NO se pudo guardar en ninguna ubicación (¿permisos de «{Path}»?).", _store.FilePath);
                return new LicenseActivationResult(false, LicenseRejection.None,
                    Application.Localization.Localizer.F("Lic_Msg_NotSaved", _store.FilePath));
            }

            var after = _current;
            if (after.State is not LicenseState.Licensed && after.IsTampered)
            {
                // La clave es auténtica y QUEDÓ GUARDADA (sobrevive a reinstalar), pero no se aplica mientras haya
                // evidencia positiva de modificación. Decir «activada» y reiniciar «para aplicar los canales» sería
                // engañar: la app volvería en periodo de prueba. Success=false + Rejection.None, como «válida pero
                // sin guardar»: la licencia pendiente del instalador se conserva y se reintenta al próximo arranque.
                _log.LogWarning("Licencias: clave válida GUARDADA pero NO aplicada: la instalación está modificada ({Files}).", after.IntegrityDetail);
                return new LicenseActivationResult(false, LicenseRejection.None,
                    Application.Localization.Localizer.T("Lic_Msg_SavedButTampered"));
            }

            _log.LogInformation("Licencia ACTIVADA para el equipo {Code}.", MachineCode);
            return new LicenseActivationResult(true, LicenseRejection.None, Application.Localization.Localizer.T("Lic_Msg_Activated"));
        }
        catch (Exception ex)
        {
            // Ni el botón «Activar» ni el endpoint HTTP pueden reventar por una clave rara.
            _log.LogError(ex, "Licencias: fallo inesperado al activar.");
            return new LicenseActivationResult(false, LicenseRejection.Malformed,
                Application.Localization.Localizer.T("Lic_Msg_ActivateFailed"));
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

    /// <summary>Recalcula el estado y, si cambió, lo publica. Suma <paramref name="elapsed"/> al uso acumulado.
    /// La publicación va DENTRO del lock: si fuera después de soltarlo, una instantánea calculada ANTES de una
    /// activación podría publicarse DESPUÉS y pisar el estado «Licensed» recién activado con un «Expired» viejo
    /// (bloqueando grabaciones nuevas hasta el siguiente ciclo). Los suscriptores de <see cref="Changed"/> solo
    /// despachan al hilo de UI (BeginInvoke), así que no hay riesgo de reentrada bajo el lock.</summary>
    private void Refresh(TimeSpan elapsed)
    {
        lock (_sync)
        {
            // Veredicto de integridad de los binarios (una vez por proceso; la primera consulta espera acotadamente).
            // Viaja en cada LicenseInfo para la UI/API; solo Tampered tiene consecuencias (más abajo).
            var integrity = _integrity?.Current ?? NotChecked;
            string integrityDetail = integrity.Verdict is IntegrityVerdict.Tampered
                ? string.Join(", ", integrity.MismatchedFiles)
                : integrity.Detail;

            LicenseReadResult read;
            try { read = _store.Read(); }
            catch (Exception ex)
            {
                // No se pudo leer: NO se bloquea. Se avisa y se sigue grabando.
                _log.LogError(ex, "Licencias: no se pudo leer el estado; no se bloqueará la grabación.");
                Publish(new LicenseInfo(LicenseState.Unknown, 0, null, MachineCode, null, integrity.Verdict, integrityDetail));
                return;
            }

            // Lectura fallida TRANSITORIA (archivo bloqueado por antivirus/copia de seguridad, registro
            // inaccesible) sin ninguna copia buena: NI se bloquea NI —crucial— se siembra una prueba nueva,
            // porque eso sobrescribiría el estado bueno que volverá a ser legible en el próximo ciclo (y con
            // él, la clave de licencia). Se publica Unknown (que no bloquea) y se reintenta. Con la instalación
            // modificada TAMBIÉN: sin saber con certeza que la prueba terminó, no hay nada que bloquear.
            if (read.Record is null && read.HadReadErrors)
            {
                Publish(new LicenseInfo(LicenseState.Unknown, 0, null, MachineCode, null, integrity.Verdict, integrityDetail));
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var record = read.Record;
            if (record is null)
            {
                // Primer arranque de verdad (no existe NINGUNA copia) o estado presente pero inválido: empieza
                // la prueba. Del estado inválido se CONSERVA la clave rescatada: no se «cree» en ella — se
                // re-verifica contra la huella como siempre —, pero perderla castigaría al cliente legítimo
                // cuya huella cambió una sesión por un fallo transitorio de lectura.
                record = NewTrialRecord() with { LicenseKey = read.SalvagedKey };
                _store.Write(record);
                if (read.SalvagedKey is not null)
                    _log.LogWarning("Licencias: el estado guardado no era válido; se reinicia la prueba CONSERVANDO la clave de licencia (se re-verificará).");
                else
                    _log.LogInformation("Periodo de prueba iniciado ({Days} días). Código de equipo: {Code}.", TrialDays, MachineCode);
            }

            LicenseInfo next;
            // Licencia: se VERIFICA siempre, nunca se confía en un estado guardado.
            bool keyValid = !string.IsNullOrWhiteSpace(record.LicenseKey) &&
                LicenseKey.Verify(record.LicenseKey, _fingerprint.Raw.Span, _publicKeyBase64) == LicenseRejection.None;
            if (keyValid && integrity.Verdict is not IntegrityVerdict.Tampered)
            {
                // Los canales pagados viajan FIRMADOS dentro de la clave: se releen de ella (la firma ya validó,
                // así que el TryDecode no puede fallar aquí; el ?: es solo cinturón).
                int? channels = LicenseKey.TryDecode(record.LicenseKey, out var payload) ? payload!.Channels : null;
                next = new LicenseInfo(LicenseState.Licensed, 0, record.StartedAt, MachineCode, channels, integrity.Verdict, integrityDetail);
            }
            else
            {
                // Evidencia POSITIVA de modificación de los binarios: la clave, aunque sea auténtica, NO se aplica.
                // La instalación se trata como no licenciada y manda el periodo de prueba (que sigue contando y
                // caducando como siempre). Un fallo NUESTRO al comprobar (Unverified) no entra aquí jamás.
                if (keyValid && !_tamperIgnoredLogged)
                {
                    _tamperIgnoredLogged = true;
                    _log.LogError("Licencias: la clave guardada es válida pero NO se aplica porque la instalación está modificada ({Files}). Reinicia el equipo o reinstala desde el instalador original.", integrityDetail);
                }

                var advanced = TrialPeriod.Advance(record.ToMark(), now, elapsed);
                if (advanced != record.ToMark()) _store.Write(record with
                {
                    HighWater = advanced.HighWater,
                    UsedSeconds = advanced.UsedSeconds,
                });

                var (days, expired) = TrialPeriod.Evaluate(advanced, now, TrialDays);
                next = new LicenseInfo(expired ? LicenseState.Expired : LicenseState.Trial, days, record.StartedAt, MachineCode,
                    null, integrity.Verdict, integrityDetail);
            }
            Publish(next);
        }
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
        // El veredicto de integridad también cuenta como cambio: pasa de «en curso» al real en el primer ciclo
        // tras un arranque lento, y la UI/API deben reflejarlo (el detalle lo registra ya AssemblyIntegrity).
        if (previous.State != info.State || previous.DaysRemaining != info.DaysRemaining || previous.Integrity != info.Integrity)
        {
            if (previous.State != info.State)
                _log.LogInformation("Estado de licencia: {Previous} → {State} ({Summary}).", previous.State, info.State, info.Summary);
            Changed?.Invoke(this, info);
        }
    }

    private static string MessageFor(LicenseRejection rejection) => rejection switch
    {
        LicenseRejection.Malformed => Application.Localization.Localizer.T("Lic_Msg_Malformed"),
        LicenseRejection.OtherMachine => Application.Localization.Localizer.T("Lic_Msg_OtherMachine"),
        LicenseRejection.UnsupportedVersion => Application.Localization.Localizer.T("Lic_Msg_UnsupportedVersion"),
        _ => Application.Localization.Localizer.T("Lic_Msg_Invalid"),
    };
}
