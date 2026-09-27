using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Planificación del clip de los últimos N segundos de una grabación en curso: márgenes, elección de archivos en
/// una grabación segmentada, anclaje al fotograma clave, nombre y argumentos de la copia.</summary>
public class ClipPlannerTests
{
    private static ClipSourceFile File(string name, double seconds, long bytes = 1_000_000) => new(name, seconds, bytes);

    [Fact]
    public void TheEnd_StaysBeforeThePartialTail_AndTheStart_IsTheEndMinusWhatWasAsked()
    {
        var plan = ClipPlanner.Plan(new[] { File("rec.mp4", 45.0) }, 30, ClipContainer.FragmentedMp4)!;
        Assert.Single(plan.Files);
        Assert.Equal(45.0 - 1.2 - 30, plan.StartSeconds, 3);   // fin = 43,8; inicio = 13,8
        Assert.Equal(30, plan.LengthSeconds, 3);
        Assert.Equal(0.5, ClipPlanner.TailMargin(ClipContainer.MpegTs));
    }

    [Fact]
    public void WhenTheRecordingIsShorterThanAsked_TheClipStartsAtZero_AndCoversWhatThereIs()
    {
        var plan = ClipPlanner.Plan(new[] { File("rec.ts", 12.0) }, 60, ClipContainer.MpegTs)!;
        Assert.Equal(0, plan.StartSeconds);
        Assert.Equal(11.5, plan.LengthSeconds, 3);
        Assert.Null(ClipPlanner.Plan(new[] { File("rec.ts", 4.0) }, 30, ClipContainer.MpegTs)); // aún sin material
        Assert.Null(ClipPlanner.Plan(Array.Empty<ClipSourceFile>(), 30, ClipContainer.MpegTs));
    }

    [Fact]
    public void WithSegments_OnlyTheOnesFromTheStartOnwardsAreUsed_AndTheStartIsLocalToTheFirst()
    {
        var files = new[] { File("s1.mp4", 60), File("s2.mp4", 60), File("s3.mp4", 60), File("s4.mp4", 25) }; // 205 s en total
        var plan = ClipPlanner.Plan(files, 90, ClipContainer.FragmentedMp4)!;
        // fin = 203,8; inicio = 113,8 → dentro de s2 (60–120), a los 53,8 s locales; entran s2, s3 y s4.
        Assert.Equal(new[] { "s2.mp4", "s3.mp4", "s4.mp4" }, plan.Files.Select(f => f.Path));
        Assert.Equal(53.8, plan.StartSeconds, 3);
        Assert.Equal(90, plan.LengthSeconds, 3);

        var list = ClipPlanner.ConcatList(plan.Files);
        Assert.Contains("file 's2.mp4'\nduration 60.000\n", list);
        Assert.Contains("file 's4.mp4'\nduration 25.000\n", list);
    }

    [Fact]
    public void TheStart_IsSnappedToTheLastKeyframeBefore_AndTheLengthGrowsByTheSame()
    {
        var plan = new ClipPlan(new[] { File("rec.mp4", 45) }, 13.8, 30) { WantedStartSeconds = 13.8 };
        var snapped = ClipPlanner.SnapToKeyframe(plan, new[] { 8.0, 10.0, 12.0, 14.0, 16.0 });
        Assert.Equal(12.0, snapped.StartSeconds);
        Assert.Equal(31.8, snapped.LengthSeconds, 3);
        Assert.Equal(13.8, snapped.WantedStartSeconds);
        // Sin keyframe por debajo (o justo en el inicio): se deja como está.
        Assert.Equal(13.8, ClipPlanner.SnapToKeyframe(plan, new[] { 14.0, 16.0 }).StartSeconds);
        Assert.Equal(13.8, ClipPlanner.SnapToKeyframe(plan, new[] { 13.8 }).StartSeconds);
        Assert.Equal(13.8, ClipPlanner.SnapToKeyframe(plan, Array.Empty<double>()).StartSeconds);
    }

    [Fact]
    public void TheClipName_TellsTheRecording_TheMoment_AndTheLength()
    {
        var at = new DateTimeOffset(2026, 9, 26, 13, 5, 9, TimeSpan.Zero);
        Assert.Equal("A_20260926_120000_clip_20260926_130509_5min.mp4", ClipPlanner.OutputFileName("A_20260926_120000", at, 300, "mp4"));
        Assert.Equal("Partido_clip_20260926_130509_30s.ts", ClipPlanner.OutputFileName("Partido", at, 30, "ts"));
        Assert.Equal("90s", ClipPlanner.Label(90));
        Assert.Equal("10min", ClipPlanner.Label(600));
    }

    [Fact]
    public void TheArguments_CopyFromTheKeyframe_AllAudioTracks_AndWriteAStandardMp4OrTs()
    {
        var single = new ClipPlan(new[] { File("C:\\rec\\a.mp4", 45) }, 12.0, 31.8);
        var args = ClipPlanner.BuildArguments(single, ClipContainer.FragmentedMp4, "C:\\rec\\clips\\a_clip.mp4");
        string joined = string.Join(' ', args);
        Assert.Contains("-ss 12.000 -i C:\\rec\\a.mp4 -t 31.800 -map 0:v:0 -map 0:a? -c copy -avoid_negative_ts make_zero -movflags +faststart -f mp4 C:\\rec\\clips\\a_clip.mp4", joined);
        var list = args.ToList();
        Assert.True(list.IndexOf("-ss") < list.IndexOf("-i"), "-ss va ANTES de -i: busca el fotograma clave en vez de descartar paquetes");

        // Varios archivos: la cola del primero desde el keyframe (pieza fragmentada) y luego concat sin buscar.
        var multi = new ClipPlan(new[] { File("s2.mp4", 60), File("s3.mp4", 25) }, 53.8, 30);
        Assert.Throws<ArgumentException>(() => ClipPlanner.BuildArguments(multi, ClipContainer.FragmentedMp4, "out.mp4"));
        string tail = string.Join(' ', ClipPlanner.BuildTailArguments(multi, ClipContainer.FragmentedMp4, "cola.mp4"));
        Assert.Contains("-ss 53.800 -i s2.mp4 -map 0:v:0 -map 0:a? -c copy -avoid_negative_ts make_zero -movflags +frag_keyframe+empty_moov+default_base_moof -f mp4 cola.mp4", tail);
        Assert.DoesNotContain("-t ", tail);
        string concat = string.Join(' ', ClipPlanner.BuildConcatArguments(multi, ClipContainer.MpegTs, "list.txt", "out.ts"));
        Assert.Contains("-f concat -safe 0 -i list.txt -t 30.000", concat);
        Assert.DoesNotContain("-ss", concat);
        Assert.EndsWith("-f mpegts out.ts", concat);
    }

    [Fact]
    public void TheSizeEstimate_UsesTheAverageBitrateOfTheSources_WithAMargin()
    {
        var files = new[] { File("s1.mp4", 100, 100_000_000), File("s2.mp4", 50, 50_000_000) }; // 1 MB/s
        Assert.Equal((long)(1_000_000 * 30 * 1.2), ClipPlanner.EstimateBytes(files, 30));
        Assert.Equal(0, ClipPlanner.EstimateBytes(Array.Empty<ClipSourceFile>(), 30));
    }
}
