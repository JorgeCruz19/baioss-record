using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Api;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Clips;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Engine.FFmpeg.Clips;
using Baioss.Record.Infrastructure;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Clips;
using Baioss.Record.Infrastructure.Messaging;
using Baioss.Record.Infrastructure.Review;
using Baioss.Record.Infrastructure.Storage;
using Baioss.Record.IntegrationTests.Fakes;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Grabaciones sintéticas para los clips, hechas una vez con el FFmpeg incluido: cada cuadro lleva su número (módulo 4096) en
/// doce franjas blancas o negras —una por bit, que ningún códec ni escalado confunde— y el audio un pitido al empezar cada
/// segundo. Como las de la aplicación: MP4 estándar (índice al final), AAC, y con el muxer <c>segment</c> (<c>reset_timestamps</c>)
/// en las segmentadas; con GOP fijo y sin B-frames (lo que graba NVENC) y con x264 por defecto (B-frames y cortes de escena).
/// </summary>
public sealed class ClipMaterial : IDisposable
{
    public const int Width = 960, Height = 540;
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), $"baioss-clips-{Guid.NewGuid():N}");
    public bool Ready { get; }

    /// <summary>x264 con GOP fijo de 50 y sin B-frames, como NVENC en la aplicación.</summary>
    public static readonly string[] NoBFrames = { "-c:v", "libx264", "-preset", "veryfast", "-g", "50", "-keyint_min", "50", "-sc_threshold", "0", "-bf", "0", "-b:v", "4M" };
    /// <summary>x264 como lo deja la aplicación en CPU: B-frames y cortes de escena (las franjas cambian en cada cuadro).</summary>
    public static readonly string[] BFrames = { "-c:v", "libx264", "-preset", "veryfast", "-g", "50", "-b:v", "4M" };

    public ClipMaterial()
    {
        if (TestAssets.FfmpegDir is null) return;
        Directory.CreateDirectory(Dir);
        Generate("nobf.mp4", "25", 30, NoBFrames);
        Generate("bf.mp4", "25", 30, BFrames);
        Generate("ntsc.mp4", "30000/1001", 20, BFrames);
        Generate("seg_%d.mp4", "25", 30, NoBFrames, segment: 10);
        Generate("segbf_%d.mp4", "25", 30, BFrames, segment: 10);
        Ready = true;
    }

    /// <summary>Los archivos de una grabación sintética, en orden («seg» → seg_1.mp4, seg_2.mp4, seg_3.mp4).</summary>
    public IReadOnlyList<string> Files(string name)
    {
        var single = Path.Combine(Dir, name + ".mp4");
        if (File.Exists(single)) return new[] { single };
        return Enumerable.Range(1, 99).Select(i => Path.Combine(Dir, $"{name}_{i}.mp4")).TakeWhile(File.Exists).ToList();
    }

    private void Generate(string file, string rate, int seconds, string[] encoder, int? segment = null)
    {
        string bands = $"nullsrc=size=64x48:rate={rate},geq=lum='if(mod(floor(mod(N\\,4096)/pow(2\\,floor(Y/4)))\\,2)\\,235\\,16)':cb=128:cr=128," +
                       $"scale={Width}:{Height}:flags=neighbor,setsar=1";
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", bands,
            "-f", "lavfi", "-i", "aevalsrc='0.5*sin(2*PI*1000*t)*lt(mod(t\\,1)\\,0.1)':s=48000:c=stereo",
            "-t", seconds.ToString(CultureInfo.InvariantCulture), "-map", "0:v", "-map", "1:a" };
        args.AddRange(encoder);
        args.AddRange(new[] { "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k" });
        if (segment is { } s)
            args.AddRange(new[] { "-f", "segment", "-segment_time", s.ToString(CultureInfo.InvariantCulture), "-segment_format", "mp4",
                                  "-reset_timestamps", "1", "-segment_start_number", "1" });
        args.Add(Path.Combine(Dir, file));
        var (_, err, exit) = ClipTools.Run(Path.Combine(TestAssets.FfmpegDir!, "ffmpeg.exe"), args);
        if (exit != 0) throw new InvalidOperationException($"No se pudo generar {file}: {err}");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Lo que las pruebas de clips miran de un archivo: el número de cada cuadro, la lista de edición, el audio.</summary>
internal static class ClipTools
{
    public static string Ffmpeg => Path.Combine(TestAssets.FfmpegDir!, "ffmpeg.exe");
    public static string Ffprobe => Path.Combine(TestAssets.FfmpegDir!, "ffprobe.exe");

    public static (string Out, string Err, int Exit) Run(string exe, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (o.Result, e.Result, p.ExitCode);
    }

    /// <summary>El número de cada cuadro tal como lo presenta FFmpeg (con la lista de edición aplicada, salvo
    /// <paramref name="ignoreEditList"/>).</summary>
    public static List<int> Codes(string file, bool ignoreEditList = false)
    {
        var psi = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        var args = new List<string> { "-hide_banner", "-loglevel", "error" };
        if (ignoreEditList) args.AddRange(new[] { "-ignore_editlist", "1" });
        args.AddRange(new[] { "-i", file, "-map", "0:v:0", "-vf", "scale=1:12:flags=area,format=gray", "-fps_mode", "passthrough", "-f", "rawvideo", "-" });
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        var bytes = ms.ToArray();
        var codes = new List<int>();
        for (int i = 0; i + 12 <= bytes.Length; i += 12)
        {
            int code = 0;
            for (int b = 0; b < 12; b++) if (bytes[i + b] > 128) code |= 1 << b;
            codes.Add(code);
        }
        return codes;
    }

    /// <summary>El número de un cuadro BGRA del reproductor.</summary>
    public static int Code(ReviewFrame f)
    {
        int code = 0;
        for (int b = 0; b < 12; b++)
        {
            int y = (int)((b + 0.5) * f.Height / 12), x = f.Width / 2;
            if (f.Bgra[(y * f.Width + x) * 4 + 1] > 128) code |= 1 << b;
        }
        return code;
    }

    /// <summary>Segundos de audio que salen al decodificar la primera pista (las muestras, una a una).</summary>
    public static double AudioSeconds(string file)
    {
        var psi = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", file, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "s16le", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        long bytes = 0;
        var buffer = new byte[65536];
        int n;
        while ((n = p.StandardOutput.BaseStream.Read(buffer, 0, buffer.Length)) > 0) bytes += n;
        p.WaitForExit();
        return bytes / 2 / 48000.0;
    }

    /// <summary>
    /// Cuánto va cada pitido por detrás (+) o por delante (−) de su cuadro: el k-ésimo segundo de la fuente empieza en el cuadro
    /// 25·k (o el que toque a su cadencia) y con su pitido. Marcas como las presenta FFmpeg (con la lista de edición aplicada).
    /// </summary>
    public static List<(int Second, double Ms)> AvOffsets(string file, double fps)
    {
        var codes = Codes(file);
        var (frames, _, _) = Run(Ffprobe, new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts_time", "-of", "csv=p=0", file });
        var times = frames.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(t => double.Parse(t.TrimEnd(','), CultureInfo.InvariantCulture)).ToList();
        var (astart, _, _) = Run(Ffprobe, new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=start_time", "-of", "csv=p=0", file });
        double a0 = double.Parse(astart.Trim().TrimEnd(','), CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo(Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-i", file, "-map", "0:a:0", "-ac", "1", "-ar", "48000", "-f", "s16le", "-" })
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        _ = p.StandardError.ReadToEndAsync();
        using var ms = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(ms);
        p.WaitForExit();
        var pcm = ms.ToArray();
        var onsets = new List<double>();
        bool loud = false;
        for (int i = 0; i + 96 <= pcm.Length; i += 96) // bloques de 1 ms
        {
            int peak = 0;
            for (int j = i; j < i + 96; j += 2) peak = Math.Max(peak, Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(j))));
            if (peak > 3000 && !loud) { onsets.Add(a0 + i / 2 / 48000.0); loud = true; }
            else if (peak < 300) loud = false;
        }
        var result = new List<(int, double)>();
        for (int second = 1; second < 4096 / fps; second++)
        {
            // El primer cuadro del segundo; a 29,97 empieza un poco DESPUÉS del pitido (el cuadro 300 a los 10,01 s): eso no es
            // desfase, se descuenta.
            long first = (long)Math.Ceiling(second * fps - 1e-9);
            double lead = (first / fps - second) * 1000;
            int at = codes.IndexOf((int)(first % 4096));
            if (at < 0 || at >= times.Count) continue;
            var near = onsets.Where(o => Math.Abs(o - times[at]) < 0.5).ToList();
            if (near.Count > 0) result.Add((second, Math.Round((near[0] - times[at]) * 1000 + lead, 1)));
        }
        return result;
    }

    /// <summary>Las listas de edición del MP4/MOV, por pista: (tipo, escala de tiempo de la pista, [(duración, inicio en el medio)]).</summary>
    public static List<(string Handler, uint Timescale, List<(long Duration, long MediaTime)> Edits)> EditLists(string file)
    {
        var data = File.ReadAllBytes(file);
        var tracks = new List<(string, uint, List<(long, long)>)>();
        void Walk(int start, int end, (string H, uint T, List<(long, long)> E)? track)
        {
            int pos = start;
            while (pos + 8 <= end)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
                string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                int header = 8;
                if (size == 1) { size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8)); header = 16; }
                else if (size == 0) size = end - pos;
                int body = pos + header, next = (int)(pos + size);
                switch (type)
                {
                    case "moov" or "mdia" or "minf" or "edts": Walk(body, next, track); break;
                    case "trak":
                        var t = ("?", 0u, new List<(long, long)>());
                        var holder = new List<(string, uint, List<(long, long)>)> { t };
                        WalkTrack(body, next, holder);
                        tracks.Add(holder[0]);
                        break;
                }
                pos = next;
            }
        }
        void WalkTrack(int start, int end, List<(string H, uint T, List<(long, long)> E)> holder)
        {
            int pos = start;
            while (pos + 8 <= end)
            {
                long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
                string type = System.Text.Encoding.ASCII.GetString(data, pos + 4, 4);
                int body = pos + 8, next = (int)(pos + size);
                var (h, ts, edits) = holder[0];
                switch (type)
                {
                    case "edts" or "mdia" or "minf": WalkTrack(body, next, holder); break;
                    case "elst":
                        int version = data[body];
                        int count = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 4));
                        int p = body + 8;
                        for (int i = 0; i < count; i++)
                        {
                            if (version == 1) { edits.Add(((long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p)), BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(p + 8)))); p += 20; }
                            else { edits.Add((BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(p + 4)))); p += 12; }
                        }
                        break;
                    case "mdhd":
                        int v = data[body];
                        ts = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(body + 4 + (v == 1 ? 16 : 8)));
                        holder[0] = (h, ts, edits);
                        break;
                    case "hdlr":
                        holder[0] = (System.Text.Encoding.ASCII.GetString(data, body + 8, 4), ts, edits);
                        break;
                }
                pos = next;
            }
        }
        Walk(0, data.Length, null);
        return tracks;
    }

    /// <summary>Una grabación terminada con esos archivos como segmentos.</summary>
    public static RecordingSession Session(IReadOnlyList<string> files, bool ended = true)
    {
        var session = new RecordingSession
        {
            ChannelId = Guid.NewGuid(), ProfileId = Guid.NewGuid(), InputSourceId = Guid.NewGuid(),
            StartedAt = new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.Zero),
            EndedAt = ended ? new DateTimeOffset(2026, 10, 21, 20, 0, 30, TimeSpan.Zero) : null,
            State = ended ? RecordingState.Idle : RecordingState.Recording,
        };
        for (int i = 0; i < files.Count; i++)
            session.Segments.Add(new Segment
            {
                SessionId = session.Id, Index = i, FilePath = files[i], Status = SegmentStatus.Completed,
                StartedAt = session.StartedAt.AddSeconds(10 * i), SizeBytes = File.Exists(files[i]) ? new FileInfo(files[i]).Length : 0,
            });
        return session;
    }
}

/// <summary>Sesiones en memoria para el servicio de clips (solo lo que él usa).</summary>
internal sealed class ClipSessions : IRecordingSessionRepository
{
    public List<RecordingSession> All { get; } = new();
    public Task<RecordingSession?> GetWithSegmentsAsync(Guid id, CancellationToken ct = default) => Task.FromResult(All.FirstOrDefault(s => s.Id == id));
    public Task<RecordingSession?> GetAsync(Guid id, CancellationToken ct = default) => GetWithSegmentsAsync(id, ct);
    public Task<IReadOnlyList<RecordingSession>> GetHistoryAsync(Guid? channelId, DateTimeOffset from, DateTimeOffset to, int skip, int take, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecordingSession>>(All.ToList());
    public Task<IReadOnlyList<RecordingSession>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RecordingSession>>(All.ToList());
    public Task AddAsync(RecordingSession entity, CancellationToken ct = default) { All.Add(entity); return Task.CompletedTask; }
    public Task UpdateAsync(RecordingSession entity, CancellationToken ct = default) => Task.CompletedTask;
    public Task RemoveAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> CloseOrphanedAsync(DateTimeOffset endedAt, CancellationToken ct = default) => Task.FromResult(0);
    public Task<IReadOnlyList<RecordingSession>> GetEndedBeforeAsync(Guid channelId, DateTimeOffset cutoff, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecordingSession>>(Array.Empty<RecordingSession>());
    public Task<bool> SetProtectionAsync(Guid id, RecordingProtection protection, CancellationToken ct = default) => Task.FromResult(false);
    public Task<IReadOnlyList<RecordingSession>> GetPurgeCandidatesAsync(int take, string? volumePrefix, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RecordingSession>>(Array.Empty<RecordingSession>());
    public Task<bool> RemoveRecordsAsync(Guid sessionId, IReadOnlyCollection<Guid> segmentIds, CancellationToken ct = default) => Task.FromResult(false);
}

/// <summary>
/// Clips con el FFmpeg real (<see cref="ClipExportService"/>) y el reproductor de la revisión (<see cref="ReviewPlayer"/>): el
/// modo EXACTO empieza y acaba en el cuadro pedido (una pieza y entre segmentos, con y sin B-frames, a 25 y a 29,97); el RÁPIDO
/// empieza en el fotograma clave anterior y su lista de edición lleva al cuadro pedido, sin huecos al final; el reproductor
/// enseña el cuadro exacto de cada búsqueda y avanza de uno en uno; la API devuelve la ruta del clip.
/// </summary>
public sealed class ClipExportTests : IClassFixture<ClipMaterial>
{
    private readonly ClipMaterial _m;
    private readonly ITestOutputHelper _out;

    public ClipExportTests(ClipMaterial material, ITestOutputHelper output)
    {
        _m = material;
        _out = output;
    }

    /// <summary>El servicio con su propia carpeta de clips (los nombres de una prueba no chocan con los de otra).
    /// <paramref name="probeGpu"/>: sondear NVENC como en la aplicación; si no, solo CPU.</summary>
    private (ClipExportService Service, ClipSessions Sessions, List<ClipExported> Events, string Folder) Service(bool probeGpu = false)
    {
        var folder = Path.Combine(_m.Dir, "clips-" + Guid.NewGuid().ToString("N")[..8]);
        var sessions = new ClipSessions();
        var bus = new InProcessEventBus();
        var events = new List<ClipExported>();
        bus.Subscribe<ClipExported>((e, _) => { lock (events) events.Add(e); return Task.CompletedTask; });
        var service = new ClipExportService(sessions, new FfmpegLocator(TestAssets.FfmpegDir!), settings: null, folder,
            NullLogger<ClipExportService>.Instance, bus, support: probeGpu ? null : ClipEncoderSupport.CpuOnly);
        return (service, sessions, events, folder);
    }

    private static void AssertContiguous(List<int> codes, string what)
    {
        for (int i = 1; i < codes.Count; i++)
            Assert.True(codes[i] == (codes[i - 1] + 1) % 4096, $"{what}: salta del cuadro {codes[i - 1]} al {codes[i]} (posición {i}).");
    }

    [SkippableTheory]
    [InlineData("nobf", 309, 425)]
    [InlineData("bf", 309, 425)]
    [InlineData("ntsc", 299, 421)]
    [InlineData("seg", 209, 325)]   // cruza el primer corte de segmento (cuadro 250)
    [InlineData("segbf", 470, 530)] // cruza el segundo
    [InlineData("seg", 249, 251)]   // dos cuadros, uno a cada lado del corte
    public async Task Exact_StartsAndEndsOnTheRequestedFrame(string recording, long from, long to)
    {
        Skip.IfNot(_m.Ready, "FFmpeg no disponible en tools/.");
        var (service, sessions, events, folder) = Service();
        var session = ClipTools.Session(_m.Files(recording));
        sessions.All.Add(session);

        var result = await service.ExportAsync(new ClipRequest(session.Id, from, to, ClipMode.Exact) { Operator = "pruebas" });
        var codes = ClipTools.Codes(result.FilePath);
        _out.WriteLine($"{Path.GetFileName(result.FilePath)}: {codes.First()}..{codes.Last()} ({codes.Count}) · {result.VideoEncoder}");

        Assert.Equal(from, codes[0]);
        Assert.Equal(to - 1, codes[^1]);
        Assert.Equal(to - from, codes.Count);
        AssertContiguous(codes, "exacto");
        Assert.Equal("libx264", result.VideoEncoder);
        // Sin B-frames: la lista de edición del vídeo empieza en 0 (el reproductor de Windows, que no la lee, lo ve igual).
        var video = ClipTools.EditLists(result.FilePath).Single(t => t.Handler == "vide");
        Assert.All(video.Edits.Where(e => e.MediaTime >= 0), e => Assert.Equal(0, e.MediaTime));
        // El audio dura lo que el vídeo (un paquete de AAC de margen) y cada pitido sigue con su cuadro, también tras la unión de
        // dos segmentos con B-frames (antes de -flags2:a +skip_manual, 64 ms menos de audio y 60 ms de adelanto tras la unión).
        var timeline = await service.LoadTimelineAsync(session.Id);
        double seconds = timeline.SecondsOf(to - from);
        Assert.InRange(ClipTools.AudioSeconds(result.FilePath), seconds - 0.03, seconds + 0.03);
        var sync = ClipTools.AvOffsets(result.FilePath, timeline.Rate.Value);
        _out.WriteLine($"pitido − cuadro (ms): {string.Join(" ", sync.Select(s => $"{s.Second}s:{s.Ms:+0.0;-0.0}"))}");
        Assert.All(sync, s => Assert.InRange(s.Ms, -5, 5));
        Assert.Single(events);
        Assert.Equal(ClipMode.Exact, events[0].Mode);
        Assert.Equal("pruebas", events[0].Operator);
        Assert.Empty(Directory.GetFiles(folder, "*" + ClipNaming.PartSuffix));
    }

    [SkippableTheory]
    [InlineData("nobf", 309, 425)]
    [InlineData("bf", 309, 425)]
    [InlineData("seg", 209, 325)]
    [InlineData("segbf", 215, 331)]
    public async Task Fast_StartsAtThePreviousKeyframe_WithAnEditListToTheRequestedFrame(string recording, long from, long to)
    {
        Skip.IfNot(_m.Ready, "FFmpeg no disponible en tools/.");
        var (service, sessions, events, _) = Service();
        var session = ClipTools.Session(_m.Files(recording));
        sessions.All.Add(session);

        var result = await service.ExportAsync(new ClipRequest(session.Id, from, to, ClipMode.Fast));
        var shown = ClipTools.Codes(result.FilePath);
        var raw = ClipTools.Codes(result.FilePath, ignoreEditList: true);
        var video = ClipTools.EditLists(result.FilePath).Single(t => t.Handler == "vide");
        _out.WriteLine($"{Path.GetFileName(result.FilePath)}: con la lista {shown.First()}..{shown.Last()} ({shown.Count}), " +
                       $"sin ella {raw.First()}..{raw.Last()} ({raw.Count}); preroll {result.PreRoll.TotalSeconds:0.000} s, " +
                       $"{result.ExtraFrames} de más; elst {string.Join(" ", video.Edits)} / {video.Timescale}");

        // Con la lista de edición (FFmpeg, editores): empieza en el cuadro pedido.
        Assert.Equal(from, shown[0]);
        // Sin ella (el reproductor de Windows): en el fotograma clave anterior, que es lo que dice el resultado.
        long keyframe = raw[0];
        Assert.True(keyframe <= from, $"la copia empieza en el {keyframe}, después de la entrada {from}");
        Assert.Equal(from - keyframe, (long)Math.Round(result.PreRoll.TotalSeconds * 25));
        Assert.Contains(video.Edits, e => e.MediaTime > 0);
        // Al final, sin huecos: hasta la salida y, con B-frames, los justos de más.
        AssertContiguous(shown, "rápido");
        Assert.Equal(to - 1 + result.ExtraFrames, shown[^1]);
        Assert.InRange(result.ExtraFrames, 0, recording.Contains("bf") ? 4 : 0);
        // La copia lleva también el audio que cada segmento tapa con su lista de edición: los pitidos siguen con su cuadro.
        var sync = ClipTools.AvOffsets(result.FilePath, 25);
        _out.WriteLine($"pitido − cuadro (ms): {string.Join(" ", sync.Select(s => $"{s.Second}s:{s.Ms:+0.0;-0.0}"))}");
        Assert.NotEmpty(sync);
        Assert.All(sync, s => Assert.InRange(s.Ms, -5, 5));
        Assert.Null(result.VideoEncoder);
        Assert.Equal(ClipMode.Fast, Assert.Single(events).Mode);
    }

    [SkippableFact]
    public async Task TheClip_IsNamedAfterTheRecordingAndTheSpan_AndNeverOverwritesAnother()
    {
        Skip.IfNot(_m.Ready, "FFmpeg no disponible en tools/.");
        var (service, sessions, events, _) = Service();
        var session = ClipTools.Session(_m.Files("seg"));
        sessions.All.Add(session);

        var first = await service.ExportAsync(new ClipRequest(session.Id, 200, 325, ClipMode.Fast));
        var second = await service.ExportAsync(new ClipRequest(session.Id, 200, 325, ClipMode.Fast));
        Assert.Equal("seg (00.00.08-00.00.13).mp4", Path.GetFileName(first.FilePath));
        Assert.Equal("seg (00.00.08-00.00.13) 2.mp4", Path.GetFileName(second.FilePath));
        var named = await service.ExportAsync(new ClipRequest(session.Id, 0, 25, ClipMode.Exact) { Name = "Gol: 1-0" });
        Assert.Equal("Gol_ 1-0.mp4", Path.GetFileName(named.FilePath));
        // Dos a la vez (la ventana y la API) con el mismo nombre: cada una se reserva el suyo antes de que FFmpeg escriba.
        var both = await Task.WhenAll(
            service.ExportAsync(new ClipRequest(session.Id, 0, 50, ClipMode.Fast) { Name = "Mismo" }),
            service.ExportAsync(new ClipRequest(session.Id, 0, 50, ClipMode.Fast) { Name = "Mismo" }));
        Assert.Equal(new[] { "Mismo 2.mp4", "Mismo.mp4" }, both.Select(r => Path.GetFileName(r.FilePath)).OrderBy(n => n));
        Assert.All(both, r => Assert.Equal(50, ClipTools.Codes(r.FilePath).Count));
        events.RemoveAll(e => e.FileName.StartsWith("Mismo", StringComparison.Ordinal));
        Assert.Equal(3, events.Count);
        Assert.Equal("seg_1.mp4", events[0].SourceFileName);
        Assert.Equal(TimeSpan.FromSeconds(8), events[0].In);
        Assert.Equal(TimeSpan.FromSeconds(13), events[0].Out);
    }

    [SkippableFact]
    public async Task ARecordingInProgress_OrMissingAllItsFiles_IsRefused()
    {
        Skip.IfNot(_m.Ready, "FFmpeg no disponible en tools/.");
        var (service, sessions, _, _) = Service();
        var live = ClipTools.Session(_m.Files("nobf"), ended: false);
        var gone = ClipTools.Session(new[] { Path.Combine(_m.Dir, "no-existe_1.mp4"), Path.Combine(_m.Dir, "no-existe_2.mp4") });
        sessions.All.AddRange(new[] { live, gone });

        Assert.Equal(ClipError.InProgress, (await Assert.ThrowsAsync<ClipException>(() => service.LoadTimelineAsync(live.Id))).Error);
        Assert.Equal(ClipError.FileMissing, (await Assert.ThrowsAsync<ClipException>(() => service.LoadTimelineAsync(gone.Id))).Error);
        Assert.Equal(ClipError.NotFound, (await Assert.ThrowsAsync<ClipException>(() => service.LoadTimelineAsync(Guid.NewGuid()))).Error);
    }

    [SkippableFact]
    public async Task AnUnreadablePiece_IsLeftOutOfTheTimeline_AndTheRestStillCuts()
    {
        Skip.IfNot(_m.Ready, "FFmpeg no disponible en tools/.");
        // La pieza que se cortó en una caída (MP4 sin índice): bytes sin moov.
        var broken = Path.Combine(_m.Dir, "seg_cortado.mp4");
        var whole = File.ReadAllBytes(_m.Files("seg")[1]);
        File.WriteAllBytes(broken, whole.Take(whole.Length / 2).ToArray());
        var (service, sessions, _, _) = Service();
        var files = _m.Files("seg");
        var session = ClipTools.Session(new[] { files[0], broken, files[2] });
        sessions.All.Add(session);

        var timeline = await service.LoadTimelineAsync(session.Id);
        Assert.Equal(500, timeline.TotalFrames);
        var skipped = Assert.Single(timeline.Skipped);
        Assert.Equal(SkipReason.Unreadable, skipped.Reason);
        // El tramo que cruza el hueco une la 1.ª y la 3.ª pieza (corte en seco donde faltaba la segunda).
        var r = await service.ExportAsync(new ClipRequest(session.Id, 240, 260, ClipMode.Exact));
        var codes = ClipTools.Codes(r.FilePath);
        Assert.Equal(Enumerable.Range(240, 10).Concat(Enumerable.Range(500, 10)), codes);
    }

    [SkippableFact]
    public async Task Exact_WithNvenc_WhenTheCardHasIt()
    {
        Skip.IfNot(_m.Ready, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        Skip.IfNot(await locator.IsVideoEncoderUsableAsync("h264_nvenc"), "h264_nvenc no abre en este equipo.");
        var (service, sessions, _, _) = Service(probeGpu: true);
        var session = ClipTools.Session(_m.Files("segbf"));
        sessions.All.Add(session);

        var result = await service.ExportAsync(new ClipRequest(session.Id, 230, 280, ClipMode.Exact));
        Assert.Equal("h264_nvenc", result.VideoEncoder);
        var codes = ClipTools.Codes(result.FilePath);
        Assert.Equal(Enumerable.Range(230, 50), codes);
    }

    [SkippableFact]
    public async Task ThePlayer_ShowsTheExactFrameOfEachSeek_AndStepsOneByOne()
    {
        Skip.IfNot(_m.Ready, "FFmpeg no disponible en tools/.");
        var (service, sessions, _, _) = Service();
        var session = ClipTools.Session(_m.Files("segbf"));
        sessions.All.Add(session);
        var timeline = await service.LoadTimelineAsync(session.Id);
        var shown = new List<(long Frame, int Code)>();
        await using var player = new ReviewPlayer(ClipTools.Ffmpeg, timeline, audio: null, NullLogger.Instance);
        player.FrameShown += (_, f) => { lock (shown) shown.Add((f.Frame, ClipTools.Code(f))); };
        Assert.Equal((ClipMaterial.Width, ClipMaterial.Height), (player.FrameWidth, player.FrameHeight));

        async Task<(long Frame, int Code)> After(Action action)
        {
            int before;
            lock (shown) before = shown.Count;
            action();
            await player.WhenIdleAsync(TimeSpan.FromSeconds(20));
            lock (shown) { Assert.True(shown.Count > before, "no se enseñó ningún cuadro"); return shown[^1]; }
        }

        foreach (long target in new long[] { 0, 209, 255, 600, 249, 250, 749 })
        {
            var (frame, code) = await After(() => player.Seek(target));
            Assert.Equal(target, frame);
            Assert.Equal(target, code); // el cuadro exacto, decodificado desde su fotograma clave
        }
        var seek = await After(() => player.Seek(300));
        Assert.Equal(300, seek.Code);
        var watch = Stopwatch.StartNew();
        Assert.Equal(301, (await After(() => player.Step(1))).Code);  // del mismo proceso
        Assert.Equal(302, (await After(() => player.Step(1))).Code);
        _out.WriteLine($"dos pasos adelante: {watch.ElapsedMilliseconds} ms");
        Assert.Equal(301, (await After(() => player.Step(-1))).Code); // vuelve a decodificar desde la clave
        Assert.Equal(276, (await After(() => player.Step(-25))).Code);

        // Reproducir un segundo: cuadros seguidos, sin saltos, desde el siguiente.
        lock (shown) shown.Clear();
        player.Play();
        await Task.Delay(1200);
        player.Pause();
        await player.WhenIdleAsync(TimeSpan.FromSeconds(5));
        List<(long Frame, int Code)> played;
        lock (shown) played = shown.ToList();
        _out.WriteLine($"reproducción: {played.Count} cuadros, {played.First().Code}..{played.Last().Code}");
        Assert.True(played.Count >= 15, $"solo {played.Count} cuadros en 1,2 s");
        Assert.Equal(277, played[0].Code);
        Assert.All(played, p => Assert.Equal(p.Frame, p.Code));
        Assert.False(player.IsPlaying);
    }

    /// <summary>
    /// Con la CPU llena, los FFmpeg del reproductor (por debajo de las grabaciones) no decodifican a tiempo: antes el vídeo se iba
    /// quedando segundos atrás del audio (medido con la CPU al 100 %: 2–10 s). Aquí se frena su FFmpeg al 30 % del tiempo real
    /// (filtro <c>realtime</c>): el vídeo tiene que saltar adelante, a fotogramas clave, y seguir al reloj.
    /// </summary>
    [SkippableFact]
    public async Task ThePlayer_JumpsAheadToKeepUpWithTheClock_WhenItCannotDecodeInTime()
    {
        Skip.IfNot(_m.Ready, "FFmpeg no disponible en tools/.");
        var (service, sessions, _, _) = Service();
        var session = ClipTools.Session(_m.Files("nobf")); // GOP de 50 cuadros (2 s)
        sessions.All.Add(session);
        var timeline = await service.LoadTimelineAsync(session.Id);
        var shown = new List<(long Frame, int Code, double At)>();
        var clock = new Stopwatch();
        int jumps = 0;
        await using var player = new ReviewPlayer(ClipTools.Ffmpeg, timeline, audio: null, NullLogger.Instance)
        {
            TestVideoFilter = "realtime=speed=0.3",
        };
        player.FellBehind += (_, _) => Interlocked.Increment(ref jumps);
        player.FrameShown += (_, f) => { lock (shown) shown.Add((f.Frame, ClipTools.Code(f), clock.Elapsed.TotalSeconds)); };
        player.Seek(0);
        await player.WhenIdleAsync(TimeSpan.FromSeconds(20));
        lock (shown) shown.Clear();

        clock.Start();
        player.Play(); // desde el cuadro 1
        await Task.Delay(8000);
        player.Pause();
        await player.WhenIdleAsync(TimeSpan.FromSeconds(5));
        List<(long Frame, int Code, double At)> played;
        lock (shown) played = shown.ToList();
        // Retraso de cada cuadro enseñado sobre el reloj de la reproducción (sin audio, el reloj es el del reproductor).
        var lags = played.Select(p => p.At - (p.Frame - 1) / 25.0).ToList();
        _out.WriteLine($"{played.Count} cuadros, hasta el {played.Last().Frame}; {jumps} saltos; retraso máximo {lags.Max():0.00} s; " +
                       $"cuadros: {string.Join(" ", played.Select(p => p.Frame).Take(60))}");

        Assert.True(jumps > 0, "no saltó nunca");
        Assert.All(played, p => Assert.Equal(p.Frame % 4096, p.Code));
        // Sin saltar, a los 8 s el vídeo iría unos 5,5 s detrás; saltando, nunca más de lo que espera antes de saltar.
        Assert.True(lags.Max() < 1.5, $"el vídeo llegó a ir {lags.Max():0.00} s detrás del reloj");
        Assert.True(played.Last().Frame > 6 * 25, $"a los 8 s solo había llegado al cuadro {played.Last().Frame}");
    }

    [SkippableFact]
    public async Task TheApi_CutsAClip_AndSaysWhereItIs()
    {
        Skip.IfNot(_m.Ready, "FFmpeg no disponible en tools/.");
        var (service, sessions, _, _) = Service();
        var session = ClipTools.Session(_m.Files("seg"));
        sessions.All.Add(session);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IChannelEngine>(new FakeChannelEngine(Guid.NewGuid(), "A"));
        builder.Services.AddSingleton<IChannelManager, ChannelManager>();
        builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
        builder.Services.AddSingleton<IStorageManager, StorageManager>();
        builder.Services.AddSingleton<IClipExportService>(service);
        builder.Services.AddBaiossCqrs();
        await using var app = builder.Build();
        app.MapBaiossApi();
        await app.StartAsync();
        var client = app.GetTestClient();

        // Segundos y timecode: del 8,36 (cuadro 209) al 00:00:13:00 (el 325 ya no entra).
        var ok = await client.PostAsJsonAsync($"/api/v1/recordings/{session.Id}/clip", new { @in = 8.36, @out = "00:00:13:00", mode = "exact" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await ok.Content.ReadFromJsonAsync<JsonElement>();
        var path = body.GetProperty("path").GetString()!;
        Assert.True(File.Exists(path));
        Assert.Equal(209, body.GetProperty("inFrame").GetInt64());
        Assert.Equal(325, body.GetProperty("outFrame").GetInt64());
        Assert.Equal("00:00:08:09", body.GetProperty("in").GetString());
        Assert.Equal("exact", body.GetProperty("mode").GetString());
        Assert.Equal(4.64, body.GetProperty("durationSeconds").GetDouble(), 3);
        var codes = ClipTools.Codes(path);
        Assert.Equal(209, codes[0]);
        Assert.Equal(324, codes[^1]);

        var fast = await client.PostAsJsonAsync($"/api/v1/recordings/{session.Id}/clip", new { @in = "8.36", @out = "13", name = "por api" });
        Assert.Equal(HttpStatusCode.OK, fast.StatusCode);
        var fastBody = await fast.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("fast", fastBody.GetProperty("mode").GetString());
        Assert.Equal("por api.mp4", fastBody.GetProperty("fileName").GetString());
        Assert.True(fastBody.GetProperty("preRollSeconds").GetDouble() > 0);

        Assert.Equal(HttpStatusCode.UnprocessableEntity,
            (await client.PostAsJsonAsync($"/api/v1/recordings/{session.Id}/clip", new { @in = 25, @out = 40 })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/v1/recordings/{session.Id}/clip", new { @in = 1, @out = 2, mode = "lento" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.PostAsJsonAsync($"/api/v1/recordings/{session.Id}/clip", new { @out = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"/api/v1/recordings/{Guid.NewGuid()}/clip", new { @in = 1, @out = 2 })).StatusCode);
    }
}
