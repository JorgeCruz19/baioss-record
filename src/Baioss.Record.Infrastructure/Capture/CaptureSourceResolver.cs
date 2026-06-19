using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Selecciona la <see cref="ICaptureSourceFactory"/> adecuada según el <see cref="InputType"/> de
/// una <see cref="InputSource"/> y construye la fuente. Añadir un protocolo (NDI, SRT…) es registrar
/// una fábrica más: el resto del sistema no cambia (principio Open/Closed).
/// </summary>
public sealed class CaptureSourceResolver
{
    private readonly IReadOnlyList<ICaptureSourceFactory> _factories;

    public CaptureSourceResolver(IEnumerable<ICaptureSourceFactory> factories)
        => _factories = factories.ToList();

    /// <summary>True si hay una fábrica registrada capaz de abrir ese tipo de entrada.</summary>
    public bool CanHandle(InputType type) => _factories.Any(f => f.CanHandle(type));

    public ICaptureSource Create(InputSource definition)
    {
        var factory = _factories.FirstOrDefault(f => f.CanHandle(definition.Type))
            ?? throw new NotSupportedException($"No hay fábrica de captura para el tipo {definition.Type}.");
        return factory.Create(definition);
    }
}
