using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

public class DeviceExclusivityTests
{
    private static InputSource Src(InputType type, string? uri) => new() { Name = uri ?? "x", Type = type, Uri = uri };

    [Fact]
    public void SameDirectShowCamera_Conflicts()
        => Assert.True(DeviceExclusivity.Conflicts(
            Src(InputType.DirectShow, "OBS Virtual Camera"), Src(InputType.DirectShow, "OBS Virtual Camera")));

    [Fact]
    public void DifferentCameras_DoNotConflict()
        => Assert.False(DeviceExclusivity.Conflicts(
            Src(InputType.DirectShow, "Cam A"), Src(InputType.DirectShow, "Cam B")));

    [Fact]
    public void SameDecklink_Conflicts()
        => Assert.True(DeviceExclusivity.Conflicts(
            Src(InputType.DecklinkSdi, "DeckLink Mini Recorder"), Src(InputType.DecklinkSdi, "DeckLink Mini Recorder")));

    [Fact]
    public void SameFile_DoesNotConflict() // los archivos se comparten entre canales
        => Assert.False(DeviceExclusivity.Conflicts(
            Src(InputType.File, @"C:\clip.mp4"), Src(InputType.File, @"C:\clip.mp4")));

    [Fact]
    public void DifferentTypes_DoNotConflict()
        => Assert.False(DeviceExclusivity.Conflicts(
            Src(InputType.DirectShow, "X"), Src(InputType.DecklinkSdi, "X")));

    [Fact]
    public void SameCamera_DifferentAudio_StillConflictsByVideoUri()
    {
        // El audio va en Parameters (compartible), no en Uri: la MISMA cámara choca aunque el micro difiera.
        var a = Src(InputType.DirectShow, "Cam"); a.Parameters["audio"] = "Mic 1";
        var b = Src(InputType.DirectShow, "Cam"); b.Parameters["audio"] = "Mic 2";
        Assert.True(DeviceExclusivity.Conflicts(a, b));
    }
}
