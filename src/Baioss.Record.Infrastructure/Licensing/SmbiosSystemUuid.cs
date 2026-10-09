using System.Runtime.InteropServices;

namespace Baioss.Record.Infrastructure.Licensing;

/// <summary>
/// UUID de sistema del SMBIOS (estructura de tipo 1), leído de la tabla cruda del firmware con
/// <c>GetSystemFirmwareTable</c>: sin WMI (que puede tardar o fallar al arrancar) y sin el registro (que no lo
/// publica). Vive en el firmware del equipo, no en el disco, así que un clon del disco no se lo lleva. Es un
/// componente de la huella CLÁSICA (equipos sin TPM); el compositor descarta los valores de relleno.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class SmbiosSystemUuid
{
    private const uint RawSmbios = 0x52534D42; // 'RSMB'

    /// <summary>Los 16 bytes del UUID en hexadecimal (orden de la tabla), o null si no se puede leer. Nunca lanza.</summary>
    public static string? TryRead()
    {
        try
        {
            uint size = GetSystemFirmwareTable(RawSmbios, 0, null, 0);
            if (size is 0 or > 1 << 20) return null;
            var raw = new byte[size];
            uint written = GetSystemFirmwareTable(RawSmbios, 0, raw, size);
            if (written == 0 || written > size) return null;
            return Parse(raw.AsSpan(0, (int)written));
        }
        catch { return null; }
    }

    /// <summary>
    /// Busca la estructura de tipo 1 en la tabla cruda (con su cabecera de 8 bytes de Windows) y devuelve su UUID
    /// en hexadecimal; null si no está o la tabla no tiene la forma esperada. Pura, para probarla sin firmware.
    /// </summary>
    internal static string? Parse(ReadOnlySpan<byte> raw)
    {
        if (raw.Length <= 8) return null;
        var table = raw[8..]; // cabecera de Windows: método, versión mayor/menor, revisión DMI, longitud
        int pos = 0;
        while (pos + 4 <= table.Length)
        {
            byte type = table[pos];
            byte length = table[pos + 1];
            if (length < 4 || pos + length > table.Length) return null;
            if (type == 1)
                // Tipo 1 (información del sistema): el UUID ocupa 16 bytes desde el desplazamiento 8 (SMBIOS 2.1+).
                return length >= 0x18 ? Convert.ToHexString(table.Slice(pos + 8, 16)) : null;
            if (type == 127) return null; // fin de tabla
            // Tras el área con formato van las cadenas, terminadas por un doble nulo.
            int p = pos + length;
            while (p + 1 < table.Length && (table[p] != 0 || table[p + 1] != 0)) p++;
            pos = p + 2;
        }
        return null;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetSystemFirmwareTable", ExactSpelling = true)]
    private static extern uint GetSystemFirmwareTable(uint firmwareTableProviderSignature, uint firmwareTableId, byte[]? buffer, uint bufferSize);
}
