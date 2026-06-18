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
