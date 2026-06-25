using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

public class CaptureDeviceTests
{
    // Salida real de `ffmpeg -f dshow -list_devices true -i dummy` (recortada).
    private const string DshowSample = """
        [in#0 @ 000] "USB2.0 HD UVC WebCam" (video)
        [in#0 @ 000]   Alternative name "@device_pnp_\\?\usb#vid_13d3&pid_56a2"
        [in#0 @ 000] "OBS Virtual Camera" (video)
        [in#0 @ 000]   Alternative name "@device_sw_{860BB310}"
        [in#0 @ 000] "Varios micrófonos (Realtek(R) Audio)" (audio)
        [in#0 @ 000] "CABLE Output (VB-Audio Virtual Cable)" (audio)
        """;

    // Salida de `ffmpeg -f decklink -list_devices 1 -i dummy` cuando hay tarjeta.
    private const string DecklinkSample = """
        [Blackmagic DeckLink indev @ 000] Blackmagic DeckLink devices:
        [Blackmagic DeckLink indev @ 000]     'DeckLink Mini Recorder'
        [Blackmagic DeckLink indev @ 000]     'DeckLink Duo (1)'
        """;

    [Fact]
    public void ParseDshowVideo_ExtractsOnlyVideoDevices()
    {
        var devices = FfmpegDeviceEnumerator.ParseDshowVideo(DshowSample);

        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.Equal(InputType.DirectShow, d.Type));
        Assert.Contains(devices, d => d.Name == "USB2.0 HD UVC WebCam" && d.Uri == "USB2.0 HD UVC WebCam");
        Assert.Contains(devices, d => d.Name == "OBS Virtual Camera");
        Assert.DoesNotContain(devices, d => d.Name.Contains("Realtek")); // el audio no entra como vídeo
    }

    [Fact]
    public void ParseDshowAudio_ExtractsAudioDevices_IncludingNamesWithParentheses()
    {
        var audio = FfmpegDeviceEnumerator.ParseDshowAudio(DshowSample);

        Assert.Equal(2, audio.Count);
        Assert.Contains("Varios micrófonos (Realtek(R) Audio)", audio); // paréntesis internos respetados
        Assert.Contains("CABLE Output (VB-Audio Virtual Cable)", audio);
    }

    [Fact]
    public void ParseDshowVideo_StableId_SameDeviceSameGuid()
    {
        var a = FfmpegDeviceEnumerator.ParseDshowVideo(DshowSample);
        var b = FfmpegDeviceEnumerator.ParseDshowVideo(DshowSample);
        Assert.Equal(a[0].Id, b[0].Id); // Id determinista → misma fila al reasignar
    }

    // Salida de `ffmpeg -f decklink -list_formats 1 -i 'DeckLink Mini Recorder'`.
    private const string DecklinkFormatsSample = """
        [Blackmagic DeckLink indev @ 000] Supported formats for 'DeckLink Mini Recorder':
        [Blackmagic DeckLink indev @ 000]   format_code  description
        [Blackmagic DeckLink indev @ 000]     ntsc        720x486 at 30000/1001 fps (interlaced, lower field first)
        [Blackmagic DeckLink indev @ 000]     pal         720x576 at 25/1 fps (interlaced, upper field first)
        [Blackmagic DeckLink indev @ 000]     Hp50        1920x1080 at 50/1 fps
        [Blackmagic DeckLink indev @ 000]     Hi50        1920x1080 at 25/1 fps (interlaced, upper field first)
        """;

    [Fact]
    public void ParseDecklinkFormats_ExtractsCodeAndDescription_SkippingHeader()
    {
        var formats = FfmpegDeviceEnumerator.ParseDecklinkFormats(DecklinkFormatsSample);

        Assert.Equal(4, formats.Count); // 4 modos; la cabecera 'format_code description' no entra
        // Resolución/tasa parseadas + etiqueta LEGIBLE (progresivo usa la tasa, entrelazado la de campos).
        Assert.Contains(formats, f => f.Code == "Hp50" && f.Resolution == new Resolution(1920, 1080)
                                      && !f.Interlaced && f.Description == "1920×1080 · 50p");
        Assert.Contains(formats, f => f.Code == "ntsc" && f.Resolution == new Resolution(720, 486)
                                      && f.Interlaced && f.Description == "720×486 · 59.94i");
        Assert.DoesNotContain(formats, f => f.Code == "format_code");
    }

    // Salida REAL de un build reciente (git-2026) para una DeckLink Duo: las líneas NO llevan el
    // prefijo de log "[Blackmagic DeckLink indev @ ...]". El parser debe anclarse en la descripción.
    private const string DecklinkFormatsNoPrefixSample = """
        Supported formats for 'DeckLink Duo (1)':
                format_code     description
                ntsc            720x486 at 30000/1001 fps (interlaced, lower field first)
                pal             720x576 at 25000/1000 fps (interlaced, upper field first)
                23ps            1920x1080 at 24000/1001 fps
                Hp59            1920x1080 at 60000/1001 fps
                Hi50            1920x1080 at 25000/1000 fps (interlaced, upper field first)
                hp60            1280x720 at 60000/1000 fps
        """;

    [Fact]
    public void ParseDecklinkFormats_HandlesOutputWithoutLogPrefix()
    {
        var formats = FfmpegDeviceEnumerator.ParseDecklinkFormats(DecklinkFormatsNoPrefixSample);

        Assert.Equal(6, formats.Count); // 6 modos; ni la cabecera ni "Supported formats for…" entran
        Assert.Contains(formats, f => f.Code == "Hp59" && f.Resolution == new Resolution(1920, 1080) && f.Description == "1920×1080 · 59.94p");
        Assert.Contains(formats, f => f.Code == "23ps" && f.Resolution == new Resolution(1920, 1080)); // código que empieza por dígitos
        Assert.Contains(formats, f => f.Code == "hp60" && f.Resolution == new Resolution(1280, 720) && f.Description == "1280×720 · 60p"); // 720p
        Assert.Contains(formats, f => f.Code == "ntsc" && f.Resolution == new Resolution(720, 486) && f.Interlaced); // NTSC 59.94i
        Assert.DoesNotContain(formats, f => f.Code == "format_code");
    }

    [Fact]
    public void ParseDecklink_ExtractsQuotedDeviceNames()
    {
        var devices = FfmpegDeviceEnumerator.ParseDecklink(DecklinkSample);

        Assert.Equal(2, devices.Count);
        Assert.All(devices, d => Assert.Equal(InputType.DecklinkSdi, d.Type));
        Assert.Contains(devices, d => d.Name == "DeckLink Mini Recorder");
        Assert.Contains(devices, d => d.Name == "DeckLink Duo (1)"); // paréntesis dentro de comillas simples
    }

    [Fact]
    public void Resolver_PicksFactoryByType()
    {
        var resolver = new CaptureSourceResolver(new ICaptureSourceFactory[]
        {
            new FileCaptureSourceFactory(), new DecklinkCaptureSourceFactory(), new DirectShowCaptureSourceFactory(),
        });

        Assert.IsType<DirectShowCaptureSource>(resolver.Create(Def(InputType.DirectShow, "Cam")));
        Assert.IsType<DecklinkCaptureSource>(resolver.Create(Def(InputType.DecklinkSdi, "DeckLink Mini")));
        Assert.IsType<FileCaptureSource>(resolver.Create(Def(InputType.File, @"C:\x\clip.mp4")));
        Assert.True(resolver.CanHandle(InputType.DecklinkSdi));
        Assert.False(resolver.CanHandle(InputType.Ndi));
        Assert.Throws<NotSupportedException>(() => resolver.Create(Def(InputType.Ndi, "x")));
    }

    [Fact]
    public void Decklink_BuildArgs_EmitsDeviceAndFormatCode()
    {
        var def = Def(InputType.DecklinkSdi, "DeckLink Mini Recorder");
        def.Parameters["format_code"] = "Hp50";

        var joined = string.Join(' ', new DecklinkCaptureSource(def).BuildInputArguments());

        Assert.Contains("-f decklink", joined);
        Assert.Contains("-format_code Hp50", joined);
        Assert.Contains("-i DeckLink Mini Recorder", joined);
    }

    [Fact]
    public void DirectShow_BuildArgs_PairsVideoAndAudio()
    {
        var def = Def(InputType.DirectShow, "My Cam");
        def.Parameters["audio"] = "My Mic";

        var joined = string.Join(' ', new DirectShowCaptureSource(def).BuildInputArguments());

        Assert.Contains("-f dshow", joined);
        Assert.Contains("video=My Cam:audio=My Mic", joined);
        // Con audio de un dispositivo distinto, sella ambos con el reloj de pared para que el vídeo no se
        // congele esperando alinear los timestamps dispares del dshow combinado.
        Assert.Contains("-use_wallclock_as_timestamps 1", joined);
    }

    [Fact]
    public void DirectShow_BuildArgs_VideoOnly_OmitsWallclockFlag()
    {
        // Sin audio, el vídeo fluye con sus propios timestamps: no se fuerza wallclock (no haría falta).
        var joined = string.Join(' ', new DirectShowCaptureSource(Def(InputType.DirectShow, "My Cam")).BuildInputArguments());

        Assert.Contains("-i video=My Cam", joined);
        Assert.DoesNotContain("use_wallclock_as_timestamps", joined);
    }

    private static InputSource Def(InputType type, string uri) => new() { Name = uri, Type = type, Uri = uri };
}
