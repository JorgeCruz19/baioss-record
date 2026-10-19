using Baioss.Record.Application.Channels;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Por qué un proceso de grabación no puede producir el archivo, leído de su stderr. Las secuencias son las REALES del argv del
/// producto (preview rawvideo + medidores + grabación) con FFmpeg 8.0 y con la build incluida (git-2026-06-14), que escriben lo mismo;
/// medidas el 2026-10-18. Antes, el «Could not open encoder before EOF» del rawvideo del preview convertía un MXF que no admite AAC en
/// «el codificador de vídeo no abre».
/// </summary>
public class RecordingFailureDetectorTests
{
    private static RecordingProfile Profile(VideoCodec video, ContainerFormat container, AudioCodec audio) => new()
    {
        Name = "prueba", VideoCodec = video, HwAccel = HwAccel.None, Container = container, AudioCodec = audio,
    };

    private static List<RecordingStderrVerdict> FeedAll(RecordingFailureDetector detector, IEnumerable<string> lines)
        => lines.Select(detector.Feed).ToList();

    // Ruido que el canal produce sin parar y que, desde otro hilo, puede colarse en cualquier sitio.
    private const string Meter = "[Parsed_ebur128_1 @ 000001895840ab40] t: 0.399979   TARGET:-23 LUFS    M: -14.6 S:-120.7     I: -14.6 LUFS       LRA:   0.0 LU  FTPK: -3.0 -3.0 dBFS  TPK: -3.0 -3.0 dBFS";
    private const string Deprecated = "[out1 @ 000002abad3f0c80] The \"all_channel_counts\" option is deprecated: accept all channel counts";
    private const string Resumed = "[vist#0:0/wrapped_avframe @ 000002abad3dd700] Resumed reading at pts 0.000 with rate 1.050 after a lag of 0.500s";

    public static TheoryData<VideoCodec, ContainerFormat, AudioCodec, string[], string> Refusals => new()
    {
        // MPEG-2 en MXF con AAC: el MXF solo admite PCM (lo dice su filtro de flujo pcm_rechunk).
        {
            VideoCodec.Mpeg2Video, ContainerFormat.Mxf, AudioCodec.Aac, new[]
            {
                Deprecated, Resumed,
                "[mpeg2video @ 00000189583fd140] Warning vbv_delay will be set to 0xFFFF (=VBR) as the specified vbv buffer is too large for the given bitrate!",
                "[pcm_rechunk @ 000002abad885700] Codec 'aac' (86018) is not supported by the bitstream filter 'pcm_rechunk'. Supported codecs are: pcm_alaw (65543) pcm_f16le (65569) pcm_s24le (65548) ",
                "[out#2/mxf @ 000002abad47d640] Could not write header (incorrect codec parameters ?): Invalid argument",
                "[fc#0 @ 000002abad3c8c80] Error sending frames to consumers: Invalid argument",
                "[vost#0:0/rawvideo @ 000002abad67ce80] [enc:rawvideo @ 000002abad3d3240] Could not open encoder before EOF",
                "[out#2/mxf @ 000002abad47d640] Nothing was written into output file, because at least one of its streams received no packets.",
                "Conversion failed!",
            },
            "Codec 'aac' (86018) is not supported by the bitstream filter 'pcm_rechunk'"
        },
        // PCM a 44,1 kHz en MXF (un preset importado o la API; el validador no mira la frecuencia): el MXF solo hace 48 kHz.
        {
            VideoCodec.Mpeg2Video, ContainerFormat.Mxf, AudioCodec.Pcm, new[]
            {
                "[mxf @ 0000022c0438e840] only 48khz is implemented",
                "[out#1/mxf @ 0000022c0438e700] Could not write header (incorrect codec parameters ?): Operation not permitted",
                "[fc#0 @ 0000022c040dde00] Error sending frames to consumers: Operation not permitted",
                "[vost#0:0/rawvideo @ 0000022c043c9d00] [enc:rawvideo @ 0000022c040e4b80] Could not open encoder before EOF",
            },
            "only 48khz is implemented"
        },
        // HEVC en MXF.
        {
            VideoCodec.H265x265, ContainerFormat.Mxf, AudioCodec.Pcm, new[]
            {
                "x265 [info]: HEVC encoder version 4.1",
                "[mxf @ 000001d4dd10de40] track 0: could not find essence container ul, codec not currently supported in container",
                "[out#2/mxf @ 000001d4dce6d980] Could not write header (incorrect codec parameters ?): Operation not permitted",
                "[vost#0:0/rawvideo @ 000001d4dcf0e840] [enc:rawvideo @ 000001d4dce639c0] Could not open encoder before EOF",
            },
            "track 0: could not find essence container ul, codec not currently supported in container"
        },
        // ProRes en MP4.
        {
            VideoCodec.ProRes, ContainerFormat.Mp4, AudioCodec.Aac, new[]
            {
                "[prores_aw @ 0000017fee571d80] encoding with ProRes standard (apcn) profile",
                "[mp4 @ 0000017fed45e980] Could not find tag for codec prores in stream #0, codec not currently supported in container",
                "[out#2/mp4 @ 0000017fed1aad80] Could not write header (incorrect codec parameters ?): Invalid argument",
                "[vost#0:0/rawvideo @ 0000017fed1be040] [enc:rawvideo @ 0000017fed1b4f40] Could not open encoder before EOF",
            },
            "Could not find tag for codec prores in stream #0, codec not currently supported in container"
        },
        // MPEG-2 en MPEG-PS con PCM de 24 bits.
        {
            VideoCodec.Mpeg2Video, ContainerFormat.ProgramStream, AudioCodec.Pcm, new[]
            {
                "[vob @ 000001d79f49de40] VBV buffer size not set, using default size of 230KB",
                "[vob @ 000001d79f49de40] Unsupported audio codec. Must be one of mp1, mp2, mp3, 16-bit pcm_dvd, pcm_s16be, ac3 or dts.",
                "[out#2/vob @ 000001d79f14d980] Could not write header (incorrect codec parameters ?): Invalid argument",
                "[vost#0:0/rawvideo @ 000001d79f1ee840] [enc:rawvideo @ 000001d79f143000] Could not open encoder before EOF",
            },
            "Unsupported audio codec. Must be one of mp1, mp2, mp3, 16-bit pcm_dvd, pcm_s16be, ac3 or dts"
        },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void AHeaderTheMuxerRefuses_IsAProfileThatCannotBeRecorded_NotAnEncoderThatCannotOpen(
        VideoCodec video, ContainerFormat container, AudioCodec audio, string[] stderr, string complaint)
    {
        var detector = new RecordingFailureDetector(Profile(video, container, audio));

        var verdicts = FeedAll(detector, stderr);

        // Una sola vez, en la línea de la cabecera; lo de después (el rawvideo del preview que no llegó a abrir) es consecuencia.
        int header = Array.FindIndex(stderr, l => l.Contains("Could not write header", StringComparison.Ordinal));
        Assert.Equal(RecordingStderrVerdict.ProfileRejected, verdicts[header]);
        Assert.Single(verdicts, v => v == RecordingStderrVerdict.ProfileRejected);
        Assert.DoesNotContain(RecordingStderrVerdict.VideoEncoderCannotOpen, verdicts);
        Assert.Null(detector.VideoEncoderFailure);
        // El motivo dice qué contenedor, qué se le pidió y la queja concreta de FFmpeg, sin la lista de códecs ni los prefijos.
        var cause = detector.Cause!;
        Assert.Contains(RecordingFailureDetector.ContainerLabel(container), cause);
        Assert.Contains(FfmpegCodecMap.VideoEncoder(video), cause);
        Assert.Contains(FfmpegCodecMap.EffectiveAudioEncoder(audio, container), cause);
        Assert.Contains($"«{complaint}»", cause);
        Assert.DoesNotContain("Supported codecs", cause);
        Assert.DoesNotContain(" @ 0000", cause);
    }

    [Fact]
    public void AHardwareEncoderThatCannotOpen_IsStillDegraded()
    {
        // h264_amf en un equipo sin AMD (8.0 y la incluida): la línea del dispositivo llega primero y manda.
        var detector = new RecordingFailureDetector(Profile(VideoCodec.H264Amf, ContainerFormat.Mp4, AudioCodec.Aac));
        var stderr = new[]
        {
            Deprecated, Resumed,
            "[AMF @ 0000015cf91bc0c0] DLL amfrt64.dll failed to open",
            "[h264_amf @ 0000015cf70eea00] Failed to create  hardware device context (AMF) : Unknown error occurred",
            "[vost#2:0/h264_amf @ 0000015cf70ee1c0] [enc:h264_amf @ 0000015cf898aa80] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.",
            "[vost#0:0/rawvideo @ 0000015cf8949540] [enc:rawvideo @ 0000015cf7043f40] Could not open encoder before EOF",
            "[vost#2:0/h264_amf @ 0000015cf70ee1c0] [enc:h264_amf @ 0000015cf898aa80] Could not open encoder before EOF",
        };

        var verdicts = FeedAll(detector, stderr);

        Assert.Equal(RecordingStderrVerdict.VideoEncoderCannotOpen, verdicts[2]);
        Assert.Single(verdicts, v => v != RecordingStderrVerdict.None);
        Assert.Null(detector.Cause);
        Assert.Equal("DLL amfrt64.dll failed to open", detector.VideoEncoderFailure);
    }

    [Fact]
    public void TheVideoEncoderGenericLine_IsExplainedByTheComplaintBeforeIt()
    {
        // libx264 con un ancho impar (8.0 y la incluida): la genérica no dice nada; la queja de antes, sí.
        var detector = new RecordingFailureDetector(Profile(VideoCodec.H264x264, ContainerFormat.Mp4, AudioCodec.Aac));

        var verdicts = FeedAll(detector, new[]
        {
            "[libx264 @ 0000026ce6063c00] width not divisible by 2 (1279x720)",
            Meter,
            "[vost#1:0/libx264 @ 0000026ce6063840] [enc:libx264 @ 0000026ce5e82dc0] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.",
            "[fc#0 @ 0000026ce5e7db00] Error sending frames to consumers: Generic error in an external library",
            "[vost#1:0/libx264 @ 0000026ce6063840] [enc:libx264 @ 0000026ce5e82dc0] Could not open encoder before EOF",
        });

        Assert.Equal(new[] { RecordingStderrVerdict.None, RecordingStderrVerdict.None, RecordingStderrVerdict.VideoEncoderCannotOpen,
            RecordingStderrVerdict.None, RecordingStderrVerdict.None }, verdicts);
        Assert.Equal("width not divisible by 2 (1279x720)", detector.VideoEncoderFailure);
        Assert.Null(detector.Cause);
    }

    [Fact]
    public void TheMetersOfEachChannel_DoNotPushTheComplaintOutOfReach()
    {
        // Con audio multicanal cada canal tiene su medidor con nombre propio (no «Parsed_…»): 16 líneas cada 100 ms que, desde
        // otro hilo, pueden caer entre la queja y el fallo.
        var detector = new RecordingFailureDetector(Profile(VideoCodec.H264x264, ContainerFormat.Mp4, AudioCodec.Aac));
        var meters = Enumerable.Range(1, 16).Select(c =>
            $"[ebur128@ch{c}of16 @ 000001b9f17e9680] t: 0.0999792  TARGET:-23 LUFS    M:-120.7 S:-120.7     I: -70.0 LUFS       LRA:   0.0 LU  FTPK:  -3.0 dBFS  TPK:  -3.0 dBFS");

        FeedAll(detector, new[] { "[libx264 @ 0000026ce6063c00] width not divisible by 2 (1279x720)" }
            .Concat(meters)
            .Append("[vost#1:0/libx264 @ 0000026ce6063840] [enc:libx264 @ 0000026ce5e82dc0] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height."));

        Assert.Equal("width not divisible by 2 (1279x720)", detector.VideoEncoderFailure);
    }

    [Fact]
    public void AnotherEncoderThatCannotOpen_IsNotTheVideoOne()
    {
        // El de audio (MP2 con un bitrate que no admite): degradar el de vídeo no lo arreglaría.
        var detector = new RecordingFailureDetector(Profile(VideoCodec.H264Nvenc, ContainerFormat.Mp4, AudioCodec.Mp2));

        var verdicts = FeedAll(detector, new[]
        {
            "[mp2 @ 000001f81caae980] bitrate 1000 is not allowed in mp2",
            "[aost#2:1/mp2 @ 000001f81caae480] [enc:mp2 @ 000001f81caabbc0] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.",
            "[af#2:1 @ 000001f81ca51840] Error sending frames to consumers: Invalid argument",
            "[aost#2:1/mp2 @ 000001f81caae480] [enc:mp2 @ 000001f81caabbc0] Could not open encoder before EOF",
        });

        Assert.Equal(RecordingStderrVerdict.ProfileRejected, verdicts[1]);
        Assert.DoesNotContain(RecordingStderrVerdict.VideoEncoderCannotOpen, verdicts);
        Assert.Contains("'mp2'", detector.Cause);
        Assert.Contains("«bitrate 1000 is not allowed in mp2»", detector.Cause);
    }

    [Fact]
    public void AnInputThatEndsBeforeAnyFrame_IsACrash_NotAnEncoder()
    {
        // Ningún codificador recibió un cuadro: todos dicen «Could not open encoder before EOF», también el de vídeo. Es una caída
        // de la entrada (pieza nueva), no un codificador que no abre (antes degradaba NVENC → QuickSync en vano).
        var detector = new RecordingFailureDetector(Profile(VideoCodec.H264Nvenc, ContainerFormat.Mp4, AudioCodec.Aac));

        var verdicts = FeedAll(detector, new[]
        {
            "[vost#0:0/rawvideo @ 0x55] [enc:rawvideo @ 0x55] Could not open encoder before EOF",
            "[vost#2:0/h264_nvenc @ 0x55] [enc:h264_nvenc @ 0x55] Could not open encoder before EOF",
            "[aost#2:1/aac @ 0x55] [enc:aac @ 0x55] Could not open encoder before EOF",
        });

        Assert.All(verdicts, v => Assert.Equal(RecordingStderrVerdict.None, v));
        Assert.Null(detector.Cause);
        Assert.Null(detector.VideoEncoderFailure);
    }

    [Fact]
    public void AHeaderThatFailsOnTheDisk_IsACrash_AndWhatFollowsIsAConsequence()
    {
        // Un error de E/S al escribir la cabecera puede pasarse: pieza nueva. Lo que un FFmpeg antiguo añade detrás («Error initializing
        // output stream») no es un codificador que no abre.
        var detector = new RecordingFailureDetector(Profile(VideoCodec.H264Nvenc, ContainerFormat.Mp4, AudioCodec.Aac));

        var verdicts = FeedAll(detector, new[]
        {
            "[out#2/mp4 @ 0x55] Could not write header (incorrect codec parameters ?): I/O error",
            "Error initializing output stream: ",
        });

        Assert.All(verdicts, v => Assert.Equal(RecordingStderrVerdict.None, v));
        Assert.Null(detector.Cause);
    }

    [Fact]
    public void OldFfmpeg_TheRefusalComesFirst_AndItsAftermathIsIgnored()
    {
        // FFmpeg ≤ 6.0: «… for output file #0 …» y después «Error initializing output stream», que antes disparaba la degradación.
        var detector = new RecordingFailureDetector(Profile(VideoCodec.H264Nvenc, ContainerFormat.Mxf, AudioCodec.Aac));

        var verdicts = FeedAll(detector, new[]
        {
            "Could not write header for output file #2 (incorrect codec parameters ?): Invalid argument",
            "Error initializing output stream: ",
        });

        Assert.Equal(new[] { RecordingStderrVerdict.ProfileRejected, RecordingStderrVerdict.None }, verdicts);
        Assert.Contains("MXF", detector.Cause);
        Assert.Contains("«Could not write header for output file #2 (incorrect codec parameters ?): Invalid argument»", detector.Cause);
    }

    [Fact]
    public void AWarningIsNotTakenForTheExplanation()
    {
        // Sin queja concreta, el motivo es la propia línea de FFmpeg; el aviso de VBV (habla de «too large») no explica nada.
        var detector = new RecordingFailureDetector(Profile(VideoCodec.Mpeg2Video, ContainerFormat.Mxf, AudioCodec.Pcm));

        detector.Feed("[mpeg2video @ 00000189583fd140] Warning vbv_delay will be set to 0xFFFF (=VBR) as the specified vbv buffer is too large for the given bitrate!");
        detector.Feed(Deprecated);
        Assert.Equal(RecordingStderrVerdict.ProfileRejected,
            detector.Feed("[out#2/mxf @ 0x55] Could not write header (incorrect codec parameters ?): Invalid argument"));

        Assert.Contains("«Could not write header (incorrect codec parameters ?): Invalid argument»", detector.Cause);
        Assert.DoesNotContain("vbv", detector.Cause);
    }

    [Fact]
    public void AnAudioOnlyProfile_NamesOnlyTheAudio()
    {
        var profile = Profile(VideoCodec.H264x264, ContainerFormat.ProgramStream, AudioCodec.Pcm);
        profile.AudioOnly = true;
        var detector = new RecordingFailureDetector(profile);

        detector.Feed("[out#1/mpeg @ 0x55] Could not write header (incorrect codec parameters ?): Invalid argument");

        Assert.Contains("(audio pcm_s24le)", detector.Cause);
        Assert.DoesNotContain("vídeo", detector.Cause);
    }

    [Fact]
    public void TheAlarmOfARecordingThatCouldNotBeProduced_IsRed()
        => Assert.True(new ChannelAlarm(AlarmType.RecordingFailed, "x", DateTimeOffset.UtcNow).IsCritical);
}
