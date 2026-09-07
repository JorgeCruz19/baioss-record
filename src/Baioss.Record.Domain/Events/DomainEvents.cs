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

// --- Eventos de grabación (la traza de auditoría del par manual / programada) ---
// El texto del registro de auditoría es el ToString() del record, así que cada propiedad que se añada aquí
// aparece automáticamente en la tabla EventLog. De ahí que lleven el CONTEXTO completo y no solo los ids.

/// <summary>Arrancó una grabación. <paramref name="Trigger"/> distingue manual de programada y de API;
/// <paramref name="ScheduledJobTitle"/> permite rastrear el archivo hasta la tarea que lo generó.</summary>
public sealed record RecordingStarted(
    Guid ChannelId, Guid SessionId, string? Operator, RecordingTrigger Trigger,
    Guid? ScheduledJobId = null, string? ScheduledJobTitle = null, string? RecordingName = null) : DomainEventBase;

/// <summary>Terminó una grabación. <paramref name="Reason"/> es lo que responde a «¿por qué se cortó?»:
/// parada del operador, fin de la franja programada, disco lleno, cierre de la aplicación…
/// <paramref name="Operator"/> se repite aquí (ya va en el inicio) para que la entrada de fin se lea SOLA:
/// en una tabla de auditoría, «de quién era esta grabación» no debería exigir buscar su pareja.</summary>
public sealed record RecordingStopped(
    Guid ChannelId, Guid SessionId, TimeSpan Duration, RecordingStopReason Reason,
    int Files = 0, long TotalBytes = 0, string? Operator = null) : DomainEventBase;

/// <summary>Una grabación NO llegó a arrancar (pre-vuelo, dispositivo, licencia…). Sin esto, un hueco en la
/// programación no deja ni rastro en la auditoría: solo faltaría el archivo.</summary>
public sealed record RecordingStartFailed(
    Guid ChannelId, string? Operator, RecordingTrigger Trigger, string Reason,
    Guid? ScheduledJobId = null, string? ScheduledJobTitle = null) : DomainEventBase;

/// <summary>
/// El proceso de grabación MURIÓ a mitad (caída, kill del watchdog, la entrada falló) y el motor siguió en una
/// pieza nueva. Es el suceso que explica un archivo cortado y un hueco de segundos en la grabación: sin él, la
/// auditoría solo mostraba «archivo cerrado» y «archivo cerrado», como si nada hubiera pasado.
/// <paramref name="ExitCode"/> es el código con el que salió FFmpeg (−1 = matado); <paramref name="Reason"/> lo que
/// se sabe del motivo (lo que dijo el watchdog o la última línea de error de FFmpeg).
/// </summary>
public sealed record RecordingInterrupted(Guid ChannelId, Guid SessionId, int ExitCode, string Reason) : DomainEventBase;

/// <summary>Un archivo recién cerrado NO pasó la verificación (sin pistas ni duración legibles): grabación dañada
/// o incompleta. Se emite además de la alarma en pantalla para que quede constancia de QUÉ archivo y de cuánto.</summary>
public sealed record RecordingFileUnverified(Guid ChannelId, Guid SessionId, string FilePath, long SizeBytes) : DomainEventBase;

/// <summary>El scheduler OMITIÓ una ocurrencia programada (el canal ya grababa, el canal no existe…). Es la
/// otra mitad del hueco: la grabación no se intentó siquiera.</summary>
public sealed record ScheduledRecordingSkipped(
    Guid ChannelId, Guid ScheduledJobId, string? ScheduledJobTitle, DateTimeOffset Occurrence, string Reason) : DomainEventBase;

/// <summary>Al arrancar se cerraron sesiones que habían quedado «grabando» de una ejecución anterior (corte de
/// luz, cierre abrupto). Deja constancia de que hubo una interrupción no controlada.</summary>
public sealed record OrphanSessionsClosed(int Count) : DomainEventBase;
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

/// <summary>El disco de destino de un canal DEJÓ DE RESPONDER (no acepta escrituras) mientras grababa. La
/// grabación espera sin cortar. Es la causa raíz real detrás de un «archivo cortado»: el disco, no FFmpeg.
/// Incidente 2026-09-06.</summary>
public sealed record StorageStalled(Guid ChannelId, string Volume) : DomainEventBase;

/// <summary>El disco de destino volvió a responder tras <paramref name="Duration"/> sin aceptar escrituras.</summary>
public sealed record StorageStallCleared(Guid ChannelId, string Volume, TimeSpan Duration) : DomainEventBase;
