using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Scheduling;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Verifica la importación de la programación desde el JSON exportado: round-trip fiel de los campos (canal,
/// acción, recurrencia, días, hora, duración, segmentación, activo) y manejo de errores (JSON inválido o sin
/// «schedule»). Función pura → sin reloj real ni E/S.
/// </summary>
public sealed class ScheduleImporterTests
{
    private static readonly Guid ChA = Guid.NewGuid();
    private static string? KeyOf(Guid id) => id == ChA ? "A" : null;

    [Fact]
    public void FromJson_RoundTripsExportedJob()
    {
        var original = new ScheduledJob
        {
            ChannelId = ChA,
            Action = ScheduledAction.StartRecording,
            Title = "Noticiero matutino",
            RunAt = new DateTimeOffset(2026, 6, 20, 20, 0, 0, TimeSpan.FromHours(-6)),
            Recurrence = RecurrenceKind.Weekly,
            Weekdays = Weekdays.Monday | Weekdays.Wednesday | Weekdays.Friday,
            Duration = TimeSpan.FromMinutes(90),
            SegmentMinutes = 15,
            Enabled = false,
        };
        var json = ScheduleExporter.ToJson(new[] { original }, KeyOf, DateTimeOffset.UnixEpoch);

        var result = ScheduleImporter.FromJson(json);

        Assert.Empty(result.Errors);
        var j = Assert.Single(result.Jobs);
        Assert.Equal(ChA, j.ChannelId);
        Assert.Equal("Noticiero matutino", j.Title);
        Assert.Equal(ScheduledAction.StartRecording, j.Action);
        Assert.Equal(RecurrenceKind.Weekly, j.Recurrence);
        Assert.Equal(Weekdays.Monday | Weekdays.Wednesday | Weekdays.Friday, j.Weekdays);
        Assert.Equal(original.RunAt, j.RunAt);
        Assert.Equal(TimeSpan.FromMinutes(90), j.Duration);
        Assert.Equal(15, j.SegmentMinutes);
        Assert.False(j.Enabled);
        Assert.NotEqual(original.Id, j.Id); // Id NUEVO (importar = añadir, no restaurar)
    }

    [Fact]
    public void FromJson_InvalidJson_ReturnsErrorNoJobs()
    {
        var result = ScheduleImporter.FromJson("{ esto no es json");
        Assert.Empty(result.Jobs);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void FromJson_MissingScheduleArray_ReturnsError()
    {
        var result = ScheduleImporter.FromJson("{\"foo\": 123}");
        Assert.Empty(result.Jobs);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void FromJson_SkipsInvalidEntry_KeepsValidOnes()
    {
        // Una entrada sin channelId (inválida) no rompe la importación de las válidas.
        string json = """
        { "schedule": [
            { "channelId": "not-a-guid", "runAt": "2026-06-20T20:00:00-06:00" },
            { "channelId": "%GUID%", "action": "StartRecording", "recurrence": "Daily", "runAt": "2026-06-20T21:00:00-06:00", "title": "Válida" }
        ]}
        """.Replace("%GUID%", ChA.ToString());

        var result = ScheduleImporter.FromJson(json);

        Assert.Single(result.Jobs);
        Assert.Single(result.Errors);
        Assert.Equal("Válida", result.Jobs[0].Title);
        Assert.Equal(ChA, result.Jobs[0].ChannelId);
    }
}
