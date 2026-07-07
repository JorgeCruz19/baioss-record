using Baioss.Record.Domain.ValueObjects;

namespace Baioss.Record.Domain.Events;

/// <summary>Marcador para todos los eventos de dominio publicados en el bus interno.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

public abstract record DomainEventBase : IDomainEvent
{
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.UtcNow;
}

// --- Eventos de grabación ---
public sealed record RecordingStarted(Guid ChannelId, Guid SessionId, string? Operator) : DomainEventBase;
public sealed record RecordingStopped(Guid ChannelId, Guid SessionId, TimeSpan Duration) : DomainEventBase;
public sealed record RecordingPaused(Guid ChannelId, Guid SessionId) : DomainEventBase;
public sealed record RecordingResumed(Guid ChannelId, Guid SessionId) : DomainEventBase;
public sealed record SegmentCompleted(Guid SessionId, int Index, string FilePath, long SizeBytes) : DomainEventBase;
public sealed record EncoderFailed(Guid ChannelId, Guid SessionId, string Reason) : DomainEventBase;
public sealed record RecordingRecovered(Guid ChannelId, Guid SessionId, int Attempt) : DomainEventBase;

// --- Eventos de señal ---
public sealed record SignalLocked(Guid ChannelId, Resolution Resolution, FrameRate FrameRate) : DomainEventBase;
public sealed record SignalLost(Guid ChannelId) : DomainEventBase;
public sealed record AudioSilenceDetected(Guid ChannelId, TimeSpan ForDuration) : DomainEventBase;
public sealed record AudioClippingDetected(Guid ChannelId, double PeakDb) : DomainEventBase;

// --- Eventos de sistema ---
public sealed record StorageLow(Guid ChannelId, long FreeBytes, TimeSpan EstimatedRemaining) : DomainEventBase;
public sealed record PerformanceDegraded(string Resource, double Value) : DomainEventBase;

// --- Eventos de retención / almacenamiento (auditoría de la limpieza automática) ---
/// <summary>La retención borró o archivó una sesión expirada (auditoría: canal, sesión, nº de archivos, acción, motivo).</summary>
public sealed record RecordingPurged(Guid ChannelId, Guid SessionId, int Files, RetentionAction Action, string Reason) : DomainEventBase;
/// <summary>La retención OMITIÓ una sesión candidata (protegida, archivo en uso/bloqueado…): motivo para la auditoría.</summary>
public sealed record RetentionSkipped(Guid ChannelId, Guid SessionId, string Reason) : DomainEventBase;

// --- Emergencia de almacenamiento (Fase 3b): el coordinador global vigila el volumen de grabación (también en
//     IDLE, que la guarda por-canal no cubre) y emite estas transiciones para auditoría/UI. ---
/// <summary>El volumen de grabación ENTRÓ en emergencia de espacio (≥ umbral, p. ej. 95% ocupado): hay que liberar
/// espacio ya. Lo emite el coordinador global de emergencia. (Fase 3b.)</summary>
public sealed record StorageEmergencyEntered(string Volume, long FreeBytes, long TotalBytes, double UsedPercent) : DomainEventBase;
/// <summary>El volumen de grabación SALIÓ de la emergencia de espacio (bajó del umbral, con histéresis). (Fase 3b.)</summary>
public sealed record StorageEmergencyCleared(string Volume, long FreeBytes, long TotalBytes, double UsedPercent) : DomainEventBase;
