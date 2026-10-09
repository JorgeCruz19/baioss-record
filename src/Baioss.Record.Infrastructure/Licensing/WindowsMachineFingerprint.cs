using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Baioss.Record.Application.Licensing;

namespace Baioss.Record.Infrastructure.Licensing;

/// <summary>
/// Huella del equipo en Windows. Se calcula UNA sola vez por proceso y se cachea.
///
/// <para><b>Dos niveles</b> (ver <see cref="MachineFingerprintComposer"/>). Si el equipo tiene un chip TPM 2.0, la
/// huella es SOLO su clave de endoso, leída del propio chip (<see cref="TpmEndorsementKey"/>): un clon del disco
/// arranca en otro chip y obtiene otra huella, así que la licencia no vale en el clon. Si no hay TPM utilizable, la
/// huella es la clásica: MachineGuid, serial del volumen del sistema, seriales de placa y de equipo, y UUID de
/// sistema del SMBIOS.</para>
///
/// <para><b>Sin WMI, a propósito.</b> Consultar <c>Win32_BaseBoard</c> u otras clases WMI puede tardar segundos o
/// fallar si el servicio no está listo al arrancar; como de la huella depende poder GRABAR, un hipo de WMI se
/// convertiría en un corte de emisión. Todo se lee del chip, del REGISTRO y de API nativas, que responden al
/// instante: Windows ya vuelca en el registro los datos de SMBIOS.</para>
///
/// <para><b>Qué NO se usa.</b> El «ProcessorId» típico de estos esquemas: no es un número de serie sino la firma
/// CPUID (familia/modelo/stepping), idéntica en todos los chips del mismo modelo, así que no aporta unicidad y solo
/// daría falsa sensación de seguridad.</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // TPM, registro y API nativas: el producto es Windows-only
public sealed class WindowsMachineFingerprint : IMachineFingerprint
{
    private readonly ILogger<WindowsMachineFingerprint> _log;
    private readonly byte[] _raw;

    public WindowsMachineFingerprint(ILogger<WindowsMachineFingerprint> log)
        : this(log, () => TpmEndorsementKey.TryRead()) { }

    /// <summary>Para las pruebas: con un lector simulado se ejercitan los DOS niveles en cualquier equipo (el real solo
    /// recorre el nivel que tenga su hardware).</summary>
    internal WindowsMachineFingerprint(ILogger<WindowsMachineFingerprint> log, Func<TpmEndorsementKey.ReadResult> readEndorsementKey)
    {
        _log = log;

        // Nivel 1: el chip TPM. Su clave de endoso no está en el disco: es lo único que un clon no puede llevarse.
        TpmEndorsementKey.ReadResult tpm;
        try { tpm = readEndorsementKey(); }
        catch (Exception ex)
        {
            // El lector real no lanza por contrato; esto es cinturón: un fallo aquí jamás puede impedir arrancar la app.
            tpm = new TpmEndorsementKey.ReadResult(null, $"fallo inesperado al leer el TPM ({ex.GetType().Name})");
        }
        if (tpm.PublicArea is { } endorsementKey)
        {
            _raw = MachineFingerprintComposer.ComposeTpm(endorsementKey);
            Code = MachineFingerprintComposer.ToCode(_raw);
            TpmAnchored = true;
            _log.LogInformation("Huella del equipo {Code}: anclada al chip TPM ({Detail}). Una copia del disco en otro equipo no valida la licencia.", Code, tpm.Detail);
            return;
        }

        // Nivel 2: huella clásica (equipos sin TPM 2.0 utilizable).
        string? machineGuid = ReadMachineGuid();
        string? volumeSerial = ReadSystemVolumeSerial();
        string? boardSerial = ReadBiosValue("BaseBoardSerialNumber");
        string? systemSerial = ReadBiosValue("SystemSerialNumber");
        string? systemUuid = SmbiosSystemUuid.TryRead();

        // ORDEN FIJO: cambiarlo invalidaría todas las licencias ya emitidas.
        _raw = MachineFingerprintComposer.Compose(machineGuid, volumeSerial, boardSerial, systemSerial, systemUuid);
        Code = MachineFingerprintComposer.ToCode(_raw);

        // Se registra QUÉ componentes participaron y por qué no hubo TPM: si un día una licencia deja de validar, el
        // registro dice por qué.
        // Sin recomendar activar el TPM: en un equipo YA licenciado eso cambia el código de equipo y deja la licencia sin validar.
        _log.LogInformation("Huella del equipo {Code}: clásica, sin ancla en el TPM ({Why}). Componentes presentes: {Components}. " +
                            "Si el TPM 2.0 se activa más adelante, el código de equipo cambiará y la licencia habrá que emitirla de nuevo.",
            Code, tpm.Detail,
            string.Join(", ", Present(("MachineGuid", machineGuid), ("VolumenSistema", volumeSerial),
                                      ("SerialPlaca", boardSerial), ("SerialEquipo", systemSerial), ("UuidSistema", systemUuid))));

        if (!MachineFingerprintComposer.HasAnyRealIdentifier(machineGuid, volumeSerial, boardSerial, systemSerial, systemUuid))
            _log.LogError("Este equipo no expone NINGÚN identificador utilizable: la licencia no podrá atarse de forma fiable.");
    }

    public ReadOnlyMemory<byte> Raw => _raw;
    public string Code { get; }

    /// <summary>True si la huella es la del chip TPM (nivel 1); false si es la clásica.</summary>
    public bool TpmAnchored { get; }

    private static IEnumerable<string> Present(params (string Name, string? Value)[] parts)
        => parts.Where(p => MachineFingerprintComposer.Normalize(p.Value).Length > 0).Select(p => p.Name).DefaultIfEmpty("ninguno");

    /// <summary>GUID que Windows genera al INSTALARSE. Vista de 64 bits explícita: desde un proceso de 32 bits, la
    /// redirección del registro devolvería otra rama y la huella cambiaría.</summary>
    private string? ReadMachineGuid()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch (Exception ex) { _log.LogWarning(ex, "No se pudo leer MachineGuid: la huella de ESTA sesión puede diferir de la registrada."); return null; }
    }

    /// <summary>Serial de SMBIOS que Windows publica en el registro (sin WMI). Muchos equipos lo dejan vacío o con
    /// un valor de relleno: el compositor ya los descarta.</summary>
    private string? ReadBiosValue(string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return key?.GetValue(name) as string;
        }
        catch (Exception ex) { _log.LogWarning(ex, "No se pudo leer {Name} de BIOS: la huella de ESTA sesión puede diferir de la registrada.", name); return null; }
    }

    /// <summary>Nº de serie del volumen del sistema (cambia al reformatear, no al mover el disco de equipo).</summary>
    private string? ReadSystemVolumeSerial()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? @"C:\";
            var label = new StringBuilder(261);
            var fs = new StringBuilder(261);
            if (GetVolumeInformationW(root, label, label.Capacity, out uint serial, out _, out _, fs, fs.Capacity))
                return serial.ToString("X8");
            // Un fallo aquí NO puede pasar en silencio: sin este componente la huella de ESTA sesión difiere de
            // la registrada y la licencia dejaría de validar hasta el siguiente arranque sano.
            _log.LogWarning("GetVolumeInformationW({Root}) devolvió false (error {Err}): la huella de ESTA sesión puede diferir de la registrada.",
                root, System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            return null;
        }
        catch (Exception ex) { _log.LogWarning(ex, "No se pudo leer el serial del volumen del sistema: la huella de ESTA sesión puede diferir de la registrada."); return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, int fileSystemNameSize);
}
