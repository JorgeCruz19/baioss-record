using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>Lo que dice una línea del stderr de un proceso de grabación sobre si puede producir el archivo.</summary>
public enum RecordingStderrVerdict
{
    /// <summary>Nada que cambie el rumbo: si el proceso muere, es una caída (la grabación sigue en una pieza nueva).</summary>
    None,
    /// <summary>El codificador de VÍDEO de la grabación no abre: se degrada al siguiente de <see cref="EncoderFallbackChain"/>.</summary>
    VideoEncoderCannotOpen,
    /// <summary>El perfil no se puede grabar (<see cref="RecordingFailureDetector.Cause"/>): el proceso va a terminar, y repetirlo en
    /// una pieza nueva fallaría igual.</summary>
    ProfileRejected,
}

/// <summary>
/// Lee el stderr de UN proceso de grabación para saber por qué no puede producir el archivo, que no siempre es una caída:
/// <list type="bullet">
///   <item>El MUXER rechazó la salida al escribir la cabecera (<see cref="FfmpegMuxerError"/>): el contenedor no admite un códec o un
///   parámetro del perfil. Los codificadores ya abrieron —la cabecera se escribe después—, así que no hay nada que degradar.</item>
///   <item>Un codificador que NO es el de vídeo de la grabación no abre (el de audio con un bitrate o una frecuencia que no admite):
///   degradar el de vídeo no lo arregla.</item>
///   <item>El codificador de vídeo no abre: eso sí se degrada (<see cref="RecordingStderrVerdict.VideoEncoderCannotOpen"/>).</item>
/// </list>
/// Lo primero que se ve manda: lo que FFmpeg escribe después son consecuencias (otros codificadores que no llegaron a abrir, el
/// «Error initializing output stream» que los FFmpeg antiguos añaden tras una cabecera rechazada). Un proceso, un detector. Las líneas
/// llegan del hilo del stderr y la causa se lee desde el de la salida del proceso.
/// </summary>
public sealed class RecordingFailureDetector
{
    private readonly object _lock = new();
    private readonly string? _videoEncoder;
    private readonly string _audioEncoder;
    private readonly string _container;
    private bool _settled;          // ya se vio lo que manda: el resto son consecuencias
    private string? _complaint;     // la última queja concreta («[mp4 @ …] Could not find tag for codec prores…»)
    private int _linesSinceComplaint;
    private string? _cause;

    /// <summary>Más líneas que esta entre la queja y el fallo y ya no se toma por su explicación (sería de otra cosa).</summary>
    private const int ComplaintReach = 2;
    private const int MaxDetail = 200;

    public RecordingFailureDetector(RecordingProfile profile)
    {
        _videoEncoder = profile.AudioOnly ? null : FfmpegCodecMap.VideoEncoder(profile.VideoCodec);
        _audioEncoder = FfmpegCodecMap.EffectiveAudioEncoder(profile.AudioCodec, profile.Container);
        _container = ContainerLabel(profile.Container);
    }

    /// <summary>Por qué este proceso no puede producir la grabación con su perfil, en palabras (para la auditoría y el registro), o null
    /// si no lo ha dicho.</summary>
    public string? Cause { get { lock (_lock) return _cause; } }

    /// <summary>Lo que dijo FFmpeg cuando el codificador de vídeo no abrió (la queja concreta si la hubo), sin sus prefijos; null si no
    /// se vio.</summary>
    public string? VideoEncoderFailure { get { lock (_lock) return _videoEncoderFailure; } }
    private string? _videoEncoderFailure;

    /// <summary>Clasifica la siguiente línea del stderr del proceso (ver <see cref="RecordingStderrVerdict"/>).</summary>
    public RecordingStderrVerdict Feed(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return RecordingStderrVerdict.None;
        lock (_lock)
        {
            if (_settled) return RecordingStderrVerdict.None;

            if (FfmpegMuxerError.IsHeaderFailure(line))
            {
                _settled = true;
                // La cabecera también puede fallar por el disco o la red: eso puede pasarse y se trata como una caída.
                if (FfmpegMuxerError.IsIoError(line)) return RecordingStderrVerdict.None;
                string streams = _videoEncoder is null ? $"audio {_audioEncoder}" : $"vídeo {_videoEncoder}, audio {_audioEncoder}";
                _cause = $"El contenedor {_container} no admite lo que pide el preset ({streams}): FFmpeg no pudo escribir la cabecera " +
                         $"del archivo («{Explanation(line)}»). Revisa el contenedor, los códecs y el formato de audio del preset.";
                return RecordingStderrVerdict.ProfileRejected;
            }

            if (FfmpegEncoderError.IsOpenFailure(line))
            {
                _settled = true;
                var name = FfmpegEncoderError.EncoderName(line);
                // Sin nombre (las líneas del propio dispositivo: «[AMF @ …] DLL amfrt64.dll failed to open») es el de vídeo, como siempre.
                if (name is null || string.Equals(name, _videoEncoder, StringComparison.OrdinalIgnoreCase))
                {
                    _videoEncoderFailure = Explanation(line);
                    return RecordingStderrVerdict.VideoEncoderCannotOpen;
                }
                _cause = $"El codificador '{name}' no abre con lo que pide el preset: «{Explanation(line)}». Revisa su configuración en el preset.";
                return RecordingStderrVerdict.ProfileRejected;
            }

            Remember(line);
            return RecordingStderrVerdict.None;
        }
    }

    /// <summary>
    /// Guarda la línea si es una queja concreta que pueda explicar un fallo justo después: la de un muxer, un filtro de flujo o un
    /// codificador («[mp2 @ …] bitrate 1000 is not allowed in mp2», «[mp4 @ …] Could not find tag for codec prores…»). No lo son las
    /// del planificador de FFmpeg («[out#2/mxf @ …]», «[vost#0:0/…]», «[fc#0 @ …]»: llevan '#') ni los avisos («Warning vbv_delay…»,
    /// «… is deprecated»). Las que el canal produce sin parar (medidores ebur128, negro, congelado, silencio, estadísticas) ni cuentan:
    /// desde otro hilo pueden colarse entre la queja y el fallo.
    /// </summary>
    private void Remember(string line)
    {
        if (IsNoise(line)) return;
        if (ContextName(line) is { } context && !context.Contains('#') && IsComplaint(Clean(line)))
        {
            _complaint = line;
            _linesSinceComplaint = 0;
        }
        else _linesSinceComplaint++;
    }

    // Los medidores de un canal cada uno llevan nombre propio («ebur128@ch3of16», ver FfmpegMeterParser.ChannelMeter), no «Parsed_…».
    private static bool IsNoise(string line)
        => line.Contains("frame=", StringComparison.Ordinal) ||
           ContextName(line) is { } c && (c.StartsWith("Parsed_", StringComparison.Ordinal) ||
                                          c.StartsWith(FfmpegMeterParser.ChannelMeterPrefix, StringComparison.Ordinal) ||
                                          c is "blackdetect" or "freezedetect" or "silencedetect");

    // Qué dicen las quejas de los muxers, los filtros de flujo y los codificadores de FFmpeg («only 48khz is implemented», «av1 only
    // supported in MP4 and AVIF», «Unsupported audio codec…», «bitrate 1000 is not allowed in mp2»). Palabras enteras.
    private static readonly string[] ComplaintWords =
    {
        " not supported ", " unsupported ", " does not support ", " only ", " not allowed ", " could not ", " cannot ", " can't ",
        " invalid ", " not implemented ", " must be ", " failed ", " error ", " requires ", " incompatible ", " not divisible ",
        " too many ",
    };

    private static bool IsComplaint(string text)
    {
        if (text.StartsWith("warning", StringComparison.OrdinalIgnoreCase)) return false;
        var words = " " + text.Replace('.', ' ').Replace(',', ' ').Replace(':', ' ') + " ";
        foreach (var w in ComplaintWords)
            if (words.Contains(w, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>«mp4» de «[mp4 @ 0000…] …», o null si la línea no empieza por un contexto «[nombre @ dirección]».</summary>
    private static string? ContextName(string line)
    {
        if (!line.StartsWith('[')) return null;
        int at = line.IndexOf(" @ ", StringComparison.Ordinal);
        int close = line.IndexOf(']');
        return at > 1 && close > at ? line[1..at] : null;
    }

    /// <summary>
    /// Lo que explica el fallo, sin prefijos: la propia línea si dice algo concreto («DLL amfrt64.dll failed to open»); si es la
    /// genérica de FFmpeg («Could not write header (incorrect codec parameters ?)…», «Error while opening encoder - maybe incorrect
    /// parameters…»), la queja concreta que la precedió, si es reciente.
    /// </summary>
    private string Explanation(string line)
    {
        var s = line.ToLowerInvariant();
        bool generic = s.Contains("could not write header", StringComparison.Ordinal) ||
                       s.Contains("error while opening encoder", StringComparison.Ordinal) ||
                       s.Contains("error initializing output stream", StringComparison.Ordinal);
        return Clean(generic && _complaint is not null && _linesSinceComplaint <= ComplaintReach ? _complaint : line);
    }

    /// <summary>La línea sin los contextos «[… @ …]» de delante, sin la lista de «Supported codecs are: …» y acotada.</summary>
    internal static string Clean(string line)
    {
        var s = line.Trim();
        while (s.StartsWith('[') && s.IndexOf(']') is var close and > 0) s = s[(close + 1)..].TrimStart();
        int list = s.IndexOf(". Supported ", StringComparison.Ordinal);
        if (list > 0) s = s[..list];
        s = s.TrimEnd(' ', '.');
        return s.Length <= MaxDetail ? s : s[..MaxDetail].TrimEnd() + "…";
    }

    /// <summary>Cómo se llama el contenedor para el operador.</summary>
    internal static string ContainerLabel(ContainerFormat container) => container switch
    {
        ContainerFormat.Mp4 => "MP4",
        ContainerFormat.Mov => "MOV",
        ContainerFormat.Mxf => "MXF",
        ContainerFormat.Mkv => "MKV",
        ContainerFormat.Ts => "MPEG-TS",
        ContainerFormat.Avi => "AVI",
        ContainerFormat.ProgramStream => "MPEG-PS",
        ContainerFormat.Wav => "WAV",
        ContainerFormat.Mp3Audio => "MP3",
        _ => container.ToString(),
    };
}
