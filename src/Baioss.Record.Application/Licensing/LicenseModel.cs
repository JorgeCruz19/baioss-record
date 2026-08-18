namespace Baioss.Record.Application.Licensing;

/// <summary>Estado de la licencia del equipo.</summary>
public enum LicenseState
{
    /// <summary>Periodo de prueba en curso.</summary>
    Trial,
    /// <summary>Licencia válida y perpetua para ESTE equipo.</summary>
    Licensed,
    /// <summary>El periodo de prueba terminó y no hay licencia: no se pueden INICIAR grabaciones nuevas.</summary>
    Expired,
    /// <summary>
    /// No se pudo determinar el estado (no se pudo leer el almacén, permisos, error interno del subsistema).
    /// <b>NO bloquea</b>: en un grabador 24/7 impedir grabar por un fallo NUESTRO sería peor que cualquier
    /// impago. Se avisa por log y en la barra, y se sigue grabando hasta poder determinarlo.
    /// </summary>
    Unknown,
}

/// <summary>Motivo por el que una clave de licencia fue rechazada, para poder decírselo al operador con precisión
/// («esta licencia es de otro equipo» no es lo mismo que «te has dejado un carácter»).</summary>
public enum LicenseRejection
{
    None,
    /// <summary>El texto no es una clave bien formada (caracteres inválidos o longitud incorrecta).</summary>
    Malformed,
    /// <summary>La firma no es válida: la clave está alterada o no la emitió el proveedor.</summary>
    BadSignature,
    /// <summary>La clave es auténtica pero fue emitida para OTRO equipo.</summary>
    OtherMachine,
    /// <summary>Formato de clave de una versión más nueva que esta app.</summary>
    UnsupportedVersion,
}

/// <summary>
/// Estado de licencia consultable por la UI/API. <see cref="DaysRemaining"/> solo tiene sentido en
/// <see cref="LicenseState.Trial"/> (una licencia es perpetua y no caduca).
/// </summary>
public sealed record LicenseInfo(
    LicenseState State,
    int DaysRemaining,
    DateTimeOffset? TrialStartedAt,
    string MachineCode)
{
    /// <summary>¿Se pueden iniciar grabaciones nuevas? Solo se bloquea al CADUCAR el periodo de prueba.</summary>
    public bool CanStartRecording => State is not LicenseState.Expired;

    /// <summary>Texto corto para la barra superior de la app.</summary>
    public string Summary => State switch
    {
        LicenseState.Licensed => "Licencia activa",
        LicenseState.Trial => DaysRemaining == 1 ? "Prueba: último día" : $"Prueba: {DaysRemaining} días restantes",
        LicenseState.Unknown => "Licencia: no verificable",
        _ => "Periodo de prueba terminado",
    };

    /// <summary>¿Conviene avisar al operador de forma visible? (últimos días de prueba, caducada o no verificable).</summary>
    public bool NeedsAttention => State is LicenseState.Expired or LicenseState.Unknown
                                  || (State is LicenseState.Trial && DaysRemaining <= 7);
}

/// <summary>
/// Compuerta de licencia consultada en el PRE-VUELO de una grabación. Se separa de <see cref="ILicenseService"/>
/// para que la ruta crítica (¿puedo empezar a grabar?) sea una lectura de campo sin E/S y sin posibilidad de
/// lanzar. <b>Solo</b> se consulta al INICIAR una grabación pedida por una persona, la API o el programador;
/// jamás en la recuperación tras una caída, ni al cortar segmento, ni al detener.
/// </summary>
public interface ILicenseGate
{
    /// <summary>True solo si el periodo de prueba CADUCÓ y no hay licencia. Ante cualquier duda devuelve false.</summary>
    bool ShouldBlockNewRecordings { get; }

    /// <summary>Motivo legible del bloqueo, o null.</summary>
    string? BlockReason { get; }
}

/// <summary>
/// Bloqueo por licencia al iniciar una grabación. Es un tipo PROPIO (y no la excepción genérica) para que el
/// programador de grabaciones lo distinga: una franja programada que no arranca por licencia NO debe darse por
/// perdida —el operador puede activar la licencia a mitad de la franja y debe grabarse el resto—.
/// </summary>
public sealed class LicenseBlockedException : InvalidOperationException
{
    public LicenseBlockedException(string message) : base(message) { }
}

/// <summary>Resultado de intentar activar una clave de licencia.</summary>
/// <remarks><c>Success=false</c> con <see cref="LicenseRejection.None"/> significa «la clave ES válida pero no
/// se pudo GUARDAR» (permisos): el llamador puede reintentar más tarde — la licencia pendiente del instalador se
/// conserva en ese caso — en vez de dar la clave por mala.</remarks>
public sealed record LicenseActivationResult(bool Success, LicenseRejection Rejection, string Message);

/// <summary>
/// Estado de licencia del equipo y activación de la clave. Lo implementa la infraestructura.
/// </summary>
public interface ILicenseService
{
    /// <summary>Estado vigente (se recalcula al consultarlo; barato).</summary>
    LicenseInfo Current { get; }

    /// <summary>Código de ESTE equipo, que el cliente envía al proveedor para que le emita la licencia.</summary>
    string MachineCode { get; }

    /// <summary>Valida y GUARDA una clave de licencia. Solo se acepta si fue emitida para este equipo.</summary>
    LicenseActivationResult Activate(string licenseKey);

    /// <summary>Se eleva cuando el estado cambia (activación, o el periodo de prueba que expira estando abierta la app).</summary>
    event EventHandler<LicenseInfo>? Changed;
}

/// <summary>
/// Huella estable del equipo. Se usa para (a) mostrar el «código de equipo» y (b) atar la licencia a esta máquina.
/// </summary>
public interface IMachineFingerprint
{
    /// <summary>Bytes de la huella (los que se firman). Estables entre reinicios del MISMO equipo.</summary>
    ReadOnlyMemory<byte> Raw { get; }

    /// <summary>Representación legible para el operador (Base32 Crockford, en grupos).</summary>
    string Code { get; }
}
