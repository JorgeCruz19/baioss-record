using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Storage;

/// <summary>
/// Borrado MANUAL de grabaciones (ventana «Borrar grabaciones», desde «Almacenamiento»): por cada grabación se borran
/// del disco TODOS sus archivos —cada segmento, o cada pieza tras una caída, es un <see cref="Segment"/>— y después su
/// registro (segmentos y sesión). Un archivo que ya no está en el disco no impide nada: de ese solo se quita el registro.
/// </summary>
public interface IRecordingDeletionService
{
    /// <summary>Instantánea de las grabaciones que algún canal está escribiendo AHORA (ver <see cref="RecordingDeletionRules"/>).</summary>
    IReadOnlySet<Guid> RecordingNow();

    /// <summary>
    /// Qué pasaría al borrar <paramref name="sessions"/> (tal como las tiene la lista, con sus segmentos): cuántas se
    /// borrarían, cuántos archivos hay de verdad en el disco y cuánto ocupan, cuáles ya no están y cuáles no se tocarían.
    /// Solo mira el disco; no borra nada. Es lo que cuenta el diálogo de confirmación.
    /// </summary>
    RecordingDeletionPlan Plan(IReadOnlyCollection<RecordingSession> sessions);

    /// <summary>
    /// Borra las grabaciones <paramref name="sessionIds"/>: relee cada una de la BD (sus rutas pueden haber cambiado al
    /// renombrarla) y vuelve a aplicar las reglas, así que pasar una en curso o protegida no la borra.
    /// <paramref name="progress"/> recibe cuántas lleva tratadas. Deja constancia de cada una con <c>RecordingDeleted</c>.
    /// </summary>
    Task<RecordingDeletionResult> DeleteAsync(IReadOnlyCollection<Guid> sessionIds, string? operatorName,
        IProgress<int>? progress = null, CancellationToken ct = default);
}

/// <summary>Qué grabaciones NO se pueden borrar a mano. Lo comparten la lista (para no ofrecer el borrado) y el servicio.</summary>
public static class RecordingDeletionRules
{
    /// <summary>En curso: aún sin fin en la BD, o la que un canal está escribiendo ahora (que la cierra al final de la parada).</summary>
    public static bool IsInProgress(RecordingSession session, IReadOnlySet<Guid> recordingNow)
        => session.EndedAt is null || recordingNow.Contains(session.Id);

    /// <summary>Protegida o Importante: hay que pasarla a «Normal» (ventana «Grabaciones») para poder borrarla.</summary>
    public static bool IsProtected(RecordingSession session) => session.Protection != RecordingProtection.None;
}

/// <summary>
/// Lo que contará la confirmación. <see cref="Recordings"/>: las que se borrarán (ni en curso ni protegidas).
/// <see cref="FilesFound"/>/<see cref="BytesFound"/>: sus archivos que SÍ están en el disco. <see cref="FilesMissing"/>: los
/// que ya no están (de esos solo se quita el registro). <see cref="UnavailableLocations"/>: discos o carpetas de red que no
/// responden (¿desconectados?) donde estaban algunos de esos: si vuelven, sus archivos seguirán ahí sin que la aplicación
/// los vea.
/// </summary>
public sealed record RecordingDeletionPlan(
    int Recordings, int FilesFound, long BytesFound, int FilesMissing,
    IReadOnlyList<string> UnavailableLocations, int InProgress, int Protected);

/// <summary>Por qué no se borró un archivo.</summary>
public enum RecordingFileProblem
{
    /// <summary>Abierto por otro programa (un reproductor, la verificación que sigue al cierre…): de su grabación no se borró nada.</summary>
    InUse,
    /// <summary>Sin permiso o de solo lectura: de su grabación no se borró nada.</summary>
    AccessDenied,
    /// <summary>Falló al borrarlo a mitad: se borró el resto y su grabación sigue en la lista con lo que queda.</summary>
    DeleteFailed,
}

/// <summary>Un archivo que impidió borrar (del todo) su grabación.</summary>
public sealed record RecordingFileIssue(string FileName, RecordingFileProblem Problem);

/// <summary>
/// Lo que se borró. <see cref="Recordings"/>: grabaciones que ya no están (ni archivos ni registro).
/// <see cref="FilesDeleted"/>/<see cref="BytesFreed"/>: archivos borrados del disco. <see cref="FilesMissing"/>: archivos que
/// ya no estaban (solo se quitó su registro). <see cref="InProgress"/>/<see cref="Protected"/>: no se tocaron.
/// <see cref="Issues"/>: archivos que impidieron borrar su grabación (en uso, sin permiso, o que fallaron a mitad).
/// <see cref="DatabaseErrors"/>: grabaciones cuyos archivos se borraron pero su registro no se pudo quitar (la lista las
/// seguirá mostrando, sin archivos; al volver a borrarlas se quita el registro).
/// </summary>
public sealed record RecordingDeletionResult(
    int Recordings, int FilesDeleted, long BytesFreed, int FilesMissing,
    int InProgress, int Protected, IReadOnlyList<RecordingFileIssue> Issues, int DatabaseErrors);
