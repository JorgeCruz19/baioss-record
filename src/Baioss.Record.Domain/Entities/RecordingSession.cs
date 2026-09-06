using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Domain.Entities;

/// <summary>
/// Una sesión de grabación: el lapso entre Start y Stop de un canal. Agrupa uno o
/// más <see cref="Segment"/> y concentra la metadata exportable (XML/JSON/CSV).
/// </summary>
public sealed class RecordingSession
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid ChannelId { get; init; }
    public required Guid ProfileId { get; init; }
    public required Guid InputSourceId { get; init; }

    public RecordingState State { get; set; } = RecordingState.Idle;

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>Operador que inició la sesión (auditoría).</summary>
    public string? Operator { get; set; }

    /// <summary>Cómo se puso en marcha: a mano, por la programación o por la API. Es el discriminante fiable
    /// entre grabación manual y programada (<see cref="Operator"/> es solo una etiqueta).
    /// <see cref="RecordingTrigger.Unknown"/> en las grabaciones anteriores a esta columna. (Auditoría.)</summary>
    public RecordingTrigger Trigger { get; set; } = RecordingTrigger.Unknown;

    /// <summary>Por qué terminó. <see cref="RecordingStopReason.Unknown"/> mientras sigue en curso, y también
    /// en las sesiones anteriores a que existiera esta columna. (Auditoría.)</summary>
    public RecordingStopReason StopReason { get; set; } = RecordingStopReason.Unknown;

    /// <summary>Tarea programada que la disparó, si <see cref="Trigger"/> es
    /// <see cref="RecordingTrigger.Scheduled"/>. Permite rastrear un archivo hasta su programación. (Auditoría.)</summary>
    public Guid? ScheduledJobId { get; set; }

    /// <summary>Protección frente a la limpieza automática: cualquier valor ≠ None EXCLUYE la sesión de la
    /// retención (nunca se borra ni archiva automáticamente). Lo marca el operador. (Gestión de almacenamiento.)</summary>
    public RecordingProtection Protection { get; set; } = RecordingProtection.None;

    // --- Características reales capturadas al hacer lock ---
    public Resolution? Resolution { get; set; }
    public FrameRate? FrameRate { get; set; }
    public AudioLayout AudioLayout { get; set; } = AudioLayout.Stereo;
    public VideoCodec VideoCodec { get; set; }
    public AudioCodec AudioCodec { get; set; }
    public Timecode? StartTimecode { get; set; }
    public Timecode? EndTimecode { get; set; }

    public List<Segment> Segments { get; init; } = new();

    /// <summary>Metadata libre adicional (clave/valor) para exportación.</summary>
    public Dictionary<string, string> Metadata { get; init; } = new();

    public TimeSpan Duration =>
        (EndedAt ?? DateTimeOffset.UtcNow) - StartedAt;

    public long TotalBytes => Segments.Sum(s => s.SizeBytes);
}
