using Baioss.Record.Application.Audit;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Exportación del registro de actividad a CSV: lo que se entrega a un cliente o se archiva.</summary>
public class AuditExporterTests
{
    private static readonly DateTimeOffset When = new(2026, 9, 6, 20, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Lleva_Cabecera_Y_Una_Linea_Por_Entrada()
    {
        var csv = AuditExporter.ToCsv(new[]
        {
            new AuditLine(When, "Aviso", "Grabación terminada", "A", "jorge", "Detenida por falta de disco"),
            new AuditLine(When, "Info", "Grabación iniciada", "B", "jorge", "Manual"),
        });

        var lines = csv.TrimEnd('\r', '\n').Split("\r\n");
        Assert.Equal(3, lines.Length);                       // cabecera + 2
        Assert.Contains("Grabación terminada", lines[1]);
        Assert.Contains("jorge", lines[2]);
    }

    [Fact]
    public void Empieza_Con_BOM_Para_Que_Excel_Muestre_Los_Acentos()
    {
        // Sin BOM, Excel abre el CSV con la página de códigos del sistema y «Grabación» sale rota.
        var csv = AuditExporter.ToCsv(new[] { new AuditLine(When, "Info", "Grabación iniciada", "A", "jorge", "Manual") });

        Assert.StartsWith("﻿", csv);
    }

    [Fact]
    public void Un_Detalle_Con_Punto_Y_Coma_No_Parte_La_Fila()
    {
        var csv = AuditExporter.ToCsv(new[]
        {
            new AuditLine(When, "Error", "No se pudo iniciar", "A", "jorge", "Falló esto; y también aquello"),
        });

        var row = csv.TrimEnd('\r', '\n').Split("\r\n")[1];
        Assert.Contains("\"Falló esto; y también aquello\"", row);
        Assert.Equal(6, SplitCsv(row).Length);   // sigue teniendo 6 columnas
    }

    [Fact]
    public void Las_Comillas_Del_Detalle_Se_Escapan()
    {
        var csv = AuditExporter.ToCsv(new[]
        {
            new AuditLine(When, "Info", "Grabación iniciada", "A", "jorge", "Programada \"Noticias\""),
        });

        Assert.Contains("\"Programada \"\"Noticias\"\"\"", csv);
    }

    [Fact]
    public void La_Hora_Va_En_LOCAL_Porque_Se_Lee_Contra_La_Parrilla()
    {
        var csv = AuditExporter.ToCsv(new[] { new AuditLine(When, "Info", "x", "A", "op", "d") });
        var expected = When.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");

        Assert.Contains(expected, csv);
    }

    [Fact]
    public void La_Cabecera_Se_Traduce_Con_El_Resolutor_Que_Se_Le_Pase()
    {
        var csv = AuditExporter.ToCsv(
            new[] { new AuditLine(When, "Info", "x", "A", "op", "d") },
            key => key == "Audit_Col_When" ? "FECHA Y HORA" : key);

        Assert.StartsWith("﻿FECHA Y HORA;", csv);
    }

    /// <summary>Partidor de CSV mínimo (respeta las comillas), solo para comprobar el nº de columnas.</summary>
    private static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ';' && !quoted) { cells.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        cells.Add(current.ToString());
        return cells.ToArray();
    }
}
