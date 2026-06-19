namespace Baioss.Record.Domain;

/// <summary>Tipo físico/lógico de la fuente de entrada de un canal.</summary>
public enum InputType
{
    DecklinkSdi,
    Ndi,
    SrtCaller,
    SrtListener,
    Rtmp,
    Rtp,
    Udp,
    MpegTs,
    File,
    DirectShow,
    MediaFoundation
}

/// <summary>Aceleración de hardware seleccionada para decode/encode.</summary>
public enum HwAccel
{
    None,
    Nvenc,   // NVIDIA encode
    Nvdec,   // NVIDIA decode
    Amf,     // AMD
    QuickSync // Intel
}

/// <summary>Códec de video de grabación.</summary>
public enum VideoCodec
{
    H264Nvenc,
    HevcNvenc,
    Av1Nvenc,
    H264x264,
    H265x265,
    ProRes,
    DnxHd,
    DnxHr,
    Mpeg2Video // MPEG-2 (PS/TS, XDCAM HD422)
}

/// <summary>Códec de audio de grabación.</summary>
public enum AudioCodec
{
    Pcm,
    Aac,
    FdkAac,
    Opus,
    Mp3,
    Mp2 // MPEG-1/2 Audio Layer II (broadcast clásico)
}

/// <summary>Contenedor de salida.</summary>
public enum ContainerFormat
{
    Mp4,
    Mov,
    Mxf,
    Mkv,
    Ts,
    Avi,
    ProgramStream, // MPEG-2 Program Stream (.mpg)
    Wav,           // audio: WAV/PCM
    Mp3Audio       // audio: MP3
}

/// <summary>Distribución de canales de audio.</summary>
public enum AudioLayout
{
    Mono,
    Stereo,
    Surround51,
    Surround71
}

/// <summary>Estado de máquina del motor de grabación de un canal.</summary>
public enum RecordingState
{
    Idle,
    Starting,
    Recording,
    Paused,
    Stopping,
    Error,
    Recovering
}

/// <summary>Estado de bloqueo (lock) de la señal de entrada.</summary>
public enum SignalState
{
    NoSignal,
    Unstable,
    Locked
}

/// <summary>Tipo de escaneo de la salida de video.</summary>
public enum ScanType
{
    Progressive,    // progresivo (1080p, 720p…)
    InterlacedTff,  // entrelazado, campo superior primero (1080i TFF)
    InterlacedBff   // entrelazado, campo inferior primero (BFF)
}

/// <summary>Modo de control de tasa del encoder de video.</summary>
public enum RateControlMode
{
    ConstantBitrate,   // CBR: tasa fija (broadcast/transporte)
    VariableBitrate,   // VBR: tasa media objetivo, varía con la complejidad
    ConstantQuality    // calidad constante (CRF/CQ): tamaño variable
}

/// <summary>Origen del timecode aplicado a la grabación.</summary>
public enum TimecodeSource
{
    Embedded,
    Ltc,
    Vitc,
    SystemClock
}

/// <summary>Disparador de corte de segmento.</summary>
public enum SegmentTrigger
{
    Duration,   // cada N minutos
    Size,       // cada N bytes
    WallClock,  // en bordes horarios (cada hora en punto, etc.)
    Manual,
    Event       // disparado por automatización/API
}

/// <summary>Protocolo de salida para streaming simultáneo.</summary>
public enum StreamProtocol
{
    Srt,
    Rtmp,
    Ndi,
    Udp
}

/// <summary>Acción aplicada por una política de retención.</summary>
public enum RetentionAction
{
    Delete,
    Archive
}

/// <summary>Acción que ejecuta un trabajo programado.</summary>
public enum ScheduledAction
{
    StartRecording,
    StopRecording,
    SwitchProfile,
    SwitchSource
}

/// <summary>Rol de usuario para control de acceso.</summary>
public enum UserRole
{
    Administrator,
    Supervisor,
    Operator
}

/// <summary>Severidad de una entrada del registro de eventos/auditoría.</summary>
public enum EventSeverity
{
    Debug,
    Info,
    Warning,
    Error,
    Critical
}

/// <summary>Formato de muestreo de píxel (profundidad de bits y submuestreo de croma).</summary>
public enum PixelFormat
{
    Auto,        // el predeterminado del códec
    Yuv420p,     // 8-bit 4:2:0 (H.264/HEVC/MPEG-2 SD/HD)
    Yuv422p,     // 8-bit 4:2:2 (DNxHD, XDCAM HD422)
    Yuv420p10le, // 10-bit 4:2:0 (HEVC Main10)
    Yuv422p10le  // 10-bit 4:2:2 (ProRes, DNxHR HQX, broadcast 10-bit)
}
