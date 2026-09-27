using System.Globalization;
using System.Text;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>Contenedores que se pueden leer MIENTRAS CRECEN (medido con el FFmpeg empaquetado): el MP4 fragmentado que
/// escribe el grabador (moov al inicio + fragmentos) y el MPEG-TS. MXF no tiene índice hasta cerrarse (solo lectura
/// secuencial desde el principio) y el MP4 estándar no tiene <c>moov</c> hasta cerrarse.</summary>
public enum ClipContainer { FragmentedMp4, MpegTs }

/// <summary>Un archivo de la grabación en curso, en orden cronológico, con su duración legible ahora mismo y su tamaño.</summary>
public sealed record ClipSourceFile(string Path, double DurationSeconds, long SizeBytes);

/// <summary>Plan de corte: los archivos que entran (del que contiene el inicio al más nuevo), el inicio en el tiempo LOCAL
/// del primero (ya anclado a un fotograma clave, si se pudo) y la longitud a copiar.</summary>
public sealed record ClipPlan(IReadOnlyList<ClipSourceFile> Files, double StartSeconds, double LengthSeconds)
{
    /// <summary>El inicio que se pedía antes de anclarlo al fotograma clave anterior (diagnóstico y tests).</summary>
    public double WantedStartSeconds { get; init; }
}

/// <summary>
/// Decide QUÉ copiar para un clip de los últimos N segundos de una grabación que aún se escribe (puro, sin E/S):
/// <list type="bullet">
///   <item>El final se deja <see cref="TailMargin"/> antes de la duración legible: el último fragmento fMP4
///   (<c>frag_duration</c> 1 s) puede estar a medias y copiarlo da «Packet corrupt»; en TS el muxer retiene un poco.</item>
///   <item>El inicio es fin − N, nunca antes del principio; se ancla al último fotograma clave anterior (el copiado sin
///   recodificar solo puede empezar ahí): el clip sale algo más largo por delante, como mucho un GOP.</item>
///   <item>Con grabación segmentada entran los segmentos desde el que contiene el inicio hasta el más nuevo, y se
///   copian con el demuxer <c>concat</c> declarando la duración de cada uno (los segmentos fMP4 no la traen en la
///   cabecera y sin ella no se puede buscar dentro de la lista).</item>
/// </list>
/// </summary>
public static class ClipPlanner
{
    /// <summary>Material mínimo para que un clip tenga sentido.</summary>
    public const double MinClipSeconds = 5;

    /// <summary>Cuánto se deja sin copiar al final. Medido: en fMP4, 1,2 s deja el clip limpio (menos: el último paquete
    /// llega truncado); en TS basta con medio segundo.</summary>
    public static double TailMargin(ClipContainer container) => container == ClipContainer.FragmentedMp4 ? 1.2 : 0.5;

    /// <summary>Elige archivos e instantes; null si aún no hay material (menos de <see cref="MinClipSeconds"/> legibles).</summary>
    public static ClipPlan? Plan(IReadOnlyList<ClipSourceFile> files, double wantedSeconds, ClipContainer container)
    {
        if (files.Count == 0 || wantedSeconds <= 0) return null;
        double total = 0;
        foreach (var f in files) total += Math.Max(0, f.DurationSeconds);
        double end = total - TailMargin(container);
        if (end < MinClipSeconds) return null;
        double start = Math.Max(0, end - wantedSeconds);

        int first = files.Count - 1;
        double offset = 0;
        for (int i = 0; i < files.Count; i++)
        {
            double next = offset + Math.Max(0, files[i].DurationSeconds);
            if (start < next || i == files.Count - 1) { first = i; break; }
            offset = next;
        }
        double local = Math.Max(0, start - offset);
        var chosen = new List<ClipSourceFile>(files.Count - first);
        for (int i = first; i < files.Count; i++) chosen.Add(files[i]);
        return new ClipPlan(chosen, local, end - start) { WantedStartSeconds = local };
    }

    /// <summary>Ancla el inicio al último fotograma clave ≤ inicio (tiempos locales del primer archivo del plan) y alarga
    /// la copia en lo que se adelanta. Sin fotogramas clave por debajo, el plan se deja como está (FFmpeg ancla él).</summary>
    public static ClipPlan SnapToKeyframe(ClipPlan plan, IEnumerable<double> keyframeTimes)
    {
        double best = double.NaN;
        foreach (var k in keyframeTimes)
            if (k <= plan.StartSeconds + 0.001 && (double.IsNaN(best) || k > best)) best = k;
        if (double.IsNaN(best) || best >= plan.StartSeconds) return plan;
        return plan with { StartSeconds = best, LengthSeconds = plan.LengthSeconds + (plan.StartSeconds - best) };
    }

    /// <summary>«{base}_clip_{fecha_hora}_{30s|5min}.{ext}»: se distingue de la grabación y de sus segmentos a simple vista.</summary>
    public static string OutputFileName(string baseName, DateTimeOffset at, int seconds, string extension)
        => string.Create(CultureInfo.InvariantCulture, $"{baseName}_clip_{at:yyyyMMdd_HHmmss}_{Label(seconds)}.{extension}");

    /// <summary>«30s», «1min», «5min», «90s».</summary>
    public static string Label(int seconds)
        => seconds > 0 && seconds % 60 == 0 ? string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}min") : string.Create(CultureInfo.InvariantCulture, $"{seconds}s");

    /// <summary>Bytes que ocupará el clip, por el bitrate medio de los archivos de origen, con un 20 % de margen.</summary>
    public static long EstimateBytes(IReadOnlyList<ClipSourceFile> files, double seconds)
    {
        double duration = 0; long bytes = 0;
        foreach (var f in files) { duration += Math.Max(0, f.DurationSeconds); bytes += Math.Max(0, f.SizeBytes); }
        if (duration <= 0 || bytes <= 0) return 0;
        return (long)(bytes / duration * seconds * 1.2);
    }

    /// <summary>Contenido de la lista del demuxer <c>concat</c>: rutas (comillas simples escapadas) y duración de cada archivo.</summary>
    public static string ConcatList(IReadOnlyList<ClipSourceFile> files)
    {
        var sb = new StringBuilder();
        foreach (var f in files)
        {
            sb.Append("file '").Append(f.Path.Replace("'", "'\\''")).Append("'\n");
            sb.Append("duration ").Append(f.DurationSeconds.ToString("0.000", CultureInfo.InvariantCulture)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Argumentos de ffmpeg para copiar el tramo de UN archivo SIN recodificar: <c>-ss</c> antes de <c>-i</c> (busca el
    /// fotograma clave), vídeo y TODAS las pistas de audio, <c>-avoid_negative_ts make_zero</c> (el clip empieza en 0 sin
    /// marcas negativas), y salida MP4 estándar con <c>faststart</c> (índice al inicio: se reproduce y comparte en
    /// cualquier sitio) o TS.
    /// </summary>
    public static IReadOnlyList<string> BuildArguments(ClipPlan plan, ClipContainer container, string outputPath)
    {
        if (plan.Files.Count != 1) throw new ArgumentException("Con varios archivos: BuildTailArguments + BuildConcatArguments.", nameof(plan));
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        a.Add("-ss"); a.Add(plan.StartSeconds.ToString("0.000", CultureInfo.InvariantCulture));
        a.Add("-i"); a.Add(plan.Files[0].Path);
        a.Add("-t"); a.Add(plan.LengthSeconds.ToString("0.000", CultureInfo.InvariantCulture));
        AddCopyTail(a, container, outputPath, final: true);
        return a;
    }

    /// <summary>
    /// Con VARIOS archivos (grabación segmentada) el corte va en dos pasos, porque el demuxer <c>concat</c> no sabe buscar
    /// dentro de un fMP4 recién abierto (medido: copiaba desde el principio del primer segmento). Paso 1: la COLA del
    /// primer archivo desde el fotograma clave (aquí la búsqueda sí funciona) a una pieza temporal del mismo contenedor.
    /// </summary>
    public static IReadOnlyList<string> BuildTailArguments(ClipPlan plan, ClipContainer container, string piecePath)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        a.Add("-ss"); a.Add(plan.StartSeconds.ToString("0.000", CultureInfo.InvariantCulture));
        a.Add("-i"); a.Add(plan.Files[0].Path);
        AddCopyTail(a, container, piecePath, final: false);
        return a;
    }

    /// <summary>Paso 2: la pieza más los segmentos siguientes, concatenados SIN buscar, hasta la longitud del clip.</summary>
    public static IReadOnlyList<string> BuildConcatArguments(ClipPlan plan, ClipContainer container, string concatListPath, string outputPath)
    {
        var a = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-y" };
        a.Add("-f"); a.Add("concat"); a.Add("-safe"); a.Add("0"); a.Add("-i"); a.Add(concatListPath);
        a.Add("-t"); a.Add(plan.LengthSeconds.ToString("0.000", CultureInfo.InvariantCulture));
        AddCopyTail(a, container, outputPath, final: true);
        return a;
    }

    private static void AddCopyTail(List<string> a, ClipContainer container, string outputPath, bool final)
    {
        a.Add("-map"); a.Add("0:v:0");
        a.Add("-map"); a.Add("0:a?");
        a.Add("-c"); a.Add("copy");
        a.Add("-avoid_negative_ts"); a.Add("make_zero");
        if (container == ClipContainer.FragmentedMp4)
        {
            // La pieza intermedia va fragmentada (se concatena tal cual); el clip final, MP4 estándar con el índice al inicio.
            a.Add("-movflags"); a.Add(final ? "+faststart" : "+frag_keyframe+empty_moov+default_base_moof");
            a.Add("-f"); a.Add("mp4");
        }
        else { a.Add("-f"); a.Add("mpegts"); }
        a.Add(outputPath);
    }
}
