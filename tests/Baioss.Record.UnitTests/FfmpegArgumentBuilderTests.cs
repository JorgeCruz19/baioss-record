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
        Assert.Contains("-movflags +frag_keyframe+empty_moov+default_base_moof", joined); // fMP4: archivo siempre reproducible
        Assert.Contains("-y", joined);                                      // archivo de salida
        Assert.Contains("TST_", joined);                                    // nombre de la grabación
        Assert.Contains("ebur128=peak=true", joined);
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
        Assert.DoesNotContain("-ac ", joined);
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
    public void BuildLive_Multichannel_Aac_CapsToTheLargestStandardLayout()
    {
        var profile = SoftwareMp4();                          // AAC en MP4: máximo 7.1
        profile.AudioTracks = AudioTrackMode.Multichannel;

        var joined = BuildLiveMultichannel(profile, recording: true, channels: 16, pairs: "all");

        Assert.Contains("[r1]pan=7.1|c0=c0|c1=c1|c2=c2|c3=c3|c4=c4|c5=c5|c6=c6|c7=c7[arec1]", joined);
        Assert.DoesNotContain("c8=c8", joined);               // los que no caben no se graban
    }

    [Theory]
    [InlineData(4, true, "pan=quad|c0=c0|c1=c1|c2=c4|c3=c5")]
    [InlineData(4, false, "pan=4c|c0=c0|c1=c1|c2=c4|c3=c5")]
    [InlineData(2, false, "pan=stereo|c0=c0|c1=c1")]
    public void MultichannelPan_ChoosesLayoutByCodecAndCount(int count, bool lossy, string expected)
    {
        var channels = new[] { 0, 1, 4, 5 }.Take(count).ToArray();
        Assert.Equal(expected, FfmpegArgumentBuilder.MultichannelPan(channels, lossy));
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
        Assert.Contains("[1:a]asplit=3[m0][r1][r2];[m0]ebur128=peak=true[amout]", joined);
        Assert.Contains("[r1]pan=stereo|c0=c0|c1=c1[aslate1];[r2]pan=stereo|c0=c4|c1=c5[aslate2]", joined);
        Assert.Contains("-map [vrec] -map [aslate1] -map [aslate2]", joined);          // mismas pistas que en vivo
        Assert.Contains("-metadata:s:a:1 title=Canales 5-6", joined);
        Assert.DoesNotContain("-ac ", joined);
    }

    [Fact]
    public void BuildSlate_StereoSource_IsUnchanged()
    {
        var joined = string.Join(' ', NewLiveBuilder(SoftwareMp4()).BuildSlate(recording: true, 640, 360));

        Assert.Contains("anullsrc=channel_layout=stereo", joined);
        Assert.Contains("-map 1:a:0 -af ebur128=peak=true -f null -", joined);
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
}
