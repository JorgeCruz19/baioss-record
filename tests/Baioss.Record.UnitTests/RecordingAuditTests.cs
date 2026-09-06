using System.Text.Json;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Events;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Infrastructure.Messaging;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Auditoría de grabaciones: lo que acaba escrito en la tabla EventLog cuando se graba a mano o por
/// programación. Blinda las tres cosas que hacen útil esa traza —de dónde vino la grabación, por qué
/// terminó, y que lo excepcional destaque sobre lo rutinario— y que antes no se registraban.
/// </summary>
public class RecordingAuditTests
{
    private static readonly Guid Channel = Guid.NewGuid();
    private static readonly Guid Session = Guid.NewGuid();
    private static readonly Guid Job = Guid.NewGuid();

    [Fact]
    public void Manual_Y_Programada_Se_Distinguen_En_La_Auditoria()
    {
        var manual = EventLogWriter.ToEntry(
            new RecordingStarted(Channel, Session, "jorge", RecordingTrigger.Manual));
        var programada = EventLogWriter.ToEntry(
            new RecordingStarted(Channel, Session, RecordingOrigin.ScheduledOperator, RecordingTrigger.Scheduled,
                Job, "Noticias 20:00", "06-09-2026_Noticias"));

        Assert.Equal("RecordingStarted", manual.Category);
        Assert.Equal("RecordingStarted", programada.Category);
        Assert.Contains("Manual", manual.Message);
        Assert.Contains("Scheduled", programada.Message);
        // El título de la tarea viaja en el propio mensaje: la auditoría se lee sin cruzar tablas.
        Assert.Contains("Noticias 20:00", programada.Message);
    }

    [Fact]
    public void El_Operador_Queda_Registrado()
    {
        // La columna Operator existía desde el principio y nadie la rellenaba: la auditoría no sabía de quién
        // era cada grabación aunque el evento trajera el dato.
        var entry = EventLogWriter.ToEntry(new RecordingStarted(Channel, Session, "jorge", RecordingTrigger.Manual));

        Assert.Equal("jorge", entry.Operator);
        Assert.Equal(Channel, entry.ChannelId);
    }

    [Theory]
    [InlineData(RecordingStopReason.Operator, EventSeverity.Info)]
    [InlineData(RecordingStopReason.ScheduledEnd, EventSeverity.Info)]
    [InlineData(RecordingStopReason.Shutdown, EventSeverity.Info)]
    [InlineData(RecordingStopReason.DiskFull, EventSeverity.Warning)]
    [InlineData(RecordingStopReason.Error, EventSeverity.Warning)]
    public void Una_Grabacion_Que_Se_Corta_Sola_Destaca_Sobre_Una_Parada_Normal(
        RecordingStopReason reason, EventSeverity expected)
    {
        var entry = EventLogWriter.ToEntry(
            new RecordingStopped(Channel, Session, TimeSpan.FromMinutes(30), reason, Files: 2, TotalBytes: 1024));

        Assert.Equal(expected, entry.Severity);
        Assert.Contains(reason.ToString(), entry.Message);
    }

    [Fact]
    public void El_Motivo_Del_Paro_Y_El_Material_Producido_Quedan_En_El_Mensaje()
    {
        var entry = EventLogWriter.ToEntry(
            new RecordingStopped(Channel, Session, TimeSpan.FromMinutes(30), RecordingStopReason.ScheduledEnd,
                Files: 3, TotalBytes: 8_000_000));

        Assert.Contains("ScheduledEnd", entry.Message);
        Assert.Contains("3", entry.Message);          // nº de archivos
        Assert.Contains("8000000", entry.Message);    // bytes producidos
    }

    [Fact]
    public void Una_Grabacion_Que_No_Arranca_Se_Audita_Como_Error()
    {
        // Sin esto, una grabación que no ocurrió no deja más rastro que la ausencia del archivo.
        var entry = EventLogWriter.ToEntry(new RecordingStartFailed(
            Channel, RecordingOrigin.ScheduledOperator, RecordingTrigger.Scheduled,
            "La carpeta de destino no admite escritura.", Job, "Noticias 20:00"));

        Assert.Equal(EventSeverity.Error, entry.Severity);
        Assert.Equal("RecordingStartFailed", entry.Category);
        Assert.Contains("carpeta de destino", entry.Message);
        Assert.Contains("Noticias 20:00", entry.Message);
    }

    [Fact]
    public void Una_Ocurrencia_Programada_Omitida_Deja_Rastro()
    {
        var entry = EventLogWriter.ToEntry(new ScheduledRecordingSkipped(
            Channel, Job, "Noticias 20:00", DateTimeOffset.UtcNow, "El canal ya estaba grabando."));

        Assert.Equal(EventSeverity.Warning, entry.Severity);
        Assert.Contains("ya estaba grabando", entry.Message);
    }

    [Fact]
    public void Un_Cierre_Abrupto_Anterior_Se_Audita()
    {
        var entry = EventLogWriter.ToEntry(new OrphanSessionsClosed(2));

        Assert.Equal(EventSeverity.Warning, entry.Severity);
        Assert.Equal("OrphanSessionsClosed", entry.Category);
    }

    [Fact]
    public void La_Entrada_Lleva_Copia_Estructurada_Para_Explotarla_Con_Herramientas()
    {
        var entry = EventLogWriter.ToEntry(
            new RecordingStarted(Channel, Session, "jorge", RecordingTrigger.Scheduled, Job, "Noticias 20:00"));

        Assert.NotNull(entry.PayloadJson);
        using var doc = JsonDocument.Parse(entry.PayloadJson!);
        Assert.Equal((int)RecordingTrigger.Scheduled, doc.RootElement.GetProperty("Trigger").GetInt32());
        Assert.Equal(Job, doc.RootElement.GetProperty("ScheduledJobId").GetGuid());
        Assert.Equal(Session, doc.RootElement.GetProperty("SessionId").GetGuid());
    }

    [Fact]
    public void Un_Evento_Sin_Canal_Ni_Operador_No_Rompe_El_Mapeo()
    {
        // ChannelId/Operator se extraen por reflexión: los eventos que no los llevan deben mapear igual.
        var entry = EventLogWriter.ToEntry(new PerformanceDegraded("cpu", 97.5));

        Assert.Null(entry.ChannelId);
        Assert.Null(entry.Operator);
        Assert.Equal(EventSeverity.Warning, entry.Severity);
    }
}
