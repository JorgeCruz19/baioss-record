using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Application.Presets;

/// <summary>Familia/formato del preset (panel izquierdo de la UI).</summary>
public enum PresetCategory
{
    Mpeg2,
    H264,
    H265,
    DnxHd,
    ProRes,
    Xdcam,
    Mxf,
    Avi,
    Mkv,
    Audio,
    Streaming,
    Proxy,
    Archive
}

/// <summary>
/// Preset de grabación/encoding: un conjunto completo de parámetros con nombre que el operador
/// elige sin tocar detalles técnicos. Campos planos para un JSON estable de import/export,
/// independiente de la entidad de dominio. <see cref="ToProfile"/> lo traduce a un
/// <see cref="RecordingProfile"/> que el motor FFmpeg sabe ejecutar.
/// </summary>
public sealed class EncodingPreset
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public PresetCategory Category { get; set; }
    public bool IsBuiltIn { get; set; }
    public bool IsFavorite { get; set; }

    // --- Video ---
    public ContainerFormat Container { get; set; } = ContainerFormat.Mp4;
    public VideoCodec VideoCodec { get; set; } = VideoCodec.H264x264;
    public int? Width { get; set; }                 // null = nativa de la fuente
    public int? Height { get; set; }
    public int FrameRateNum { get; set; }           // 0 = la de la fuente
    public int FrameRateDen { get; set; } = 1;
    public double VideoBitrateMbps { get; set; }
    public double MaxBitrateMbps { get; set; }      // 0 = sin límite explícito
    public int GopSize { get; set; } = 50;
    public PixelFormat PixelFormat { get; set; } = PixelFormat.Auto;
    public ScanType ScanType { get; set; } = ScanType.Progressive;
    public RateControlMode RateControl { get; set; } = RateControlMode.ConstantBitrate;
    public int Quality { get; set; } = 23;

    /// <summary>Preset solo de audio (sin pista de video).</summary>
    public bool AudioOnly { get; set; }

    // --- Audio ---
    public AudioCodec AudioCodec { get; set; } = AudioCodec.Aac;
    public AudioLayout AudioLayout { get; set; } = AudioLayout.Stereo;
    public int AudioSampleRate { get; set; } = 48_000;
    public int AudioBitrateKbps { get; set; } = 256;

    /// <summary>Protocolo sugerido para presets de streaming (IPTV/RTMP/SRT); informativo.</summary>
    public StreamProtocol? StreamProtocol { get; set; }

    public int AudioChannels => AudioLayout switch
    {
        AudioLayout.Mono => 1, AudioLayout.Stereo => 2,
        AudioLayout.Surround51 => 6, AudioLayout.Surround71 => 8, _ => 2
    };

    /// <summary>
    /// Traduce el preset a la "receta" de dominio que ejecuta el motor. Opcionalmente conserva
    /// el <paramref name="id"/>/<paramref name="name"/> del perfil del canal al aplicarlo.
    /// </summary>
    public RecordingProfile ToProfile(Guid? id = null, string? name = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = name ?? Name,
        Container = Container,
        VideoCodec = VideoCodec,
        HwAccel = HwAccel.None, // decode software; el encoder (incl. NVENC) recibe frames de CPU
        VideoBitrate = Bitrate.FromMbps(VideoBitrateMbps),
        MaxBitrate = MaxBitrateMbps > 0 ? Bitrate.FromMbps(MaxBitrateMbps) : null,
        TargetResolution = Width is { } w && Height is { } h ? new Resolution(w, h) : null,
        OutputFrameRate = FrameRateNum > 0 ? new FrameRate(FrameRateNum, FrameRateDen) : null,
        PixelFormat = PixelFormat,
        ScanType = ScanType,
        RateControl = RateControl,
        Quality = Quality,
        GopSize = GopSize,
        AudioOnly = AudioOnly,
        AudioCodec = AudioCodec,
        AudioLayout = AudioLayout,
        AudioSampleRate = AudioSampleRate,
        AudioBitrate = Bitrate.FromKbps(AudioBitrateKbps),
    };

    /// <summary>Copia editable (custom) con nueva identidad.</summary>
    public EncodingPreset DeepCopy()
    {
        var clone = (EncodingPreset)MemberwiseClone();
        clone.Id = Guid.NewGuid();
        clone.IsBuiltIn = false;
        return clone;
    }

    /// <summary>Copia que conserva el Id (borrador de edición para upsert).</summary>
    public EncodingPreset CloneKeepId() => (EncodingPreset)MemberwiseClone();
}
