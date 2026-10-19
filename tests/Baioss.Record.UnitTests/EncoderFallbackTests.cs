using Baioss.Record.Domain;
using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.UnitTests;

public class EncoderFallbackChainTests
{
    [Theory]
    // H.264 por hardware: NVENC → QuickSync → AMF → CPU.
    [InlineData(VideoCodec.H264Nvenc, VideoCodec.H264Qsv)]
    [InlineData(VideoCodec.H264Qsv, VideoCodec.H264Amf)]
    [InlineData(VideoCodec.H264Amf, VideoCodec.H264x264)]
    // HEVC por NVENC → QuickSync → CPU; AV1 por NVENC → CPU (H.264).
    [InlineData(VideoCodec.HevcNvenc, VideoCodec.HevcQsv)]
    [InlineData(VideoCodec.HevcQsv, VideoCodec.H265x265)]
    [InlineData(VideoCodec.Av1Nvenc, VideoCodec.H264x264)]
    public void Next_DegradesHardwareEncoders(VideoCodec current, VideoCodec expected)
        => Assert.Equal(expected, EncoderFallbackChain.Next(current));

    [Theory]
    // Ya es CPU/intra: sin degradación (deberían abrir siempre).
    [InlineData(VideoCodec.H264x264)]
    [InlineData(VideoCodec.H265x265)]
    [InlineData(VideoCodec.ProRes)]
    [InlineData(VideoCodec.DnxHd)]
    [InlineData(VideoCodec.DnxHr)]
    [InlineData(VideoCodec.Mpeg2Video)]
    public void Next_ReturnsNullForCpuAndIntra(VideoCodec current)
        => Assert.Null(EncoderFallbackChain.Next(current));

    [Fact]
    public void Next_H264HardwareChain_ConvergesToLibx264InThreeSteps()
    {
        // Equipo sin NINGUNA GPU compatible: la cadena baja escalón a escalón hasta CPU y se detiene.
        var c = VideoCodec.H264Nvenc;
        var seen = new System.Collections.Generic.List<VideoCodec>();
        for (int i = 0; i < 10 && EncoderFallbackChain.Next(c) is { } n; i++) { c = n; seen.Add(c); }

        Assert.Equal(new[] { VideoCodec.H264Qsv, VideoCodec.H264Amf, VideoCodec.H264x264 }, seen);
        Assert.Null(EncoderFallbackChain.Next(c)); // libx264 = último recurso, sin siguiente
    }

    [Fact]
    public void Next_HevcHardwareChain_TriesQuickSyncBeforeTheCpu_AndKeepsTheCodec()
    {
        // libx265 no llega a 1080p29.97 en tiempo real en un i7-8700 (medido con una DeckLink: 267 cuadros perdidos en 15 s);
        // la GPU integrada de Intel sí. Sin Intel, hevc_qsv no abre y se baja a CPU como antes. Siempre HEVC.
        var c = VideoCodec.HevcNvenc;
        var seen = new System.Collections.Generic.List<VideoCodec>();
        for (int i = 0; i < 10 && EncoderFallbackChain.Next(c) is { } n; i++) { c = n; seen.Add(c); }

        Assert.Equal(new[] { VideoCodec.HevcQsv, VideoCodec.H265x265 }, seen);
        Assert.Equal("hevc_qsv", FfmpegCodecMap.VideoEncoder(VideoCodec.HevcQsv));
    }

    [Theory]
    [InlineData(PixelFormat.Yuv422p)]
    [InlineData(PixelFormat.Yuv420p10le)]
    [InlineData(PixelFormat.Yuv422p10le)]
    [InlineData(PixelFormat.Yuv444p10le)]
    public void Next_WithAFormatQuickSyncAndAmfDoNotEncode_GoesStraightToTheCpu(PixelFormat pixel)
    {
        // H.264 por QuickSync y por AMF solo hacen 4:2:0 de 8 bits: FFmpeg les da nv12 sin avisar. Medido en una P400 (que no hace
        // H.264 4:2:2 ni de 10 bits): el NVENC degradaba a h264_qsv y el archivo salía en 4:2:0 de 8 bits.
        Assert.Equal(VideoCodec.H264x264, EncoderFallbackChain.Next(VideoCodec.H264Nvenc, pixel));
        Assert.Equal(VideoCodec.H264x264, EncoderFallbackChain.Next(VideoCodec.H264Qsv, pixel));
        // HEVC por QuickSync no los cambia: si no los hace, no abre y baja a libx265.
        Assert.Equal(VideoCodec.HevcQsv, EncoderFallbackChain.Next(VideoCodec.HevcNvenc, pixel));
    }

    [Theory]
    [InlineData(PixelFormat.Auto)]
    [InlineData(PixelFormat.Yuv420p)]
    public void Next_With420At8Bits_KeepsTheWholeChain(PixelFormat pixel)
        => Assert.Equal(VideoCodec.H264Qsv, EncoderFallbackChain.Next(VideoCodec.H264Nvenc, pixel));
}

public class FfmpegEncoderErrorTests
{
    [Theory]
    // Formato GENÉRICO real de FFmpeg reciente (capturado en este equipo) y el antiguo «for output stream».
    [InlineData("[vost#0:0/h264_amf] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.")]
    [InlineData("[vost#2:0/h264_amf @ 0000015cf70ee1c0] [enc:h264_amf @ 0000015cf898aa80] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.")]
    [InlineData("[h264_nvenc @ 0x55] Error while opening encoder for output stream #0:0 - maybe incorrect parameters")]
    [InlineData("Error initializing output stream 0:0 --")]
    // NVENC: sesiones agotadas / driver / GPU / códec sin soporte (líneas reales).
    [InlineData("[h264_nvenc @ 0x55] OpenEncodeSessionEx failed: out of memory (10): (no details)")]
    [InlineData("[av1_nvenc @ 0x55] No capable devices found")]
    [InlineData("[h264_nvenc @ 0x55] incompatible client key (21)")]
    [InlineData("Cannot load nvcuda.dll")]
    // QuickSync.
    [InlineData("[h264_qsv @ 0x55] Error initializing an internal MFX session")]
    // AMF: línea real capturada en este equipo (sin GPU AMD instalada).
    [InlineData("[AMF @ 0x55] DLL amfrt64.dll failed to open")]
    [InlineData("[h264_amf @ 0x55] Failed to initialize AMF")]
    public void IsOpenFailure_DetectsEncoderOpenFailures(string line)
        => Assert.True(FfmpegEncoderError.IsOpenFailure(line));

    [Theory]
    // Líneas normales de operación: NO deben confundirse con un fallo de apertura.
    [InlineData("frame=  120 fps= 25 q=28.0 size=    2048kB time=00:00:04.80 bitrate=...")]
    [InlineData("[h264_nvenc @ 0x55] using cq 23.0")]
    [InlineData("[libx264 @ 0x55] frame I:1 Avg QP:18.50")]
    [InlineData("[silencedetect @ 0x55] silence_start: 5.20")]
    // Consecuencias, no causas: FFmpeg ≥ 7 la escribe por cada codificador que no había recibido un cuadro cuando el proceso
    // termina por otra cosa. Con el rawvideo del preview tras un MXF que no admite AAC (8.0 y la incluida) se degradaba en vano.
    [InlineData("[vost#0:0/rawvideo @ 00000189583ef9c0] [enc:rawvideo @ 000001895837cfc0] Could not open encoder before EOF")]
    [InlineData("[vost#0:0/h264_amf] Could not open encoder before EOF")]
    // El muxer que rechaza la cabecera tampoco es un codificador que no abre.
    [InlineData("[out#2/mxf @ 0000018958380ec0] Could not write header (incorrect codec parameters ?): Invalid argument")]
    [InlineData("")]
    [InlineData(null)]
    public void IsOpenFailure_IgnoresNormalLines(string? line)
        => Assert.False(FfmpegEncoderError.IsOpenFailure(line));

    [Theory]
    // FFmpeg 8.0 y la incluida: el flujo de salida y el codificador.
    [InlineData("[aost#2:1/mp2 @ 000001f81caae480] [enc:mp2 @ 000001f81caabbc0] Error while opening encoder - maybe incorrect parameters", "mp2")]
    [InlineData("[vost#0:0/rawvideo @ 00000189583ef9c0] [enc:rawvideo @ 000001895837cfc0] Could not open encoder before EOF", "rawvideo")]
    [InlineData("[vost#2:0/h264_amf @ 0000015cf70ee1c0] [enc:h264_amf @ 0000015cf898aa80] Error while opening encoder", "h264_amf")]
    // Solo el flujo (como se capturó con otra build).
    [InlineData("[vost#0:0/h264_amf] Error while opening encoder - maybe incorrect parameters", "h264_amf")]
    // Sin nombre de codificador: las líneas del dispositivo, las del muxer y las de FFmpeg antiguos.
    [InlineData("[AMF @ 000002d6d264f080] DLL amfrt64.dll failed to open", null)]
    [InlineData("[h264_nvenc @ 0x55] OpenEncodeSessionEx failed: out of memory (10): (no details)", null)]
    [InlineData("[out#2/mxf @ 0000018958380ec0] Could not write header (incorrect codec parameters ?): Invalid argument", null)]
    [InlineData("Error initializing output stream 0:0 --", null)]
    [InlineData(null, null)]
    public void EncoderName_ReadsTheEncoderFfmpegNames(string? line, string? expected)
        => Assert.Equal(expected, FfmpegEncoderError.EncoderName(line));
}

public class FfmpegMuxerErrorTests
{
    [Theory]
    [InlineData("[out#2/mxf @ 0000018958380ec0] Could not write header (incorrect codec parameters ?): Invalid argument")]
    [InlineData("[out#2/mxf @ 000001d4dce6d980] Could not write header (incorrect codec parameters ?): Operation not permitted")]
    [InlineData("Could not write header for output file #0 (incorrect codec parameters ?): Invalid argument")] // FFmpeg ≤ 6.0
    public void IsHeaderFailure_RecognisesTheMuxerRefusal(string line)
    {
        Assert.True(FfmpegMuxerError.IsHeaderFailure(line));
        Assert.False(FfmpegMuxerError.IsIoError(line));
    }

    [Theory]
    [InlineData("[out#2/mp4 @ 0x55] Could not write header (incorrect codec parameters ?): I/O error")]
    [InlineData("[out#2/mov @ 0x55] Could not write header (incorrect codec parameters ?): No space left on device")]
    [InlineData("[out#2/mxf @ 0x55] Could not write header (incorrect codec parameters ?): Permission denied")]
    public void IsIoError_TellsADiskOrNetworkFailureFromARefusal(string line)
    {
        Assert.True(FfmpegMuxerError.IsHeaderFailure(line));
        Assert.True(FfmpegMuxerError.IsIoError(line));
    }

    [Theory]
    [InlineData("[mxf @ 000001d4dd10de40] track 0: could not find essence container ul, codec not currently supported in container")]
    [InlineData("[vost#0:0/rawvideo @ 0x55] [enc:rawvideo @ 0x55] Could not open encoder before EOF")]
    [InlineData("[out#2/mp3 @ 000001f42c4fb940] Error muxing a packet")]
    [InlineData(null)]
    public void IsHeaderFailure_IgnoresOtherLines(string? line) => Assert.False(FfmpegMuxerError.IsHeaderFailure(line));
}
