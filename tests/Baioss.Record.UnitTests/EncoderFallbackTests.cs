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
    // HEVC/AV1 por NVENC → CPU.
    [InlineData(VideoCodec.HevcNvenc, VideoCodec.H265x265)]
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
}

public class FfmpegEncoderErrorTests
{
    [Theory]
    // Formato GENÉRICO real de FFmpeg reciente (capturado en este equipo) y el antiguo «for output stream».
    [InlineData("[vost#0:0/h264_amf] Error while opening encoder - maybe incorrect parameters such as bit_rate, rate, width or height.")]
    [InlineData("[h264_nvenc @ 0x55] Error while opening encoder for output stream #0:0 - maybe incorrect parameters")]
    [InlineData("[vost#0:0/h264_amf] Could not open encoder before EOF")]
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
    [InlineData("")]
    [InlineData(null)]
    public void IsOpenFailure_IgnoresNormalLines(string? line)
        => Assert.False(FfmpegEncoderError.IsOpenFailure(line));
}
