using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.UnitTests.Fakes;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Grabación segmentada con fin previsto (una programada con duración): el corte que caería junto al fin no se hace, para que
/// el Detener —que llega un instante DESPUÉS: tick del programador, relevo del preview— no deje un último segmento ridículo.
/// Medido con una DeckLink: 30 min con segmentos de 10 dieron 3 × 600 s y un cuarto de 0,6 s.
/// </summary>
public class SegmentEndAlignmentTests
{
    private static RecordingProfile Segmented(SegmentTrigger trigger = SegmentTrigger.Duration) => new()
    {
        Name = "x264", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(8), GopSize = 50,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
        Segmentation = new SegmentationPolicy { Trigger = trigger, Duration = TimeSpan.FromMinutes(10), MaxBytes = 600L * 1_000_000 },
    };

    private static FfmpegArgumentBuilder Builder(RecordingProfile profile)
    {
        var source = new FakeCaptureSource("C:/clips/in.mp4");
        source.Emit(new SignalInfo(SignalState.Locked, new Resolution(1920, 1080), new FrameRate(25, 1),
            AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null));
        return new FfmpegArgumentBuilder().From(source).Using(profile).ForChannel("TST").ToDirectory("C:/out")
            .WithPreviewSink("tcp://127.0.0.1:9001").WithBaseName("15-10-2026_Programa");
    }

    [Fact]
    public void ThirtyMinutesInTenMinuteSegments_SkipsTheCutAtTheEnd()
    {
        // El proceso arrancó 0,7 s después de la hora: el fin queda a 1799,3 s y el corte de 1800 s caería justo antes del Detener.
        var splits = FfmpegArgumentBuilder.SegmentSplitTimes(600, TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(0.7));

        // 600 y 1200 como siempre; 1800 no; y después del fin se sigue cortando cada 10 min (1 h) por si el Detener no llegara.
        Assert.Equal("600,1200,2400,3000,3600,4200,4800,5400", splits);
    }

    [Theory]
    [InlineData(1812)]  // el operador programó 30 min 12 s: mejor un último de 612 s que uno de 12
    [InlineData(1790)]  // el corte cae DESPUÉS del fin previsto pero antes de que llegue el Detener
    public void ACutWithinTheToleranceOfTheEnd_IsSkippedEitherSide(int remainingSeconds)
    {
        var splits = FfmpegArgumentBuilder.SegmentSplitTimes(600, TimeSpan.FromSeconds(remainingSeconds));
        Assert.NotNull(splits);
        Assert.DoesNotContain("1800", splits!.Split(','));
        Assert.StartsWith("600,1200,2400", splits);
    }

    [Theory]
    [InlineData(25 * 60)]   // 25 min: el último segmento ya dura 5 min, no hay nada que arreglar
    [InlineData(1700)]      // 100 s antes del corte: más allá de la tolerancia
    public void WhenNoCutFallsNearTheEnd_ThereIsNoList(int remainingSeconds)
        => Assert.Null(FfmpegArgumentBuilder.SegmentSplitTimes(600, TimeSpan.FromSeconds(remainingSeconds)));

    [Fact]
    public void WithoutAPlannedEnd_OrWithItAlreadyPast_ThereIsNoList()
    {
        Assert.Null(FfmpegArgumentBuilder.SegmentSplitTimes(600, null));
        Assert.Null(FfmpegArgumentBuilder.SegmentSplitTimes(600, TimeSpan.FromSeconds(-3))); // pieza nueva tras el fin
    }

    [Fact]
    public void TheToleranceIsAtMostAQuarterOfASegment()
    {
        // Segmentos de 1 min: 15 s antes del corte aún se junta; 20 s ya es un segmento con sentido propio.
        Assert.NotNull(FfmpegArgumentBuilder.SegmentSplitTimes(60, TimeSpan.FromSeconds(75)));
        Assert.Null(FfmpegArgumentBuilder.SegmentSplitTimes(60, TimeSpan.FromSeconds(80)));
    }

    [Fact]
    public void WithVeryShortSegments_TheCutsAfterTheEndStopAtTheCap()
    {
        // Segmentos de 2 s: una hora de cortes tras el fin serían 1800; la lista se queda en el tope.
        var cuts = FfmpegArgumentBuilder.SegmentSplitTimes(2, TimeSpan.FromSeconds(5.95))!.Split(',');
        Assert.Equal(FfmpegArgumentBuilder.MaxListedSplits, cuts.Length);
        Assert.Equal(new[] { "2", "4", "8" }, cuts.Take(3));
    }

    [Fact]
    public void AListThatWouldBeTooLong_FallsBackToTheUsualCut()
    {
        // Un día con segmentos de un minuto serían 1500 cortes en la línea de comandos: se acepta el segmento corto.
        Assert.Null(FfmpegArgumentBuilder.SegmentSplitTimes(60, TimeSpan.FromHours(24)));
        // Un día con segmentos de 10 min sí cabe.
        Assert.NotNull(FfmpegArgumentBuilder.SegmentSplitTimes(600, TimeSpan.FromHours(24)));
    }

    [Fact]
    public void BuildLive_WithAPlannedEndOnACut_UsesTheListInsteadOfTheFixedCut()
    {
        var b = Builder(Segmented()).WithPlannedRemaining(TimeSpan.FromMinutes(30) - TimeSpan.FromSeconds(0.7));
        var joined = string.Join(' ', b.BuildLive(recording: true, 640, 360));

        Assert.Contains("-f segment -segment_times 600,1200,2400,", joined);
        Assert.DoesNotContain("-segment_time ", joined);
        Assert.Contains("-segment_format mp4 -reset_timestamps 1", joined);  // lo demás del muxer, como siempre
        Assert.Contains("-segment_start_number 1", joined);
        Assert.True(b.IsSegmentedOutput);
    }

    [Fact]
    public void BuildLive_WithoutAPlannedEnd_KeepsTheFixedCut()
    {
        var joined = string.Join(' ', Builder(Segmented()).BuildLive(recording: true, 640, 360));
        Assert.Contains("-segment_time 600", joined);
        Assert.DoesNotContain("-segment_times", joined);
    }

    [Fact]
    public void BuildSlate_APieceOfBarsBeforeTheEnd_AlsoSkipsTheCutAtTheEnd()
    {
        // La carta de ajuste es una pieza más de la misma grabación: su proceso arranca con lo que quede hasta el fin.
        var b = Builder(Segmented()).WithPlannedRemaining(TimeSpan.FromMinutes(10) + TimeSpan.FromSeconds(2));
        var joined = string.Join(' ', b.BuildSlate(recording: true, 640, 360));
        Assert.Contains("-segment_times 1200,1800,", joined);
        Assert.DoesNotContain("-segment_time ", joined);
    }

    [Theory]
    [InlineData(SegmentTrigger.Size)]
    [InlineData(SegmentTrigger.WallClock)]
    public void OnlyTheCutByDuration_IsAligned(SegmentTrigger trigger)
    {
        var b = Builder(Segmented(trigger)).WithPlannedRemaining(TimeSpan.FromMinutes(30));
        var joined = string.Join(' ', b.BuildLive(recording: true, 640, 360));
        Assert.Contains("-segment_time ", joined);
        Assert.DoesNotContain("-segment_times", joined);
    }
}
