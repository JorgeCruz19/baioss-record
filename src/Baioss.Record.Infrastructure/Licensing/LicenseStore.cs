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
/// Almacén del estado de licencia. Guarda POR DUPLICADO —archivo en <c>%ProgramData%</c> y clave del registro del
/// usuario— y al leer combina lo que encuentre quedándose con lo más ESTRICTO (la prueba más antigua, el mayor uso).
///
/// <para><b>Dónde y por qué.</b> En <c>%ProgramData%\Baioss\Record</c>, no en la carpeta del programa: actualizar el
/// producto suele reemplazar esa carpeta entera y se llevaría por delante la marca de la prueba (regalando pruebas
/// infinitas a cada actualización). La copia en el registro cubre el caso de que se borre el archivo.</para>
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

    /// <summary>
    /// Lee el estado combinando las copias disponibles. <c>null</c> si no hay ninguna (primer arranque).
    /// Se toma la fecha de inicio MÁS ANTIGUA y el uso MAYOR: borrar una copia no reinicia la prueba.
    /// </summary>
    public LicenseRecord? Read()
    {
        var fromFile = ReadFile();
        var fromRegistry = ReadRegistry();
        if (fromFile is null) return fromRegistry;
        if (fromRegistry is null) return fromFile;

        return new LicenseRecord(
            StartedAt: fromFile.StartedAt <= fromRegistry.StartedAt ? fromFile.StartedAt : fromRegistry.StartedAt,
            HighWater: fromFile.HighWater >= fromRegistry.HighWater ? fromFile.HighWater : fromRegistry.HighWater,
            UsedSeconds: Math.Max(fromFile.UsedSeconds, fromRegistry.UsedSeconds),
            LicenseKey: fromFile.LicenseKey ?? fromRegistry.LicenseKey);
    }

    /// <summary>Escribe en TODAS las copias que se pueda. Devuelve false si no se pudo en ninguna (sin permisos):
    /// el llamador lo registra y sigue funcionando, nunca bloquea.</summary>
    public bool Write(LicenseRecord record)
    {
        bool any = WriteFile(record);
        any |= WriteRegistry(record);
        if (!any) _log.LogWarning("Licencias: no se pudo guardar el estado en ninguna ubicación (¿permisos?); se sigue funcionando.");
        return any;
    }

    // --- Archivo ---

    private LicenseRecord? ReadFile()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            return Deserialize(File.ReadAllText(_filePath));
        }
        catch (Exception ex) { _log.LogWarning(ex, "Licencias: no se pudo leer «{Path}».", _filePath); return null; }
    }

    private bool WriteFile(LicenseRecord record)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            // Escritura ATÓMICA (tmp + move), igual que los ajustes de almacenamiento: un corte a mitad no deja
            // un archivo medio escrito que luego se leería como manipulado.
            var tmp = _filePath + ".tmp";
            File.WriteAllText(tmp, Serialize(record));
            File.Move(tmp, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Licencias: no se pudo escribir «{Path}».", _filePath); return false; }
    }

    // --- Registro (copia de respaldo) ---

    private LicenseRecord? ReadRegistry()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath);
            return Deserialize(key?.GetValue("State") as string);
        }
        catch (Exception ex) { _log.LogDebug(ex, "Licencias: no se pudo leer el registro."); return null; }
    }

    private bool WriteRegistry(LicenseRecord record)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath);
            key?.SetValue("State", Serialize(record));
            return key is not null;
        }
        catch (Exception ex) { _log.LogDebug(ex, "Licencias: no se pudo escribir el registro."); return false; }
    }

    // --- Serialización firmada ---

    private string Serialize(LicenseRecord record)
    {
        string json = JsonSerializer.Serialize(record);
        string mac = Convert.ToBase64String(HMACSHA256.HashData(_hmacKey, Encoding.UTF8.GetBytes(json)));
        return mac + "." + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Deserializa y COMPRUEBA el HMAC. Si no cuadra (editado a mano o copiado de otro equipo), se
    /// devuelve null: para el llamador equivale a «no hay copia aquí», no a «caducado».</summary>
    private LicenseRecord? Deserialize(string? blob)
    {
        if (string.IsNullOrWhiteSpace(blob)) return null;
        try
        {
            int dot = blob.IndexOf('.');
            if (dot <= 0) return null;
            var mac = Convert.FromBase64String(blob[..dot]);
            var payload = Convert.FromBase64String(blob[(dot + 1)..]);
            var expected = HMACSHA256.HashData(_hmacKey, payload);
            if (!CryptographicOperations.FixedTimeEquals(mac, expected))
            {
                _log.LogWarning("Licencias: una copia del estado no supera la comprobación de integridad; se ignora.");
                return null;
            }
            return JsonSerializer.Deserialize<LicenseRecord>(Encoding.UTF8.GetString(payload));
        }
        catch (Exception ex) { _log.LogDebug(ex, "Licencias: copia del estado ilegible."); return null; }
    }

    private static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var r = new byte[a.Length + b.Length];
        a.CopyTo(r);
        b.CopyTo(r.AsSpan(a.Length));
        return r;
    }
}
