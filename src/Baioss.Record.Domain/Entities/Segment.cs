using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Domain.Entities;

/// <summary>Estado de un archivo de segmento individual.</summary>
public enum SegmentStatus
{
    Recording,
    Completed,
    Corrupt,
    Missing
}

/// <summary>
/// Un archivo físico producido por la segmentación. La secuencia de segmentos de una
/// sesión, ordenada por <see cref="Index"/>, reconstruye la continuidad temporal completa.
/// </summary>
public sealed class Segment
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid SessionId { get; init; }

    /// <summary>Índice 0-based dentro de la sesión; garantiza orden y continuidad.</summary>
    public required int Index { get; init; }

    public required string FilePath { get; set; }
    public SegmentStatus Status { get; set; } = SegmentStatus.Recording;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public TimeSpan Duration { get; set; }
    public long SizeBytes { get; set; }

    public Timecode? StartTimecode { get; set; }
    public Timecode? EndTimecode { get; set; }

    /// <summary>Hash opcional para verificación de integridad/archivado.</summary>
    public string? Checksum { get; set; }
}
