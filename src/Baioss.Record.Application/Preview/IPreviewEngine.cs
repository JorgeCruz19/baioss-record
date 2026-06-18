using Baioss.Record.Application.Capture;

namespace Baioss.Record.Application.Preview;

public enum PreviewMode { Preview, Program, Fullscreen }

/// <summary>Niveles de audio para medidores (VU/Peak) y detección de clipping.</summary>
public readonly record struct AudioLevels(double PeakDb, double RmsDb, bool Clipping);

/// <summary>Tipos de scope de video disponibles para el monitor profesional.</summary>
[Flags]
public enum ScopeKind
{
    None = 0,
    Waveform = 1,
    Vectorscope = 2,
    Histogram = 4,
    Rgbparade = 8
}

/// <summary>
/// Motor de preview de baja latencia y render por GPU. Entrega un handle de
/// textura D3D11 compartida que WPF compone vía D3DImage (cero copias a CPU).
/// </summary>
public interface IPreviewEngine : IAsyncDisposable
{
    PreviewMode Mode { get; set; }
    ScopeKind ActiveScopes { get; set; }
    bool ShowSafeArea { get; set; }
    bool ShowTimecode { get; set; }

    /// <summary>Handle de textura D3D11 compartida para interop WPF (D3DImage/SharpDX).</summary>
    nint SharedTextureHandle { get; }

    event EventHandler<AudioLevels>? AudioLevelsUpdated;

    Task StartAsync(ICaptureSource source, CancellationToken ct = default);
    Task StopAsync(CancellationToken ct = default);
}
