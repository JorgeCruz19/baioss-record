using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using NewTek;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Formato de una fuente NDI: cambio de formato EN CALIENTE (N20), la señal que se publica (barrido incluido, sin creer un
/// «entrelazado» a más de 30 cuadros/s) y el formato en que se sirve el vídeo.</summary>
public sealed class NdiReceiverFormatTests
{
    [Fact]
    public void SameFormat_IsNotAChange()
        => Assert.False(NdiReceiver.FormatDiffers(1920, 1080, "uyvy422", false, 1920, 1080, "uyvy422", false));

    [Theory]
    [InlineData(1280, 720, "uyvy422", false)]   // resolución distinta (720 vs 1080)
    [InlineData(1920, 1080, "bgra", false)]     // mismo tamaño, pixel distinto (uyvy422 → bgra)
    [InlineData(1280, 1080, "uyvy422", false)]  // solo cambia el ancho
    [InlineData(1920, 1080, "uyvy422", true)]   // solo cambia el barrido (1080p29.97 → 1080i59.94)
    public void DifferentResolutionPixelOrScan_IsAChange(int w, int h, string pix, bool interlaced)
        => Assert.True(NdiReceiver.FormatDiffers(w, h, pix, interlaced, oldW: 1920, oldH: 1080, oldPix: "uyvy422", oldInterlaced: false));

    [Theory]
    [InlineData(NDIlib.frame_format_type_e.frame_format_type_progressive)]
    [InlineData(NDIlib.frame_format_type_e.frame_format_type_field_0)] // los campos sueltos se tejen aparte (IsField)
    [InlineData(NDIlib.frame_format_type_e.frame_format_type_field_1)]
    public void OnlyInterleavedFramesCarryTwoWovenFields(NDIlib.frame_format_type_e type)
        => Assert.False(NdiReceiver.IsInterleaved(type, 30000, 1001));

    [Theory]
    [InlineData(30000, 1001, true)]  // 1080i59.94 (vMix): 29.97 cuadros, 59.94 campos por segundo
    [InlineData(25, 1, true)]        // 1080i50 / 576i
    [InlineData(30, 1, true)]        // 1080i60, lo más rápido que va un entrelazado
    [InlineData(0, 0, true)]         // sin tasa: se cree la marca
    [InlineData(60000, 1000, false)] // Offside Engine: un 1080p60 marcado «interleaved» (el 0 de la estructura); se enseñaba «120i»
    [InlineData(60000, 1001, false)]
    [InlineData(50, 1, false)]
    public void TheInterleavedFlag_IsOnlyBelievedUpTo30FramesPerSecond(int rateN, int rateD, bool interlaced)
        => Assert.Equal(interlaced, NdiReceiver.IsInterleaved(NDIlib.frame_format_type_e.frame_format_type_interleaved, rateN, rateD));

    [Fact]
    public void AGraphicsSourceFlaggedInterleavedAt60_IsA60p_NeitherLabelled120iNorDeinterlaced()
    {
        // «DESKTOP-JCHGGGC (Offside Engine NDI Output)», 2026-10-17: 1080 BGRA marcado «interleaved» a 60000/1000. Antes la UI decía
        // «1920×1080 · 120i · NDI», la grabación lo desentrelazaba cuadro a cuadro y el preview se quedaba con un campo: con la
        // cadencia declarada (60 cuadros servidos por segundo, de ~4 reales) el proceso del canal pasó de 0,36 a ~5,8 núcleos.
        bool interlaced = NdiReceiver.IsInterleaved(NDIlib.frame_format_type_e.frame_format_type_interleaved, 60000, 1000);
        var signal = NdiCaptureSource.SignalFor(1920, 1080, 60000, 1000, interlaced, channels: 2, AudioSelection.FromParameters(null));

        Assert.Equal(ScanType.Progressive, signal.Scan);
        Assert.Equal("1920×1080 · 60p · NDI", signal.FormatLabel);
        Assert.Null(FfmpegArgumentBuilder.DeinterlaceFilter(signal, new RecordingProfile { Name = "1080p", VideoCodec = VideoCodec.H264Nvenc }));
        Assert.Null(FfmpegArgumentBuilder.PreviewDeinterlaceFilter(signal, 640, 360, perField: false));
    }

    [Theory]
    [InlineData(NDIlib.FourCC_type_e.FourCC_type_UYVY, 1920, "uyvy422")]
    [InlineData(NDIlib.FourCC_type_e.FourCC_type_BGRA, 1920, "uyvy422")] // con alfa (un grafismo): se pasa a UYVY al recibirlo
    [InlineData(NDIlib.FourCC_type_e.FourCC_type_BGRX, 1280, "uyvy422")]
    [InlineData(NDIlib.FourCC_type_e.FourCC_type_BGRA, 1919, "bgra")]    // ancho impar: UYVY va por pares, se sirve tal cual
    public void TheVideoIsServedAsUyvy_AlsoFromASourceWithAlpha(NDIlib.FourCC_type_e fourCC, int width, string served)
    {
        Assert.Equal(served, NdiReceiver.PixelFormatFor(fourCC, width));
        Assert.Equal(fourCC != NDIlib.FourCC_type_e.FourCC_type_UYVY && served == "uyvy422", NdiReceiver.ConvertsToUyvy(fourCC, width));
    }

    [Fact]
    public void AnInterleavedSource_IsPublishedAsInterlacedTopFieldFirst_SoThePreviewAndRecordingDeinterlaceIt()
    {
        // Un 1080i de vMix por NDI: cuadros entretejidos a 30000/1001 (la tasa de cuadro). Antes la señal no decía nada del
        // barrido: preview con doble imagen en lo que se mueve y presets progresivos grabados sin desentrelazar.
        var signal = NdiCaptureSource.SignalFor(1920, 1080, 30000, 1001, interlaced: true, channels: 2, AudioSelection.FromParameters(null));
        Assert.Equal(ScanType.InterlacedTff, signal.Scan);
        Assert.Equal("1920×1080 · 59.94i · NDI", signal.FormatLabel);
        Assert.Equal(30000, signal.FrameRate!.Value.Numerator);
        Assert.True(signal.HasAudio); // aunque la fuente no traiga audio: el receptor sirve silencio
    }

    [Fact]
    public void AProgressiveSource_IsPublishedAsProgressive()
    {
        var signal = NdiCaptureSource.SignalFor(1920, 1080, 30000, 1001, interlaced: false, channels: 2, AudioSelection.FromParameters(null));
        Assert.Equal(ScanType.Progressive, signal.Scan);
        Assert.Equal("1920×1080 · 29.97p · NDI", signal.FormatLabel);
    }
}
