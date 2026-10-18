using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.Events;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Recording;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Messaging;
using Baioss.Record.Infrastructure.Preview;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// El codificador de VÍDEO de la grabación no abre con lo que entrega la FUENTE y no hay a qué degradar (ya es CPU o intra), y el
/// canal tiene que decirlo. Lo provoca la señal, no el preset: a tamaño nativo, lo que se codifica lo decide la fuente, así que ningún
/// validador del perfil lo ve antes. Con el FFmpeg real y el canal completo (motor unificado + <see cref="StandaloneChannelEngine"/>):
/// <list type="bullet">
///   <item>MPEG-2 con una fuente DCI 4K (4096×2160): «Width or Height are not allowed to be multiples of 4096».</item>
///   <item>DNxHR con una fuente de 160×90: «Input dimensions too small, input must be at least 256x120».</item>
/// </list>
/// FFmpeg 8.0 y el incluido lo escriben igual (medido el 2026-10-18). Una cadencia rara no sirve para esto: el codificador MPEG-2
/// admite 15 fps, FFmpeg lleva las que no admite a la más cercana que sí (7 → 7500/1001), y en MXF quien rechaza una cadencia fuera
/// de norma es el muxer, el camino de <see cref="MuxerRefusalTests"/>.
/// <list type="bullet">
///   <item>Antes, la degradación llegaba a «no hay alternativa» y se quedaba ahí, y la caída del proceso se ignoraba («la resuelve la
///   degradación»): el canal seguía «Grabando» sin proceso —ni preview, que iba en él—, sin alarma ni auditoría.</item>
///   <item>Ahora termina como un preset que el muxer rechaza: alarma crítica en el canal, el fallo con su motivo y la parada por error
///   en la auditoría, sin archivo vacío; el preview sigue, y con un tamaño de salida que el codificador admite el mismo canal graba.</item>
/// </list>
/// </summary>
public sealed class VideoEncoderRefusalTests
{
    private readonly ITestOutputHelper _output;
    public VideoEncoderRefusalTests(ITestOutputHelper output) => _output = output;

    [SkippableTheory]
    [InlineData(VideoCodec.Mpeg2Video, 4096, 2160, 24)] // DCI 4K
    [InlineData(VideoCodec.DnxHr, 160, 90, 25)]
    public async Task AVideoEncoderThatCannotOpenWhatTheSourceSends_EndsTheRecordingVisibly(VideoCodec codec, int width, int height, int fps)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var native = Profile(codec, target: null);
        // El preset es válido y el pre-vuelo lo deja pasar: lo que no se puede codificar lo trae la fuente.
        Assert.True(RecordingProfileValidator.IsValid(native, out var invalid), $"el validador rechaza el preset: {string.Join(" ", invalid)}");
        string encoder = FfmpegCodecMap.VideoEncoder(codec);
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);

        var dir = NewDir("baioss-encoder-refusal");
        var logs = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (logs) logs.Add(l); });
        var events = new List<IDomainEvent>();
        var bus = new InProcessEventBus();
        using var subscription = bus.Subscribe<IDomainEvent>((e, _) => { lock (events) events.Add(e); return Task.CompletedTask; });
        var source = new LavfiLiveSource(width, height, new FrameRate(fps, 1));
        try
        {
            await source.OpenAsync();
            await using var capture = new FfmpegChannelEngine(locator, loggers.CreateLogger("Capture")) { OutputRoot = dir };
            var alarms = new List<string>();
            bool fallback = false, unverified = false;
            capture.AlarmChanged += (_, a) =>
            {
                lock (alarms) alarms.Add($"{a.Type}={(a.Active ? "on" : "off")}");
                if (a.Active && a.Type == AlarmType.EncoderFallback) fallback = true;
                if (a.Active && a.Type == AlarmType.RecordingUnverified) unverified = true;
            };
            var states = new List<RecordingState>();
            capture.StateChanged += (_, s) => { lock (states) states.Add(s); };
            long frames = 0;
            capture.FrameReady += (_, _) => Interlocked.Increment(ref frames);
            await capture.StartPreviewAsync(source, native, "ENC");
            await using var channel = new StandaloneChannelEngine("ENC", source, native, capture, bus: bus,
                log: new Logger<StandaloneChannelEngine>(loggers));

            await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
            var session = channel.Status.SessionId;
            Assert.NotNull(session); // arranca: que el codificador no admite el cuadro solo se sabe cuando le llega el primero

            // El canal deja de decir «Grabando» por sí solo, y cierra la sesión.
            var clock = Stopwatch.StartNew();
            while ((capture.State != RecordingState.Idle || channel.Status.SessionId is not null) && clock.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(100);
            double settled = clock.Elapsed.TotalSeconds;
            var status = channel.Status;
            var channelAlarms = status.Alarms ?? Array.Empty<ChannelAlarm>();

            _output.WriteLine($"{encoder} a tamaño nativo con una fuente de {width}×{height} a {fps} fps ({locator.FfmpegPath}), {settled:0.0} s:");
            lock (states) _output.WriteLine($"  estados: {string.Join(" → ", states)}");
            lock (alarms) _output.WriteLine($"  alarmas: {(alarms.Count == 0 ? "ninguna" : string.Join(", ", alarms))}");
            _output.WriteLine($"  alarmas del canal: {string.Join(", ", channelAlarms.Select(a => $"{a.Type}{(a.IsCritical ? " (crítica)" : "")}: {a.Message}"))}");
            lock (events) foreach (var e in events) _output.WriteLine($"  auditoría: {e}");
            _output.WriteLine($"  archivos: {string.Join(", ", Directory.GetFiles(dir).Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length} B)"))}");
            lock (logs)
                foreach (var l in logs.Where(l => l.StartsWith("[Warning]", StringComparison.Ordinal) || l.StartsWith("[Error]", StringComparison.Ordinal)))
                    _output.WriteLine("  " + l);

            Assert.True(capture.State == RecordingState.Idle, $"el canal sigue en {capture.State} a los {settled:0} s sin grabar nada");
            Assert.Null(status.SessionId);
            // Visible: una alarma crítica (roja) en el canal.
            Assert.Contains(channelAlarms, a => a.IsCritical);
            // En la auditoría: el fallo, con el codificador en el motivo, y la parada por error de ESA sesión. No es una caída que
            // se recupere en una pieza nueva: con la misma fuente fallaría igual.
            List<IDomainEvent> audited;
            lock (events) audited = events.ToList();
            var failed = Assert.Single(audited.OfType<EncoderFailed>());
            Assert.Equal(session, failed.SessionId);
            Assert.Contains(encoder, failed.Reason, StringComparison.Ordinal);
            var stopped = Assert.Single(audited.OfType<RecordingStopped>());
            Assert.Equal(session, stopped.SessionId);
            Assert.Equal(RecordingStopReason.Error, stopped.Reason);
            Assert.Empty(audited.OfType<RecordingInterrupted>());
            // Sin alternativa no hay degradación: ni otro codificador ni su alarma.
            Assert.False(fallback, "un codificador sin alternativa encendió la alarma EncoderFallback");
            lock (logs) Assert.DoesNotContain(logs, l => l.Contains("degradando", StringComparison.Ordinal));
            // Ni el archivo vacío del intento ni una falsa «grabación sin verificar».
            Assert.Empty(Directory.GetFiles(dir));
            Assert.False(unverified, "el intento vacío se verificó como si fuera una pieza de la grabación");

            // El proceso que murió llevaba también el preview: tiene que seguir.
            long before = Interlocked.Read(ref frames);
            await Task.Delay(TimeSpan.FromSeconds(2));
            long previewed = Interlocked.Read(ref frames) - before;
            Assert.True(previewed >= 10, $"el preview no siguió tras el fallo ({previewed} frames en 2 s)");

            // Con un tamaño de salida que el codificador admite (el preset escala la fuente), el mismo canal graba, y la alarma del
            // fallo se va al empezar.
            channel.Profile = Profile(codec, Resolution.Hd720);
            await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.Equal(RecordingState.Recording, capture.State);
            Assert.DoesNotContain(channel.Status.Alarms ?? Array.Empty<ChannelAlarm>(), a => a.IsCritical);
            await channel.StopRecordingAsync(RecordingStopReason.Operator);
            var file = capture.LastOutputFile;
            Assert.True(file is not null && File.Exists(file) && new FileInfo(file).Length > 0, $"no se grabó a 1280×720: {file}");
        }
        finally
        {
            await source.DisposeAsync();
            TryDelete(dir);
        }
    }

    /// <summary>Perfil en MXF con PCM a 48 kHz (lo que el MXF admite); <paramref name="target"/> null = el tamaño de la fuente.</summary>
    private static RecordingProfile Profile(VideoCodec codec, Resolution? target) => new()
    {
        Name = $"{codec} · MXF · {(target is { } r ? r.ToString() : "nativo")}", VideoCodec = codec, HwAccel = HwAccel.None,
        TargetResolution = target,
        VideoBitrate = Bitrate.FromMbps(8), GopSize = 12, RateControl = RateControlMode.ConstantBitrate,
        Container = ContainerFormat.Mxf,
        AudioCodec = AudioCodec.Pcm, AudioLayout = AudioLayout.Stereo, AudioSampleRate = 48_000,
    };

    private static string NewDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        for (int i = 0; i < 10; i++)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); return; }
            catch { Thread.Sleep(300); } // la verificación del archivo puede tenerlo aún abierto
        }
    }
}
