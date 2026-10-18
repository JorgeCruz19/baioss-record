namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Reconoce en el stderr de FFmpeg los fallos de APERTURA de un codificador de vídeo por hardware
/// (NVENC/QuickSync/AMF): sesiones agotadas, driver/DLL ausente, GPU sin soporte. Es la señal para
/// DEGRADAR el codificador (ver <see cref="EncoderFallbackChain"/>) en vez de reintentar el mismo en vano
/// —el supervisor, por sí solo, relanzaría el proceso con idénticos argumentos y volvería a fallar—.
/// </summary>
public static class FfmpegEncoderError
{
    // La PRIMERA línea es genérica: FFmpeg la emite siempre que un codificador (de cualquier familia) no
    // logra abrir su salida; es el disparador principal y el más fiable. Las demás son mensajes específicos
    // del dispositivo por hardware que confirman la causa, por si la genérica no llega antes de que el
    // proceso se cierre. Todas se comparan en minúsculas.
    private static readonly string[] Patterns =
    {
        // Genéricas de FFmpeg (cualquier codificador que no abre). El texto exacto VARÍA entre versiones:
        // las builds recientes dicen «Error while opening encoder - maybe incorrect parameters…»; las
        // antiguas «Error while opening encoder for output stream #0:0…». El substring corto cubre ambas.
        // NO está «Could not open encoder before EOF»: FFmpeg ≥ 7 la escribe por CADA codificador que aún no había
        // recibido un cuadro cuando el proceso termina, sea por lo que sea —un muxer que rechaza la cabecera, una entrada
        // que falla, otro codificador que no abre—. Es una consecuencia, nunca la causa: con ella, el rawvideo del preview
        // convertía un MXF que no admite AAC en «el codificador de vídeo no abre» (medido con 8.0 y la incluida). Un
        // codificador que de verdad no abre escribe además la genérica de arriba o la de su dispositivo.
        "error while opening encoder",
        "error initializing output stream",

        // NVIDIA NVENC.
        "cannot load nvcuda",
        "openencodesessionex failed",
        "no capable devices found",
        "no nvenc capable devices",
        "nvenc capable devices",
        "cannot init cuda",
        "incompatible client key",       // GeForce: típico al exceder el límite de sesiones concurrentes
        "driver does not support",
        "openencodesessionex",

        // Intel QuickSync (libmfx/oneVPL).
        "error initializing an internal mfx session",
        "cannot load libmfx",
        "error creating a mfx session",
        "device creation failed",

        // AMD AMF.
        "amfrt64",
        "failed to initialize amf",
        "amf failed",
    };

    /// <summary>True si la línea de stderr indica que un codificador por hardware no pudo abrir.</summary>
    public static bool IsOpenFailure(string? stderrLine)
    {
        if (string.IsNullOrEmpty(stderrLine)) return false;
        var s = stderrLine.ToLowerInvariant();
        foreach (var p in Patterns)
            if (s.Contains(p, System.StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// El codificador del que habla la línea, si FFmpeg lo nombra en su contexto, o null. FFmpeg ≥ 6.1 antepone el flujo de salida
    /// y su codificador: «[aost#2:1/mp2 @ 0000…] [enc:mp2 @ 0000…] Error while opening encoder…» (8.0 y la incluida), «[vost#0:0/h264_amf]
    /// …». Las líneas del propio dispositivo («[AMF @ …] DLL amfrt64.dll failed to open», «[h264_nvenc @ …] OpenEncodeSessionEx
    /// failed…») y las de FFmpeg antiguos no lo nombran así.
    /// </summary>
    public static string? EncoderName(string? stderrLine)
    {
        if (string.IsNullOrEmpty(stderrLine)) return null;
        int enc = stderrLine.IndexOf("[enc:", System.StringComparison.Ordinal);
        if (enc >= 0) return NameUntil(stderrLine, enc + "[enc:".Length);
        foreach (var stream in new[] { "[vost#", "[aost#" })
        {
            int at = stderrLine.IndexOf(stream, System.StringComparison.Ordinal);
            if (at < 0) continue;
            int slash = stderrLine.IndexOf('/', at);
            int close = stderrLine.IndexOf(']', at);
            if (slash > at && (close < 0 || slash < close)) return NameUntil(stderrLine, slash + 1);
        }
        return null;
    }

    /// <summary>El nombre que empieza en <paramref name="start"/> y acaba en « @» o «]».</summary>
    private static string? NameUntil(string line, int start)
    {
        int end = start;
        while (end < line.Length && line[end] is not (' ' or ']')) end++;
        return end > start ? line[start..end] : null;
    }
}
