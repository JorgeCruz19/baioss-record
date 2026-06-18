using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Domain.Entities;

/// <summary>
/// Perfil reutilizable que define cómo se codifica, segmenta, transmite y
/// genera proxy una grabación. Es la "receta" que se pasa al motor FFmpeg.
/// </summary>
public sealed class RecordingProfile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }

    // --- Video ---
    public VideoCodec VideoCodec { get; set; } = VideoCodec.HevcNvenc;
    public HwAccel HwAccel { get; set; } = HwAccel.Nvenc;
    public Bitrate VideoBitrate { get; set; } = Bitrate.FromMbps(50);

    /// <summary>Resolución de salida. <c>null</c> = nativa de la fuente (sin escalado).</summary>
    public Resolution? TargetResolution { get; set; }

    /// <summary>Frecuencia de cuadro de salida. <c>null</c> = la de la fuente (sin re-temporizar).</summary>
    public FrameRate? OutputFrameRate { get; set; }

    /// <summary>Escaneo de salida: progresivo o entrelazado (TFF/BFF).</summary>
    public ScanType ScanType { get; set; } = ScanType.Progressive;

    /// <summary>Modo de control de tasa (CBR/VBR/calidad constante).</summary>
    public RateControlMode RateControl { get; set; } = RateControlMode.ConstantBitrate;

    /// <summary>Valor de calidad (CRF/CQ) cuando <see cref="RateControl"/> = ConstantQuality. Menor = mejor.</summary>
    public int Quality { get; set; } = 23;

    public int GopSize { get; set; } = 50;
    public bool ClosedGop { get; set; } = true;

    // --- Audio ---
    public AudioCodec AudioCodec { get; set; } = AudioCodec.Pcm;
    public AudioLayout AudioLayout { get; set; } = AudioLayout.Stereo;
    public Bitrate AudioBitrate { get; set; } = Bitrate.FromKbps(256);
    public int AudioSampleRate { get; set; } = 48_000;

    // --- Contenedor / overlays ---
    public ContainerFormat Container { get; set; } = ContainerFormat.Mxf;
    public bool BurnTimecode { get; set; }

    // --- Sub-políticas (todas opcionales) ---
    public SegmentationPolicy? Segmentation { get; set; }
    public ProxyProfile? Proxy { get; set; }
    public IList<StreamTarget> StreamTargets { get; init; } = new List<StreamTarget>();
}

/// <summary>Define cuándo cortar a un nuevo archivo de segmento.</summary>
public sealed class SegmentationPolicy
{
    public SegmentTrigger Trigger { get; set; } = SegmentTrigger.Duration;

    /// <summary>Umbral de duración cuando Trigger = Duration.</summary>
    public TimeSpan? Duration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Umbral de tamaño en bytes cuando Trigger = Size.</summary>
    public long? MaxBytes { get; set; }

    /// <summary>Alinear cortes con bordes de reloj (ej. cada hora en punto) cuando Trigger = WallClock.</summary>
    public bool AlignToWallClock { get; set; }

    /// <summary>Patrón de nombre con tokens: {channel} {date} {time} {index} {tc}.</summary>
    public string FileNamePattern { get; set; } = "{channel}_{date}_{time}_{index}";
}

/// <summary>Configuración de generación de proxy en paralelo a la grabación principal.</summary>
public sealed class ProxyProfile
{
    public VideoCodec Codec { get; set; } = VideoCodec.H264Nvenc;
    public Resolution Resolution { get; set; } = new(960, 540);
    public Bitrate Bitrate { get; set; } = Bitrate.FromMbps(3);
    public ContainerFormat Container { get; set; } = ContainerFormat.Mp4;
}

/// <summary>Destino de streaming simultáneo (push) durante la grabación.</summary>
public sealed class StreamTarget
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required StreamProtocol Protocol { get; set; }
    public required string Url { get; set; }
    public Bitrate? Bitrate { get; set; }
    public Dictionary<string, string> Parameters { get; init; } = new();
    public bool Enabled { get; set; } = true;
}
