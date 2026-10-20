using System.Diagnostics;
using System.Globalization;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Presets;
using Baioss.Record.Domain;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Qué frame de la fuente enseña el preview en cada instante alrededor de Grabar y Detener, con el motor real y una entrada
/// DeckLink sintética por el relé. Cada frame de la fuente lleva su número (módulo 128) en ocho franjas blancas o negras, una por
/// bit, que ninguna conversión ni escalado confunde; el relé dice cuántos frames lleva repartidos, es decir, cuál es el directo.
/// Grabar y Detener cambian el proceso del canal: el preview no debe volver nunca a un frame que ya se vio ni quedarse sin imagen.
/// Antes del arreglo (2026-10-19) saltaba atrás en cada cambio y volvía a pasar por los mismos frames: 4–5 con x264, 8–10 al
/// Grabar con NVENC («es como que retrocede y vuelve al mismo frame»). Variables: BAIOSS_PREVIEW_HANDOVER_CYCLES (3) y
/// BAIOSS_PREVIEW_HANDOVER_LOG (archivo al que se añade el resultado y, frame a frame, cada cambio).
/// </summary>
public sealed class PreviewHandoverTests
{
    private const int Wrap = 128;
    private readonly ITestOutputHelper _out;
    public PreviewHandoverTests(ITestOutputHelper output) => _out = output;

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private sealed record Shown(double At, long Live, int Code);

    private sealed record Change(string What, double Took, int Baseline, long Rewind, double RewindAt, int MaxLag, double Gap);

    [SkippableTheory]
    [InlineData("H.264 Broadcast · 1080i59.94 MP4", null)]
    [InlineData("H.264 NVENC · FullHD 1080p29.97", "h264_nvenc")]
    public async Task RecordAndStop_ThePreviewNeverGoesBack(string presetName, string? encoder)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        if (encoder is not null)
        {
            var (ok, detail) = await locator.ProbeVideoEncoderAsync(encoder);
            Skip.IfNot(ok, $"{encoder} no abre con este FFmpeg en este equipo: {detail}");
        }
        int cycles = int.Parse(Env("BAIOSS_PREVIEW_HANDOVER_CYCLES") ?? "3", CultureInfo.InvariantCulture);
        var profile = PresetCatalog.CreateBuiltIns().Single(p => p.Name == presetName).ToProfile();

        var clock = Stopwatch.StartNew();
        var lines = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(s => { lock (lines) lines.Add($"{clock.Elapsed.TotalSeconds,8:0.000} s {s}"); });
        string dir = Path.Combine(Path.GetTempPath(), $"baioss-handover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var shown = new List<Shown>();
        var transitions = new List<(string What, double At, double Returned)>();
        var changes = new List<Change>();
        try
        {
            // Lo que entrega una DeckLink con señal a 29,97 (uyvy422 y estéreo, en tiempo real), con el número de cada frame en
            // ocho franjas horizontales: la franja b es blanca si el bit b de (N mod 128) vale 1.
            var synthetic = new[]
            {
                "-re", "-f", "lavfi", "-i",
                "nullsrc=size=64x32:rate=30000/1001,geq=lum='if(mod(floor(mod(N\\,128)/pow(2\\,floor(Y/4)))\\,2)\\,235\\,16)':cb=128:cr=128," +
                "scale=1920:1080:flags=neighbor,format=uyvy422[out0];sine=frequency=1000:sample_rate=48000,aformat=channel_layouts=stereo[out1]",
            };
            var (source, _) = DecklinkRelayContinuityTests.SyntheticSource(locator, synthetic, loggers);
            await using var _s = source;
            await source.OpenAsync();
            var receiver = (RawCaptureReceiver)source.Receiver!; // la fuente lo crea al abrirse
            await using var engine = new FfmpegChannelEngine(locator, loggers.CreateLogger("Engine")) { OutputRoot = dir, FragmentedMp4 = false };
            engine.FrameReady += (_, f) =>
            {
                int code = 0;
                for (int b = 0; b < 8; b++)
                {
                    int y = (int)((b + 0.5) * f.Height / 8), x = f.Width / 2;
                    if (f.Bgra[y * f.Stride + x * 4 + 1] > 128) code |= 1 << b;
                }
                lock (shown) shown.Add(new Shown(clock.Elapsed.TotalSeconds, receiver.VideoFramesForwarded, code));
            };
            await engine.StartPreviewAsync(source, profile, "A");
            await WaitUntilAsync(() => source.CurrentSignal.State == SignalState.Locked, TimeSpan.FromSeconds(20), "SEÑAL OK");
            await WaitUntilAsync(() => { lock (shown) return shown.Count > 60; }, TimeSpan.FromSeconds(20), "preview en marcha");
            await Task.Delay(2000);

            for (int i = 1; i <= cycles; i++)
            {
                double t = clock.Elapsed.TotalSeconds;
                await engine.StartRecordingAsync(Guid.NewGuid(), profile);
                transitions.Add(("Grabar", t, clock.Elapsed.TotalSeconds));
                await Task.Delay(4000);
                t = clock.Elapsed.TotalSeconds;
                await engine.StopRecordingAsync();
                transitions.Add(("Detener", t, clock.Elapsed.TotalSeconds));
                await Task.Delay(3000);
            }
        }
        finally
        {
            List<Shown> frames;
            lock (shown) frames = shown.ToList();
            // Retardo de cada frame del preview en frames de la fuente: el directo (el último que el relé repartió) menos el que se
            // enseña, por su número módulo 128 (el retardo real nunca llega a 4 s).
            static int Lag(Shown s) => (int)(((s.Live - 1 - s.Code) % Wrap + Wrap) % Wrap);
            static long Index(Shown s) => s.Live - 1 - Lag(s);
            foreach (var (what, at, returned) in transitions)
            {
                var before = frames.Where(f => f.At >= at - 1 && f.At < at).Select(Lag).OrderBy(x => x).ToArray();
                var after = frames.Where(f => f.At >= at && f.At <= at + 3).ToList();
                if (before.Length == 0 || after.Count == 0) continue;
                int baseline = before[before.Length / 2];
                // Un salto atrás es un frame de la fuente que ya se vio (o uno anterior): cuántos vuelven a pasar.
                var last = frames.Last(f => f.At < at);
                long maxShown = Index(last);
                long rewind = 0;
                double rewindAt = double.NaN, gap = 0, previousAt = last.At;
                int maxLag = baseline;
                foreach (var f in after)
                {
                    long index = Index(f);
                    if (index <= maxShown && maxShown - index + 1 > rewind) { rewind = maxShown - index + 1; rewindAt = f.At - at; }
                    maxShown = Math.Max(maxShown, index);
                    maxLag = Math.Max(maxLag, Lag(f));
                    gap = Math.Max(gap, f.At - previousAt);
                    previousAt = f.At;
                }
                changes.Add(new Change(what, returned - at, baseline, rewind, rewindAt, maxLag, gap));
            }
            var report = new List<string>
            {
                $"## {presetName} · DeckLink sintética 1080 29,97 · {changes.Count} cambios · {DateTime.Now:yyyy-MM-dd HH:mm}",
                "cambio · vuelve s · retardo antes (frames) · frames que vuelven a pasar · a los s · retardo máx después · hueco máx s",
            };
            report.AddRange(changes.Select(c => string.Create(CultureInfo.InvariantCulture,
                $"{c.What} · {c.Took:0.00} · {c.Baseline} · {c.Rewind} · {(double.IsNaN(c.RewindAt) ? "—" : c.RewindAt.ToString("0.00", CultureInfo.InvariantCulture))} · {c.MaxLag} · {c.Gap:0.000}")));
            foreach (var l in report) _out.WriteLine(l);
            lock (lines) foreach (var l in lines.Where(l => l.Contains("relevo", StringComparison.Ordinal) || l.Contains("preview pasa", StringComparison.Ordinal) || l.Contains("no alcanzó", StringComparison.Ordinal))) _out.WriteLine(l);
            if (Env("BAIOSS_PREVIEW_HANDOVER_LOG") is { } log)
            {
                File.AppendAllLines(log, report.Append(""));
                // La secuencia entera de cada cambio, frame a frame: instante, directo, número enseñado y retardo.
                foreach (var (what, at, _) in transitions)
                    File.AppendAllLines(log, new[] { $"# {what} a {at:0.000} s" }.Concat(frames.Where(f => f.At >= at - 0.3 && f.At <= at + 2)
                        .Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.At - at:+0.000;-0.000} {f.Live - 1} {f.Code} {Lag(f)}"))));
                lock (lines) File.AppendAllLines(log, lines.Where(l => l.Contains("relevo", StringComparison.Ordinal) || l.Contains("preview pasa", StringComparison.Ordinal) || l.Contains("Pipeline canal", StringComparison.Ordinal)));
            }
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }

        Assert.Equal(cycles * 2, changes.Count);
        foreach (var c in changes)
        {
            // Ni un frame de la fuente que ya se vio vuelve a pasar: el viejo pinta hasta que el nuevo le alcanza.
            Assert.True(c.Rewind == 0, $"{c.What}: el preview volvió atrás y pasó otra vez por {c.Rewind} frames de la fuente (a los {c.RewindAt:0.00} s).");
            // Ni se queda sin imagen: un hueco de más de unos frames sería un congelado.
            Assert.True(c.Gap < 0.25, $"{c.What}: el preview se quedó {c.Gap:0.000} s sin frames.");
            // Ni se queda más atrasado que antes: el proceso nuevo toma el mando cuando ya va por donde iba el viejo.
            Assert.True(c.MaxLag <= c.Baseline + 2, $"{c.What}: el preview pasó a ir {c.MaxLag} frames por detrás del directo (antes, {c.Baseline}).");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"No llegó «{what}» en {timeout.TotalSeconds:0} s.");
            await Task.Delay(50);
        }
    }
}
