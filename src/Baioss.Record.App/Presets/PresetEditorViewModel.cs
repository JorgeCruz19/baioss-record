using Baioss.Record.App.Localization;
using Baioss.Record.Domain;
using Baioss.Record.Application.Presets;
using Baioss.Record.Application.Recording;

namespace Baioss.Record.App.Presets;

/// <summary>
/// ViewModel del editor de un preset personalizado. Bindea directamente al
/// <see cref="EncodingPreset"/> (borrador); expone los valores de enum para los ComboBox y
/// proxies numéricos para los campos opcionales (resolución como 0 = nativa, cadencia vacía = la de la fuente).
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
    public Array AudioTrackValues => Enum.GetValues<AudioTrackMode>();
    public Array ScanTypeValues => Enum.GetValues<ScanType>();
    public Array RateControlValues => Enum.GetValues<RateControlMode>();
    public Array PixelFormatValues => Enum.GetValues<PixelFormat>();
    public Array EncoderProfileValues => Enum.GetValues<EncoderProfile>();

    // Proxies numéricos (0 = nativa/auto) para enlazar TextBox sin lidiar con nullables.
    public int WidthValue { get => Preset.Width ?? 0; set => Preset.Width = value <= 0 ? null : value; }
    public int HeightValue { get => Preset.Height ?? 0; set => Preset.Height = value <= 0 ? null : value; }
    public double VideoBitrateValue { get => Preset.VideoBitrateMbps; set => Preset.VideoBitrateMbps = value; }
    public double MaxBitrateValue { get => Preset.MaxBitrateMbps; set => Preset.MaxBitrateMbps = value; }
    public int GopValue { get => Preset.GopSize; set => Preset.GopSize = value; }
    public int AudioSampleRateValue { get => Preset.AudioSampleRate; set => Preset.AudioSampleRate = value; }
    public int AudioBitrateValue { get => Preset.AudioBitrateKbps; set => Preset.AudioBitrateKbps = value; }

    /// <summary>La cadencia como la escribe el operador («25», «29.97», «59.94» o «30000/1001»; vacía = la de la fuente), guardada
    /// exacta en numerador y denominador (ver <see cref="PresetFrameRate"/>). Antes era el numerador a secas: un preset nuevo no podía
    /// ser 29.97 y en la copia de uno 29.97 escribir 25 dejaba 25/1001.</summary>
    public string FrameRateText
    {
        get => _frameRateText ??= PresetFrameRate.Format(Preset.FrameRateNum, Preset.FrameRateDen);
        set
        {
            _frameRateText = value;
            _frameRateUnreadable = !PresetFrameRate.TryParse(value, out int numerator, out int denominator);
            if (_frameRateUnreadable) return;
            Preset.FrameRateNum = numerator;
            Preset.FrameRateDen = denominator;
        }
    }

    private string? _frameRateText;
    private bool _frameRateUnreadable;

    /// <summary>Por qué el borrador no podría grabar (las mismas reglas que el pre-vuelo al pulsar Grabar, y una cadencia que no se
    /// entiende); vacío = se puede guardar. Así un preset roto —HEVC en AVI, ProRes en MP4, bitrate a cero…— no llega a la lista ni a
    /// un canal.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = RecordingProfileValidator.Validate(Preset.ToProfile()).ToList();
        if (_frameRateUnreadable) problems.Insert(0, Loc.F("PreEd_Err_Fps", _frameRateText ?? ""));
        return problems;
    }
}
