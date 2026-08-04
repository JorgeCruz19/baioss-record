using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Baioss.Record.Application.Licensing;

namespace Baioss.Record.Infrastructure.Licensing;

/// <summary>
/// Huella del equipo en Windows. Se calcula UNA sola vez por proceso y se cachea.
///
/// <para><b>Sin WMI, a propósito.</b> Consultar <c>Win32_BaseBoard</c> u otras clases WMI puede tardar segundos o
/// fallar si el servicio no está listo al arrancar; como de la huella depende poder GRABAR, un hipo de WMI se
/// convertiría en un corte de emisión. Todos los identificadores se leen del REGISTRO y de una API nativa, que
/// responden siempre y al instante: Windows ya vuelca ahí los datos de SMBIOS.</para>
///
/// <para><b>Qué se usa y qué NO.</b> Se usan MachineGuid (el más fuerte: se genera por instalación de Windows),
/// el nº de serie del volumen del sistema y los seriales de placa/equipo de SMBIOS. NO se usa el «ProcessorId»
/// típico de estos esquemas: no es un número de serie sino la firma CPUID (familia/modelo/stepping), idéntica en
/// todos los chips del mismo modelo, así que no aporta unicidad y solo daría falsa sensación de seguridad.</para>
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")] // registro + API nativa: el producto es Windows-only
public sealed class WindowsMachineFingerprint : IMachineFingerprint
{
    private readonly ILogger<WindowsMachineFingerprint> _log;
    private readonly byte[] _raw;

    public WindowsMachineFingerprint(ILogger<WindowsMachineFingerprint> log)
    {
        _log = log;

        string? machineGuid = ReadMachineGuid();
        string? volumeSerial = ReadSystemVolumeSerial();
        string? boardSerial = ReadBiosValue("BaseBoardSerialNumber");
        string? systemSerial = ReadBiosValue("SystemSerialNumber");

        // ORDEN FIJO: cambiarlo invalidaría todas las licencias ya emitidas.
        _raw = MachineFingerprintComposer.Compose(machineGuid, volumeSerial, boardSerial, systemSerial);
        Code = MachineFingerprintComposer.ToCode(_raw);

        // Se registra QUÉ componentes participaron: si un día una licencia deja de validar, el log dice por qué.
        _log.LogInformation("Huella del equipo {Code} (componentes presentes: {Components}).", Code,
            string.Join(", ", Present(("MachineGuid", machineGuid), ("VolumenSistema", volumeSerial),
                                      ("SerialPlaca", boardSerial), ("SerialEquipo", systemSerial))));

        if (!MachineFingerprintComposer.HasAnyRealIdentifier(machineGuid, volumeSerial, boardSerial, systemSerial))
            _log.LogError("Este equipo no expone NINGÚN identificador utilizable: la licencia no podrá atarse de forma fiable.");
    }

    public ReadOnlyMemory<byte> Raw => _raw;
    public string Code { get; }

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
        catch (Exception ex) { _log.LogDebug(ex, "No se pudo leer MachineGuid."); return null; }
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
        catch (Exception ex) { _log.LogDebug(ex, "No se pudo leer {Name} de BIOS.", name); return null; }
    }

    /// <summary>Nº de serie del volumen del sistema (cambia al reformatear, no al mover el disco de equipo).</summary>
    private string? ReadSystemVolumeSerial()
    {
        try
        {
            string root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? "C:\\";
            var label = new StringBuilder(261);
            var fs = new StringBuilder(261);
            if (GetVolumeInformationW(root, label, label.Capacity, out uint serial, out _, out _, fs, fs.Capacity))
                return serial.ToString("X8");
            return null;
        }
        catch (Exception ex) { _log.LogDebug(ex, "No se pudo leer el serial del volumen del sistema."); return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationW(
        string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, int fileSystemNameSize);
}
