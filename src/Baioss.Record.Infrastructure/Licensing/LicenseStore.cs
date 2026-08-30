using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Baioss.Record.Application.Licensing;

namespace Baioss.Record.Infrastructure.Licensing;

/// <summary>Lo que se persiste: la marca del periodo de prueba y, si la hay, la clave de licencia TAL CUAL.</summary>
/// <remarks>Solo se guarda el TEXTO de la clave, nunca un «ya activado»: el estado <c>Licensed</c> se deriva
/// verificando la firma contra la huella del equipo EN CADA arranque. Si se guardara una bandera, bastaría
/// editarla para activar el producto.</remarks>
public sealed record LicenseRecord(DateTimeOffset StartedAt, DateTimeOffset HighWater, long UsedSeconds, string? LicenseKey)
{
    public TrialMark ToMark() => new(StartedAt, HighWater, UsedSeconds);
}

/// <summary>
/// Resultado de leer el almacén. Distingue TRES situaciones que el servicio debe tratar distinto:
/// <list type="bullet">
/// <item><see cref="Record"/> con valor: hay estado bueno (combinado de las copias válidas).</item>
/// <item><see cref="Record"/> nulo y <see cref="HadReadErrors"/> true: alguna copia NO SE PUDO LEER (bloqueo
/// transitorio del antivirus/backup, registro inaccesible) y ninguna se leyó bien. NO es un primer arranque:
/// sembrar una prueba nueva aquí sobrescribiría —y perdería— el estado bueno que volverá a leerse en el
/// siguiente ciclo. El servicio publica <c>Unknown</c> (que no bloquea) y reintenta.</item>
/// <item><see cref="Record"/> nulo sin errores de lectura: no existe ninguna copia (primer arranque) o las que
/// hay no superan la integridad. En este último caso <see cref="SalvagedKey"/> puede traer la CLAVE rescatada
/// del estado inválido: la clave no es un dato a proteger —se re-verifica criptográficamente contra la huella
/// en cada uso—, pero perderla obligaría al cliente legítimo a reintroducirla a mano. El caso real: un fallo
/// transitorio al leer un identificador del equipo cambia la huella (y con ella la clave del HMAC) UNA sesión;
/// sin el rescate, esa sesión sobrescribía el estado sin la clave y la activación se perdía en silencio.</item>
/// </list>
/// </summary>
public sealed record LicenseReadResult(LicenseRecord? Record, bool HadReadErrors, string? SalvagedKey);

/// <summary>
/// Almacén del estado de licencia. Guarda POR TRIPLICADO —archivo en <c>%ProgramData%</c>, registro del EQUIPO
/// (HKLM) y registro del USUARIO (HKCU)— y al leer combina las copias válidas quedándose con lo más ESTRICTO
/// (la prueba más antigua, el mayor uso).
///
/// <para><b>Dónde y por qué.</b> En <c>%ProgramData%\Baioss\Record</c>, no en la carpeta del programa: actualizar el
/// producto suele reemplazar esa carpeta entera y se llevaría por delante la marca de la prueba (regalando pruebas
/// infinitas a cada actualización). La copia HKLM es COMPARTIDA por todos los usuarios del equipo (cubre «borro el
/// archivo y entro con otra cuenta», que la copia HKCU no cubre); la escribe la app sin elevación porque el
/// instalador concede permiso de escritura a esa clave — sin instalador (desarrollo/portable) simplemente no está,
/// y quedan las otras dos. La copia HKCU cubre el borrado del archivo en instalaciones sin la clave HKLM.</para>
///
/// <para><b>Nunca lanza.</b> Un problema de permisos o un archivo corrupto NO puede impedir grabar: todas las
/// operaciones son «best-effort» y el servicio decide en consecuencia (ver <see cref="LicenseService"/>).</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class LicenseStore
{
    private const string RegistryPath = @"Software\Baioss\Record";
    private readonly ILogger _log;
    private readonly byte[] _hmacKey;
    private readonly string _filePath;

    public LicenseStore(IMachineFingerprint fingerprint, ILogger log)
    {
        _log = log;
        // Clave del HMAC derivada de la huella: una marca de prueba copiada de otro PC no valida aquí.
        _hmacKey = SHA256.HashData(Concat(fingerprint.Raw.Span, "baioss-trial-v1"u8));
        _filePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Baioss", "Record", "license.dat");
    }

    /// <summary>Ruta del archivo de estado (para diagnóstico y soporte).</summary>
    public string FilePath => _filePath;

    // Qué encontró la lectura de UNA copia. La distinción Missing/Unreadable/Invalid es la que evita confundir
    // «no existe» (primer arranque) con «no se pudo leer» (transitorio) o «existe pero no valida» (manipulado o
    // huella cambiada): tratarlas igual era el bug que perdía la clave de licencia ante un hipo de E/S.
    private enum SourceState { Missing, Unreadable, Invalid, Valid }
    private readonly record struct SourceRead(SourceState State, LicenseRecord? Record, string? SalvagedKey);

    /// <summary>
    /// Lee el estado combinando las copias VÁLIDAS disponibles. Ver <see cref="LicenseReadResult"/> para la
    /// semántica de cada resultado. Se toma la fecha de inicio MÁS ANTIGUA y el uso MAYOR: borrar una copia no
    /// reinicia la prueba.
    /// </summary>
    public LicenseReadResult Read()
    {
        var sources = new[] { ReadFile(), ReadMachineRegistry(), ReadUserRegistry() };

        LicenseRecord? merged = null;
        foreach (var s in sources)
        {
            if (s.State is not SourceState.Valid) continue;
            merged = merged is null ? s.Record : Merge(merged, s.Record!);
        }

        bool hadReadErrors = sources.Any(s => s.State is SourceState.Unreadable);
        string? salvaged = merged is null
            ? sources.Select(s => s.SalvagedKey).FirstOrDefault(k => !string.IsNullOrWhiteSpace(k))
            : null;
        return new LicenseReadResult(merged, hadReadErrors, salvaged);
    }

    private static LicenseRecord Merge(LicenseRecord a, LicenseRecord b) => new(
        StartedAt: a.StartedAt <= b.StartedAt ? a.StartedAt : b.StartedAt,
        HighWater: a.HighWater >= b.HighWater ? a.HighWater : b.HighWater,
        UsedSeconds: Math.Max(a.UsedSeconds, b.UsedSeconds),
        LicenseKey: a.LicenseKey ?? b.LicenseKey);

    /// <summary>Escribe en TODAS las copias que se pueda. Devuelve false si no se pudo en NINGUNA (sin permisos):
    /// el llamador decide qué decirle al operador, pero nunca se bloquea nada por ello.</summary>
    public bool Write(LicenseRecord record)
    {
        string blob = Serialize(record);
        bool any = WriteFile(blob);
        any |= WriteMachineRegistry(blob);
        any |= WriteUserRegistry(blob);
        if (!any) _log.LogWarning("Licencias: no se pudo guardar el estado en ninguna ubicación (¿permisos?); se sigue funcionando.");
        return any;
    }

    // --- Archivo ---

    private SourceRead ReadFile()
    {
        try
        {
            if (!File.Exists(_filePath)) return new(SourceState.Missing, null, null);
            return Parse(File.ReadAllText(_filePath), "archivo");
        }
        catch (Exception ex)
        {
            // Ilegible ≠ ausente: un bloqueo transitorio (antivirus, copia de seguridad) no es un primer arranque.
            _log.LogWarning(ex, "Licencias: no se pudo leer «{Path}» (transitorio); se reintentará.", _filePath);
            return new(SourceState.Unreadable, null, null);
        }
    }

    private bool WriteFile(string blob)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            // Escritura ATÓMICA (tmp + move) CON volcado físico: sin el Flush(true), un corte de energía justo
            // tras el rename podía dejar el archivo final vacío o a medias aunque el rename «hubiera ocurrido».
            var tmp = _filePath + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(blob);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Licencias: no se pudo escribir «{Path}».", _filePath); return false; }
    }

    // --- Registro del EQUIPO (HKLM, compartido por todos los usuarios) ---

    private SourceRead ReadMachineRegistry()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(RegistryPath);
            return Parse(key?.GetValue("State") as string, "registro del equipo");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Licencias: no se pudo leer el registro del equipo (transitorio).");
            return new(SourceState.Unreadable, null, null);
        }
    }

    private bool WriteMachineRegistry(string blob)
    {
        try
        {
            // NO se crea la clave: la crea el INSTALADOR (elevado) concediendo escritura a Usuarios. Sin
            // instalador (desarrollo/portable) la clave no existe y esta copia simplemente se omite.
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(RegistryPath, writable: true);
            if (key is null) return false;
            key.SetValue("State", blob);
            return true;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Licencias: no se pudo escribir el registro del equipo."); return false; }
    }

    // --- Registro del USUARIO (HKCU, copia de respaldo por cuenta) ---

    private SourceRead ReadUserRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return Parse(key?.GetValue("State") as string, "registro del usuario");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Licencias: no se pudo leer el registro del usuario (transitorio).");
            return new(SourceState.Unreadable, null, null);
        }
    }

    private bool WriteUserRegistry(string blob)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue("State", blob);
            return key is not null;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Licencias: no se pudo escribir el registro del usuario."); return false; }
    }

    // --- Serialización firmada ---

    private string Serialize(LicenseRecord record)
    {
        string json = JsonSerializer.Serialize(record);
        string mac = Convert.ToBase64String(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(json)));
        return mac + "." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Deserializa y COMPRUEBA el HMAC. Una copia que no cuadra (editada a mano, copiada de otro equipo
    /// o escrita con una huella distinta) se marca <c>Invalid</c> y NO aporta marcas de prueba, pero se intenta
    /// RESCATAR su clave de licencia: la clave se re-verifica criptográficamente siempre, así que conservarla no
    /// regala nada y perderla castigaría al cliente legítimo.</summary>
    private SourceRead Parse(string? blob, string origin)
    {
        if (string.IsNullOrWhiteSpace(blob)) return new(SourceState.Missing, null, null);
        try
        {
            int dot = blob.IndexOf('.');
            if (dot <= 0) return new(SourceState.Invalid, null, null);
            var mac = Convert.FromBase64String(blob[..dot]);
            var payload = Convert.FromBase64String(blob[(dot + 1)..]);
            string json = Encoding.UTF8.GetString(payload);
            var expected = HMACSHA256.HashData(_hmacKey, payload);
            if (!CryptographicOperations.FixedTimeEquals(mac, expected))
            {
                _log.LogWarning("Licencias: la copia de {Origin} no supera la comprobación de integridad; se ignoran sus marcas.", origin);
                return new(SourceState.Invalid, null, SalvageKey(json));
            }
            var record = JsonSerializer.Deserialize<LicenseRecord>(json);
            return record is null
                ? new(SourceState.Invalid, null, SalvageKey(json))
                : new(SourceState.Valid, record, null);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Licencias: copia de {Origin} ilegible.", origin);
            return new(SourceState.Invalid, null, null);
        }
    }

    private static string? SalvageKey(string json)
    {
        try
        {
            var record = JsonSerializer.Deserialize<LicenseRecord>(json);
            return string.IsNullOrWhiteSpace(record?.LicenseKey) ? null : record!.LicenseKey;
        }
        catch { return null; }
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r);
        b.CopyTo(r.AsSpan(a.Length));
        return r;
    }
}
