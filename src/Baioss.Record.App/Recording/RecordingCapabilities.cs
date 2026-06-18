namespace Baioss.Record.App.Recording;

/// <summary>
/// Capacidades de codificación del equipo, detectadas al arranque. Hoy: si los encoders por
/// GPU (NVENC) están disponibles, para no ofrecer en la UI códecs que fallarían al grabar.
/// </summary>
public sealed class RecordingCapabilities
{
    public bool GpuEncoders { get; init; }
}
