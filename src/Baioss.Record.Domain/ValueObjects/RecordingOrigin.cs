namespace Baioss.Record.Domain.ValueObjects;

/// <summary>
/// De dónde viene una grabación: quién la pidió y, si fue la programación, qué tarea la disparó.
///
/// Se pasa a <c>StartRecordingAsync</c> en lugar de un simple <c>string operator</c> para que sea IMPOSIBLE
/// iniciar una grabación sin declarar su procedencia — que es justo lo que hace auditable el par
/// «manual / programada». Antes ambas llegaban como una cadena libre y solo se distinguían porque el
/// programador escribía «Programación» en ella.
/// </summary>
/// <param name="Trigger">Quién la puso en marcha (operador, programación o API).</param>
/// <param name="Operator">
/// Persona o sistema al que atribuirla. En manual, el usuario de Windows; en programada, la etiqueta de la
/// programación; por API, lo que declare el llamador. Es informativo: el discriminante es
/// <paramref name="Trigger"/>.
/// </param>
/// <param name="ScheduledJobId">Tarea programada que la disparó (solo si <paramref name="Trigger"/> es Scheduled).</param>
/// <param name="ScheduledJobTitle">Título de esa tarea, para que la auditoría se lea sin cruzar tablas.</param>
public sealed record RecordingOrigin(
    RecordingTrigger Trigger,
    string? Operator,
    Guid? ScheduledJobId = null,
    string? ScheduledJobTitle = null)
{
    /// <summary>El operador pulsó ● Grabar en la aplicación.</summary>
    public static RecordingOrigin Manual(string? user) => new(RecordingTrigger.Manual, user);

    /// <summary>La disparó una grabación programada. Se guarda la tarea para poder rastrear el archivo hasta ella.</summary>
    public static RecordingOrigin Scheduled(Guid jobId, string? title, string? label = null)
        => new(RecordingTrigger.Scheduled, label ?? ScheduledOperator, jobId, title);

    /// <summary>La pidió un sistema externo por la API REST.</summary>
    public static RecordingOrigin Api(string? caller) => new(RecordingTrigger.Api, caller);

    /// <summary>
    /// Etiqueta histórica que el scheduler escribía en <c>Operator</c>. Se conserva para que las filas nuevas
    /// sigan mostrando lo mismo que las anteriores en la columna OPERADOR del historial; el discriminante de
    /// verdad es <see cref="Trigger"/>.
    /// </summary>
    public const string ScheduledOperator = "Programación";
}
