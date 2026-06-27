using System.Text.Json;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Scheduling;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Verifica la exportación de la programación: CSV (con BOM, cabecera y una fila por trabajo, escapando el
/// separador) y JSON (válido, con recuento y títulos). Funciones puras → sin reloj real ni E/S.
/// </summary>
public sealed class ScheduleExporterTests
{
    private static readonly Guid ChA = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 6, 27, 10, 0, 0, TimeSpan.Zero);
    private static string? KeyOf(Guid id) => id == ChA ? "A" : null;

    private static ScheduledJob Daily(string title, int hour, bool enabled = true) => new()
    {
        ChannelId = ChA,
        Action = ScheduledAction.StartRecording,
        Title = title,
        RunAt = new DateTimeOffset(2026, 6, 1, hour, 0, 0, TimeSpan.Zero),
        Recurrence = RecurrenceKind.Daily,
        Duration = TimeSpan.FromMinutes(60),
        SegmentMinutes = 15,
        Enabled = enabled,
    };

    [Fact]
    public void ToCsv_HasBomHeaderAndRowPerJob_WithChannelKeyAndStatus()
    {
        var jobs = new[] { Daily("Noticiero", 20), Daily("Cierre", 23, enabled: false) };

        var csv = ScheduleExporter.ToCsv(jobs, KeyOf, Now);

        Assert.StartsWith("﻿", csv); // BOM para que Excel lea los acentos
        Assert.Contains("Canal;Título;Acción;Repetición;Inicio;Duración (min);Segmentar (min);Activo;Próxima ejecución", csv);
        var lines = csv.TrimEnd().Split("\r\n");
        Assert.Equal(3, lines.Length); // cabecera + 2 filas
        Assert.Contains("A;Noticiero;Grabar;Cada día;20:00:00;60;15;Sí;", csv);
        Assert.Contains(";No;", csv); // «Cierre» está pausada
    }

    [Fact]
    public void ToCsv_QuotesFieldsContainingSeparator()
    {
        var csv = ScheduleExporter.ToCsv(new[] { Daily("Programa; especial", 20) }, KeyOf, Now);
        Assert.Contains("\"Programa; especial\"", csv);
    }

    [Fact]
    public void ToJson_IsValidJson_WithCountAndContent()
    {
        var jobs = new[] { Daily("Noticiero", 20), Daily("Cierre", 23) };

        var json = ScheduleExporter.ToJson(jobs, KeyOf, Now);

        using var doc = JsonDocument.Parse(json); // válido
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("schedule").GetArrayLength());
        var first = doc.RootElement.GetProperty("schedule")[0];
        Assert.Equal("A", first.GetProperty("channel").GetString());
        Assert.Equal("StartRecording", first.GetProperty("action").GetString()); // enum crudo (reimportable)
        Assert.Equal(60, first.GetProperty("durationMinutes").GetInt32());
    }

    [Fact]
    public void WeekdaysLabel_FormatsSubsetAndAllAndNone()
    {
        Assert.Equal("L,X,V", ScheduleExporter.WeekdaysLabel(Weekdays.Monday | Weekdays.Wednesday | Weekdays.Friday));
        Assert.Equal("Todos", ScheduleExporter.WeekdaysLabel(Weekdays.EveryDay));
        Assert.Equal("", ScheduleExporter.WeekdaysLabel(Weekdays.None));
    }
}
