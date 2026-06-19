using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Domain;
using Baioss.Record.Application.Presets;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;

// Verifica los PRESETS: imprime la línea de comandos de cada built-in y graba con el motor real
// un subconjunto (hasta 1080p) sobre un clip de prueba, confirmando que FFmpeg los acepta.
string ffmpegDir = args.Length > 0 ? args[0] : "tools/ffmpeg";
string clip = args.Length > 1 ? args[1] : "tools/test/clip.mp4";

var locator = new FfmpegLocator(ffmpegDir);
var presets = PresetCatalog.CreateBuiltIns();
Console.WriteLine($"Presets built-in: {presets.Count}\n");

Console.WriteLine("==== LÍNEAS DE COMANDO (todas) ====");
foreach (var p in presets)
    Console.WriteLine($"\n[{p.Category}] {p.Name}\n  {FfmpegCommandPreview.Build(p.ToProfile())}");

Console.WriteLine("\n==== GRABACIÓN REAL (presets ≤ 1080p) ====");
int ok = 0, fail = 0;
foreach (var preset in presets.Where(p => (p.Width ?? 0) <= 1920))
{
    var def = new InputSource
    {
        Name = "clip", Type = InputType.File, Uri = Path.GetFullPath(clip),
        Parameters = { ["realtime"] = "1" },
    };
    var source = new FileCaptureSourceFactory().Create(def);
    await source.OpenAsync();

    var engine = new FfmpegRecorderEngine(locator, NullLogger<FfmpegRecorderEngine>.Instance)
    {
        OutputRoot = "tools/test/presetrec", ProxyRoot = "tools/test/proxy",
    };
    var session = new RecordingSession
    {
        ChannelId = Guid.NewGuid(), ProfileId = preset.Id, InputSourceId = def.Id, StartedAt = DateTimeOffset.UtcNow,
    };

    try
    {
        await engine.StartAsync(session, preset.ToProfile(), source);
        await Task.Delay(TimeSpan.FromSeconds(2));
        await engine.StopAsync();
        await Task.Delay(TimeSpan.FromMilliseconds(400));

        var file = engine.LastOutputFile;
        bool good = file is not null && File.Exists(file) && new FileInfo(file).Length > 0;
        Console.WriteLine($"  {(good ? "OK  " : "FALLO")} · {preset.Name} → {(file is null ? "(sin archivo)" : $"{new FileInfo(file).Length / 1024} KB")}");
        if (good) ok++; else fail++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  FALLO · {preset.Name} → {ex.Message}");
        fail++;
    }
    finally { await engine.DisposeAsync(); await source.DisposeAsync(); }
}

Console.WriteLine($"\nResultado grabación: {ok} OK, {fail} fallidos.");
return fail == 0 ? 0 : 1;
