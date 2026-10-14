using System.Globalization;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.UnitTests.Fakes;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Cola de escritura de la salida de grabación (<c>-thread_queue_size</c> de SALIDA): sin ella, un disco que tarda unos segundos
/// en aceptar escrituras para el proceso entero (preview congelado y entrada atascada; con el relé DeckLink, frames
/// descartados). Va SOLO en la salida que escribe a disco y se dimensiona en paquetes según los flujos de esa salida.
/// </summary>
public class FfmpegWriteQueueTests
{
    private static readonly TimeSpan TenSeconds = TimeSpan.FromSeconds(10);

    private static RecordingProfile SoftwareMp4() => new()
    {
        Name = "x264", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(8), GopSize = 50,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    private static FakeCaptureSource Source(int channels = 2, string? pairs = null)
    {
        var source = new FakeCaptureSource("C:/clips/in.mp4") { AudioChannelCount = channels };
        if (pairs is not null) source.Definition.Parameters[AudioSelection.PairsKey] = pairs;
        source.Emit(new SignalInfo(SignalState.Locked, new Resolution(1920, 1080), new FrameRate(25, 1),
            AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null, AudioChannels: channels));
        return source;
    }

    private static FfmpegArgumentBuilder Builder(RecordingProfile profile, TimeSpan queue, FakeCaptureSource? source = null)
        => new FfmpegArgumentBuilder()
            .From(source ?? Source()).Using(profile).ForChannel("TST").ToDirectory("C:/out")
            .WithPreviewSink("tcp://127.0.0.1:9001").WithSignalAnalysis(false)
            .WithWriteQueue(queue);

    private static int QueueValue(IReadOnlyList<string> args)
        => int.Parse(args[args.ToList().IndexOf("-thread_queue_size") + 1], CultureInfo.InvariantCulture);

    [Fact]
    public void Recording_PutsTheQueueOnlyOnTheRecordingOutput_BeforeItsFile()
    {
        var builder = Builder(SoftwareMp4(), TenSeconds);
        var args = builder.BuildLive(recording: true, 640, 360).ToList();

        Assert.Single(args, a => a == "-thread_queue_size");
        int queue = args.IndexOf("-thread_queue_size");
        // Ni en la salida de preview ni en la de medidores (no escriben a disco): entre las opciones de la grabación…
        Assert.True(queue > args.IndexOf("tcp://127.0.0.1:9001"), "la cola no debe aplicarse a la salida de preview");
        Assert.True(queue > args.IndexOf("-"), "ni a la de medidores");
        Assert.True(queue > args.IndexOf("-c:v"), "va entre las opciones de la salida de grabación");
        Assert.True(queue < args.IndexOf(builder.OutputFilePath), "y antes de su archivo");
        // …para 25 fps de la fuente (el perfil no fija tasa) + 1 pista de audio a ~50 paquetes/s, durante 10 s.
        Assert.Equal(750, QueueValue(args));
    }

    [Fact]
    public void PreviewOnly_HasNoQueue()
        => Assert.DoesNotContain("-thread_queue_size", Builder(SoftwareMp4(), TenSeconds).BuildLive(recording: false, 640, 360));

    [Fact]
    public void WithoutQueue_LeavesTheFfmpegDefault()
        => Assert.DoesNotContain("-thread_queue_size", Builder(SoftwareMp4(), TimeSpan.Zero).BuildLive(recording: true, 640, 360));

    [Fact]
    public void ProfileRateAndOneTrackPerPair_SizeTheQueueForEveryStream()
    {
        var profile = SoftwareMp4();
        profile.OutputFrameRate = new FrameRate(60000, 1001);
        // 16 canales con todos los pares: 8 pistas estéreo en la grabación.
        var args = Builder(profile, TenSeconds, Source(channels: 16, pairs: "all")).BuildLive(recording: true, 640, 360);
        Assert.Equal((int)Math.Ceiling(10 * (60000 / 1001.0 + 8 * 50)), QueueValue(args));
    }

    [Fact]
    public void AudioOnly_SizesTheQueueWithoutVideo()
    {
        var profile = SoftwareMp4();
        profile.AudioOnly = true;
        Assert.Equal(500, QueueValue(Builder(profile, TenSeconds).BuildLive(recording: true, 640, 360)));
    }

    [Fact]
    public void SegmentedOutput_KeepsTheQueueBeforeTheSegmentMuxer()
    {
        var profile = SoftwareMp4();
        profile.Segmentation = new SegmentationPolicy { Trigger = SegmentTrigger.Duration, Duration = TimeSpan.FromMinutes(15) };
        var args = Builder(profile, TenSeconds).BuildLive(recording: true, 640, 360).ToList();
        Assert.True(args.IndexOf("-thread_queue_size") < args.IndexOf("segment"), string.Join(' ', args));
    }

    [Fact]
    public void Slate_RecordingAlsoGetsTheQueue()
    {
        var args = Builder(SoftwareMp4(), TenSeconds).BuildSlate(recording: true, 640, 360).ToList();
        Assert.Single(args, a => a == "-thread_queue_size");
        Assert.True(args.IndexOf("-thread_queue_size") > args.IndexOf("-"), "solo en la salida de grabación de la carta");
        Assert.Equal(750, QueueValue(args)); // barras a 25 fps (las de la fuente) + 1 pista de silencio
        Assert.DoesNotContain("-thread_queue_size", Builder(SoftwareMp4(), TenSeconds).BuildSlate(recording: false, 640, 360));
    }

    [Theory]
    [InlineData(0, 30, 1, 0)]          // sin cola pedida
    [InlineData(10, 29.97, 1, 800)]    // vídeo + 1 pista
    [InlineData(10, 0, 2, 1000)]       // solo audio, 2 pistas
    [InlineData(0.5, 25, 0, 64)]       // mínimo
    [InlineData(3600, 60, 16, 32768)]  // tope
    public void WriteQueuePackets_CoversAtLeastTheRequestedTime(double seconds, double fps, int audio, int expected)
        => Assert.Equal(expected, FfmpegArgumentBuilder.WriteQueuePackets(TimeSpan.FromSeconds(seconds), fps, audio));
}
