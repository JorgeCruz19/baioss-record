namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Reconoce en el stderr de FFmpeg que el MUXER rechazó un archivo de salida al escribir su cabecera: el contenedor no admite un
/// códec o un parámetro del perfil. FFmpeg ≥ 6.1 lo dice en la línea del archivo de salida, precedida por la queja concreta del
/// muxer o de su filtro de flujo (medido con 8.0 y con la incluida, iguales):
/// <code>
/// [pcm_rechunk @ …] Codec 'aac' (86018) is not supported by the bitstream filter 'pcm_rechunk'. Supported codecs are: …
/// [out#2/mxf @ …] Could not write header (incorrect codec parameters ?): Invalid argument
/// </code>
/// (AAC en MXF; igual con ProRes en MP4 —«Could not find tag for codec prores…»—, HEVC en MXF —«could not find essence container
/// ul…», «Operation not permitted»— o PCM de 24 bits en MPEG-PS —«Unsupported audio codec…»—). Los FFmpeg anteriores dicen
/// «Could not write header for output file #0 (…)». No es un codificador que no abre: la cabecera se escribe cuando todos ya abrieron.
/// </summary>
public static class FfmpegMuxerError
{
    /// <summary>True si la línea dice que FFmpeg no pudo escribir la cabecera de un archivo de salida.</summary>
    public static bool IsHeaderFailure(string? stderrLine)
        => stderrLine?.Contains("Could not write header", System.StringComparison.OrdinalIgnoreCase) == true;

    // Errores de ENTRADA/SALIDA con los que también puede fallar la cabecera (el disco se llenó, la carpeta de red se fue): eso no
    // es un rechazo del formato y puede pasarse, así que se trata como una caída. Un rechazo viene con «Invalid argument»,
    // «Operation not permitted», «Invalid data found…», «Not yet implemented…».
    private static readonly string[] IoErrors =
    {
        "i/o error", "input/output error", "no space left", "not enough space", "disk full", "broken pipe", "permission denied",
        "no such file", "connection", "network", "timed out", "timeout", "temporarily unavailable", "cannot allocate memory",
        "out of memory",
    };

    /// <summary>True si el error de un fallo de la cabecera es de entrada/salida (disco, red, memoria) y no un rechazo del formato.</summary>
    public static bool IsIoError(string? stderrLine)
    {
        if (string.IsNullOrEmpty(stderrLine)) return false;
        var s = stderrLine.ToLowerInvariant();
        foreach (var e in IoErrors)
            if (s.Contains(e, System.StringComparison.Ordinal)) return true;
        return false;
    }
}
