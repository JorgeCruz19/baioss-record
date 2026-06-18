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
    DnxHr
}

/// <summary>Códec de audio de grabación.</summary>
public enum AudioCodec
{
    Pcm,
    Aac,
    FdkAac,
    Opus,
    Mp3
}

/// <summary>Contenedor de salida.</summary>
public enum ContainerFormat
{
    Mp4,
    Mov,
    Mxf,
    Mkv,
    Ts
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
