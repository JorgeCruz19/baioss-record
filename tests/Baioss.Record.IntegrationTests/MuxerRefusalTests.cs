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
/// Un perfil que el MUXER rechaza al escribir la cabecera no graba nada, y el canal tiene que decirlo. Aquí, MPEG-2 (o H.264 por
/// QuickSync) en MXF con audio AAC —el MXF de FFmpeg solo admite PCM— o con PCM a 44,1 kHz —«only 48khz is implemented»: un preset
/// importado o la API pueden pedirlo—. Con el FFmpeg real y el canal completo (motor unificado + <see cref="StandaloneChannelEngine"/>):
/// <list type="bullet">
///   <item>Antes, el codificador rawvideo del PREVIEW, que aún no había abierto, decía «Could not open encoder before EOF»; el
///   motor lo tomaba por un codificador de vídeo que no abre y degradaba en vano (QuickSync → AMF → x264, con la alarma
///   EncoderFallback) hasta llegar a CPU, donde «no hay alternativa»: el canal se quedaba «Grabando» sin proceso, con un archivo
///   de 0 B, sin alarma ni auditoría y sin reintentos (30 s observados el 2026-10-18).</item>
///   <item>Ahora la grabación termina sola y lo dice: alarma crítica en el canal, el fallo con su motivo y la parada por error en
///   la auditoría, sin degradar el códec ni dejar el archivo vacío; el preview sigue, y con un audio que el MXF admite el mismo
///   canal graba.</item>
/// </list>
/// </summary>
public sealed class MuxerRefusalTests
{
    private readonly ITestOutputHelper _output;
    public MuxerRefusalTests(ITestOutputHelper output) => _output = output;

    [SkippableTheory]
    [InlineData(VideoCodec.Mpeg2Video, AudioCodec.Aac, 48_000)]
    [InlineData(VideoCodec.H264Qsv, AudioCodec.Aac, 48_000)] // por hardware: el rechazo del muxer no debe degradarlo
    [InlineData(VideoCodec.Mpeg2Video, AudioCodec.Pcm, 44_100)]
    [InlineData(VideoCodec.H264Qsv, AudioCodec.Pcm, 44_100)]
    public async Task AnOutputTheMuxerRefuses_EndsTheRecordingVisibly_WithoutDegradingTheEncoder(VideoCodec codec, AudioCodec audio, int sampleRate)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var refused = Profile(codec, audio, sampleRate);
        // Lo que el validador ya rechaza no llega a FFmpeg (el pre-vuelo lo para con su motivo): no queda nada del motor que probar.
        Skip.IfNot(RecordingProfileValidator.IsValid(refused, out var invalid), $"el validador ya rechaza este perfil: {string.Join(" ", invalid)}");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        if (codec is VideoCodec.H264Qsv)
        {
            var (ok, detail) = await locator.ProbeVideoEncoderAsync(FfmpegCodecMap.VideoEncoder(codec));
            Skip.IfNot(ok, $"h264_qsv no abre en este equipo ({detail}): no hay codificador por hardware que no degradar.");
        }

        var dir = NewDir("baioss-muxer-refusal");
        var logs = new List<string>();
        var loggers = new DecklinkRelayContinuityTests.CaptureLogger(l => { lock (logs) logs.Add(l); });
        var events = new List<IDomainEvent>();
        var bus = new InProcessEventBus();
        using var subscription = bus.Subscribe<IDomainEvent>((e, _) => { lock (events) events.Add(e); return Task.CompletedTask; });
        var source = new LavfiLiveSource(1280, 720, FrameRate.P25);
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
            await capture.StartPreviewAsync(source, refused, "MUX");
            await using var channel = new StandaloneChannelEngine("MUX", source, refused, capture, bus: bus,
                log: new Logger<StandaloneChannelEngine>(loggers));

            await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
            var session = channel.Status.SessionId;
            Assert.NotNull(session); // arranca: el pre-vuelo no lo ve, el rechazo llega de FFmpeg al escribir la cabecera

            // El canal deja de decir «Grabando» por sí solo, y cierra la sesión.
            var clock = Stopwatch.StartNew();
            while ((capture.State != RecordingState.Idle || channel.Status.SessionId is not null) && clock.Elapsed < TimeSpan.FromSeconds(20))
                await Task.Delay(100);
            double settled = clock.Elapsed.TotalSeconds;
            var status = channel.Status;
            var channelAlarms = status.Alarms ?? Array.Empty<ChannelAlarm>();

            _output.WriteLine($"{codec} en MXF con {audio} a {sampleRate} Hz ({locator.FfmpegPath}), {settled:0.0} s:");
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
            // En la auditoría: el fallo, con el contenedor en el motivo, y la parada por error de ESA sesión. No es una caída
            // que se recupere en una pieza nueva: repetir el mismo perfil fallaría igual.
            List<IDomainEvent> audited;
            lock (events) audited = events.ToList();
            var failed = Assert.Single(audited.OfType<EncoderFailed>());
            Assert.Equal(session, failed.SessionId);
            Assert.Contains("MXF", failed.Reason, StringComparison.Ordinal);
            var stopped = Assert.Single(audited.OfType<RecordingStopped>());
            Assert.Equal(session, stopped.SessionId);
            Assert.Equal(RecordingStopReason.Error, stopped.Reason);
            Assert.Empty(audited.OfType<RecordingInterrupted>());
            // No es un codificador que no abre: ni degradación ni alarma de codificador alternativo.
            Assert.False(fallback, "un rechazo del muxer degradó el codificador y encendió la alarma EncoderFallback");
            lock (logs) Assert.DoesNotContain(logs, l => l.Contains("degradando", StringComparison.Ordinal));
            // Ni el archivo vacío del intento ni una falsa «grabación sin verificar».
            Assert.Empty(Directory.GetFiles(dir));
            Assert.False(unverified, "el intento vacío se verificó como si fuera una pieza de la grabación");

            // El proceso que murió llevaba también el preview: tiene que seguir.
            long before = Interlocked.Read(ref frames);
            await Task.Delay(TimeSpan.FromSeconds(2));
            long previewed = Interlocked.Read(ref frames) - before;
            Assert.True(previewed >= 10, $"el preview no siguió tras el fallo ({previewed} frames en 2 s)");

            // Con un audio que el MXF admite, el mismo canal graba, y la alarma del fallo se va al empezar.
            channel.Profile = Profile(codec, AudioCodec.Pcm, 48_000);
            await channel.StartRecordingAsync(Guid.Empty, RecordingOrigin.Manual("prueba"));
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.Equal(RecordingState.Recording, capture.State);
            Assert.DoesNotContain(channel.Status.Alarms ?? Array.Empty<ChannelAlarm>(), a => a.IsCritical);
            await channel.StopRecordingAsync(RecordingStopReason.Operator);
            var file = capture.LastOutputFile;
            Assert.True(file is not null && File.Exists(file) && new FileInfo(file).Length > 0, $"no se grabó con PCM: {file}");
        }
        finally
        {
            await source.DisposeAsync();
            TryDelete(dir);
        }
    }

    /// <summary>Perfil por bitrate en MXF con el audio pedido (el MXF de FFmpeg solo admite PCM, y a 48 kHz).</summary>
    private static RecordingProfile Profile(VideoCodec codec, AudioCodec audio, int sampleRate) => new()
    {
        Name = $"{codec} · MXF · {audio} {sampleRate}", VideoCodec = codec, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(8), GopSize = 12, RateControl = RateControlMode.ConstantBitrate,
        Container = ContainerFormat.Mxf,
        AudioCodec = audio, AudioLayout = AudioLayout.Stereo, AudioBitrate = Bitrate.FromKbps(192), AudioSampleRate = sampleRate,
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
