using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;

// --- Argumentos ---
// Por defecto se localiza el FFmpeg y el clip incluidos en el repo (carpeta tools/),
// subiendo desde el ejecutable; así no depende de rutas absolutas de una máquina concreta.
string ffmpegDir = args.Length > 0 ? args[0]
    : FindUpwards(Path.Combine("tools", "ffmpeg", "ffmpeg.exe")) is { } exe ? Path.GetDirectoryName(exe)! : "ffmpeg";
string clip = args.Length > 1 ? args[1]
    : FindUpwards(Path.Combine("tools", "test", "clip.mp4")) ?? "tools/test/clip.mp4";
int seconds = args.Length > 2 && int.TryParse(args[2], out var s) ? s : 6;

static string? FindUpwards(string relative)
{
    foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        var dir = new DirectoryInfo(start);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
    }
    return null;
}

var locator = new FfmpegLocator(ffmpegDir);
Console.WriteLine($"ffmpeg : {locator.FfmpegPath}");

// Elegir encoder: NVENC si el driver está presente, si no libx264.
bool nvenc = await locator.IsVideoEncoderUsableAsync("h264_nvenc");
var videoCodec = nvenc ? VideoCodec.H264Nvenc : VideoCodec.H264x264;
Console.WriteLine($"encoder: {videoCodec} (nvenc usable = {nvenc})");

// Fuente: archivo leído en tiempo real (simula una entrada en vivo).
var sourceDef = new InputSource
{
    Name = "clip", Type = InputType.File, Uri = Path.GetFullPath(clip),
    Parameters = { ["realtime"] = "1" }
};
var source = new FileCaptureSourceFactory().Create(sourceDef);
await source.OpenAsync();

var profile = new RecordingProfile
{
    Name = "smoke",
    VideoCodec = videoCodec,
    HwAccel = HwAccel.None,                 // decode por software; encode en GPU si NVENC
    VideoBitrate = Bitrate.FromMbps(8),
    GopSize = 50,
    AudioCodec = AudioCodec.Aac,
    AudioLayout = AudioLayout.Stereo,
    Container = ContainerFormat.Mp4,
};

var engine = new FfmpegRecorderEngine(locator, NullLogger<FfmpegRecorderEngine>.Instance)
{
    OutputRoot = "tools/test/rec",
    ProxyRoot = "tools/test/proxy",
};
engine.StateChanged += (_, st) => Console.WriteLine($"[state ] {st}");
engine.StatsUpdated += (_, x) => Console.WriteLine($"[stats ] tc={x.Timecode} fps={x.OutputFps:0.#} br={x.Bitrate} frames={x.FrameCount}");
engine.SegmentClosed += (_, seg) => Console.WriteLine($"[segment] #{seg.Index} {seg.FilePath} ({seg.SizeBytes} bytes)");
int audioTick = 0;
engine.AudioLevelsUpdated += (_, lr) => { if (audioTick++ % 8 == 0) Console.WriteLine($"[audio ] L={lr.Left:0.0} R={lr.Right:0.0} dBFS"); };

var session = new RecordingSession
{
    ChannelId = Guid.NewGuid(), ProfileId = profile.Id, InputSourceId = sourceDef.Id,
    StartedAt = DateTimeOffset.UtcNow,
};

Console.WriteLine($"Grabando {seconds}s…");
await engine.StartAsync(session, profile, source);
await Task.Delay(TimeSpan.FromSeconds(seconds));
await engine.StopAsync();
await Task.Delay(TimeSpan.FromMilliseconds(500)); // dejar cerrar el contenedor

var outFile = engine.LastOutputFile;
Console.WriteLine($"\nArchivo: {outFile}");
if (outFile is not null && File.Exists(outFile) && new FileInfo(outFile).Length > 0)
{
    Console.WriteLine($"OK · {new FileInfo(outFile).Length} bytes");
    Console.WriteLine($"RESULT={outFile}");
    return 0;
}
Console.Error.WriteLine("FALLO: no se generó el archivo de salida.");
return 1;
