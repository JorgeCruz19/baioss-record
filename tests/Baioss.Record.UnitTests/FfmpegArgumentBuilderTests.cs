using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Localization;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.UnitTests.Fakes;
using Xunit;

namespace Baioss.Record.UnitTests;

[Collection("Localizer")] // los títulos de pista van en el idioma de la aplicación (estado global)
public class FfmpegArgumentBuilderTests : IDisposable
{
    // Cada test arranca en español (los títulos «Canales 1-2» dependen del idioma) y deja el idioma como estaba.
    private readonly AppLanguage _originalLanguage = Localizer.Language;
    public FfmpegArgumentBuilderTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _originalLanguage;

    private static (string Joined, string OutputFile) Build(RecordingProfile profile)
    {
        var builder = new FfmpegArgumentBuilder()
            .From(new FakeCaptureSource("C:/clips/in.mp4"))
            .Using(profile)
            .ForChannel("TST")
            .ToDirectory("C:/out")
            .ProxyToDirectory("C:/proxy");
        var args = builder.Build();
        return (string.Join(' ', args), builder.OutputFilePath);
    }

    private static RecordingProfile SoftwareMp4() => new()
    {
        Name = "x264", VideoCodec = VideoCodec.H264x264, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(8), GopSize = 50,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    private static RecordingProfile ProResMov(EncoderProfile profile, PixelFormat pix = PixelFormat.Auto) => new()
    {
        Name = "prores", VideoCodec = VideoCodec.ProRes, HwAccel = HwAccel.None, GopSize = 1,
        EncoderProfile = profile, PixelFormat = pix,
        AudioCodec = AudioCodec.Pcm, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mov,
    };

    private static RecordingProfile DnxhrMov(EncoderProfile profile, PixelFormat pix = PixelFormat.Auto) => new()
    {
        Name = "dnxhr", VideoCodec = VideoCodec.DnxHr, HwAccel = HwAccel.None, GopSize = 1,
        EncoderProfile = profile, PixelFormat = pix,
        AudioCodec = AudioCodec.Pcm, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mov,
    };

    [Fact]
    public void Build_SoftwareMp4_UsesLibx264AndAac()
    {
        var (joined, outFile) = Build(SoftwareMp4());

        Assert.Contains("-i C:/clips/in.mp4", joined);
        Assert.Contains("-c:v libx264", joined);
        Assert.Contains("-c:a aac", joined);
        Assert.Contains("-ac 2", joined);                       // estéreo
        Assert.Contains("ebur128=peak=true", joined);           // metering para VU
        Assert.Contains("-movflags +frag_keyframe+empty_moov+default_base_moof", joined); // MP4 robusto (fMP4)
        Assert.EndsWith(".mp4", outFile);
        Assert.Contains("TST_", outFile);                       // patrón con la clave de canal
    }

    [Fact]
    public void Build_NvencProfile_EmitsGpuEncoderAndHwAccel()
    {
        var profile = SoftwareMp4();
        profile.VideoCodec = VideoCodec.H264Nvenc;
        profile.HwAccel = HwAccel.Nvenc;

        var (joined, _) = Build(profile);

        Assert.Contains("-c:v h264_nvenc", joined);
        Assert.Contains("-hwaccel cuda", joined);
        Assert.Contains("-preset p5", joined);                  // preajuste NVENC
    }

    [Fact]
    public void Build_NvencClosedGop_EmitsNoScenecutWithValue()
    {
        // Regresión: -no-scenecut (NVENC) es booleano y EXIGE valor. La forma «bare» hace que FFmpeg tome el
        // -c:a siguiente como su valor y deje «aac» suelto como filename → aborta con EINVAL (-22).
        var profile = SoftwareMp4();
        profile.VideoCodec = VideoCodec.H264Nvenc;
        profile.HwAccel = HwAccel.Nvenc;
        profile.ClosedGop = true;

        var (joined, _) = Build(profile);

        Assert.Contains("-no-scenecut 1", joined);              // con valor explícito
        Assert.DoesNotContain("-no-scenecut -c:a", joined);     // NO la forma «bare» que se come el codec de audio
    }

    [Fact]
    public void Build_Qsv_UsesQuickSyncEncoderBitrateAndNv12()
    {
        var profile = SoftwareMp4();
        profile.VideoCodec = VideoCodec.H264Qsv;                // GPU integrada Intel

        var (joined, _) = Build(profile);

        Assert.Contains("-c:v h264_qsv", joined);
        Assert.Contains("-b:v", joined);                        // por bitrate
        Assert.Contains("-pix_fmt nv12", joined);               // formato nativo QSV
        Assert.DoesNotContain("-crf", joined);                  // no es software libx264
        Assert.DoesNotContain("libx264", joined);
    }

    [Fact]
    public void Build_Amf_UsesAmfEncoderBitrateAndNv12()
    {
        var profile = SoftwareMp4();
        profile.VideoCodec = VideoCodec.H264Amf;                // GPU integrada AMD

        var (joined, _) = Build(profile);

        Assert.Contains("-c:v h264_amf", joined);
        Assert.Contains("-b:v", joined);
        Assert.Contains("-pix_fmt nv12", joined);
        Assert.DoesNotContain("-crf", joined);
    }

    [Fact]
    public void Build_WithTargetResolution_AddsScaleFilter()
    {
        var profile = SoftwareMp4();
        profile.TargetResolution = new Resolution(640, 360);

        var (joined, _) = Build(profile);

        Assert.Contains("-filter_complex", joined);
        Assert.Contains("scale=640:360", joined);
    }

    [Fact]
    public void Build_WithoutTargetResolution_DoesNotScale()
    {
        var (joined, _) = Build(SoftwareMp4());
        Assert.DoesNotContain("scale=", joined);
    }

    [Fact]
    public void Build_Interlaced_SetsFieldOrderAndFlags()
    {
        var profile = SoftwareMp4();
        profile.ScanType = ScanType.InterlacedTff;

        var (joined, _) = Build(profile);

        Assert.Contains("-field_order tt", joined);
        Assert.Contains("+ilme+ildct", joined);
    }

    [Fact]
    public void Build_Progressive_HasNoFieldOrder()
        => Assert.DoesNotContain("-field_order", Build(SoftwareMp4()).Joined);

    [Fact]
    public void Build_WithOutputFrameRate_AddsRateOption()
    {
        var profile = SoftwareMp4();
        profile.OutputFrameRate = FrameRate.P2997; // 30000/1001

        var (joined, _) = Build(profile);

        Assert.Contains("-r 30000/1001", joined);
    }

    [Fact]
    public void Build_ConstantQuality_UsesCrfNotBitrate()
    {
        var profile = SoftwareMp4();
        profile.RateControl = RateControlMode.ConstantQuality;
        profile.Quality = 20;

        var (joined, _) = Build(profile);

        Assert.Contains("-crf 20", joined);
        Assert.DoesNotContain("-b:v", joined);
    }

    [Fact]
    public void Build_ConstantBitrate_ConstrainsRate()
    {
        var (joined, _) = Build(SoftwareMp4()); // CBR por defecto
        Assert.Contains("-maxrate", joined);
        Assert.Contains("-bufsize", joined);
    }

    [Fact]
    public void Build_PcmInMp4_PromotesAudioToAac()
    {
        var profile = SoftwareMp4();
        profile.AudioCodec = AudioCodec.Pcm;                    // PCM no es estándar en MP4

        var (joined, _) = Build(profile);

        Assert.Contains("-c:a aac", joined);                    // promovido automáticamente
        Assert.DoesNotContain("pcm_s24le", joined);
    }

    // --- Familias ProRes / DNxHR (edición) ---

    [Fact]
    public void Build_ProRes4444_UsesProfile4AndYuv444()
    {
        var (joined, outFile) = Build(ProResMov(EncoderProfile.ProRes4444));

        Assert.Contains("-c:v prores_aw", joined);
        Assert.Contains("-profile:v 4", joined);                // 4 = ProRes 4444
        Assert.Contains("-pix_fmt yuv444p10le", joined);        // 4:4:4 obligatorio para 4444
        Assert.EndsWith(".mov", outFile);
    }

    [Fact]
    public void Build_ProRes_UsesTheRealTimeEncoder()
    {
        // prores_ks no llega a tiempo real en un i7-8700 (1080i29.97 HQ a 0,8–1,2× con todos los hilos): una captura en vivo
        // perdía 149–302 cuadros en 15 s. prores_aw da 5–6× con los mismos perfiles (medido con una DeckLink, 2026-10-15).
        var joined = Build(ProResMov(EncoderProfile.ProResHq)).Joined;
        Assert.Contains("-c:v prores_aw -profile:v 3", joined);
        Assert.DoesNotContain("prores_ks", joined);
    }

    [Theory]
    [InlineData(EncoderProfile.ProRes4444, PixelFormat.Yuv422p10le, "4", "yuv444p10le")]   // prores_aw no abre 4444 con 4:2:2
    [InlineData(EncoderProfile.ProRes4444Xq, PixelFormat.Yuv420p, "5", "yuv444p10le")]
    [InlineData(EncoderProfile.ProResHq, PixelFormat.Yuv444p10le, "3", "yuv422p10le")]      // ni convierte un 422 en 4444
    [InlineData(EncoderProfile.ProResLt, PixelFormat.Yuv422p, "1", "yuv422p10le")]          // ProRes solo es de 10 bits
    [InlineData(EncoderProfile.Auto, PixelFormat.Yuv444p10le, "4", "yuv444p10le")]          // Auto con 4:4:4 → 4444
    [InlineData(EncoderProfile.Auto, PixelFormat.Auto, "3", "yuv422p10le")]                 // Auto → 422 HQ
    public void Build_ProRes_ChromaFollowsTheProfile_NotALoosePixelFormat(EncoderProfile profile, PixelFormat pix,
        string expectedProfile, string expectedPix)
    {
        var joined = Build(ProResMov(profile, pix)).Joined;
        Assert.Contains($"-profile:v {expectedProfile}", joined);
        Assert.Contains($"-pix_fmt {expectedPix}", joined);
    }

    [Fact]
    public void Build_ProResAuto_DefaultsTo422Hq()
        => Assert.Contains("-profile:v 3", Build(ProResMov(EncoderProfile.Auto)).Joined); // 3 = 422 HQ

    [Theory]
    [InlineData(EncoderProfile.ProResProxy, "0")]
    [InlineData(EncoderProfile.ProResLt, "1")]
    [InlineData(EncoderProfile.ProResStandard, "2")]
    [InlineData(EncoderProfile.ProRes4444Xq, "5")]
    public void Build_ProResFamily_MapsProfileNumber(EncoderProfile profile, string expected)
        => Assert.Contains($"-profile:v {expected}", Build(ProResMov(profile)).Joined);

    [Fact]
    public void Build_DnxHrHqx_Uses10BitProfile()
    {
        var (joined, _) = Build(DnxhrMov(EncoderProfile.DnxHrHqx));

        Assert.Contains("-c:v dnxhd", joined);
        Assert.Contains("-profile:v dnxhr_hqx", joined);
        Assert.Contains("-pix_fmt yuv422p10le", joined);
    }

    [Fact]
    public void Build_DnxHr444_Uses444ProfileAndPixelFormat()
    {
        var (joined, _) = Build(DnxhrMov(EncoderProfile.DnxHr444));

        Assert.Contains("-profile:v dnxhr_444", joined);
        Assert.Contains("-pix_fmt yuv444p10le", joined);
    }

    [Theory]
    [InlineData(EncoderProfile.DnxHrLb, "dnxhr_lb")]
    [InlineData(EncoderProfile.DnxHrSq, "dnxhr_sq")]
    [InlineData(EncoderProfile.DnxHrHq, "dnxhr_hq")]
    public void Build_DnxHrFamily_MapsProfileName(EncoderProfile profile, string expected)
        => Assert.Contains($"-profile:v {expected}", Build(DnxhrMov(profile)).Joined);

    [Fact]
    public void Build_DnxHrAuto_With444PixelFormat_Uses444Profile()
    {
        // 4:4:4 solo lo admite dnxhr_444: con hqx el codificador no abre («pixel format is incompatible with DNxHD profile»).
        var joined = Build(DnxhrMov(EncoderProfile.Auto, PixelFormat.Yuv444p10le)).Joined;
        Assert.Contains("-profile:v dnxhr_444", joined);
        Assert.Contains("-pix_fmt yuv444p10le", joined);
    }

    private static RecordingProfile Dnxhr1080(EncoderProfile profile, FrameRate rate, ScanType scan, PixelFormat pix = PixelFormat.Yuv422p)
    {
        var p = DnxhrMov(profile, pix);
        p.TargetResolution = new Resolution(1920, 1080);
        p.OutputFrameRate = rate;
        p.ScanType = scan;
        return p;
    }

    [Theory]
    // DNxHR no entrelaza (FFmpeg rechaza +ildct en sus perfiles) y grababa los campos en cuadros progresivos. Un perfil DNxHR
    // entrelazado —los canales que aplicaron los antiguos «DNxHR HQ · 1080i…» guardan esa copia— se graba como su DNxHD.
    [InlineData(EncoderProfile.DnxHrHq, 30000, 1001, PixelFormat.Yuv422p, "220000000 -flags +ildct -pix_fmt yuv422p")]
    [InlineData(EncoderProfile.DnxHrHq, 25, 1, PixelFormat.Yuv422p, "185000000 -flags +ildct -pix_fmt yuv422p")]
    [InlineData(EncoderProfile.DnxHrSq, 30000, 1001, PixelFormat.Yuv422p, "145000000 -flags +ildct -pix_fmt yuv422p")]
    [InlineData(EncoderProfile.DnxHrSq, 25, 1, PixelFormat.Yuv422p, "120000000 -flags +ildct -pix_fmt yuv422p")]
    [InlineData(EncoderProfile.DnxHrHqx, 30000, 1001, PixelFormat.Yuv422p10le, "220000000 -flags +ildct -pix_fmt yuv422p10le")]
    [InlineData(EncoderProfile.Auto, 25, 1, PixelFormat.Auto, "185000000 -flags +ildct -pix_fmt yuv422p")]
    public void Build_InterlacedDnxHr_RecordsTheEquivalentClassicDnxhd(EncoderProfile profile, int num, int den, PixelFormat pix, string expected)
    {
        var joined = Build(Dnxhr1080(profile, new FrameRate(num, den), ScanType.InterlacedTff, pix)).Joined;
        Assert.Contains($"-c:v dnxhd -profile:v dnxhd -b:v {expected}", joined);
        Assert.DoesNotContain("dnxhr", joined);
    }

    [Theory]
    [InlineData(EncoderProfile.DnxHrLb, PixelFormat.Yuv422p, "dnxhr_lb")]      // no hay DNxHD 1080i por debajo de 145
    [InlineData(EncoderProfile.DnxHr444, PixelFormat.Yuv444p10le, "dnxhr_444")] // ni DNxHD 444 en FFmpeg
    public void Build_InterlacedDnxHr_WithoutADnxhdEquivalent_StaysDnxHrWithoutInterlacedDct(EncoderProfile profile, PixelFormat pix, string expected)
    {
        // Pedir +ildct a un perfil DNxHR impediría grabar («Interlaced encoding is not supported for DNxHR profiles»).
        var joined = Build(Dnxhr1080(profile, FrameRate.P2997, ScanType.InterlacedTff, pix)).Joined;
        Assert.Contains($"-profile:v {expected}", joined);
        Assert.DoesNotContain("ildct", joined);
    }

    [Fact]
    public void Build_ProgressiveDnxHr_StaysDnxHr()
    {
        var joined = Build(Dnxhr1080(EncoderProfile.DnxHrHq, FrameRate.P25, ScanType.Progressive)).Joined;
        Assert.Contains("-c:v dnxhd -profile:v dnxhr_hq -pix_fmt yuv422p", joined);
        Assert.DoesNotContain("-b:v", joined);
    }

    // --- DNxHD clásico: el bitrate elige el CID ---

    private static RecordingProfile Dnxhd(int width, int height, FrameRate rate, ScanType scan, double mbps, PixelFormat pix = PixelFormat.Yuv422p) => new()
    {
        Name = "dnxhd", VideoCodec = VideoCodec.DnxHd, HwAccel = HwAccel.None, GopSize = 1,
        TargetResolution = new Resolution(width, height), OutputFrameRate = rate, ScanType = scan,
        VideoBitrate = Bitrate.FromMbps(mbps), PixelFormat = pix,
        AudioCodec = AudioCodec.Pcm, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mxf,
    };

    [Fact]
    public void BuildLive_Dnxhd1080i_RecordsClassicDnxhdWithBothFields()
    {
        // DNxHR no puede entrelazar: los presets «1080i» de DNxHR grababan cuadros progresivos (MXF los declaraba progresivos).
        // DNxHD clásico sí: CID 1243 (220 Mbps a 29.97) con DCT entrelazada y los cuadros marcados TFF.
        var joined = BuildLiveFrom(Signal1080(FrameRate.P2997, ScanType.InterlacedTff),
            Dnxhd(1920, 1080, FrameRate.P2997, ScanType.InterlacedTff, 220));
        Assert.Contains("[vrec]scale=1920:1080,setfield=mode=tff,format=yuv422p[vmain]", joined);
        Assert.Contains("-c:v dnxhd -profile:v dnxhd -b:v 220000000 -flags +ildct -pix_fmt yuv422p", joined);
        Assert.Contains("-field_order tt", joined);
        Assert.DoesNotContain("dnxhr", joined);
        Assert.DoesNotContain(RecordChain(joined), f => f.StartsWith("bwdif", StringComparison.Ordinal)); // los campos, tal cual
    }

    [Theory]
    // Los nombres de Avid: mismo CID, distinto nombre según la cadencia (los Mbps reales son tamaño de cuadro × cadencia).
    [InlineData(30000, 1001, 145, 145)]   // CID 1242 · DNxHD 145
    [InlineData(30000, 1001, 220, 220)]   // CID 1243 · DNxHD 220
    [InlineData(25, 1, 120, 120)]         // CID 1242 · DNxHD 120
    [InlineData(25, 1, 185, 185)]         // CID 1243 · DNxHD 185
    [InlineData(25, 1, 220, 220)]         // una clave válida se respeta (FFmpeg da el mismo CID 1243 que con 185)
    // Fuera de la lista de FFmpeg la grabación no arrancaría: se ajusta al CID de Mbps reales más cercanos.
    [InlineData(30000, 1001, 200, 220)]
    [InlineData(25, 1, 160, 185)]         // 160 queda más cerca de los 183,5 reales del CID 1243 que de los 121 del 1242
    [InlineData(25, 1, 150, 120)]         // …y 150, de los 121
    [InlineData(30000, 1001, 16, 145)]    // un bitrate que quedó de otro códec
    // Sin bitrate (un preset DNxHD que confiaba en el perfil, como antes): el CID de más calidad, el de DNxHR HQ.
    [InlineData(30000, 1001, 0, 220)]
    [InlineData(25, 1, 0, 185)]
    public void Build_Dnxhd1080i_BitrateSelectsAValidCid(int num, int den, double requested, int expected)
    {
        var joined = Build(Dnxhd(1920, 1080, new FrameRate(num, den), ScanType.InterlacedTff, requested)).Joined;
        Assert.Contains($"-c:v dnxhd -profile:v dnxhd -b:v {expected * 1_000_000L} -flags +ildct -pix_fmt yuv422p", joined);
    }

    [Fact]
    public void Build_Dnxhd10Bit_UsesTheXCidsWithTenBitPixels()
    {
        // DNxHD 220x (CID 1241, 1080i 10 bits).
        var joined = Build(Dnxhd(1920, 1080, FrameRate.P2997, ScanType.InterlacedTff, 220, PixelFormat.Yuv422p10le)).Joined;
        Assert.Contains("-profile:v dnxhd -b:v 220000000 -flags +ildct -pix_fmt yuv422p10le", joined);
    }

    [Fact]
    public void Build_Dnxhd1080p10Bit_At2997_AvoidsTheKeyFfmpegRejects()
    {
        // El CID 1235 (1080p 10 bits) da 220 Mbps a 29.97, pero FFmpeg no tiene «220» en su lista para él: con -b:v 220M no
        // abre (comprobado). Se pide con la clave más próxima del mismo CID.
        var joined = Build(Dnxhd(1920, 1080, FrameRate.P2997, ScanType.Progressive, 220, PixelFormat.Yuv422p10le)).Joined;
        Assert.Contains("-profile:v dnxhd -b:v 185000000 -pix_fmt yuv422p10le", joined);
        Assert.DoesNotContain("ildct", joined);
    }

    [Theory]
    [InlineData(3840, 2160, PixelFormat.Yuv422p)]       // 4K: DNxHD clásico no existe
    [InlineData(720, 576, PixelFormat.Yuv422p)]         // SD tampoco
    [InlineData(1920, 1080, PixelFormat.Yuv444p10le)]   // DNxHD 444: el dnxhd de FFmpeg solo admite 4:4:4 con dnxhr_444
    public void Build_DnxhdWithoutAClassicCid_RecordsDnxHrLikeBefore(int width, int height, PixelFormat pix)
    {
        var profile = Dnxhd(width, height, FrameRate.P25, ScanType.Progressive, 185, pix);
        var joined = Build(profile).Joined;
        Assert.Contains("-c:v dnxhd -profile:v dnxhr_", joined);
        Assert.DoesNotContain("-profile:v dnxhd", joined);
    }

    [Fact]
    public void Build_DnxhdAtNativeSizeFromAnUnknownSource_RecordsDnxHr()
    {
        // Sin tamaño (preset en nativo y una fuente que aún no lo ha dicho) no hay CID que elegir: DNxHR, como siempre.
        var profile = Dnxhd(1920, 1080, FrameRate.P25, ScanType.Progressive, 185);
        profile.TargetResolution = null;
        Assert.Contains("-profile:v dnxhr_hq", Build(profile).Joined);
    }

    [Fact]
    public void Build_CatalogDnxhdPresets_AskFfmpegForExactlyTheirBitrate_WithBothFields()
    {
        // El bitrate de cada DNxHD del catálogo es una clave válida de su CID: la línea de comandos dice lo que dice el preset.
        var dnxhd = Baioss.Record.Application.Presets.PresetCatalog.CreateBuiltIns().Where(p => p.VideoCodec == VideoCodec.DnxHd).ToList();
        Assert.NotEmpty(dnxhd);
        foreach (var preset in dnxhd)
        {
            var profile = preset.ToProfile();
            Assert.Equal((int)preset.VideoBitrateMbps, FfmpegArgumentBuilder.DnxhdBitrateMbps(profile, profile.TargetResolution, profile.OutputFrameRate));
            var joined = Build(profile).Joined;
            Assert.Contains($"-c:v dnxhd -profile:v dnxhd -b:v {(long)preset.VideoBitrateMbps * 1_000_000} -flags +ildct -pix_fmt yuv422p", joined);
            Assert.Contains("setfield=mode=tff", joined);
        }
    }

    [Fact]
    public void BuildLive_DnxhdAtNativeSize_TakesTheSizeAndRateFromTheSignal()
    {
        var profile = Dnxhd(1920, 1080, FrameRate.P25, ScanType.InterlacedTff, 0);
        profile.TargetResolution = null;
        profile.OutputFrameRate = null;
        var joined = BuildLiveFrom(Signal1080(FrameRate.P2997, ScanType.InterlacedTff), profile);
        Assert.Contains("-profile:v dnxhd -b:v 220000000 -flags +ildct", joined); // 1080i29.97 de la señal → CID 1243
    }

    // --- Pipeline en vivo: preview + grabación en un solo proceso ---

    private static FfmpegArgumentBuilder NewLiveBuilder(RecordingProfile profile, bool hasAudio = true, bool analyze = false)
    {
        var source = new FakeCaptureSource("C:/clips/in.mp4");
        // La fuente declara (o no) pista de audio: un dispositivo solo-vídeo (cámara/OBS) no tiene
        // audio que medir ni grabar, y el builder debe omitir esas salidas en consecuencia.
        source.Emit(new SignalInfo(SignalState.Locked, new Resolution(1920, 1080), new FrameRate(25, 1),
            hasAudio ? AudioLayout.Stereo : null, HasAudio: hasAudio, Timecode: null, Bitrate: null));
        return new FfmpegArgumentBuilder()
            .From(source)
            .Using(profile).ForChannel("TST").ToDirectory("C:/out")
            .WithPreviewSink("tcp://127.0.0.1:9001")
            .WithSignalAnalysis(analyze); // por defecto OFF: mantiene precisas las aserciones de la tubería base
    }

    private static string BuildLive(RecordingProfile profile, bool recording, bool hasAudio = true, bool analyze = false)
        => string.Join(' ', NewLiveBuilder(profile, hasAudio, analyze).BuildLive(recording, 640, 360));

    [Fact]
    public void BuildLive_PreviewOnly_HasPreviewAndMetersButNoEncoder()
    {
        var joined = BuildLive(SoftwareMp4(), recording: false);

        Assert.Contains("-progress pipe:1", joined);                        // telemetría/watchdog del supervisor
        Assert.Contains("[0:v]scale=640:360,format=bgra[pv]", joined);      // rama de preview
        Assert.Contains("-map [pv] -fps_mode passthrough -f rawvideo tcp://127.0.0.1:9001", joined);
        Assert.Contains("ebur128=peak=true", joined);                       // medidores
        Assert.DoesNotContain("-c:v libx264", joined);                      // idle: NO graba
        Assert.DoesNotContain("-y", joined);                                // …ni escribe archivo
        Assert.DoesNotContain("TST_", joined);                              // (in.mp4 de entrada sí lleva .mp4)
    }

    [Fact]
    public void BuildLive_Recording_SplitsToPreviewAndFileAtOnce()
    {
        var joined = BuildLive(SoftwareMp4(), recording: true);

        Assert.Contains("split=2[vrec][vprev]", joined);                    // una apertura → dos ramas
        Assert.Contains("[vprev]scale=640:360,format=bgra[pv]", joined);    // preview…
        Assert.Contains("-map [pv] -fps_mode passthrough -f rawvideo tcp://127.0.0.1:9001", joined);
        Assert.Contains("-c:v libx264", joined);                            // …y grabación a la vez
        Assert.Contains("-movflags +frag_keyframe+empty_moov+default_base_moof", joined); // fMP4: archivo siempre reproducible
        Assert.Contains("-y", joined);                                      // archivo de salida
        Assert.Contains("TST_", joined);                                    // nombre de la grabación
        Assert.Contains("ebur128=peak=true", joined);
    }

    [Fact]
    public void BuildLive_AndSlate_ThePreviewIsTheFirstOutput_SoProgressFramesAreInputFrames()
    {
        // FFmpeg informa frame/fps/drop_frames del progreso con la PRIMERA salida de vídeo (las versiones ≤ 6.0 suman además
        // los descartes de todas). Con el preview primero —en passthrough, un frame por frame de entrada— «frame» cuenta frames
        // de ENTRADA: la alarma de frames perdidos descuenta la conversión de cadencia sobre esa base (SaturationDetector).
        // (Con una señal entrelazada el preview saca uno por campo: PreviewFramesPerSourceFrame lo dice y el parser lo divide.)
        // Si alguien reordena las salidas, la cuenta cambia de significado: este test lo avisa.
        var profile = SoftwareMp4();
        profile.OutputFrameRate = FrameRate.P25;
        foreach (var args in new[]
                 {
                     NewLiveBuilder(profile).BuildLive(recording: true, 640, 360),
                     NewLiveBuilder(profile).BuildSlate(recording: true, 640, 360),
                 })
        {
            int firstMap = args.ToList().IndexOf("-map");
            Assert.Equal("[pv]", args[firstMap + 1]);
            Assert.Equal("passthrough", args[firstMap + 3]);
            int rate = args.ToList().LastIndexOf("-r");
            Assert.True(rate > firstMap, "la conversión de cadencia (-r) va en la salida de grabación, detrás del preview");
            Assert.Equal("25/1", args[rate + 1]);
        }
    }

    [Fact]
    public void BuildLive_Recording_Nvenc_ConvertsRecordBranchToEncoderFormat()
    {
        // Regresión (NDI): la fuente sirve uyvy422 y h264_nvenc lo RECHAZA con EINVAL (-22) si la rama de
        // grabación no convierte el formato — desde un filter_complex FFmpeg NO auto-inserta la conversión.
        // La rama [vrec] debe terminar en format=nv12 (nativo de NVENC) antes de mapearse al encoder.
        var profile = SoftwareMp4();
        profile.VideoCodec = VideoCodec.H264Nvenc;
        profile.HwAccel = HwAccel.Nvenc;

        var joined = BuildLive(profile, recording: true);

        Assert.Contains("format=nv12[vmain]", joined);   // conversión al final de la rama de grabación
        Assert.Contains("-map [vmain]", joined);          // …y es esa rama la que va al encoder
        Assert.Contains("-c:v h264_nvenc", joined);
    }

    // --- Fuente entrelazada (1080i de una DeckLink) ---

    private static string BuildLiveFrom(SignalInfo signal, RecordingProfile profile)
    {
        var source = new FakeCaptureSource("C:/clips/in.mp4");
        source.Emit(signal);
        return string.Join(' ', new FfmpegArgumentBuilder().From(source).Using(profile).ForChannel("TST").ToDirectory("C:/out")
            .WithPreviewSink("tcp://127.0.0.1:9001").BuildLive(recording: true, 640, 360));
    }

    private static SignalInfo Signal1080(FrameRate rate, ScanType? scan) => new(SignalState.Locked, new Resolution(1920, 1080), rate,
        AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null, Scan: scan);

    private static RecordingProfile Progressive(VideoCodec codec, FrameRate rate) => new()
    {
        Name = "p", VideoCodec = codec, HwAccel = HwAccel.None, VideoBitrate = Bitrate.FromMbps(16), GopSize = 50,
        TargetResolution = new Resolution(1920, 1080), OutputFrameRate = rate,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    [Fact]
    public void BuildLive_InterlacedSignal_ProgressivePreset_DeinterlacesBeforeScaling()
    {
        // Medido con una Duo 2 en 1080i59.94 y el preset NVENC 1080p29.97: sin esto, idet daba 300 de 301 cuadros TFF (peine).
        var joined = BuildLiveFrom(Signal1080(FrameRate.P2997, ScanType.InterlacedTff), Progressive(VideoCodec.H264Nvenc, FrameRate.P2997));
        Assert.Contains("[vrec]bwdif=mode=send_frame:parity=tff:deint=all,scale=1920:1080,format=nv12[vmain]", joined);
    }

    [Fact]
    public void BuildLive_InterlacedSignal_PresetAtTheFieldRate_GetsOneFramePerField()
    {
        // 1080i59.94 → 1080p59.94: un cuadro por campo (movimiento real), no cada cuadro entretejido repetido.
        var joined = BuildLiveFrom(Signal1080(FrameRate.P2997, ScanType.InterlacedTff), Progressive(VideoCodec.H264x264, FrameRate.P5994));
        Assert.Contains("[vrec]bwdif=mode=send_field:parity=tff:deint=all,scale=1920:1080", joined);
        Assert.Contains("-r 60000/1001", joined);
    }

    private static string BuildLiveSparse(SignalInfo signal, RecordingProfile profile, bool recording = true)
    {
        var source = new FakeCaptureSource("C:/clips/in.mp4") { DeliversSparseVideo = true };
        source.Emit(signal);
        return string.Join(' ', new FfmpegArgumentBuilder().From(source).Using(profile).ForChannel("TST").ToDirectory("C:/out")
            .WithPreviewSink("tcp://127.0.0.1:9001").WithSignalAnalysis(false).BuildLive(recording, 640, 360));
    }

    [Fact]
    public void BuildLive_ASourceWithGaps_IsRecordedAtItsDeclaredRate_FilledAtTheEndOfTheBranch()
    {
        // NDI sirve solo los cuadros que llegan (un grafismo que declara 60 y manda 4): la grabación los repite hasta el siguiente con
        // un fps al FINAL de la rama —convertir y escalar solo los reales— y a la tasa declarada si el preset la deja en nativo; el
        // preview los pinta tal como llegan.
        var profile = Progressive(VideoCodec.H264Nvenc, FrameRate.P60);
        profile.OutputFrameRate = null;
        profile.TargetResolution = null;
        var joined = BuildLiveSparse(Signal1080(new FrameRate(60000, 1000), ScanType.Progressive), profile);
        Assert.Contains("[vrec]format=nv12,fps=60000/1000[vmain]", joined);
        Assert.Contains("[vprev]scale=640:360,format=bgra[pv]", joined);
        Assert.Contains("-map [pv] -fps_mode passthrough -f rawvideo", joined);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(joined, "fps="));
    }

    [Fact]
    public void BuildLive_ASourceWithGaps_AndAPresetRate_IsFilledStraightToThePresetRate()
    {
        var joined = BuildLiveSparse(Signal1080(new FrameRate(60000, 1000), ScanType.Progressive), Progressive(VideoCodec.H264x264, FrameRate.P2997));
        Assert.Contains("[vrec]scale=1920:1080,format=yuv420p,fps=30000/1001[vmain]", joined);
        Assert.Contains("-r 30000/1001", joined);
    }

    [Fact]
    public void BuildLive_ASourceWithGaps_DeinterlacedPerField_OrWithABurntTimecode_IsFilledFirst_AtItsRate()
    {
        // bwdif pone el segundo campo a medio camino del cuadro siguiente (tras un hueco, lejos) y drawtext cuenta cuadros: esas ramas
        // reciben un cuadro por periodo de la fuente, como antes.
        var perField = BuildLiveSparse(Signal1080(FrameRate.P2997, ScanType.InterlacedTff), Progressive(VideoCodec.H264x264, FrameRate.P5994));
        Assert.Contains("[vrec]fps=30000/1001,bwdif=mode=send_field:parity=tff:deint=all,scale=1920:1080", perField);
        Assert.DoesNotContain(",fps=", perField);

        var perFrame = BuildLiveSparse(Signal1080(FrameRate.P2997, ScanType.InterlacedTff), Progressive(VideoCodec.H264x264, FrameRate.P2997));
        Assert.Contains("[vrec]bwdif=mode=send_frame:parity=tff:deint=all,scale=1920:1080,format=yuv420p,fps=30000/1001[vmain]", perFrame);

        var timecode = Progressive(VideoCodec.H264x264, FrameRate.P2997);
        timecode.BurnTimecode = true;
        var burnt = BuildLiveSparse(Signal1080(new FrameRate(60000, 1000), ScanType.Progressive), timecode);
        Assert.Contains("[vrec]fps=60000/1000,scale=1920:1080,drawtext=", burnt);
        Assert.Contains("-r 30000/1001", burnt);
    }

    [Fact]
    public void BuildLive_ASourceWithoutGaps_OrOnlyPreviewing_GetsNoFill()
    {
        Assert.DoesNotContain("fps=", BuildLiveFrom(Signal1080(FrameRate.P2997, ScanType.Progressive), Progressive(VideoCodec.H264x264, FrameRate.P2997)));
        Assert.DoesNotContain("fps=", BuildLiveSparse(Signal1080(FrameRate.P2997, ScanType.Progressive), Progressive(VideoCodec.H264x264, FrameRate.P2997), recording: false));
    }

    [Fact]
    public void Build_ASourceWithGaps_IsFilledAtTheInput()
    {
        var source = new FakeCaptureSource("C:/clips/in.mp4") { DeliversSparseVideo = true };
        source.Emit(Signal1080(new FrameRate(60000, 1000), ScanType.Progressive));
        var profile = Progressive(VideoCodec.H264x264, FrameRate.P2997);
        profile.TargetResolution = null;
        var joined = string.Join(' ', new FfmpegArgumentBuilder().From(source).Using(profile).ForChannel("TST").ToDirectory("C:/out").Build());
        Assert.Contains("-filter_complex [0:v]fps=60000/1000[vfill] -map [vfill]", joined);
    }

    [Fact]
    public void BuildLive_BottomFieldFirstSignal_DeinterlacesWithThatParity()
        => Assert.Contains("bwdif=mode=send_frame:parity=bff:deint=all",
            BuildLiveFrom(Signal1080(FrameRate.P2997, ScanType.InterlacedBff), Progressive(VideoCodec.H264x264, FrameRate.P2997)));

    [Theory]
    [InlineData(ScanType.Progressive, ScanType.Progressive)]    // señal progresiva: nada que desentrelazar
    [InlineData(null, ScanType.Progressive)]                    // barrido desconocido (archivo, NDI…): como siempre
    [InlineData(ScanType.InterlacedTff, ScanType.InterlacedTff)] // preset entrelazado: se graban los campos tal cual
    public void BuildLive_DoesNotDeinterlace_WhenThereIsNothingToFix(ScanType? signalScan, ScanType presetScan)
    {
        // La rama de GRABACIÓN (el preview de una señal entrelazada se desentrelaza siempre: ver los tests del preview).
        var profile = Progressive(VideoCodec.H264x264, FrameRate.P2997);
        profile.ScanType = presetScan;
        Assert.DoesNotContain(RecordChain(BuildLiveFrom(Signal1080(FrameRate.P2997, signalScan), profile)),
            f => f.StartsWith("bwdif", StringComparison.Ordinal));
        // …ni el scale sin tamaño que acompaña a bwdif: a resolución nativa la rama sigue sin escalar.
        profile.TargetResolution = null;
        Assert.DoesNotContain("scale", RecordChain(BuildLiveFrom(Signal1080(FrameRate.P2997, signalScan), profile)));
    }

    // --- Preview de una señal entrelazada: sin la doble imagen de reducir los dos campos juntos ---

    private static FfmpegArgumentBuilder LiveBuilderFor(SignalInfo signal, int prerollFrames = 0, bool analyze = false)
    {
        var source = new FakeCaptureSource("C:/clips/in.mp4") { PreviewFramesToSkip = prerollFrames };
        source.Emit(signal);
        return new FfmpegArgumentBuilder().From(source).Using(Progressive(VideoCodec.H264Nvenc, FrameRate.P2997)).ForChannel("TST")
            .ToDirectory("C:/out").WithPreviewSink("tcp://127.0.0.1:9001").WithSignalAnalysis(analyze);
    }

    [Theory]
    [InlineData(ScanType.InterlacedTff, "top", "tff")]
    [InlineData(ScanType.InterlacedBff, "bottom", "bff")]
    public void BuildLive_InterlacedSignal_ByDefault_PreviewsTheFirstFieldOfEachFrame(ScanType scan, string field, string parity)
    {
        // Reducir a 640×360 un cuadro con los dos campos entretejidos los mezclaba: doble imagen en todo lo que se mueve. Por
        // defecto el preview se queda con el primer campo de cada cuadro (540 líneas, de sobra para 360): sin fantasma, a la
        // cadencia de cuadro de la fuente y con el coste de siempre.
        string chain = $"field=type={field},scale=640:360,format=bgra[pv]";
        var builder = LiveBuilderFor(Signal1080(FrameRate.P2997, scan));

        Assert.Contains($"-filter_complex [0:v]{chain} ", string.Join(' ', builder.BuildLive(recording: false, 640, 360)));
        Assert.Equal(1, builder.PreviewFramesPerSourceFrame);

        var recording = string.Join(' ', builder.BuildLive(recording: true, 640, 360));
        Assert.Contains($"[vprev]{chain}", recording);
        Assert.Equal(1, builder.PreviewFramesPerSourceFrame);
        // La rama de grabación sigue a lo suyo: desentrelaza según el preset (aquí 1080p29.97, un cuadro por cuadro).
        Assert.Contains($"[vrec]bwdif=mode=send_frame:parity={parity}:deint=all,scale=1920:1080,format=nv12[vmain]", recording);
    }

    [Theory]
    [InlineData(720, 486, ScanType.InterlacedBff, "bff")] // NTSC (DeckLink: el campo inferior primero)
    [InlineData(720, 576, ScanType.InterlacedTff, "tff")] // PAL
    public void BuildLive_InterlacedSdSignal_ByDefault_RebuildsTheWholeFrame_InsteadOfStretchingAField(int width, int height, ScanType scan, string parity)
    {
        // Un campo de SD (243 o 288 líneas) habría que estirarlo a las 360 del preview: bwdif reconstruye el cuadro entero, uno
        // por cuadro de la fuente (con SD es barato).
        var signal = new SignalInfo(SignalState.Locked, new Resolution(width, height), height == 576 ? FrameRate.P25 : FrameRate.P2997,
            AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null, Scan: scan);
        var builder = LiveBuilderFor(signal);
        Assert.Contains($"-filter_complex [0:v]scale=640:ih,bwdif=mode=send_frame:parity={parity}:deint=all:threads=1,scale=640:360,format=bgra[pv] ",
            string.Join(' ', builder.BuildLive(recording: false, 640, 360)));
        Assert.Equal(1, builder.PreviewFramesPerSourceFrame);
    }

    [Theory]
    [InlineData(ScanType.InterlacedTff, "tff")]
    [InlineData(ScanType.InterlacedBff, "bff")]
    public void BuildLive_InterlacedSignal_WithFieldRatePreview_IsDeinterlacedOneFramePerField(ScanType scan, string parity)
    {
        // El movimiento de la tele (59.94 desde un 1080i59.94), a cambio de más CPU: primero solo en horizontal (no mezcla
        // campos), bwdif a 640×1080 en un hilo con la paridad de la señal y un cuadro por campo, y luego a la altura del preview.
        string chain = $"scale=640:ih,bwdif=mode=send_field:parity={parity}:deint=all:threads=1,scale=640:360,format=bgra[pv]";
        var builder = LiveBuilderFor(Signal1080(FrameRate.P2997, scan)).WithFieldRatePreview(true);

        Assert.Contains($"-filter_complex [0:v]{chain} ", string.Join(' ', builder.BuildLive(recording: false, 640, 360)));
        Assert.Equal(2, builder.PreviewFramesPerSourceFrame);

        var recording = string.Join(' ', builder.BuildLive(recording: true, 640, 360));
        Assert.Contains($"[vprev]{chain}", recording);
        Assert.Equal(2, builder.PreviewFramesPerSourceFrame);
        Assert.Contains($"[vrec]bwdif=mode=send_frame:parity={parity}:deint=all,scale=1920:1080,format=nv12[vmain]", recording);
    }

    [Theory]
    [InlineData(ScanType.Progressive)]
    [InlineData(null)] // barrido desconocido (archivo, NDI…)
    public void BuildLive_ProgressiveOrUnknownSignal_PreviewIsUnchanged(ScanType? scan)
    {
        foreach (bool fieldRate in new[] { false, true })
        {
            var builder = LiveBuilderFor(Signal1080(FrameRate.P5994, scan)).WithFieldRatePreview(fieldRate);
            Assert.Contains("-filter_complex [0:v]scale=640:360,format=bgra[pv] ", string.Join(' ', builder.BuildLive(recording: false, 640, 360)));
            Assert.Equal(1, builder.PreviewFramesPerSourceFrame);
            var recording = string.Join(' ', builder.BuildLive(recording: true, 640, 360));
            Assert.Contains("[vprev]scale=640:360,format=bgra[pv]", recording);
            Assert.DoesNotContain("bwdif", recording);
            Assert.DoesNotContain("field=", recording);
            Assert.Equal(1, builder.PreviewFramesPerSourceFrame);
        }
    }

    [Theory]
    [InlineData(false, "field=type=top")]
    [InlineData(true, "scale=640:ih,bwdif=mode=send_field:parity=tff:deint=all:threads=1")]
    public void BuildLive_InterlacedSignal_SkipsThePrerollFirst_AndTheDetectorsSeeTheDeinterlacedPreview(bool fieldRate, string deinterlace)
    {
        // El salto del pre-roll cuenta frames de la FUENTE: va antes de desentrelazar (bwdif saca dos por cada uno). Los
        // detectores de negro/congelado, al final como siempre, miran lo que se ve (sus umbrales van en segundos, no en frames).
        var builder = LiveBuilderFor(Signal1080(FrameRate.P2997, ScanType.InterlacedTff), prerollFrames: 75, analyze: true)
            .WithFieldRatePreview(fieldRate);
        var joined = string.Join(' ', builder.BuildLive(recording: true, 640, 360));

        Assert.Contains($"[vprev]select=gte(n\\,75),{deinterlace},scale=640:360," +
                        "blackdetect=d=2:pix_th=0.10,freezedetect=n=-60dB:d=2,format=bgra[pv]", joined);
        Assert.DoesNotContain("[vrec]select", joined); // la grabación se queda con el pre-roll
        Assert.Contains("-map [pv] -fps_mode passthrough -f rawvideo tcp://127.0.0.1:9001", joined);
    }

    [Fact]
    public void BuildSlate_AfterAnInterlacedSignal_ThePreviewOfTheBarsIsOneFramePerFrame()
    {
        // Las barras de la carta de ajuste son progresivas aunque la señal a la que sustituyen fuera entrelazada.
        var builder = LiveBuilderFor(Signal1080(FrameRate.P2997, ScanType.InterlacedTff)).WithFieldRatePreview(true);
        builder.BuildLive(recording: true, 640, 360);
        Assert.Equal(2, builder.PreviewFramesPerSourceFrame);

        var slate = string.Join(' ', builder.BuildSlate(recording: true, 640, 360));
        Assert.Contains("[vprev]scale=640:360,format=bgra[pv]", slate);
        Assert.DoesNotContain("bwdif", slate);
        Assert.Equal(1, builder.PreviewFramesPerSourceFrame);
    }

    /// <summary>Los filtros de la rama de grabación, en orden: «;[vrec]a,b,c[vmain]» → a, b, c.</summary>
    private static string[] RecordChain(string joined)
    {
        int start = joined.IndexOf(";[vrec]", StringComparison.Ordinal) + ";[vrec]".Length;
        return joined[start..joined.IndexOf("[vmain]", start, StringComparison.Ordinal)].Split(',');
    }

    [Theory]
    [InlineData(VideoCodec.H264x264, "yuv420p")]
    [InlineData(VideoCodec.H265x265, "yuv420p")]
    [InlineData(VideoCodec.Mpeg2Video, "yuv420p")]
    [InlineData(VideoCodec.HevcNvenc, "nv12")] // el perfil por defecto: HEVC NVENC a resolución nativa
    public void BuildLive_InterlacedSignal_PresetWithoutFixedSize_ConvertsAfterDeinterlacing(VideoCodec codec, string encoderFormat)
    {
        // Con bwdif seguido directamente del format= final, FFmpeg convertía la fuente (uyvy422 de la DeckLink) a 4:2:0 ANTES
        // de desentrelazar: el croma de los dos campos se mezclaba en una franja de color en cada borde que se mueve. El scale
        // sin tamaño hace la conversión detrás de bwdif.
        var profile = Progressive(codec, FrameRate.P2997);
        profile.TargetResolution = null;
        var joined = BuildLiveFrom(Signal1080(FrameRate.P2997, ScanType.InterlacedTff), profile);
        Assert.Contains($"[vrec]bwdif=mode=send_frame:parity=tff:deint=all,scale,format={encoderFormat}[vmain]", joined);
    }

    [Fact]
    public void BuildLive_InterlacedSignal_EveryProgressiveCatalogPreset_ConvertsOnlyAfterDeinterlacing()
    {
        // bwdif (y drawtext) salen en el formato en que entran: el filtro que sigue a bwdif tiene que ser un scale, con el
        // tamaño del preset o sin tamaño, para que la conversión al formato del encoder ocurra DESPUÉS de desentrelazar.
        // Cada preset progresivo del catálogo, con su tamaño y a resolución nativa, con el timecode quemado.
        var presets = Baioss.Record.Application.Presets.PresetCatalog.CreateBuiltIns()
            .Select(p => p.ToProfile())
            .Where(p => !p.AudioOnly && p.ScanType is ScanType.Progressive)
            .ToList();
        Assert.NotEmpty(presets);

        foreach (var preset in presets)
        {
            foreach (bool nativeSize in new[] { false, true })
            {
                var profile = preset.Clone();
                if (nativeSize) profile.TargetResolution = null;
                profile.BurnTimecode = true;
                var chain = RecordChain(BuildLiveFrom(Signal1080(FrameRate.P2997, ScanType.InterlacedTff), profile));
                string why = $"{preset.Name}{(nativeSize ? " (nativa)" : "")}: {string.Join(',', chain)}";
                Assert.True(chain[0].StartsWith("bwdif=", StringComparison.Ordinal), why);
                Assert.True(chain[1] == "scale" || chain[1].StartsWith("scale=", StringComparison.Ordinal), why);
                Assert.True(chain[^1].StartsWith("format=", StringComparison.Ordinal), why);
            }
        }
    }

    [Fact]
    public void Build_InterlacedProRes_AsksForInterlacedDct()
    {
        // ProRes codifica progresivo salvo que se le pida DCT entrelazada: un «1080i» grababa cuadros progresivos.
        var profile = ProResMov(EncoderProfile.ProResHq);
        profile.ScanType = ScanType.InterlacedTff;
        var joined = Build(profile).Joined;
        Assert.Contains("-c:v prores_aw -profile:v 3 -flags +ildct", joined);
        Assert.Contains("-field_order tt", joined);
        Assert.DoesNotContain("+ildct", Build(ProResMov(EncoderProfile.ProResHq)).Joined);
    }

    [Theory]
    [InlineData(ScanType.InterlacedTff, "setfield=mode=tff")]
    [InlineData(ScanType.InterlacedBff, "setfield=mode=bff")]
    public void BuildSlate_InterlacedPreset_MarksTheBarsWithTheFieldOrder(ScanType scan, string setfield)
    {
        // Las barras de lavfi son cuadros progresivos, sin marca de campo: con un preset 1080i TFF, x264 codificaba las piezas
        // de la carta de ajuste en BFF y el MOV de ProRes las declaraba con el campo inferior primero («bt»).
        var profile = ProResMov(EncoderProfile.ProResHq);
        profile.ScanType = scan;
        var joined = string.Join(' ', NewLiveBuilder(profile).BuildSlate(recording: true, 640, 360));
        Assert.Contains($";[vrec]{setfield}[vslate]", joined);
        Assert.Contains("-map [vslate]", joined);
        Assert.Contains("-flags +ildct", joined);
    }

    [Fact]
    public void BuildSlate_ProgressivePreset_RecordsTheBarsAsTheyAre()
    {
        var joined = string.Join(' ', NewLiveBuilder(ProResMov(EncoderProfile.ProResHq)).BuildSlate(recording: true, 640, 360));
        Assert.DoesNotContain("setfield", joined);
        Assert.Contains("-map [vrec]", joined);
    }

    [Fact]
    public void BuildLive_CatalogNvencPresets_EveryFallbackStepGetsItsNativePixelFormat()
    {
        // Los presets NVENC del catálogo dejan el píxel en Auto. Sin NVIDIA el motor degrada clonando el perfil y
        // cambiando SOLO el códec (EncoderFallbackChain), así que cada escalón debe recibir su formato nativo:
        // NVENC nv12 (solo en el filtro: elige solo), QSV/AMF nv12, x264/x265 yuv420p. Un yuv420p fijo en el preset
        // haría fallar a h264_qsv, y un yuv420p10le a NVENC.
        var nvenc = Baioss.Record.Application.Presets.PresetCatalog.CreateBuiltIns()
            .Where(p => p.VideoCodec is VideoCodec.H264Nvenc or VideoCodec.HevcNvenc)
            .ToList();
        Assert.NotEmpty(nvenc);

        foreach (var preset in nvenc)
        {
            var profile = preset.ToProfile();
            for (VideoCodec? codec = profile.VideoCodec; codec is { } c; codec = EncoderFallbackChain.Next(c))
            {
                var step = profile.Clone();
                step.VideoCodec = c;
                var joined = BuildLive(step, recording: true);

                string expected = c is VideoCodec.H264x264 or VideoCodec.H265x265 ? "yuv420p" : "nv12";
                Assert.Contains($"-c:v {FfmpegCodecMap.VideoEncoder(c)}", joined);
                Assert.Contains($"format={expected}[vmain]", joined);
                if (FfmpegCodecMap.IsGpuEncoder(c)) Assert.DoesNotContain("-pix_fmt", joined);
                else Assert.Contains($"-pix_fmt {expected}", joined);
            }
        }
    }

    [Fact]
    public void BuildLive_PreviewSkipsTheSourcePreroll_BeforeScalingAndDetectors_AndRecordingKeepsIt()
    {
        // Una entrada de red entrega un pre-roll (la grabación empieza antes del botón): el preview se lo salta —antes de
        // escalar y de los detectores de negro/congelado— y la rama de grabación lo conserva. Y cada frame decodificado
        // sale una vez al preview (passthrough), sin las duplicaciones del CFR.
        var source = new FakeCaptureSource("C:/clips/in.mp4") { PreviewFramesToSkip = 75 };
        source.Emit(new SignalInfo(SignalState.Locked, new Resolution(1920, 1080), new FrameRate(30000, 1001), AudioLayout.Stereo,
            HasAudio: true, Timecode: null, Bitrate: null));
        var builder = new FfmpegArgumentBuilder().From(source).Using(SoftwareMp4()).ForChannel("TST")
            .ToDirectory("C:/out").WithPreviewSink("tcp://127.0.0.1:9001").WithSignalAnalysis(true);
        var joined = string.Join(' ', builder.BuildLive(recording: true, 640, 360));

        Assert.Contains("[0:v]split=2[vrec][vprev];", joined);
        Assert.Contains("[vprev]select=gte(n\\,75),scale=640:360,", joined);   // primero el salto, luego escala y detectores
        Assert.DoesNotContain("[vrec]select", joined);                          // la grabación se queda con el pre-roll
        Assert.Contains("-map [pv] -fps_mode passthrough -f rawvideo tcp://127.0.0.1:9001", joined);

        // Sin pre-roll (0), la rama de preview es la de siempre.
        source.PreviewFramesToSkip = 0;
        Assert.DoesNotContain("select=", string.Join(' ', builder.BuildLive(recording: false, 640, 360)));
    }

    [Fact]
    public void BuildLive_Recording_Software_ConvertsRecordBranchToYuv420p()
        // El mismo format= explícito también para libx264 (yuv420p): no-op si ya coincide, robusto si no.
        => Assert.Contains("format=yuv420p[vmain]", BuildLive(SoftwareMp4(), recording: true));

    [Fact]
    public void BuildLive_PreviewOnly_NoAudioSource_KeepsPreviewButOmitsMeter()
    {
        var joined = BuildLive(SoftwareMp4(), recording: false, hasAudio: false);

        // El preview sigue funcionando (es la regresión que reportó el usuario con OBS solo-vídeo)…
        Assert.Contains("[0:v]scale=640:360,format=bgra[pv]", joined);
        Assert.Contains("-map [pv] -fps_mode passthrough -f rawvideo tcp://127.0.0.1:9001", joined);
        // …pero no se pide medición ni un output solo-audio sin streams (que abortaría todo FFmpeg).
        Assert.DoesNotContain("ebur128", joined);
        Assert.DoesNotContain("-f null", joined);
        Assert.DoesNotContain("0:a:0?", joined);
    }

    [Fact]
    public void BuildLive_Recording_NoAudioSource_RecordsVideoOnly()
    {
        var joined = BuildLive(SoftwareMp4(), recording: true, hasAudio: false);

        Assert.Contains("split=2[vrec][vprev]", joined);    // sigue bifurcando a preview + grabación
        Assert.Contains("-c:v libx264", joined);            // graba vídeo…
        Assert.Contains("-y", joined);
        Assert.DoesNotContain("-c:a", joined);              // …sin pista ni códec de audio
        Assert.DoesNotContain("0:a:0?", joined);
        Assert.DoesNotContain("ebur128", joined);
    }

    // --- Análisis de señal: detectores de negro/congelado/silencio (alarmas) ---

    [Fact]
    public void BuildLive_WithAnalysis_InsertsBlackFreezeAndSilenceDetectors()
    {
        var joined = BuildLive(SoftwareMp4(), recording: false, analyze: true);

        Assert.Contains("blackdetect", joined);                              // negro → rama de preview
        Assert.Contains("freezedetect", joined);                            // congelado → rama de preview
        Assert.Contains("silencedetect", joined);                           // silencio → rama de audio
        Assert.Contains("format=bgra[pv]", joined);                         // el preview sigue saliendo BGRA
    }

    // --- Segmentación: muxer segment, cada archivo completo es recuperable por separado ---

    private static RecordingProfile SegmentedMp4(int minutes)
    {
        var p = SoftwareMp4();
        p.Segmentation = new SegmentationPolicy { Trigger = SegmentTrigger.Duration, Duration = TimeSpan.FromMinutes(minutes) };
        return p;
    }

    [Fact]
    public void BuildLive_Recording_Segmented_UsesSegmentMuxerAndExposesGlob()
    {
        var b = NewLiveBuilder(SegmentedMp4(2));
        var joined = string.Join(' ', b.BuildLive(recording: true, 640, 360));

        Assert.Contains("-f segment", joined);
        Assert.Contains("-segment_time 120", joined);                       // 2 min → 120 s
        Assert.Contains("-reset_timestamps 1", joined);
        Assert.True(b.IsSegmentedOutput);
        Assert.Equal("TST_*.mp4", b.SegmentFileGlob);                       // glob para vigilar los segmentos
        Assert.Empty(b.OutputFilePath);                                     // no hay archivo único
        Assert.DoesNotContain("-movflags", joined);                        // los movflags del archivo único no aplican por segmento
    }

    [Fact]
    public void BuildLive_Recording_SegmentBySize_DerivesDurationFromBitrate()
    {
        var p = SoftwareMp4();                                              // 8 Mbps vídeo
        p.AudioCodec = AudioCodec.Aac; p.AudioBitrate = Bitrate.FromKbps(0); // aísla el cálculo al vídeo
        // 8 Mbit/s → 1 MByte/s; 50 MB ⇒ ~50 s por segmento.
        p.Segmentation = new SegmentationPolicy { Trigger = SegmentTrigger.Size, MaxBytes = 50L * 1_000_000 };

        var joined = string.Join(' ', NewLiveBuilder(p).BuildLive(recording: true, 640, 360));

        Assert.Contains("-f segment", joined);
        Assert.Contains("-segment_time 50", joined);
    }

    // --- Carta de ajuste (slate): barras + silencio generados, sin tocar el dispositivo ---

    [Fact]
    public void BuildSlate_Recording_GeneratesBarsAndSilenceKeepingPreviewAndFile()
    {
        var b = NewLiveBuilder(SoftwareMp4());
        var joined = string.Join(' ', b.BuildSlate(recording: true, 640, 360));

        Assert.Contains("smptebars", joined);                               // barras SMPTE…
        Assert.Contains("anullsrc", joined);                                // …y silencio
        Assert.Contains("drawtext", joined);                                // rótulo "SIN SEÑAL"
        Assert.Contains("-map [pv] -fps_mode passthrough -f rawvideo tcp://127.0.0.1:9001", joined); // el preview sigue
        Assert.Contains("-c:v libx264", joined);                            // graba el slate
        Assert.DoesNotContain("in.mp4", joined);                            // NO abre el dispositivo/fuente
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BuildSlate_GeneratesBarsAndSilenceInOneInputAtTheWallClockRate(bool recording)
    {
        // Sin -re, lavfi genera tan deprisa como codifica la máquina: la carta de ajuste escribía barras a ~14× el tiempo real
        // (medido con el FFmpeg empaquetado) y una caída de señal de minutos dejaba horas de barras en el disco. Y en UNA sola
        // entrada: con un generador por entrada, al cerrar la pieza el audio acababa 0,6 s antes que el vídeo.
        var args = NewLiveBuilder(SoftwareMp4()).BuildSlate(recording, 640, 360);
        var joined = string.Join(' ', args);

        Assert.Contains("-re -f lavfi -i smptebars=size=1920x1080:rate=25/1[out0];anullsrc=channel_layout=stereo:sample_rate=48000[out1]", joined);
        Assert.Equal(1, args.Count(a => a == "-i"));
        Assert.Equal(1, args.Count(a => a == "-re"));  // opción de ENTRADA: delante de una salida, FFmpeg aborta
        Assert.Contains("-map 0:a:0 -af ebur128=peak=true -f null -", joined); // el silencio sale de la misma entrada
    }

    // --- Nombre del archivo: manual (nombre del operador) y programada (dd-MM-yyyy_Título_N) ---

    [Fact]
    public void BuildLive_Recording_WithBaseName_NamesSingleFileByBase()
    {
        var b = NewLiveBuilder(SoftwareMp4()).WithBaseName("Partido");
        var joined = string.Join(' ', b.BuildLive(recording: true, 640, 360));

        Assert.EndsWith("Partido.mp4", b.OutputFilePath);   // usa el nombre dado…
        Assert.DoesNotContain("TST_", b.OutputFilePath);    // …sin el prefijo de canal ni la fecha
        Assert.Contains("Partido.mp4", joined);
    }

    [Fact]
    public void BuildLive_Recording_Segmented_WithBaseName_UsesUnderscoreCounterAndStartNumber()
    {
        var b = NewLiveBuilder(SegmentedMp4(2)).WithBaseName("21-06-2026_Noticias").WithSegmentStartNumber(3);
        var joined = string.Join(' ', b.BuildLive(recording: true, 640, 360));

        Assert.Contains("21-06-2026_Noticias_%d.mp4", joined);   // «_1, _2…» (1-based, sin relleno)
        Assert.Contains("-segment_start_number 3", joined);       // numeración CONTINUA entre reinicios/slate
        Assert.Equal("21-06-2026_Noticias_*.mp4", b.SegmentFileGlob);
        Assert.DoesNotContain("%03d", joined);                    // no el contador legado relleno
    }

    [Fact]
    public void BuildLive_Recording_Segmented_WithoutBaseName_KeepsLegacyPaddedCounter()
    {
        var joined = string.Join(' ', NewLiveBuilder(SegmentedMp4(2)).BuildLive(recording: true, 640, 360));

        Assert.Contains("_%03d.mp4", joined);                     // legado: {canal}_{fecha}_%03d
        Assert.DoesNotContain("-segment_start_number", joined);   // sin numeración forzada
    }

    // --- Seguridad: validación de URLs de streaming en el muxer tee (#56) ---

    private static RecordingProfile Mp4WithStream(string url)
    {
        var p = SoftwareMp4();
        p.StreamTargets.Add(new StreamTarget { Protocol = StreamProtocol.Rtmp, Url = url, Enabled = true });
        return p;
    }

    [Theory]
    [InlineData("rtmp://srv/app|[f=mpegts]udp://ajeno:1234")] // inyección de una rama tee adicional
    [InlineData("rtmp://srv[f=flv]/app")]                     // inyección de opciones por rama
    [InlineData("ftp://srv/app")]                             // esquema no permitido
    [InlineData("")]                                          // vacía
    public void Build_StreamTargetWithMaliciousUrl_Throws(string url)
        => Assert.Throws<ArgumentException>(() => Build(Mp4WithStream(url)));

    [Fact]
    public void Build_StreamTargetWithValidUrl_BuildsTeeBranch()
    {
        var (joined, _) = Build(Mp4WithStream("rtmp://live.example.com/app/key"));

        Assert.Contains("-f tee", joined);                                  // salida por muxer tee
        Assert.Contains("rtmp://live.example.com/app/key", joined);         // la URL legítima pasa intacta
    }

    // --- Modo de contenedor MP4: fMP4 fragmentado (default) vs MP4 estándar (moov al final) ---

    [Fact]
    public void BuildLive_StandardMp4_OmitsFragmentation()
    {
        // WithFragmentedMp4(false): MP4 estándar → SIN movflags de fragmentación (moov al final, seekable, sin remux).
        var b = NewLiveBuilder(SoftwareMp4()).WithFragmentedMp4(false);
        var joined = string.Join(' ', b.BuildLive(recording: true, 640, 360));

        Assert.DoesNotContain("frag_keyframe", joined);
        Assert.DoesNotContain("empty_moov", joined);
        Assert.Contains("-c:v libx264", joined);   // sigue grabando con normalidad
        Assert.Contains("-y", joined);
    }

    [Fact]
    public void BuildLive_FragmentedMp4_IsDefault()
    {
        // Por defecto (sin WithFragmentedMp4) se mantiene el fMP4 robusto ante corte.
        var joined = BuildLive(SoftwareMp4(), recording: true);
        Assert.Contains("+frag_keyframe+empty_moov+default_base_moof", joined);
    }

    // --- Audio multicanal: la fuente entrega 8/16 canales y se ELIGE el par con pan (sin mezclar) ---

    private static FakeCaptureSource MultichannelSource(int channels, string? pairs)
    {
        var source = new FakeCaptureSource("C:/clips/in.mp4") { AudioChannelCount = channels };
        if (pairs is not null) source.Definition.Parameters[AudioSelection.PairsKey] = pairs;
        source.Emit(new SignalInfo(SignalState.Locked, new Resolution(1920, 1080), new FrameRate(25, 1),
            AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null, AudioChannels: channels));
        return source;
    }

    private static string BuildLiveMultichannel(RecordingProfile profile, bool recording, int channels, string? pairs, bool analyze = false)
        => string.Join(' ', new FfmpegArgumentBuilder()
            .From(MultichannelSource(channels, pairs)).Using(profile).ForChannel("TST").ToDirectory("C:/out")
            .WithPreviewSink("tcp://127.0.0.1:9001").WithSignalAnalysis(analyze)
            .BuildLive(recording, 640, 360));

    [Fact]
    public void BuildLive_EightChannels_PairTwo_SelectsWithPanAndSplitsToMetersAndRecording()
    {
        var joined = BuildLiveMultichannel(SoftwareMp4(), recording: true, channels: 8, pairs: "2");

        // El par 3-4 (0-based c2/c3) va por pan a la grabación, dentro del grafo; los medidores miden los 8 canales.
        Assert.Contains("[0:a:0]asplit=2[m0][r1];[m0]ebur128=peak=true[amout];[r1]pan=stereo|c0=c2|c1=c3[arec1]", joined);
        Assert.Contains("-map [amout] -f null -", joined);
        Assert.Contains("-map [vmain] -map [arec1]", joined);
        Assert.DoesNotContain("-ac ", joined);       // el nº de canales lo fija pan: -ac volvería a mezclar
        Assert.DoesNotContain("0:a:0?", joined);     // no se mapea el audio crudo (8 canales) a ningún sitio
    }

    [Fact]
    public void BuildLive_EightChannels_DefaultPair_StillSelectsFirstPairWithPan()
        // Aunque sea el par 1-2, con 8 canales hay que elegir: -ac 2 mezclaría los ocho en el estéreo.
        => Assert.Contains("[r1]pan=stereo|c0=c0|c1=c1[arec1]", BuildLiveMultichannel(SoftwareMp4(), recording: true, channels: 8, pairs: null));

    // --- Modos de pistas (fase 3): un estéreo por par, o todos los canales en una pista ---

    [Fact]
    public void BuildLive_PairsAsTracks_OneStereoStreamPerPairWithTitles()
    {
        var profile = SoftwareMp4();
        profile.AudioTracks = AudioTrackMode.PairsAsTracks;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "1,3");

        Assert.Contains("[0:a:0]asplit=3[m0][r1][r2];[m0]ebur128=peak=true[amout]", joined); // medidores: todos los canales
        Assert.Contains("[r1]pan=stereo|c0=c0|c1=c1[arec1];[r2]pan=stereo|c0=c4|c1=c5[arec2]", joined);
        Assert.Contains("-map [vmain] -map [arec1] -map [arec2]", joined);
        Assert.Contains("-metadata:s:a:0 title=Canales 1-2", joined);
        Assert.Contains("-metadata:s:a:1 title=Canales 5-6", joined);
        Assert.Contains("-metadata:s:a:1 handler_name=Canales 5-6", joined); // MP4: el nombre que ffprobe ve (ver la teoría de abajo)
        Assert.DoesNotContain("-ac ", joined);
    }

    [Theory]
    // Medido con FFmpeg 8.0: en MP4/MOV el title acaba en un átomo udta/name que ffprobe no lee y que el remux a faststart
    // del Detener pierde; ffprobe solo veía «SoundHandler». El handler_name sí queda, también tras el remux y en segmentos.
    // MKV y AVI guardan el title (MKV repetiría el handler_name como etiqueta HANDLER_NAME); MXF y TS no guardan ninguno.
    [InlineData(ContainerFormat.Mp4, true)]
    [InlineData(ContainerFormat.Mov, true)]
    [InlineData(ContainerFormat.Mkv, false)]
    [InlineData(ContainerFormat.Avi, false)]
    [InlineData(ContainerFormat.Mxf, false)]
    [InlineData(ContainerFormat.Ts, false)]
    public void BuildLive_PairsAsTracks_InMp4AndMov_NamesEachTrackAlsoAsItsHandlerName(ContainerFormat container, bool handlerName)
    {
        var profile = SoftwareMp4();
        profile.Container = container;
        profile.AudioTracks = AudioTrackMode.PairsAsTracks;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "1,3");

        if (handlerName)
            Assert.Contains("-metadata:s:a:0 title=Canales 1-2 -metadata:s:a:0 handler_name=Canales 1-2 " +
                            "-metadata:s:a:1 title=Canales 5-6 -metadata:s:a:1 handler_name=Canales 5-6", joined);
        else
        {
            Assert.Contains("-metadata:s:a:0 title=Canales 1-2 -metadata:s:a:1 title=Canales 5-6", joined);
            Assert.DoesNotContain("handler_name", joined);
        }
    }

    [Fact]
    public void BuildLive_Multichannel_Pcm_KeepsAllChosenChannelsInOneTrack()
    {
        // PCM de verdad solo queda en MXF/MKV (en MP4/MOV/TS la app lo promueve a AAC): ahí cualquier recuento vale.
        var profile = DnxhrMov(EncoderProfile.DnxHrHq);
        profile.Container = ContainerFormat.Mxf;
        profile.AudioTracks = AudioTrackMode.Multichannel;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 16, pairs: "all");

        Assert.Contains("[r1]pan=16c|c0=c0|c1=c1|c2=c2|c3=c3|c4=c4|c5=c5|c6=c6|c7=c7|c8=c8|c9=c9|c10=c10|c11=c11|c12=c12|c13=c13|c14=c14|c15=c15[arec1]", joined);
        Assert.Contains("-c:a pcm_s24le", joined);
        Assert.DoesNotContain("-ac ", joined);
    }

    [Fact]
    public void BuildLive_Multichannel_LossyCodec_StoresOneStereoTrackPerPair_NeverASurroundTrack()
    {
        // Auditoría 2026-09-18 (medido con el FFmpeg empaquetado): una pista 5.1/7.1 en AAC codifica el canal 4 como
        // LFE y lo deja sin banda (un tono de 1,2 kHz salía a −89 dB). Con pérdida, «multicanal» = una pista por par.
        var profile = SoftwareMp4();                          // AAC en MP4
        profile.AudioTracks = AudioTrackMode.Multichannel;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "all");

        Assert.Contains("[0:a:0]asplit=5[m0][r1][r2][r3][r4]", joined);
        Assert.Contains("[r1]pan=stereo|c0=c0|c1=c1[arec1];[r2]pan=stereo|c0=c2|c1=c3[arec2];[r3]pan=stereo|c0=c4|c1=c5[arec3];[r4]pan=stereo|c0=c6|c1=c7[arec4]", joined);
        Assert.Contains("-map [vmain] -map [arec1] -map [arec2] -map [arec3] -map [arec4]", joined);
        Assert.DoesNotContain("pan=7.1", joined);
        Assert.DoesNotContain("pan=5.1", joined);
    }

    [Fact]
    public void BuildLive_Multichannel_Mp2_NeverHandsTheEncoderMoreThanStereo()
    {
        // MP2/MP3 solo admiten estéreo y FFmpeg NO falla con más: remuestrea y MEZCLA los ocho canales en silencio.
        var profile = SoftwareMp4();
        profile.Container = ContainerFormat.Ts;
        profile.AudioCodec = AudioCodec.Mp2;
        profile.AudioTracks = AudioTrackMode.Multichannel;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "1,3");

        Assert.Contains("[r1]pan=stereo|c0=c0|c1=c1[arec1];[r2]pan=stereo|c0=c4|c1=c5[arec2]", joined);
        Assert.DoesNotContain("pan=quad", joined);
    }

    [Fact]
    public void BuildLive_Single_SurroundLayoutTheCodecCannotCarry_FallsBackToTheFirstPair()
    {
        var profile = SoftwareMp4();
        profile.Container = ContainerFormat.Ts;
        profile.AudioCodec = AudioCodec.Mp2;                  // 2 canales como mucho
        profile.AudioLayout = AudioLayout.Surround51;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "all");

        Assert.Contains("[r1]pan=stereo|c0=c0|c1=c1[arec1]", joined);
        Assert.DoesNotContain("pan=5.1", joined);
    }

    [Fact]
    public void BuildLive_PairsAsTracks_IntoWav_BecomesOnePcmTrackWithEveryChosenChannel()
    {
        // WAV solo admite un flujo de audio: con dos pistas el muxer aborta y no se graba nada. Al ser PCM, los canales
        // elegidos van todos en UNA pista, sin mezclar ni perder ninguno.
        var profile = SoftwareMp4();
        profile.AudioOnly = true;
        profile.Container = ContainerFormat.Wav;
        profile.AudioCodec = AudioCodec.Pcm;
        profile.AudioTracks = AudioTrackMode.PairsAsTracks;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "1,3");

        Assert.Contains("[0:a:0]asplit=2[m0][r1]", joined);
        Assert.Contains("[r1]pan=4c|c0=c0|c1=c1|c2=c4|c3=c5[arec1]", joined);
        Assert.DoesNotContain("[arec2]", joined);
    }

    [Fact]
    public void BuildLive_PairsAsTracks_IntoMp3_KeepsOnlyTheFirstPair()
    {
        var profile = SoftwareMp4();
        profile.AudioOnly = true;
        profile.Container = ContainerFormat.Mp3Audio;         // un solo flujo y, además, solo estéreo
        profile.AudioCodec = AudioCodec.Mp3;
        profile.AudioTracks = AudioTrackMode.PairsAsTracks;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "2,3");

        Assert.Contains("[r1]pan=stereo|c0=c2|c1=c3[arec1]", joined);
        Assert.DoesNotContain("[arec2]", joined);
    }

    [Fact]
    public void BuildLive_PairsAsTracks_TitlesFollowTheApplicationLanguage_AndALoneChannelIsSingular()
    {
        var profile = SoftwareMp4();
        profile.AudioTracks = AudioTrackMode.PairsAsTracks;
        var previous = Localizer.Language;
        try
        {
            Localizer.Language = AppLanguage.English;
            var english = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "1,3");
            Assert.Contains("-metadata:s:a:1 title=Channels 5-6", english);
            Assert.Contains("-metadata:s:a:1 handler_name=Channels 5-6", english);

            // Fuente con recuento impar (NDI de 3 canales): el canal suelto va duplicado en L/R y se titula en singular.
            Localizer.Language = AppLanguage.Spanish;
            var odd = BuildLiveMultichannel(profile, recording: true, channels: 3, pairs: "all");
            Assert.Contains("[r2]pan=stereo|c0=c2|c1=c2[arec2]", odd);
            Assert.Contains("-metadata:s:a:0 title=Canales 1-2", odd);
            Assert.Contains("-metadata:s:a:1 title=Canal 3", odd);
            Assert.Contains("-metadata:s:a:1 handler_name=Canal 3", odd);
        }
        finally { Localizer.Language = previous; }
    }

    // --- Single + estéreo con varios pares elegidos: una pista estéreo no puede llevar más de un par (2026-09-22) ---

    [Fact]
    public void BuildLive_Single_Stereo_WithAllPairsChosen_StoresOneStereoTrackPerPair()
    {
        // Todo preset trae Single + estéreo de fábrica; la entrada dice «Todos los pares». Antes se guardaba SOLO el par
        // 1-2, en silencio, mientras los medidores marcaban los ocho pares como grabados: el operador lo vio en el archivo.
        var joined = BuildLiveMultichannel(SoftwareMp4(), recording: true, channels: 16, pairs: "all");

        Assert.Contains("[0:a:0]asplit=9[m0][r1][r2][r3][r4][r5][r6][r7][r8];[m0]ebur128=peak=true[amout]", joined);
        Assert.Contains("[r1]pan=stereo|c0=c0|c1=c1[arec1]", joined);
        Assert.Contains("[r8]pan=stereo|c0=c14|c1=c15[arec8]", joined);
        Assert.Contains("-map [vmain] -map [arec1] -map [arec2] -map [arec3] -map [arec4] -map [arec5] -map [arec6] -map [arec7] -map [arec8]", joined);
        Assert.Contains("-metadata:s:a:0 title=Canales 1-2", joined);
        Assert.Contains("-metadata:s:a:7 title=Canales 15-16", joined);
        Assert.Contains("-metadata:s:a:7 handler_name=Canales 15-16", joined);
        Assert.DoesNotContain("-ac ", joined);
    }

    [Fact]
    public void BuildLive_Single_Stereo_WithTwoPairsChosen_StoresBoth()
        => Assert.Contains("[r1]pan=stereo|c0=c0|c1=c1[arec1];[r2]pan=stereo|c0=c4|c1=c5[arec2]",
            BuildLiveMultichannel(SoftwareMp4(), recording: true, channels: 8, pairs: "1,3"));

    [Fact]
    public void BuildLive_Single_Surround_WithAllPairsChosen_IsStillOneSurroundTrack()
    {
        // 5.1 sí es una decisión del preset (un 5.1 embebido en los canales 1-6): se respeta, con los seis primeros.
        var profile = SoftwareMp4();
        profile.AudioLayout = AudioLayout.Surround51;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "all");

        Assert.Contains("[0:a:0]asplit=2[m0][r1]", joined);
        Assert.Contains("[r1]pan=5.1|c0=c0|c1=c1|c2=c2|c3=c3|c4=c4|c5=c5[arec1]", joined);
        Assert.DoesNotContain("[arec2]", joined);
    }

    [Fact]
    public void BuildLive_Single_Mono_WithAllPairsChosen_IsStillOneMonoTrack()
    {
        var profile = SoftwareMp4();
        profile.AudioLayout = AudioLayout.Mono;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "all");

        Assert.Contains("[r1]pan=mono|c0=0.5*c0+0.5*c1[arec1]", joined);
        Assert.DoesNotContain("[arec2]", joined);
    }

    [Fact]
    public void BuildLive_Single_Stereo_WithAllPairsChosen_IntoMp3_StillKeepsOnlyTheFirstPair()
    {
        // MP3 solo admite un flujo estéreo: la promoción a «una pista por par» no puede saltarse el contenedor.
        var profile = SoftwareMp4();
        profile.AudioOnly = true;
        profile.Container = ContainerFormat.Mp3Audio;
        profile.AudioCodec = AudioCodec.Mp3;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 8, pairs: "all");

        Assert.Contains("[r1]pan=stereo|c0=c0|c1=c1[arec1]", joined);
        Assert.DoesNotContain("[arec2]", joined);
    }

    [Fact]
    public void AudioTrackPlan_TellsWhichPairsReachTheFile_AndHowManyTracks()
    {
        // Single + estéreo + todos los pares: una pista por par, y todos los pares van al archivo.
        var all = AudioTrackPlan.For(MultichannelSource(16, "all"), SoftwareMp4())!;
        Assert.Equal(Enumerable.Range(1, 8), all.Pairs);
        Assert.Equal(8, all.Tracks);
        Assert.Equal("8 pistas", all.Label);

        // Single + 5.1: al archivo van SOLO los tres primeros pares aunque la entrada eligiera todos (esto es lo que la
        // UI/API tienen que marcar; antes marcaban los ocho).
        var surround = SoftwareMp4();
        surround.AudioLayout = AudioLayout.Surround51;
        var plan51 = AudioTrackPlan.For(MultichannelSource(16, "all"), surround)!;
        Assert.Equal(new[] { 1, 2, 3 }, plan51.Pairs);
        Assert.Equal(1, plan51.Tracks);
        Assert.Equal("1 pista 5.1", plan51.Label);

        // Multichannel en MXF (PCM): todos en una sola pista sin nombre.
        var mxf = SoftwareMp4();
        mxf.Container = ContainerFormat.Mxf;
        mxf.AudioCodec = AudioCodec.Pcm;
        mxf.AudioTracks = AudioTrackMode.Multichannel;
        var multi = AudioTrackPlan.For(MultichannelSource(16, "all"), mxf)!;
        Assert.Equal(Enumerable.Range(1, 8), multi.Pairs);
        Assert.Equal("1 pista · 16 canales", multi.Label);

        // Un solo par elegido: una pista estéreo con ese par.
        var one = AudioTrackPlan.For(MultichannelSource(8, "3"), SoftwareMp4())!;
        Assert.Equal(new[] { 3 }, one.Pairs);
        Assert.Equal("1 pista estéreo", one.Label);

        // PairsAsTracks a MP3: solo el primer par elegido.
        var mp3 = SoftwareMp4();
        mp3.AudioOnly = true;
        mp3.Container = ContainerFormat.Mp3Audio;
        mp3.AudioCodec = AudioCodec.Mp3;
        mp3.AudioTracks = AudioTrackMode.PairsAsTracks;
        Assert.Equal(new[] { 2 }, AudioTrackPlan.For(MultichannelSource(8, "2,3"), mp3)!.Pairs);

        // En inglés, en inglés. Fuente estéreo: nada que contar.
        Localizer.Language = AppLanguage.English;
        Assert.Equal("8 tracks", AudioTrackPlan.For(MultichannelSource(16, "all"), SoftwareMp4())!.Label);
        Assert.Null(AudioTrackPlan.For(MultichannelSource(2, null), SoftwareMp4()));
    }

    [Theory]
    [InlineData(4, "pan=4c|c0=c0|c1=c1|c2=c4|c3=c5")]
    [InlineData(2, "pan=stereo|c0=c0|c1=c1")]
    [InlineData(1, "pan=mono|c0=c0")]
    public void MultichannelPan_IsAnUnnamedPcmLayoutSoNoChannelIsTreatedAsLfe(int count, string expected)
    {
        var channels = new[] { 0, 1, 4, 5 }.Take(count).ToArray();
        Assert.Equal(expected, FfmpegArgumentBuilder.MultichannelPan(channels));
    }

    [Fact]
    public void Build_Legacy_ProxyOfAMultichannelSource_CarriesTheFirstChosenPair()
    {
        // El proxy mapeaba el audio crudo: 8 canales en un AAC de 128 k, y con 16 el codificador ni abre.
        var profile = SoftwareMp4();
        profile.Proxy = new ProxyProfile();
        var joined = string.Join(' ', new FfmpegArgumentBuilder()
            .From(MultichannelSource(16, pairs: "2")).Using(profile).ForChannel("TST").ToDirectory("C:/out")
            .ProxyToDirectory("C:/proxy").Build());

        Assert.Contains("-map [proxout] -map 0:a? -af pan=stereo|c0=c2|c1=c3 -c:v", joined);
    }

    [Fact]
    public void BuildSlate_Multichannel_ProducesTheSameTracksAsTheLiveRecording()
    {
        var profile = SoftwareMp4();
        profile.AudioTracks = AudioTrackMode.PairsAsTracks;
        var joined = string.Join(' ', new FfmpegArgumentBuilder()
            .From(MultichannelSource(8, pairs: "1,3")).Using(profile).ForChannel("TST").ToDirectory("C:/out")
            .WithPreviewSink("tcp://127.0.0.1:9001").BuildSlate(recording: true, 640, 360));

        Assert.Contains("anullsrc=channel_layout=8c", joined);                        // silencio con los canales de la fuente
        Assert.Contains("[0:a]asplit=3[m0][r1][r2];[m0]ebur128=peak=true[amout]", joined);
        Assert.Contains("[r1]pan=stereo|c0=c0|c1=c1[aslate1];[r2]pan=stereo|c0=c4|c1=c5[aslate2]", joined);
        Assert.Contains("-map [vrec] -map [aslate1] -map [aslate2]", joined);          // mismas pistas que en vivo
        Assert.Contains("-metadata:s:a:1 title=Canales 5-6 -metadata:s:a:1 handler_name=Canales 5-6", joined);
        Assert.DoesNotContain("-ac ", joined);
    }

    [Fact]
    public void BuildSlate_StereoSource_IsUnchanged()
    {
        var joined = string.Join(' ', NewLiveBuilder(SoftwareMp4()).BuildSlate(recording: true, 640, 360));

        Assert.Contains("anullsrc=channel_layout=stereo", joined);
        Assert.Contains("-map 0:a:0 -af ebur128=peak=true -f null -", joined);
        Assert.Contains("-ac 2", joined);
    }

    [Fact]
    public void BuildLive_TwoChannels_KeepsThePipelineAsAlways()
    {
        // Con una fuente estéreo no hay nada que elegir: misma tubería de siempre (y un par 2 pedido se ignora).
        var joined = BuildLiveMultichannel(SoftwareMp4(), recording: true, channels: 2, pairs: "2");

        Assert.Contains("-map 0:a:0?", joined);
        Assert.Contains("-ac 2", joined);
        Assert.DoesNotContain("pan=", joined);
        Assert.DoesNotContain("asplit", joined);
    }

    [Fact]
    public void BuildLive_PreviewOnly_EightChannels_MetersInsideTheGraph()
    {
        var joined = BuildLiveMultichannel(SoftwareMp4(), recording: false, channels: 8, pairs: "2", analyze: true);

        // ebur128 ANTES del pan (un true-peak por cada uno de los 8 canales → un medidor por par) y silencedetect
        // DESPUÉS (vigila el par que se graba, no el resto del SDI).
        Assert.Contains("[0:a:0]ebur128=peak=true,pan=stereo|c0=c2|c1=c3,silencedetect=n=-50dB:d=2[amout]", joined);
        Assert.Contains("-map [amout] -f null -", joined);
        Assert.DoesNotContain("-af ", joined);      // un stream que sale del grafo no admite -af aparte
        Assert.DoesNotContain("asplit", joined);    // sin grabación no hay que repartir
    }

    [Fact]
    public void BuildLive_Recording_EightChannels_WithAnalysis_MetersAllChannelsAndWatchesSilenceOnTheRecordedPair()
    {
        var joined = BuildLiveMultichannel(SoftwareMp4(), recording: true, channels: 8, pairs: "2", analyze: true);

        Assert.Contains("[m0]ebur128=peak=true,pan=stereo|c0=c2|c1=c3,silencedetect=n=-50dB:d=2[amout]", joined);
        Assert.Contains("[r1]pan=stereo|c0=c2|c1=c3[arec1]", joined);
    }

    [Fact]
    public void BuildLive_PreviewOnly_EightChannels_WithoutAnalysis_MetersTheRawStream()
        // Sin análisis no hace falta elegir nada para medir: ebur128 sobre los 8 canales y a null.
        => Assert.Contains("[0:a:0]ebur128=peak=true[amout]", BuildLiveMultichannel(SoftwareMp4(), recording: false, channels: 8, pairs: "2"));

    [Theory]
    [InlineData(AudioLayout.Mono, "pan=mono|c0=0.5*c2+0.5*c3")]
    [InlineData(AudioLayout.Stereo, "pan=stereo|c0=c2|c1=c3")]
    [InlineData(AudioLayout.Surround51, "pan=5.1|c0=c2|c1=c3")]   // solo hay dos canales elegidos: el resto en silencio
    public void PanFilter_PlacesTheChosenPairInEachLayout(AudioLayout layout, string expected)
        => Assert.Equal(expected, FfmpegArgumentBuilder.PanFilter(layout, new[] { 2, 3 }));

    [Fact]
    public void PanFilter_Surround_TakesChannelsInOrder()
    {
        Assert.Equal("pan=5.1|c0=c0|c1=c1|c2=c2|c3=c3|c4=c4|c5=c5", FfmpegArgumentBuilder.PanFilter(AudioLayout.Surround51, Enumerable.Range(0, 8).ToArray()));
        Assert.Equal("pan=7.1|c0=c0|c1=c1|c2=c2|c3=c3|c4=c4|c5=c5|c6=c6|c7=c7", FfmpegArgumentBuilder.PanFilter(AudioLayout.Surround71, Enumerable.Range(0, 16).ToArray()));
    }

    [Fact]
    public void Build_Legacy_EightChannels_SelectsWithAfPanBeforeMeters()
    {
        var builder = new FfmpegArgumentBuilder()
            .From(MultichannelSource(8, pairs: "3")).Using(SoftwareMp4()).ForChannel("TST").ToDirectory("C:/out");

        var joined = string.Join(' ', builder.Build());

        Assert.Contains("-af pan=stereo|c0=c4|c1=c5,ebur128=peak=true", joined);
        Assert.DoesNotContain("-ac ", joined);
    }

    // --- Contenedores que no guardan la reordenación del códec (AVI; MXF con H.264): el vídeo a la hora del audio ---

    private static RecordingProfile Coded(VideoCodec codec, ContainerFormat container, ScanType scan = ScanType.Progressive) => new()
    {
        Name = "c", VideoCodec = codec, HwAccel = HwAccel.None, VideoBitrate = Bitrate.FromMbps(20), GopSize = 50,
        TargetResolution = new Resolution(1920, 1080), OutputFrameRate = FrameRate.P25, ScanType = scan,
        AudioCodec = AudioCodec.Pcm, AudioLayout = AudioLayout.Stereo, Container = container,
    };

    /// <summary>Las tres construcciones que graban: el proceso del canal, la carta de ajuste y el archivo único.</summary>
    private static IEnumerable<string[]> RecordingCommands(RecordingProfile profile) => new[]
    {
        NewLiveBuilder(profile).BuildLive(recording: true, 640, 360).ToArray(),
        NewLiveBuilder(profile).BuildSlate(recording: true, 640, 360).ToArray(),
        new FfmpegArgumentBuilder().From(new FakeCaptureSource("C:/clips/in.mp4")).Using(profile).ForChannel("TST").ToDirectory("C:/out")
            .Build().ToArray(),
    };

    /// <summary>Veces que aparecen seguidos, en este orden, los tokens de <paramref name="sequence"/>.</summary>
    private static int Occurrences(string[] args, params string[] sequence)
        => Enumerable.Range(0, Math.Max(0, args.Length - sequence.Length + 1)).Count(i => args.Skip(i).Take(sequence.Length).SequenceEqual(sequence));

    [Fact]
    public void CatalogAviPresets_RecordWithoutBFrames()
    {
        // AVI no guarda cuándo se presenta cada cuadro: con los B-frames de x264 el vídeo quedaba 2 cuadros (80 ms a 25 fps) por
        // detrás del audio (barrido NDI del 2026-10-18, y lo mismo con una fuente lavfi). En el canal, en la carta de ajuste y en
        // el archivo único; el entrelazado conserva sus banderas.
        var avi = Baioss.Record.Application.Presets.PresetCatalog.CreateBuiltIns().Where(p => p.Container == ContainerFormat.Avi).ToList();
        Assert.Equal(new[] { "AVI · H.264 1080p25", "AVI · H.264 1080i25" }, avi.Select(p => p.Name));
        foreach (var preset in avi)
        {
            var profile = preset.ToProfile();
            string flags = profile.ScanType is ScanType.Progressive ? "" : "-flags +ilme+ildct ";
            foreach (var args in RecordingCommands(profile))
            {
                Assert.Contains($"-c:v libx264 -preset veryfast -b:v 20000000 -maxrate 20000000 -bufsize 50000000 -g 50 {flags}-bf 0 -pix_fmt yuv420p",
                    string.Join(' ', args));
                Assert.Equal(1, Occurrences(args, "-bf"));
            }
        }
    }

    [Theory]
    [InlineData(VideoCodec.H264x264, ContainerFormat.Avi)]
    [InlineData(VideoCodec.H265x265, ContainerFormat.Avi)]
    [InlineData(VideoCodec.H264Qsv, ContainerFormat.Avi)]
    [InlineData(VideoCodec.HevcQsv, ContainerFormat.Avi)]
    [InlineData(VideoCodec.H264Amf, ContainerFormat.Avi)]
    [InlineData(VideoCodec.H264x264, ContainerFormat.Mxf)] // el MXF de FFmpeg solo guarda la reordenación de MPEG-2
    [InlineData(VideoCodec.H264Qsv, ContainerFormat.Mxf)]
    [InlineData(VideoCodec.H264Amf, ContainerFormat.Mxf)]
    public void ACodecWithBFrames_GoesWithoutThem_WhereTheContainerCannotKeepTheirOrder(VideoCodec codec, ContainerFormat container)
    {
        // Medido con FFmpeg 8.0 y una fuente con un destello y un pitido por segundo: x264 2 cuadros tarde, QuickSync 1.
        foreach (var args in RecordingCommands(Coded(codec, container)))
        {
            Assert.Equal(1, Occurrences(args, "-bf", "0"));
            Assert.Equal(1, Occurrences(args, "-bf"));
        }
    }

    [Theory]
    [InlineData(ContainerFormat.Mp4)]
    [InlineData(ContainerFormat.Mov)]
    [InlineData(ContainerFormat.Mkv)]
    [InlineData(ContainerFormat.Ts)]
    [InlineData(ContainerFormat.ProgramStream)]
    public void ACodecWithBFrames_KeepsThem_WhereTheContainerStoresTheirOrder(ContainerFormat container)
    {
        // Ahí la marca de presentación de cada cuadro viaja con él: los B-frames no desplazan el vídeo y se quedan (comprimen más).
        foreach (var codec in new[] { VideoCodec.H264x264, VideoCodec.H265x265, VideoCodec.H264Qsv, VideoCodec.HevcQsv, VideoCodec.H264Amf })
            foreach (var args in RecordingCommands(Coded(codec, container)))
            {
                Assert.Equal(0, Occurrences(args, "-bf"));
                Assert.Equal(0, Occurrences(args, "-bsf:v"));
            }
    }

    [Fact]
    public void H264Nvenc_InAvi_DropsTheTimingSeiThatDelaysEveryFrame()
    {
        // NVENC ya va sin B-frames, pero su SEI de temporización presenta cada cuadro uno después (dpb_output_delay): en AVI, sin
        // otra marca, FFmpeg lo aplica y el vídeo quedaba un cuadro por detrás del audio. Solo en AVI (en MP4 o MXF manda la marca
        // del contenedor) y solo en H.264: en HEVC el tipo de unidad 6 es un corte de imagen.
        foreach (var args in RecordingCommands(Coded(VideoCodec.H264Nvenc, ContainerFormat.Avi)))
        {
            Assert.Equal(1, Occurrences(args, "-bsf:v", "filter_units=remove_types=6"));
            Assert.Equal(1, Occurrences(args, "-bf", "0"));
            Assert.Equal(1, Occurrences(args, "-bf"));
        }
        foreach (var (codec, container) in new[] { (VideoCodec.H264Nvenc, ContainerFormat.Mp4), (VideoCodec.H264Nvenc, ContainerFormat.Mxf),
                                                   (VideoCodec.HevcNvenc, ContainerFormat.Avi) })
            foreach (var args in RecordingCommands(Coded(codec, container)))
                Assert.Equal(0, Occurrences(args, "-bsf:v"));
    }

    [Theory]
    [InlineData(ScanType.Progressive, "+low_delay")]
    [InlineData(ScanType.InterlacedTff, "+ilme+ildct+low_delay")]
    public void Mpeg2_InAvi_PromisesNoBFrames_SoTheDecoderHoldsNoPicture(ScanType scan, string aviFlags)
    {
        // El MPEG-2 de FFmpeg no usa B-frames, pero sin low_delay la secuencia no lo promete y el decodificador retiene una imagen:
        // en AVI el vídeo quedaba un cuadro por detrás del audio. Con marcas (TS, PS, MXF) ese retén no se nota: como siempre.
        foreach (var args in RecordingCommands(Coded(VideoCodec.Mpeg2Video, ContainerFormat.Avi, scan)))
        {
            Assert.Equal(1, Occurrences(args, "-flags", aviFlags));
            Assert.Equal(1, Occurrences(args, "-flags"));
            Assert.Equal(0, Occurrences(args, "-bf"));
        }
        foreach (var container in new[] { ContainerFormat.Ts, ContainerFormat.ProgramStream, ContainerFormat.Mxf })
            foreach (var args in RecordingCommands(Coded(VideoCodec.Mpeg2Video, container, scan)))
            {
                Assert.DoesNotContain(args, a => a.Contains("low_delay", StringComparison.Ordinal));
                Assert.Equal(scan is ScanType.Progressive ? 0 : 1, Occurrences(args, "-flags", "+ilme+ildct"));
            }
    }
}
