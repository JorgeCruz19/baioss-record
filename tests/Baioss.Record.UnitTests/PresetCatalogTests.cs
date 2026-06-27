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
}
