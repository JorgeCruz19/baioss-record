using Baioss.Record.Domain;

namespace Baioss.Record.App.Audit;

/// <summary>
/// Una fila del registro de actividad. Los textos se calculan una vez al cargar (la lista puede tener cientos
/// de filas y se desplaza; recalcularlos al pintar se nota). Inmutable: la auditoría no se edita.
/// </summary>
public sealed class AuditRow
{
    public AuditRow(DateTimeOffset when, EventSeverity severity, string severityText,
        string eventText, string channelKey, string @operator, string detail)
    {
        When = when;
        Severity = severity;
        SeverityText = severityText;
        EventText = eventText;
        ChannelKey = channelKey;
        Operator = @operator;
        Detail = detail;
    }

    public DateTimeOffset When { get; }
    public EventSeverity Severity { get; }

    public string DateText => When.ToLocalTime().ToString("dd/MM/yyyy");
    public string TimeText => When.ToLocalTime().ToString("HH:mm:ss");

    /// <summary>Nivel en palabras, para el «chip» de la fila.</summary>
    public string SeverityText { get; }

    /// <summary>Tipo de suceso traducido («Grabación terminada»), no el nombre técnico del evento.</summary>
    public string EventText { get; }

    /// <summary>Letra del canal (A, B…), o «—» si el suceso no es de un canal concreto.</summary>
    public string ChannelKey { get; }

    public string Operator { get; }

    /// <summary>Lo que pasó, en palabras: «Fin de la programación · 40 s · 1 archivo · 39,3 MB».</summary>
    public string Detail { get; }

    // Para el color del chip. Se exponen como booleanos porque los DataTrigger de WPF no comparan enums
    // de un ensamblado externo sin un conversor.
    public bool IsWarning => Severity == EventSeverity.Warning;
    public bool IsError => Severity == EventSeverity.Error;
    public bool IsCritical => Severity == EventSeverity.Critical;
}
