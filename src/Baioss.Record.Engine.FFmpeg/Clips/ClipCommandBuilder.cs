using System.Globalization;
using Baioss.Record.Application.Clips;
using Baioss.Record.Domain;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Engine.FFmpeg.Clips;

/// <summary>
/// Las líneas de FFmpeg de los clips y del reproductor de la revisión (puro: tokens, sin procesos, para poder probarlas).
///
/// <para>Todas leen con <c>-seek_timestamp 1</c> y marcas absolutas (ver <see cref="TimelineInput"/>): <c>-ss</c> va
/// <see cref="Margin"/> por DEBAJO de la marca del primer cuadro y <c>-to</c> por debajo de la del primero que ya no entra. Con
/// ese milisegundo el corte cae siempre del lado bueno del cuadro, aunque la marca del archivo esté redondeada (a 1 ms en MKV) o
/// la del concat difiera en unas decenas de µs de la calculada; y el audio, que se corta en el mismo sitio, queda a 1 ms.</para>
///
/// <para>Al DECODIFICAR (exacto y reproductor), el audio va con <c>-flags2:a +skip_manual</c>: el muxer <c>segment</c> deja al
/// principio de cada segmento de una grabación con B-frames ~64 ms de audio que van antes de su vídeo, tapados por la lista de
/// edición del archivo; el decodificador los tiraba y en la unión de dos segmentos faltaban esos 64 ms: el clip perdía audio y
/// todo lo de después iba 60 ms adelantado respecto a la imagen (medido). Con la opción se decodifican, cada uno con su marca, y
/// rellenan justo el hueco. En un archivo solo no cambia nada (lo de antes del corte se tira por su marca).</para>
/// </summary>
public static class ClipCommandBuilder
{
    /// <summary>Margen bajo la marca de un cuadro: 1 ms (o una décima de cuadro, si el cuadro dura menos de 10 ms).</summary>
    public static double Margin(FrameRate rate) => Math.Min(0.001, 0.1 / rate.Value);

    /// <summary>Segundos con la precisión con la que FFmpeg los lee (µs).</summary>
    public static string Seconds(double seconds) => seconds.ToString("0.000000", CultureInfo.InvariantCulture);

    /// <summary>
    /// Contenedor de un clip RÁPIDO (copia): el de la grabación si es MP4 o MOV; si no, MP4 cuando sus códecs caben (H.264, HEVC
    /// o AV1 con AAC, MP3, Opus o AC-3) y MOV en los demás casos (ProRes, DNxHD/HR, MPEG-2, PCM, MP2), que admite todos los de la
    /// aplicación. Una grabación de solo audio queda en su formato (WAV, MP3…).
    /// </summary>
    public static ContainerFormat FastContainer(MediaDescription media, string sourcePath)
    {
        string ext = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        if (media.Video is null)
            return ext switch
            {
                "wav" => ContainerFormat.Wav,
                "mp3" => ContainerFormat.Mp3Audio,
                "mov" => ContainerFormat.Mov,
                "mkv" => ContainerFormat.Mkv,
                _ => media.Audio.All(a => Mp4Audio(a.Codec)) ? ContainerFormat.Mp4 : ContainerFormat.Mov,
            };
        if (ext is "mp4" or "m4v") return ContainerFormat.Mp4;
        if (ext is "mov") return ContainerFormat.Mov;
        return media.Video.Codec is "h264" or "hevc" or "av1" && media.Audio.All(a => Mp4Audio(a.Codec))
            ? ContainerFormat.Mp4
            : ContainerFormat.Mov;
    }

    private static bool Mp4Audio(string codec) => codec is "aac" or "mp3" or "opus" or "ac3" or "eac3" or "alac";

    /// <summary>Extensión del archivo del clip para el contenedor.</summary>
    public static string Extension(ContainerFormat container) => FfmpegCodecMap.Container(container).Extension;

    /// <summary>
    /// Clip RÁPIDO: copia sin recodificar del tramo. El demuxer arranca en el fotograma clave anterior a la entrada (vídeo y
    /// audio) y el muxer MP4/MOV escribe una lista de edición para que el reproductor empiece en el cuadro pedido; con
    /// <c>+faststart</c>, el índice al principio. <paramref name="end"/>: la marca (de decodificación) donde acaba la copia, que
    /// el llamador calcula para que no falte ningún cuadro con B-frames (<see cref="CopyEnd"/>).
    /// </summary>
    public static IReadOnlyList<string> Fast(ClipPlan plan, string input, double end, ContainerFormat container, string output, FrameRate rate)
    {
        var args = Head(plan.Input, input, plan.Start - Margin(rate), end);
        args.AddRange(new[] { "-map", "0:v:0?", "-map", "0:a?", "-c", "copy", "-map_chapters", "-1" });
        return Tail(args, container, output);
    }

    /// <summary>
    /// Clip EXACTO: se decodifica desde el fotograma clave anterior, se tiran los cuadros de antes de la entrada (el <c>-ss</c> de
    /// entrada es exacto cuando se recodifica) y se recodifica solo el tramo, que empieza y acaba en el cuadro pedido.
    /// </summary>
    public static IReadOnlyList<string> Exact(ClipPlan plan, string input, ClipEncoding encoding, string output, FrameRate rate)
    {
        double margin = Margin(rate);
        var args = Head(plan.Input, input, plan.Start - margin, plan.End - margin, keepHiddenAudio: true);
        args.AddRange(new[] { "-map", "0:v:0?", "-map", "0:a?" });
        if (encoding.VideoFilter is { } vf) { args.Add("-vf"); args.Add(vf); }
        args.AddRange(encoding.VideoArgs);
        args.AddRange(encoding.AudioArgs);
        args.AddRange(new[] { "-map_chapters", "-1" });
        return Tail(args, encoding.Container, output);
    }

    /// <summary>
    /// Dónde cortar la COPIA para acabar en <paramref name="end"/> (marca del primer cuadro que ya no entra) sin dejar huecos.
    /// La copia corta por la marca de DECODIFICACIÓN: con B-frames, un cuadro P de después de la salida se decodifica antes que
    /// los B que lo preceden, y cortando en la salida salían 2–3 cuadros de más y faltaban algunos (el clip saltaba al final;
    /// medido con x264). Se busca el umbral más bajo que deja dentro todo cuadro de antes de la salida y ningún hueco entre los
    /// que entran. <paramref name="packets"/>: los paquetes de vídeo alrededor de la salida (presentación, decodificación), en
    /// la escala de la entrada. Devuelve el umbral y cuántos cuadros quedan de más tras la salida.
    /// </summary>
    public static (double End, int ExtraFrames) CopyEnd(IReadOnlyList<(double Pts, double Dts)> packets, double end, FrameRate rate)
    {
        double margin = Margin(rate);
        double cut = end - margin;
        if (packets.Count == 0) return (cut, 0);
        var inClip = packets.Where(p => p.Pts < cut).ToList();
        if (inClip.Count == 0) return (cut, 0);
        // Empieza por la decodificación del último cuadro de antes de la salida: todos los que entran tienen que caber.
        double threshold = Math.Max(cut, inClip.Max(p => p.Dts) + margin);
        while (true)
        {
            double last = packets.Where(p => p.Dts < threshold).Max(p => p.Pts);
            var missing = packets.Where(p => p.Pts <= last + margin && p.Dts >= threshold).ToList();
            if (missing.Count == 0)
            {
                int extra = packets.Count(p => p.Dts < threshold && p.Pts >= cut);
                return (threshold, extra);
            }
            threshold = missing.Max(p => p.Dts) + margin;
        }
    }

    /// <summary>
    /// Reproductor de la revisión, VÍDEO: cuadros BGRA de <paramref name="width"/>×<paramref name="height"/> por la salida
    /// estándar desde el cuadro de la marca <paramref name="time"/> (decodificado desde el fotograma clave anterior: nunca se ve
    /// un cuadro a medias, como al buscar en un reproductor que salta al GOP). Un vídeo entrelazado se desentrelaza (bwdif, un
    /// cuadro por cuadro) para que no peine al reducirlo. Cada cuadro sale una vez (passthrough), en orden.
    /// </summary>
    public static IReadOnlyList<string> PlayerVideo(TimelineInput input, string inputPath, double time, int width, int height,
        bool deinterlace, FrameRate rate)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-seek_timestamp", "1",
            "-ss", Seconds(Math.Max(0, time - Margin(rate))) };
        AddInput(args, input, inputPath);
        string scale = string.Create(CultureInfo.InvariantCulture, $"scale={width}:{height}:flags=bilinear,format=bgra");
        args.AddRange(new[]
        {
            "-map", "0:v:0", "-an", "-sn", "-dn",
            "-vf", deinterlace ? "bwdif=mode=send_frame:parity=auto:deint=all," + scale : scale,
            "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "bgra", "pipe:1",
        });
        return args;
    }

    /// <summary>Reproductor de la revisión, AUDIO: la primera pista en PCM de 16 bits estéreo a 48 kHz por la salida estándar,
    /// desde la marca <paramref name="time"/> (a la muestra).</summary>
    public static IReadOnlyList<string> PlayerAudio(TimelineInput input, string inputPath, double time, FrameRate rate)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-flags2:a", "+skip_manual",
            "-seek_timestamp", "1", "-ss", Seconds(Math.Max(0, time - Margin(rate))) };
        AddInput(args, input, inputPath);
        args.AddRange(new[] { "-map", "0:a:0", "-vn", "-sn", "-dn", "-ac", "2", "-ar", "48000", "-f", "s16le", "pipe:1" });
        return args;
    }

    private static List<string> Head(TimelineInput input, string inputPath, double start, double end, bool keepHiddenAudio = false)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-nostats", "-progress", "pipe:1" };
        if (keepHiddenAudio) args.AddRange(new[] { "-flags2:a", "+skip_manual" });
        args.AddRange(new[] { "-seek_timestamp", "1", "-ss", Seconds(Math.Max(0, start)), "-to", Seconds(end) });
        AddInput(args, input, inputPath);
        return args;
    }

    private static void AddInput(List<string> args, TimelineInput input, string inputPath)
    {
        if (input.IsConcat) args.AddRange(new[] { "-f", "concat", "-safe", "0" });
        args.Add("-i"); args.Add(inputPath);
    }

    private static IReadOnlyList<string> Tail(List<string> args, ContainerFormat container, string output)
    {
        // El índice al principio: el clip se puede abrir y buscar en cuanto se copia o se sube a otro sitio.
        if (container is ContainerFormat.Mp4 or ContainerFormat.Mov) args.AddRange(new[] { "-movflags", "+faststart" });
        args.AddRange(new[] { "-f", FfmpegCodecMap.Container(container).Muxer, "-y", output });
        return args;
    }
}
