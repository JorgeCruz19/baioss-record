using Baioss.Record.Domain;
using Baioss.Record.Application.Presets;

namespace Baioss.Record.App.Presets;

/// <summary>
/// ViewModel del editor de un preset personalizado. Bindea directamente al
/// <see cref="EncodingPreset"/> (borrador); expone los valores de enum para los ComboBox y
/// proxies numéricos para los campos opcionales (resolución/fps como 0 = nativa).
/// </summary>
public sealed class PresetEditorViewModel
{
    public EncodingPreset Preset { get; }

    public PresetEditorViewModel(EncodingPreset preset) => Preset = preset;

    public Array CategoryValues => Enum.GetValues<PresetCategory>();
    public Array ContainerValues => Enum.GetValues<ContainerFormat>();
    public Array VideoCodecValues => Enum.GetValues<VideoCodec>();
    public Array AudioCodecValues => Enum.GetValues<AudioCodec>();
    public Array AudioLayoutValues => Enum.GetValues<AudioLayout>();
    public Array ScanTypeValues => Enum.GetValues<ScanType>();
    public Array RateControlValues => Enum.GetValues<RateControlMode>();
    public Array PixelFormatValues => Enum.GetValues<PixelFormat>();

    // Proxies numéricos (0 = nativa/auto) para enlazar TextBox sin lidiar con nullables.
    public int WidthValue { get => Preset.Width ?? 0; set => Preset.Width = value <= 0 ? null : value; }
    public int HeightValue { get => Preset.Height ?? 0; set => Preset.Height = value <= 0 ? null : value; }
    public int FrameRateValue { get => Preset.FrameRateNum; set => Preset.FrameRateNum = value; }
    public double VideoBitrateValue { get => Preset.VideoBitrateMbps; set => Preset.VideoBitrateMbps = value; }
    public double MaxBitrateValue { get => Preset.MaxBitrateMbps; set => Preset.MaxBitrateMbps = value; }
    public int GopValue { get => Preset.GopSize; set => Preset.GopSize = value; }
    public int AudioSampleRateValue { get => Preset.AudioSampleRate; set => Preset.AudioSampleRate = value; }
    public int AudioBitrateValue { get => Preset.AudioBitrateKbps; set => Preset.AudioBitrateKbps = value; }
}
