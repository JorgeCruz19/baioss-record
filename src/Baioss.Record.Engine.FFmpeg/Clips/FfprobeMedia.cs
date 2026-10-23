using System.Globalization;
using System.Text.Json;
using Baioss.Record.Application.Clips;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Engine.FFmpeg.Clips;

/// <summary>
/// Lo que la revisión y los clips le preguntan a ffprobe de un archivo: sus pistas (<see cref="DescribeAsync"/>), el fotograma
/// clave en el que empieza un corte sin recodificar (<see cref="KeyframeAtOrBeforeAsync"/>) y los paquetes de vídeo alrededor de
/// un instante (<see cref="VideoPacketsAsync"/>), para cortar sin huecos un vídeo con B-frames.
/// </summary>
public static class FfprobeMedia
{
    internal const string Entries =
        "format=format_name,start_time,duration:" +
        "stream=index,codec_type,codec_name,profile,pix_fmt,width,height,r_frame_rate,avg_frame_rate,field_order," +
        "sample_aspect_ratio,sample_rate,channels,bit_rate,start_time,duration,nb_frames,has_b_frames";

    /// <summary>Describe el archivo, o null si ffprobe no lo lee (no existe, está dañado, MP4 sin índice…).</summary>
    public static async Task<MediaDescription?> DescribeAsync(string ffprobe, string file, CancellationToken ct = default)
    {
        var (json, _, exit) = await ClipProcess.RunAsync(ffprobe, new[] { "-v", "error", "-of", "json", "-show_entries", Entries, file },
            TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        return exit == 0 ? Parse(json) : null;
    }

    /// <summary>Lo que dice el JSON de ffprobe (<c>-of json -show_entries …</c>), o null si no tiene pistas que sirvan.</summary>
    public static MediaDescription? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            MediaStreamInfo? video = null;
            var audio = new List<MediaStreamInfo>();
            if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
                foreach (var s in streams.EnumerateArray())
                {
                    var type = Str(s, "codec_type");
                    if (type == "video" && video is null)
                    {
                        // Una portada (attached_pic) o un vídeo sin cadencia no es la imagen de la grabación.
                        var rate = Rate(Str(s, "r_frame_rate")) ?? Rate(Str(s, "avg_frame_rate"));
                        if (rate is null) continue;
                        video = Stream(s, "video") with { FrameRate = rate };
                    }
                    else if (type == "audio") audio.Add(Stream(s, "audio"));
                }
            if (video is null && audio.Count == 0) return null;
            var format = root.TryGetProperty("format", out var f) ? f : default;
            double formatStart = Num(format, "start_time") ?? video?.Start ?? audio.FirstOrDefault()?.Start ?? 0;
            return new MediaDescription
            {
                FormatName = format.ValueKind == JsonValueKind.Object ? Str(format, "format_name") : null,
                FormatStart = formatStart,
                FormatDuration = Num(format, "duration") ?? 0,
                Video = video,
                Audio = audio,
            };
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Cuadros de vídeo de la pieza: los que declara el contenedor (MP4/MOV lo dicen exacto) o, si no (MKV, TS), lo que da su
    /// duración a su cadencia. En una pieza sin vídeo, los «cuadros» de la rejilla de <paramref name="audioOnlyRate"/>.
    /// </summary>
    public static long FrameCount(MediaDescription m, FrameRate audioOnlyRate)
    {
        if (m.Video is { } v)
        {
            if (v.Frames is > 0) return v.Frames.Value;
            double fps = v.FrameRate?.Value ?? 25;
            double duration = v.Duration ?? (m.FormatDuration - ((v.Start ?? m.FormatStart) - m.FormatStart));
            return Math.Max(0, (long)Math.Round(duration * fps));
        }
        var a = m.Audio.FirstOrDefault();
        double audioDuration = a?.Duration ?? (m.FormatDuration - ((a?.Start ?? m.FormatStart) - m.FormatStart));
        return Math.Max(0, (long)Math.Floor(audioDuration * audioOnlyRate.Value + 1e-6));
    }

    /// <summary>
    /// Marca (s) del fotograma clave del vídeo en el que empezaría a leer un corte en <paramref name="time"/>: el último que
    /// empieza en él o antes. Lo que el demuxer hace con <c>-ss</c>: <c>-read_intervals</c> busca ahí y el primer paquete es esa
    /// clave. Null si ffprobe no lo dice.
    /// </summary>
    public static async Task<double?> KeyframeAtOrBeforeAsync(string ffprobe, string file, double time, CancellationToken ct = default)
    {
        var (json, _, exit) = await ClipProcess.RunAsync(ffprobe, new[]
        {
            "-v", "error", "-of", "json", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags",
            "-read_intervals", string.Create(CultureInfo.InvariantCulture, $"{time:0.000000}%+#1"), file,
        }, TimeSpan.FromSeconds(30), ct).ConfigureAwait(false);
        if (exit != 0) return null;
        return Packets(json).Where(p => p.Key).Select(p => (double?)p.Pts).FirstOrDefault();
    }

    /// <summary>Paquetes de vídeo (marca de presentación y de decodificación, en s, en orden de decodificación) entre
    /// <paramref name="from"/> y <paramref name="to"/>, empezando en el fotograma clave anterior a <paramref name="from"/>.</summary>
    public static async Task<IReadOnlyList<(double Pts, double Dts)>> VideoPacketsAsync(string ffprobe, string file, double from, double to,
        CancellationToken ct = default)
    {
        var (json, _, exit) = await ClipProcess.RunAsync(ffprobe, new[]
        {
            "-v", "error", "-of", "json", "-select_streams", "v:0", "-show_entries", "packet=pts_time,dts_time,flags",
            "-read_intervals", string.Create(CultureInfo.InvariantCulture, $"{Math.Max(0, from):0.000000}%{to:0.000000}"), file,
        }, TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        if (exit != 0) return Array.Empty<(double, double)>();
        return Packets(json).Where(p => !double.IsNaN(p.Dts)).Select(p => (p.Pts, p.Dts)).ToList();
    }

    /// <summary>Los paquetes del JSON de ffprobe (<c>-show_entries packet=pts_time,dts_time,flags</c>).</summary>
    internal static IEnumerable<(double Pts, double Dts, bool Key)> Packets(string json)
    {
        var list = new List<(double, double, bool)>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("packets", out var packets) || packets.ValueKind != JsonValueKind.Array) return list;
            foreach (var p in packets.EnumerateArray())
            {
                if (Num(p, "pts_time") is not { } pts) continue;
                list.Add((pts, Num(p, "dts_time") ?? double.NaN, (Str(p, "flags") ?? "").StartsWith('K')));
            }
        }
        catch (JsonException) { /* sin paquetes */ }
        return list;
    }

    private static MediaStreamInfo Stream(JsonElement s, string type) => new()
    {
        Index = (int)(Num(s, "index") ?? 0),
        Type = type,
        Codec = Str(s, "codec_name") ?? "?",
        Profile = Str(s, "profile"),
        PixelFormat = Str(s, "pix_fmt"),
        Width = (int)(Num(s, "width") ?? 0),
        Height = (int)(Num(s, "height") ?? 0),
        FieldOrder = Str(s, "field_order"),
        SampleAspectRatio = Str(s, "sample_aspect_ratio"),
        SampleRate = (int)(Num(s, "sample_rate") ?? 0),
        Channels = (int)(Num(s, "channels") ?? 0),
        BitRate = Num(s, "bit_rate") is { } b && b > 0 ? (long)b : null,
        Start = Num(s, "start_time"),
        Duration = Num(s, "duration"),
        Frames = Num(s, "nb_frames") is { } n && n > 0 ? (long)n : null,
        HasBFrames = Num(s, "has_b_frames") is > 0,
    };

    /// <summary>«30000/1001» → 30000/1001; «0/0» (audio, imágenes sueltas) → null.</summary>
    internal static FrameRate? Rate(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var parts = text.Split('/');
        if (parts.Length != 2 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var den) || num <= 0 || den <= 0)
            return null;
        // Una cadencia absurda (90000/1 de un TS sin cadencia) no es una rejilla de cuadros.
        return num / (double)den is > 0.5 and <= 300 ? new FrameRate(num, den) : null;
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;

    private static double? Num(JsonElement e, string name)
        => Str(e, name) is { } text && text != "N/A"
           && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
}
