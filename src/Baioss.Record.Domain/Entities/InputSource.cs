using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Domain.Entities;

/// <summary>
/// Definición persistente de una fuente de entrada (SDI, IP, archivo o cámara).
/// Los parámetros específicos del protocolo viajan en <see cref="Parameters"/>.
/// </summary>
public sealed class InputSource
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required InputType Type { get; set; }

    /// <summary>
    /// URI/identificador dependiente del tipo. Ejemplos:
    /// DeckLink → "DeckLink 8K Pro (1)"; SRT → "srt://0.0.0.0:9000"; NDI → "STUDIO-PC (Cam 1)".
    /// </summary>
    public string? Uri { get; set; }

    /// <summary>Parámetros adicionales por protocolo (latencia SRT, passphrase, pixel format, etc.).</summary>
    public Dictionary<string, string> Parameters { get; init; } = new();

    // Características nominales esperadas (se validan contra la señal real al hacer lock).
    public Resolution? ExpectedResolution { get; set; }
    public FrameRate? ExpectedFrameRate { get; set; }
    public AudioLayout ExpectedAudioLayout { get; set; } = AudioLayout.Stereo;
    public TimecodeSource TimecodeSource { get; set; } = TimecodeSource.Embedded;
}
