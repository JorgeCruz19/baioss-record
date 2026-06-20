using System.Security.Cryptography;
using System.Text;
using Baioss.Record.Domain;

namespace Baioss.Record.Application.Presets;

/// <summary>
/// Presets profesionales de fábrica (built-in). Cubren los formatos broadcast habituales en
/// SD/HD/FullHD/4K. Son de solo lectura: para personalizar se duplican.
/// </summary>
public static class PresetCatalog
{
    public static IReadOnlyList<EncodingPreset> CreateBuiltIns() => new[]
    {
        // ---------------- MPEG-2 ----------------
        Make("MPEG-2 PS · SD PAL", PresetCategory.Mpeg2, "Program Stream 720×576i25, 6 Mbps CBR, audio MP2.", p =>
        { p.Container = ContainerFormat.ProgramStream; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 720; p.Height = 576;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 6; p.MaxBitrateMbps = 6; p.GopSize = 15; p.PixelFormat = PixelFormat.Yuv420p;
          p.ScanType = ScanType.InterlacedTff; p.AudioCodec = AudioCodec.Mp2; p.AudioBitrateKbps = 384; }),

        Make("MPEG-2 TS · HD 1080i25", PresetCategory.Mpeg2, "Transport Stream 1920×1080i25, 18 Mbps CBR, audio MP2.", p =>
        { p.Container = ContainerFormat.Ts; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 18; p.MaxBitrateMbps = 18; p.GopSize = 12; p.PixelFormat = PixelFormat.Yuv420p;
          p.ScanType = ScanType.InterlacedTff; p.AudioCodec = AudioCodec.Mp2; p.AudioBitrateKbps = 384; }),

        Make("MPEG-2 TS · SD PAL", PresetCategory.Mpeg2, "Transport Stream 720×576i25, 6 Mbps CBR, audio MP2.", p =>
        { p.Container = ContainerFormat.Ts; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 720; p.Height = 576;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 6; p.MaxBitrateMbps = 6; p.GopSize = 15; p.PixelFormat = PixelFormat.Yuv420p;
          p.ScanType = ScanType.InterlacedTff; p.AudioCodec = AudioCodec.Mp2; p.AudioBitrateKbps = 256; }),

        // ---------------- H.264 ----------------
        Make("H.264 Broadcast · HD 720p50", PresetCategory.H264, "1280×720p50, 10 Mbps CBR, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 1280; p.Height = 720;
          p.FrameRateNum = 50; p.VideoBitrateMbps = 10; p.MaxBitrateMbps = 12; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        Make("H.264 Broadcast · FullHD 1080p25", PresetCategory.H264, "1920×1080p25, 16 Mbps CBR, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 16; p.MaxBitrateMbps = 20; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        Make("H.264 Broadcast · 1080i25 TS", PresetCategory.H264, "1920×1080i25 entrelazado, 16 Mbps, Transport Stream.", p =>
        { p.Container = ContainerFormat.Ts; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 16; p.MaxBitrateMbps = 20; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.ScanType = ScanType.InterlacedTff; }),

        Make("H.264 · 4K UHD 2160p50", PresetCategory.H264, "3840×2160p50, 60 Mbps, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 3840; p.Height = 2160;
          p.FrameRateNum = 50; p.VideoBitrateMbps = 60; p.MaxBitrateMbps = 80; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        Make("H.264 · SD PAL", PresetCategory.H264, "720×576i25, 5 Mbps, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 720; p.Height = 576;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 5; p.MaxBitrateMbps = 6; p.GopSize = 25; p.PixelFormat = PixelFormat.Yuv420p;
          p.ScanType = ScanType.InterlacedTff; p.AudioBitrateKbps = 192; }),

        // ---- H.264 · familia 59.94/60/50 (MP4/TS) ----
        Make("H.264 Broadcast · FullHD 1080p59.94", PresetCategory.H264, "1920×1080p59.94 (60000/1001), 24 Mbps CBR, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 24; p.MaxBitrateMbps = 30; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        Make("H.264 Broadcast · FullHD 1080p60", PresetCategory.H264, "1920×1080p60 (60/1, entero), 24 Mbps CBR, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 60; p.FrameRateDen = 1; p.VideoBitrateMbps = 24; p.MaxBitrateMbps = 30; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        Make("H.264 Broadcast · FullHD 1080p50", PresetCategory.H264, "1920×1080p50 (50/1), 20 Mbps CBR, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 50; p.FrameRateDen = 1; p.VideoBitrateMbps = 20; p.MaxBitrateMbps = 25; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        Make("H.264 Broadcast · FullHD 1080p29.97", PresetCategory.H264, "1920×1080p29.97 (30000/1001), 16 Mbps CBR, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 30000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 16; p.MaxBitrateMbps = 20; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        Make("H.264 Broadcast · FullHD 1080p23.98", PresetCategory.H264, "1920×1080p23.98 (24000/1001), cadencia cine, 16 Mbps CBR, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 24000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 16; p.MaxBitrateMbps = 20; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        Make("H.264 Broadcast · HD 720p59.94", PresetCategory.H264, "1280×720p59.94 (60000/1001), 12 Mbps CBR, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 1280; p.Height = 720;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 12; p.MaxBitrateMbps = 15; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        Make("H.264 Broadcast · 1080i59.94 TS", PresetCategory.H264, "1920×1080i59.94 (30000/1001) entrelazado, 18 Mbps, Transport Stream.", p =>
        { p.Container = ContainerFormat.Ts; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 30000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 18; p.MaxBitrateMbps = 24; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.ScanType = ScanType.InterlacedTff; }),

        Make("H.264 · 4K UHD 2160p59.94", PresetCategory.H264, "3840×2160p59.94 (60000/1001), 80 Mbps, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 3840; p.Height = 2160;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 80; p.MaxBitrateMbps = 100; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        // ---------------- H.265 / HEVC ----------------
        Make("HEVC · FullHD 1080p25", PresetCategory.H265, "1920×1080p25, 10 Mbps VBR, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H265x265; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 10; p.MaxBitrateMbps = 14; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.RateControl = RateControlMode.VariableBitrate; }),

        Make("HEVC · 4K UHD 2160p50 10-bit", PresetCategory.H265, "3840×2160p50 10-bit, 35 Mbps VBR, MP4.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H265x265; p.Width = 3840; p.Height = 2160;
          p.FrameRateNum = 50; p.VideoBitrateMbps = 35; p.MaxBitrateMbps = 45; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p10le;
          p.RateControl = RateControlMode.VariableBitrate; }),

        // ---- HEVC · familia 59.94 (MP4) ----
        Make("HEVC · FullHD 1080p59.94", PresetCategory.H265, "1920×1080p59.94 (60000/1001), 12 Mbps VBR, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H265x265; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 12; p.MaxBitrateMbps = 16; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.RateControl = RateControlMode.VariableBitrate; }),

        Make("HEVC · 4K UHD 2160p59.94 10-bit", PresetCategory.H265, "3840×2160p59.94 (60000/1001) 10-bit, 45 Mbps VBR, MP4.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H265x265; p.Width = 3840; p.Height = 2160;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 45; p.MaxBitrateMbps = 60; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p10le;
          p.RateControl = RateControlMode.VariableBitrate; }),

        // ---------------- DNxHR (Avid, edición) ----------------
        Make("DNxHR LB · 1080p25 (offline)", PresetCategory.DnxHd, "Low Bandwidth: 1920×1080p25 4:2:2 8-bit, intra, MOV, PCM. Edición offline/proxy.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p; p.EncoderProfile = EncoderProfile.DnxHrLb; p.AudioCodec = AudioCodec.Pcm; }),

        Make("DNxHR SQ · 1080p25", PresetCategory.DnxHd, "Standard Quality: 1920×1080p25 4:2:2 8-bit, intra, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p; p.EncoderProfile = EncoderProfile.DnxHrSq; p.AudioCodec = AudioCodec.Pcm; }),

        Make("DNxHR HQ · 1080p25", PresetCategory.DnxHd, "High Quality: 1920×1080p25 4:2:2 8-bit, intra, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p; p.EncoderProfile = EncoderProfile.DnxHrHq; p.AudioCodec = AudioCodec.Pcm; }),

        Make("DNxHR HQ · 1080p59.94 (MOV)", PresetCategory.DnxHd, "High Quality: 1920×1080p59.94 (60000/1001) 4:2:2 8-bit, intra, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p; p.EncoderProfile = EncoderProfile.DnxHrHq; p.AudioCodec = AudioCodec.Pcm; }),

        Make("DNxHR HQX · 1080p25 10-bit", PresetCategory.DnxHd, "High Quality 10-bit: 1920×1080p25 4:2:2, intra, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p10le; p.EncoderProfile = EncoderProfile.DnxHrHqx; p.AudioCodec = AudioCodec.Pcm; }),

        Make("DNxHR 444 · 1080p25 10-bit", PresetCategory.DnxHd, "4:4:4 10-bit (grading/masterización): 1920×1080p25, intra, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv444p10le; p.EncoderProfile = EncoderProfile.DnxHr444; p.AudioCodec = AudioCodec.Pcm; }),

        Make("DNxHR HQ · 1080i25 (MXF)", PresetCategory.DnxHd, "1920×1080i25 4:2:2 8-bit, MXF OP1A, audio PCM (entrega broadcast).", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p; p.EncoderProfile = EncoderProfile.DnxHrHq;
          p.ScanType = ScanType.InterlacedTff; p.AudioCodec = AudioCodec.Pcm; }),

        // ---------------- Apple ProRes (edición) ----------------
        Make("ProRes 422 Proxy · 1080p25", PresetCategory.ProRes, "Proxy (offline): 1920×1080p25 4:2:2 10-bit, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.ProRes; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p10le; p.EncoderProfile = EncoderProfile.ProResProxy; p.AudioCodec = AudioCodec.Pcm; }),

        Make("ProRes 422 LT · 1080p25", PresetCategory.ProRes, "Ligero: 1920×1080p25 4:2:2 10-bit, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.ProRes; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p10le; p.EncoderProfile = EncoderProfile.ProResLt; p.AudioCodec = AudioCodec.Pcm; }),

        Make("ProRes 422 · 1080p25", PresetCategory.ProRes, "Estándar: 1920×1080p25 4:2:2 10-bit, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.ProRes; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p10le; p.EncoderProfile = EncoderProfile.ProResStandard; p.AudioCodec = AudioCodec.Pcm; }),

        Make("ProRes 422 HQ · 1080p25", PresetCategory.ProRes, "Alta calidad: 1920×1080p25 4:2:2 10-bit, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.ProRes; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p10le; p.EncoderProfile = EncoderProfile.ProResHq; p.AudioCodec = AudioCodec.Pcm; }),

        Make("ProRes 4444 · 1080p25", PresetCategory.ProRes, "4:4:4 10-bit (grading, soporta alfa): 1920×1080p25, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.ProRes; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv444p10le; p.EncoderProfile = EncoderProfile.ProRes4444; p.AudioCodec = AudioCodec.Pcm; }),

        Make("ProRes 4444 XQ · 1080p25", PresetCategory.ProRes, "4:4:4 10-bit máxima calidad: 1920×1080p25, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.ProRes; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv444p10le; p.EncoderProfile = EncoderProfile.ProRes4444Xq; p.AudioCodec = AudioCodec.Pcm; }),

        Make("ProRes 422 HQ · 4K 2160p25", PresetCategory.ProRes, "3840×2160p25 4:2:2 10-bit, MOV, audio PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.ProRes; p.Width = 3840; p.Height = 2160;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p10le; p.EncoderProfile = EncoderProfile.ProResHq; p.AudioCodec = AudioCodec.Pcm; }),

        Make("ProRes 422 HQ · 1080p59.94", PresetCategory.ProRes, "1920×1080p59.94 (60000/1001) 4:2:2 10-bit, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.ProRes; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p10le; p.EncoderProfile = EncoderProfile.ProResHq; p.AudioCodec = AudioCodec.Pcm; }),

        // ---------------- XDCAM ----------------
        Make("XDCAM HD422 · 1080i25 50 Mbps", PresetCategory.Xdcam, "MPEG-2 4:2:2 50 Mbps CBR, MXF, audio PCM (estándar broadcast).", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 50; p.MaxBitrateMbps = 50; p.GopSize = 12; p.PixelFormat = PixelFormat.Yuv422p;
          p.ScanType = ScanType.InterlacedTff; p.AudioCodec = AudioCodec.Pcm; }),

        Make("XDCAM HD · 1080i25 35 Mbps", PresetCategory.Xdcam, "MPEG-2 4:2:0 35 Mbps CBR, MXF, audio PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 35; p.MaxBitrateMbps = 35; p.GopSize = 12; p.PixelFormat = PixelFormat.Yuv420p;
          p.ScanType = ScanType.InterlacedTff; p.AudioCodec = AudioCodec.Pcm; }),

        // ---------------- MXF OP1A ----------------
        Make("MXF OP1A · XDCAM HD422 50", PresetCategory.Mxf, "MXF OP1A con MPEG-2 4:2:2 50 Mbps, audio PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 50; p.MaxBitrateMbps = 50; p.GopSize = 12; p.PixelFormat = PixelFormat.Yuv422p;
          p.ScanType = ScanType.InterlacedTff; p.AudioCodec = AudioCodec.Pcm; }),

        Make("MXF OP1A · DNxHR HQ 1080p25", PresetCategory.Mxf, "MXF OP1A con DNxHR HQ 4:2:2, audio PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p; p.EncoderProfile = EncoderProfile.DnxHrHq; p.AudioCodec = AudioCodec.Pcm; }),

        // ---- MXF · familia 59.94 / NTSC (XDCAM HD422, MPEG-2 4:2:2 50 Mbps) ----
        // 1080i59.94 = 30000/1001 entrelazado (29.97 cuadros = 59.94 campos); 1080p59.94 = 60000/1001.
        Make("MXF · XDCAM HD422 1080i59.94 50", PresetCategory.Mxf, "MPEG-2 4:2:2 50 Mbps CBR · 1920×1080i59.94 (30000/1001) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 30000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 50; p.MaxBitrateMbps = 50; p.GopSize = 12; p.PixelFormat = PixelFormat.Yuv422p;
          p.ScanType = ScanType.InterlacedTff; p.AudioCodec = AudioCodec.Pcm; }),

        Make("MXF · XDCAM HD422 1080p59.94 50", PresetCategory.Mxf, "MPEG-2 4:2:2 50 Mbps CBR · 1920×1080p59.94 (60000/1001) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 50; p.MaxBitrateMbps = 50; p.GopSize = 15; p.PixelFormat = PixelFormat.Yuv422p;
          p.AudioCodec = AudioCodec.Pcm; }),

        Make("MXF · XDCAM HD422 1080p29.97 50", PresetCategory.Mxf, "MPEG-2 4:2:2 50 Mbps CBR · 1920×1080p29.97 (30000/1001) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 30000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 50; p.MaxBitrateMbps = 50; p.GopSize = 12; p.PixelFormat = PixelFormat.Yuv422p;
          p.AudioCodec = AudioCodec.Pcm; }),

        Make("MXF · XDCAM HD422 1080p23.98 50", PresetCategory.Mxf, "MPEG-2 4:2:2 50 Mbps CBR · 1920×1080p23.98 (24000/1001), cadencia cine · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 24000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 50; p.MaxBitrateMbps = 50; p.GopSize = 12; p.PixelFormat = PixelFormat.Yuv422p;
          p.AudioCodec = AudioCodec.Pcm; }),

        Make("MXF · XDCAM HD422 720p59.94 50", PresetCategory.Mxf, "MPEG-2 4:2:2 50 Mbps CBR · 1280×720p59.94 (60000/1001) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1280; p.Height = 720;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.VideoBitrateMbps = 50; p.MaxBitrateMbps = 50; p.GopSize = 15; p.PixelFormat = PixelFormat.Yuv422p;
          p.AudioCodec = AudioCodec.Pcm; }),

        Make("MXF · XDCAM HD422 1080p60 50", PresetCategory.Mxf, "MPEG-2 4:2:2 50 Mbps CBR · 1920×1080p60 (60/1, entero) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 60; p.FrameRateDen = 1; p.VideoBitrateMbps = 50; p.MaxBitrateMbps = 50; p.GopSize = 15; p.PixelFormat = PixelFormat.Yuv422p;
          p.AudioCodec = AudioCodec.Pcm; }),

        // ---- MXF · familia 59.94 (DNxHR, intra, edición/entrega) ----
        Make("MXF OP1A · DNxHR HQ 1080i59.94", PresetCategory.Mxf, "DNxHR HQ 4:2:2 8-bit · 1920×1080i59.94 (30000/1001) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 30000; p.FrameRateDen = 1001; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p; p.EncoderProfile = EncoderProfile.DnxHrHq;
          p.ScanType = ScanType.InterlacedTff; p.AudioCodec = AudioCodec.Pcm; }),

        Make("MXF OP1A · DNxHR HQ 1080p59.94", PresetCategory.Mxf, "DNxHR HQ 4:2:2 8-bit · 1920×1080p59.94 (60000/1001) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p; p.EncoderProfile = EncoderProfile.DnxHrHq;
          p.AudioCodec = AudioCodec.Pcm; }),

        Make("MXF OP1A · DNxHR HQX 1080p59.94 10-bit", PresetCategory.Mxf, "DNxHR HQX 4:2:2 10-bit · 1920×1080p59.94 (60000/1001) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 60000; p.FrameRateDen = 1001; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p10le; p.EncoderProfile = EncoderProfile.DnxHrHqx;
          p.AudioCodec = AudioCodec.Pcm; }),

        // ---- MXF · familia 50 Hz / PAL (frame rates enteros) ----
        Make("MXF · XDCAM HD422 1080p50 50", PresetCategory.Mxf, "MPEG-2 4:2:2 50 Mbps CBR · 1920×1080p50 (50/1) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 50; p.FrameRateDen = 1; p.VideoBitrateMbps = 50; p.MaxBitrateMbps = 50; p.GopSize = 15; p.PixelFormat = PixelFormat.Yuv422p;
          p.AudioCodec = AudioCodec.Pcm; }),

        Make("MXF · XDCAM HD422 720p50 50", PresetCategory.Mxf, "MPEG-2 4:2:2 50 Mbps CBR · 1280×720p50 (50/1) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.Mpeg2Video; p.Width = 1280; p.Height = 720;
          p.FrameRateNum = 50; p.FrameRateDen = 1; p.VideoBitrateMbps = 50; p.MaxBitrateMbps = 50; p.GopSize = 15; p.PixelFormat = PixelFormat.Yuv422p;
          p.AudioCodec = AudioCodec.Pcm; }),

        Make("MXF OP1A · DNxHR HQ 1080p50", PresetCategory.Mxf, "DNxHR HQ 4:2:2 8-bit · 1920×1080p50 (50/1) · MXF OP1A · PCM.", p =>
        { p.Container = ContainerFormat.Mxf; p.VideoCodec = VideoCodec.DnxHr; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 50; p.FrameRateDen = 1; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p; p.EncoderProfile = EncoderProfile.DnxHrHq;
          p.AudioCodec = AudioCodec.Pcm; }),

        // ---------------- AVI ----------------
        Make("AVI · H.264 1080p25", PresetCategory.Avi, "AVI con H.264 1920×1080p25, 20 Mbps, audio PCM.", p =>
        { p.Container = ContainerFormat.Avi; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 20; p.MaxBitrateMbps = 25; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.AudioCodec = AudioCodec.Pcm; }),

        // ---------------- MKV ----------------
        Make("MKV · H.264 1080p25", PresetCategory.Mkv, "Matroska con H.264 1920×1080p25, 16 Mbps, AAC.", p =>
        { p.Container = ContainerFormat.Mkv; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 16; p.MaxBitrateMbps = 20; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p; }),

        Make("MKV · HEVC 4K 10-bit", PresetCategory.Mkv, "Matroska con HEVC 3840×2160p50 10-bit, 35 Mbps VBR.", p =>
        { p.Container = ContainerFormat.Mkv; p.VideoCodec = VideoCodec.H265x265; p.Width = 3840; p.Height = 2160;
          p.FrameRateNum = 50; p.VideoBitrateMbps = 35; p.MaxBitrateMbps = 45; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p10le;
          p.RateControl = RateControlMode.VariableBitrate; }),

        // ---------------- Audio ----------------
        Make("Audio · WAV PCM 48k 24-bit", PresetCategory.Audio, "Solo audio: WAV PCM 24-bit, 48 kHz, estéreo.", p =>
        { p.AudioOnly = true; p.Container = ContainerFormat.Wav; p.AudioCodec = AudioCodec.Pcm; p.AudioSampleRate = 48_000; }),

        Make("Audio · AAC 256k (M4A)", PresetCategory.Audio, "Solo audio: AAC 256 kbps, 48 kHz, estéreo.", p =>
        { p.AudioOnly = true; p.Container = ContainerFormat.Mp4; p.AudioCodec = AudioCodec.Aac; p.AudioBitrateKbps = 256; }),

        Make("Audio · MP3 320k", PresetCategory.Audio, "Solo audio: MP3 320 kbps, 48 kHz, estéreo.", p =>
        { p.AudioOnly = true; p.Container = ContainerFormat.Mp3Audio; p.AudioCodec = AudioCodec.Mp3; p.AudioBitrateKbps = 320; }),

        // ---------------- Proxy ----------------
        Make("Proxy · H.264 540p", PresetCategory.Proxy, "Edición/revisión: 960×540p25, 3 Mbps, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 960; p.Height = 540;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 3; p.MaxBitrateMbps = 3; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.AudioBitrateKbps = 128; }),

        Make("Proxy · H.264 360p", PresetCategory.Proxy, "Ligero: 640×360p25, 1.5 Mbps, MP4, AAC.", p =>
        { p.Container = ContainerFormat.Mp4; p.VideoCodec = VideoCodec.H264x264; p.Width = 640; p.Height = 360;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 1.5; p.MaxBitrateMbps = 1.5; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.AudioBitrateKbps = 96; }),

        // ---------------- Archive ----------------
        Make("Archive · HEVC 1080p CRF20", PresetCategory.Archive, "Archivado eficiente: HEVC calidad constante (CRF 20), MKV.", p =>
        { p.Container = ContainerFormat.Mkv; p.VideoCodec = VideoCodec.H265x265; p.Width = 1920; p.Height = 1080;
          p.RateControl = RateControlMode.ConstantQuality; p.Quality = 20; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.AudioBitrateKbps = 192; }),

        Make("Archive · ProRes 422 1080p", PresetCategory.Archive, "Masterización: ProRes 422 HQ 1080p25 10-bit, MOV, PCM.", p =>
        { p.Container = ContainerFormat.Mov; p.VideoCodec = VideoCodec.ProRes; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.GopSize = 1; p.PixelFormat = PixelFormat.Yuv422p10le; p.EncoderProfile = EncoderProfile.ProResHq; p.AudioCodec = AudioCodec.Pcm; }),

        // ---------------- Streaming (IPTV / RTMP / SRT) ----------------
        Make("IPTV · H.264 720p TS (UDP)", PresetCategory.Streaming, "1280×720p25, 4 Mbps CBR, MPEG-TS para UDP/IPTV.", p =>
        { p.Container = ContainerFormat.Ts; p.VideoCodec = VideoCodec.H264x264; p.Width = 1280; p.Height = 720;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 4; p.MaxBitrateMbps = 4; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.AudioBitrateKbps = 128; p.StreamProtocol = StreamProtocol.Udp; }),

        Make("RTMP · H.264 1080p", PresetCategory.Streaming, "1920×1080p25, 6 Mbps CBR, GOP 2 s, para RTMP.", p =>
        { p.Container = ContainerFormat.Ts; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 6; p.MaxBitrateMbps = 6; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.AudioBitrateKbps = 128; p.StreamProtocol = StreamProtocol.Rtmp; }),

        Make("SRT · H.264 1080p", PresetCategory.Streaming, "1920×1080p25, 8 Mbps CBR, baja latencia, para SRT.", p =>
        { p.Container = ContainerFormat.Ts; p.VideoCodec = VideoCodec.H264x264; p.Width = 1920; p.Height = 1080;
          p.FrameRateNum = 25; p.VideoBitrateMbps = 8; p.MaxBitrateMbps = 8; p.GopSize = 50; p.PixelFormat = PixelFormat.Yuv420p;
          p.AudioBitrateKbps = 160; p.StreamProtocol = StreamProtocol.Srt; }),
    };

    private static EncodingPreset Make(string name, PresetCategory category, string description, Action<EncodingPreset> configure)
    {
        var preset = new EncodingPreset { Name = name, Category = category, Description = description, IsBuiltIn = true };
        configure(preset);
        preset.Id = StableGuid("preset:" + name); // GUID determinista → favoritos estables entre reinicios
        return preset;
    }

    /// <summary>GUID determinista a partir de una clave (MD5), para identidad estable de los built-in.</summary>
    private static Guid StableGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));
}
