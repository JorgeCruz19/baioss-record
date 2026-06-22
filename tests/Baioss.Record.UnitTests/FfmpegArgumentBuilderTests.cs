using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.UnitTests.Fakes;
using Xunit;

namespace Baioss.Record.UnitTests;

public class FfmpegArgumentBuilderTests
{
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
        Assert.Contains("-movflags +faststart", joined);        // MP4
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

        Assert.Contains("-c:v prores_ks", joined);
        Assert.Contains("-profile:v 4", joined);                // 4 = ProRes 4444
        Assert.Contains("-pix_fmt yuv444p10le", joined);        // 4:4:4 obligatorio para 4444
        Assert.EndsWith(".mov", outFile);
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
        Assert.Contains("-map [pv] -f rawvideo tcp://127.0.0.1:9001", joined);
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
        Assert.Contains("-map [pv] -f rawvideo tcp://127.0.0.1:9001", joined);
        Assert.Contains("-c:v libx264", joined);                            // …y grabación a la vez
        Assert.Contains("-movflags +faststart", joined);
        Assert.Contains("-y", joined);                                      // archivo de salida
        Assert.Contains("TST_", joined);                                    // nombre de la grabación
        Assert.Contains("ebur128=peak=true", joined);
    }

    [Fact]
    public void BuildLive_PreviewOnly_NoAudioSource_KeepsPreviewButOmitsMeter()
    {
        var joined = BuildLive(SoftwareMp4(), recording: false, hasAudio: false);

        // El preview sigue funcionando (es la regresión que reportó el usuario con OBS solo-vídeo)…
        Assert.Contains("[0:v]scale=640:360,format=bgra[pv]", joined);
        Assert.Contains("-map [pv] -f rawvideo tcp://127.0.0.1:9001", joined);
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
        Assert.DoesNotContain("-movflags +faststart", joined);             // faststart no aplica por segmento
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
        Assert.Contains("-map [pv] -f rawvideo tcp://127.0.0.1:9001", joined); // el preview sigue
        Assert.Contains("-c:v libx264", joined);                            // graba el slate
        Assert.DoesNotContain("in.mp4", joined);                            // NO abre el dispositivo/fuente
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
}
