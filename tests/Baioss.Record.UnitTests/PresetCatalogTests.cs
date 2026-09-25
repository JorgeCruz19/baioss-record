using Baioss.Record.Application.Presets;
using Baioss.Record.Domain;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Valida el catálogo de presets de fábrica. Protege dos invariantes que, de romperse, fallarían
/// en silencio: (1) identidad estable de los built-in (Id/Nombre únicos → favoritos no colisionan),
/// y (2) que el entrelazado SOLO se ofrece donde es técnicamente válido (SD/1080, códecs que lo
/// codifican). NVENC/QSV/AMF NO codifican entrelazado y 720/4K no existen entrelazados en broadcast.
/// </summary>
public sealed class PresetCatalogTests
{
    private static readonly IReadOnlyList<EncodingPreset> Catalog = PresetCatalog.CreateBuiltIns();

    private static bool IsInterlaced(EncodingPreset p) =>
        p.ScanType is ScanType.InterlacedTff or ScanType.InterlacedBff;

    [Fact]
    public void BuiltIns_HaveUniqueIdsAndNames()
    {
        // El Id es un GUID determinista del nombre (MD5): un nombre duplicado colisiona el Id y
        // un favorito "salta" de un preset a otro entre reinicios. Ambos deben ser únicos.
        Assert.Equal(Catalog.Count, Catalog.Select(p => p.Name).Distinct().Count());
        Assert.Equal(Catalog.Count, Catalog.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void InterlacedPresets_OnlyUseValidHeights()
    {
        // Entrelazado real de broadcast: SD (480/576) y FullHD (1080). NUNCA 720 (siempre progresivo)
        // ni 2160/4K (siempre progresivo). La altura nula (nativa de la fuente) no aplica a entrelazado.
        var bad = Catalog.Where(IsInterlaced)
                         .Where(p => p.Height is not (480 or 576 or 1080))
                         .Select(p => $"{p.Name} (h={p.Height?.ToString() ?? "null"})")
                         .ToList();
        Assert.True(bad.Count == 0, "Presets entrelazados con altura inválida (solo 480/576/1080): " + string.Join(", ", bad));
    }

    [Fact]
    public void InterlacedPresets_NeverUseEncodersThatCannotInterlace()
    {
        // NVENC/QuickSync/AMF no codifican entrelazado; HEVC entrelazado está mal soportado por los
        // reproductores. El entrelazado se codifica por software intra/long-GOP: x264, MPEG-2, ProRes, DNxHR.
        VideoCodec[] forbidden =
        {
            VideoCodec.H264Nvenc, VideoCodec.HevcNvenc, VideoCodec.Av1Nvenc,
            VideoCodec.H264Qsv, VideoCodec.H264Amf, VideoCodec.H265x265,
        };
        var bad = Catalog.Where(IsInterlaced)
                         .Where(p => forbidden.Contains(p.VideoCodec))
                         .Select(p => $"{p.Name} ({p.VideoCodec})")
                         .ToList();
        Assert.True(bad.Count == 0, "Presets entrelazados con un encoder que no codifica entrelazado: " + string.Join(", ", bad));
    }

    [Fact]
    public void Catalog_CoversInterlaced_BothPalAndNtsc()
    {
        var interlaced = Catalog.Where(IsInterlaced).ToList();
        Assert.NotEmpty(interlaced);

        // PAL/europeo: 25 cuadros entrelazados (1080i25, 576i25…).
        Assert.Contains(interlaced, p => p.FrameRateNum == 25 && p.FrameRateDen == 1);
        // NTSC/americano (zona del usuario): 30000/1001 entrelazado (1080i59.94, 480i59.94…).
        Assert.Contains(interlaced, p => p.FrameRateNum == 30000 && p.FrameRateDen == 1001);

        // La familia NTSC entrelazada debe existir tanto en 1080 como en SD.
        Assert.Contains(interlaced, p => p is { Height: 1080, FrameRateNum: 30000, FrameRateDen: 1001 });
        Assert.Contains(interlaced, p => p is { Height: 480, FrameRateNum: 30000, FrameRateDen: 1001 });
    }

    private static bool IsNvenc(EncodingPreset p) =>
        p.VideoCodec is VideoCodec.H264Nvenc or VideoCodec.HevcNvenc or VideoCodec.Av1Nvenc;

    [Fact]
    public void NvencPresets_AreProgressiveMp4WithAutoPixelFormat()
    {
        // Píxel Auto, no Yuv420p: sin NVIDIA el canal degrada a QuickSync/AMF/CPU con el MISMO perfil, y un
        // -pix_fmt fijo viajaría con él (h264_qsv no acepta yuv420p; NVENC tampoco yuv420p10le). Con Auto cada
        // codificador recibe su formato nativo. Y NVENC no codifica entrelazado.
        var nvenc = Catalog.Where(IsNvenc).ToList();
        Assert.NotEmpty(nvenc);
        var bad = nvenc.Where(p => p.PixelFormat != PixelFormat.Auto || p.ScanType != ScanType.Progressive
                                   || p.Container != ContainerFormat.Mp4 || p.AudioOnly)
                       .Select(p => $"{p.Name} ({p.PixelFormat}, {p.ScanType}, {p.Container})")
                       .ToList();
        Assert.True(bad.Count == 0, "Presets NVENC que no son MP4 progresivo con píxel Auto: " + string.Join(", ", bad));
    }

    [Fact]
    public void NvencH264Presets_MirrorTheirCpuCounterparts()
    {
        // Cada H.264 NVENC es el gemelo por GPU de un H.264 de CPU del catálogo: misma resolución, cadencia,
        // bitrate y GOP → cambiar uno por otro solo cambia quién codifica, no el archivo que se entrega.
        var cpu = Catalog.Where(p => p is { VideoCodec: VideoCodec.H264x264, Container: ContainerFormat.Mp4, ScanType: ScanType.Progressive })
                         .ToList();
        var orphans = Catalog.Where(p => p.VideoCodec == VideoCodec.H264Nvenc)
                             .Where(g => !cpu.Any(c => c.Width == g.Width && c.Height == g.Height
                                                       && c.FrameRateNum == g.FrameRateNum && c.FrameRateDen == g.FrameRateDen
                                                       && c.VideoBitrateMbps == g.VideoBitrateMbps && c.GopSize == g.GopSize))
                             .Select(g => g.Name)
                             .ToList();
        Assert.True(orphans.Count == 0, "Presets H.264 NVENC sin gemelo de CPU equivalente: " + string.Join(", ", orphans));
    }

    [Theory]
    [InlineData(VideoCodec.H264Nvenc, 25, 1)]
    [InlineData(VideoCodec.H264Nvenc, 30000, 1001)]
    [InlineData(VideoCodec.H264Nvenc, 50, 1)]
    [InlineData(VideoCodec.H264Nvenc, 60000, 1001)]
    [InlineData(VideoCodec.H264Nvenc, 60, 1)]
    [InlineData(VideoCodec.HevcNvenc, 25, 1)]
    [InlineData(VideoCodec.HevcNvenc, 30000, 1001)]
    [InlineData(VideoCodec.HevcNvenc, 50, 1)]
    [InlineData(VideoCodec.HevcNvenc, 60000, 1001)]
    public void Catalog_OffersNvenc1080_InPalAndNtscCadences(VideoCodec codec, int num, int den)
        // Grabar MP4 por GPU no debe depender de la zona: 1080p en las cadencias PAL (25/50) y NTSC (29.97/59.94).
        => Assert.Contains(Catalog, p => p.VideoCodec == codec && p.Height == 1080
                                         && p.FrameRateNum == num && p.FrameRateDen == den);
}
