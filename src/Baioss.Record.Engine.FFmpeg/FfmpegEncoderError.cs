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
        // «Could not open encoder before EOF» acompaña al fallo en builds recientes (verificado).
        "error while opening encoder",
        "could not open encoder before eof",
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
}
