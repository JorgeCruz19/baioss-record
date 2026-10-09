using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Licensing;

namespace Baioss.Record.Infrastructure.Licensing;

/// <summary>
/// Autocomprobación de integridad de los binarios: localiza el manifiesto firmado junto al ejecutable
/// (<see cref="IntegrityManifest.FileName"/>), recalcula el SHA-256 de cada archivo que cubre y publica el
/// veredicto. La CONSECUENCIA la decide <see cref="LicenseService"/>; esta clase solo informa.
///
/// <para><b>Nunca lanza y nunca retrasa el arranque más de un plazo acotado.</b> El cálculo corre en un hilo del
/// pool; la PRIMERA consulta espera como mucho el plazo (3 s por defecto; resumir cinco archivos de pocos MB
/// cuesta milisegundos). Si el disco no responde a tiempo se devuelve «comprobación en curso» —un
/// <see cref="IntegrityVerdict.Unverified"/> que no cambia nada— y el resultado real se recoge en el siguiente
/// ciclo del servicio de licencia (un minuto después). Cualquier excepción es también Unverified.</para>
///
/// <para><b>Una vez por proceso.</b> Lo que está corriendo ya se cargó en memoria: cambiar los archivos del disco
/// solo afecta al PRÓXIMO arranque, así que repetir la comprobación no aportaría nada.</para>
///
/// <para><b>Sin manifiesto no se afirma nada.</b> En desarrollo y en una copia portable no hay manifiesto y es lo
/// normal: se anota en el registro y se sigue igual. Tratar su ausencia como manipulación convertiría un error de
/// empaquetado nuestro en un bloqueo en casa del cliente; es el límite asumido (ver docs/LICENCIAMIENTO.md).</para>
/// </summary>
public sealed class AssemblyIntegrity
{
    private static readonly IntegrityReport Pending = IntegrityReport.Unverified("comprobación en curso");

    private readonly ILogger _log;
    private readonly string _baseDirectory;
    private readonly string _publicKeyBase64;
    private readonly Func<string, string?> _hashFile;
    private readonly TimeSpan _firstWait;
    private readonly Lazy<Task<IntegrityReport>> _check;
    private int _waited; // la espera acotada se concede UNA vez (la del arranque); después, o está o se reintenta al minuto

    public AssemblyIntegrity(ILogger<AssemblyIntegrity> log)
        : this(log, AppContext.BaseDirectory, LicensePublicKey.ProviderPublicKeyBase64, null, TimeSpan.FromSeconds(3)) { }

    /// <summary>Para las pruebas: carpeta propia, clave pública de usar y tirar, lector de resúmenes simulado (que
    /// puede bloquearse, lanzar o tardar) y plazo de la primera espera.</summary>
    internal AssemblyIntegrity(ILogger log, string baseDirectory, string publicKeyBase64, Func<string, string?>? hashFile, TimeSpan firstWait)
    {
        _log = log;
        _baseDirectory = baseDirectory;
        _publicKeyBase64 = publicKeyBase64;
        _hashFile = hashFile ?? HashFileOnDisk;
        _firstWait = firstWait;
        _check = new Lazy<Task<IntegrityReport>>(() => Task.Run(Compute), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>Ruta del manifiesto que se busca (para diagnóstico y soporte).</summary>
    public string ManifestPath => Path.Combine(_baseDirectory, IntegrityManifest.FileName);

    /// <summary>Veredicto vigente. Lectura barata una vez calculado; la primera consulta espera acotadamente.</summary>
    public IntegrityReport Current
    {
        get
        {
            Task<IntegrityReport> task;
            try { task = _check.Value; }
            catch (Exception ex) { return IntegrityReport.Unverified("no se pudo iniciar la comprobación: " + ex.Message); }

            if (!task.IsCompleted && Interlocked.Exchange(ref _waited, 1) == 0)
            {
                try { task.Wait(_firstWait); }
                catch (AggregateException) { /* Compute no lanza; si lo hiciera, se trata abajo como no completada con éxito */ }
            }
            if (task.IsCompletedSuccessfully) return task.Result;
            return task.IsCompleted ? IntegrityReport.Unverified("fallo interno en la comprobación") : Pending;
        }
    }

    private IntegrityReport Compute()
    {
        try
        {
            string path = ManifestPath;
            if (!File.Exists(path))
            {
                _log.LogInformation("Integridad: no hay manifiesto firmado en «{Dir}» (normal en desarrollo o en una copia portable); los binarios quedan SIN verificar, lo que no cambia nada.", _baseDirectory);
                return IntegrityReport.Unverified("no hay manifiesto de integridad en «" + _baseDirectory + "»");
            }

            string text;
            try { text = File.ReadAllText(path); }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Integridad: el manifiesto «{Path}» existe pero no se pudo leer; los binarios quedan SIN verificar, lo que no cambia nada.", path);
                return IntegrityReport.Unverified("no se pudo leer el manifiesto: " + ex.Message);
            }

            var report = IntegrityManifest.Verify(text, _publicKeyBase64, _hashFile);
            switch (report.Verdict)
            {
                case IntegrityVerdict.Intact:
                    _log.LogInformation("Integridad: {Detail}.", report.Detail);
                    break;
                case IntegrityVerdict.Tampered:
                    _log.LogError("Integridad: la instalación NO es la original — {Detail}. La licencia guardada no se aplicará hasta reiniciar el equipo o reinstalar desde el instalador original; las grabaciones en curso no se tocan.", report.Detail);
                    break;
                default:
                    _log.LogWarning("Integridad: hay manifiesto pero no se pudo verificar ({Detail}); los binarios quedan SIN verificar, lo que no cambia nada.", report.Detail);
                    break;
            }
            return report;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Integridad: fallo inesperado en la autocomprobación; no se bloqueará nada.");
            return IntegrityReport.Unverified("fallo interno: " + ex.Message);
        }
    }

    /// <summary>SHA-256 hex del archivo <paramref name="name"/> de la carpeta del producto; null si no existe; lanza
    /// si no se puede leer (el verificador lo anota como ilegible, nunca como modificado).</summary>
    private string? HashFileOnDisk(string name)
    {
        var path = Path.Combine(_baseDirectory, name);
        if (!File.Exists(path)) return null;
        // Compartición amplia: los ensamblados los tiene abiertos este mismo proceso (y a veces el antivirus).
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1 << 16, FileOptions.SequentialScan);
        return IntegrityManifest.HashStream(stream);
    }
}
