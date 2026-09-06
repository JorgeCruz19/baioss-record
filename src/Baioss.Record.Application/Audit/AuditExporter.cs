using System.Globalization;
using System.Text;

namespace Baioss.Record.Application.Audit;

/// <summary>Una línea del registro de actividad, ya en palabras, lista para exportar.</summary>
public sealed record AuditLine(
    DateTimeOffset When, string Severity, string Event, string Channel, string Operator, string Detail);

/// <summary>
/// Exporta el registro de actividad a <b>CSV</b>. Función PURA y testeable: recibe las líneas ya compuestas;
/// la interfaz se ocupa del diálogo de archivo y de escribir el contenido. Mismo formato que la exportación
/// de la programación (separador «;», CRLF y BOM), que es el que Excel abre sin preguntar nada en español.
/// </summary>
public static class AuditExporter
{
    public static string ToCsv(IReadOnlyList<AuditLine> lines, Func<string, string>? header = null)
    {
        string T(string key) => header?.Invoke(key) ?? key;

        var sb = new StringBuilder();
        sb.Append('﻿'); // BOM UTF-8: Excel muestra los acentos correctamente
        sb.Append(string.Join(";", new[]
        {
            T("Audit_Col_When"), T("Audit_Col_Severity"), T("Audit_Col_Event"),
            T("Audit_Col_Channel"), T("Audit_Col_Operator"), T("Audit_Col_Detail"),
        }.Select(Cell))).Append("\r\n");

        foreach (var l in lines)
        {
            var cells = new[]
            {
                // Hora LOCAL y con segundos: la auditoría se lee contra la parrilla de emisión, no en UTC.
                l.When.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture),
                l.Severity, l.Event, l.Channel, l.Operator, l.Detail,
            };
            sb.Append(string.Join(";", cells.Select(Cell))).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Entrecomilla si el valor lleva separador, comillas o salto de línea (RFC 4180).</summary>
    private static string Cell(string value)
    {
        value ??= "";
        return value.IndexOfAny(new[] { ';', '"', '\r', '\n' }) >= 0
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }
}
