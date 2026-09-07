using Baioss.Record.Application.Audit;
using Baioss.Record.Application.Localization;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Presentación del registro de actividad: convertir lo que hay guardado —el <c>ToString()</c> de un record y
/// su copia en JSON— en algo que un supervisor pueda leer de un vistazo. Es lo que separa una tabla útil de un
/// volcado técnico.
/// </summary>
public class AuditTextTests : IDisposable
{
    private readonly AppLanguage _original = Localizer.Language;

    public AuditTextTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _original;

    [Fact]
    public void El_Tipo_De_Suceso_Se_Traduce()
    {
        Assert.Equal("Grabación iniciada", AuditText.Category("RecordingStarted"));
        Assert.Equal("Programación omitida", AuditText.Category("ScheduledRecordingSkipped"));

        Localizer.Language = AppLanguage.English;
        Assert.Equal("Recording started", AuditText.Category("RecordingStarted"));
    }

    [Fact]
    public void Un_Suceso_Sin_Traducir_Muestra_Su_Nombre_Tecnico()
    {
        // Preferible un nombre feo a una fila sin identificar: si mañana se añade un evento y se olvida su
        // etiqueta, la auditoría lo sigue mostrando.
        Assert.Equal("AlgoQueNadieTradujo", AuditText.Category("AlgoQueNadieTradujo"));
    }

    [Fact]
    public void Una_Grabacion_Manual_Se_Lee_Con_Su_Operador()
    {
        var payload = """{"ChannelId":"...","SessionId":"...","Operator":"jorge","Trigger":1,"RecordingName":null}""";
        var detail = AuditText.Detail("RecordingStarted", payload, "irrelevante");

        Assert.Equal("Manual · jorge", detail);
    }

    [Fact]
    public void Una_Grabacion_Programada_Nombra_Su_Tarea()
    {
        var payload = """
            {"Operator":"Programación","Trigger":2,"ScheduledJobTitle":"Noticias 20:00","RecordingName":"06-09-2026_Noticias"}
            """;
        var detail = AuditText.Detail("RecordingStarted", payload, "irrelevante");

        Assert.Contains("Programada «Noticias 20:00»", detail);
        Assert.Contains("06-09-2026_Noticias", detail);
    }

    [Fact]
    public void El_Motivo_Del_Paro_Sale_En_Palabras_No_Como_Numero()
    {
        var payload = """{"Duration":"00:40:00","Reason":5,"Files":3,"TotalBytes":8388608}""";
        var detail = AuditText.Detail("RecordingStopped", payload, "irrelevante");

        Assert.StartsWith("Detenida por falta de disco", detail);   // Reason = 5 (DiskFull)
        Assert.Contains("40 min", detail);
        Assert.Contains("3 archivos", detail);
        Assert.Contains("8 MB", detail);
    }

    [Fact]
    public void Una_Grabacion_Corta_Muestra_Segundos_Y_Una_Larga_Horas()
    {
        var corta = AuditText.Detail("RecordingStopped", """{"Duration":"00:00:40","Reason":1,"Files":1}""", "x");
        var larga = AuditText.Detail("RecordingStopped", """{"Duration":"02:05:00","Reason":3,"Files":1}""", "x");

        Assert.Contains("40 s", corta);
        Assert.Contains("2 h 05 min", larga);
    }

    [Fact]
    public void Un_Fallo_De_Arranque_Muestra_Su_Motivo()
    {
        var payload = """{"Operator":"Programación","Trigger":2,"Reason":"La carpeta de destino no admite escritura.","ScheduledJobTitle":"Noticias"}""";
        var detail = AuditText.Detail("RecordingStartFailed", payload, "irrelevante");

        Assert.Contains("«Noticias»", detail);
        Assert.Contains("carpeta de destino", detail);
    }

    [Fact]
    public void Una_Interrupcion_Distingue_Proceso_Matado_De_Salida_Propia()
    {
        var matado = AuditText.Detail("RecordingInterrupted", """{"ExitCode":-1,"Reason":"x"}""", "x");
        var salio = AuditText.Detail("RecordingInterrupted", """{"ExitCode":1,"Reason":"x"}""", "x");

        Assert.Contains("terminado a la fuerza", matado);
        Assert.Contains("salió por su cuenta (código 1)", salio);
        Assert.Contains("siguió en una pieza nueva", matado);
    }

    [Fact]
    public void Un_Archivo_Danado_Muestra_Nombre_Tamano_Y_Que_No_Se_Reproduce()
    {
        var payload = """{"FilePath":"D:\\rec\\06-09-2026_TEST SUNDAY_9.mp4","SizeBytes":2924741632}""";
        var detail = AuditText.Detail("RecordingFileUnverified", payload, "x");

        Assert.StartsWith("06-09-2026_TEST SUNDAY_9.mp4", detail);
        Assert.Contains("2.7 GB", detail.Replace(',', '.'));
        Assert.Contains("no se puede reproducir", detail);
    }

    [Fact]
    public void Un_Disco_Colgado_Se_Lee_Con_Su_Volumen_Y_Cuanto_Duro()
    {
        var stalled = AuditText.Detail("StorageStalled", """{"Volume":"D:\\"}""", "x");
        var cleared = AuditText.Detail("StorageStallCleared", """{"Volume":"D:\\","Duration":"00:01:50"}""", "x");

        Assert.Contains(@"D:\ no acepta escrituras", stalled);
        Assert.Contains("espera sin cortar", stalled);
        Assert.Contains(@"D:\ volvió a responder tras 1 min 50 s", cleared);
    }

    [Fact]
    public void Sin_Payload_Se_Limpia_El_Mensaje_Tecnico()
    {
        // Los eventos de los que no se conoce la forma (o cuyo JSON no se pudo guardar) no deben salir como el
        // volcado crudo del record: se quitan el nombre del tipo y los campos que ya van en sus columnas.
        var message = "StorageLow { OccurredAt = 6/9/2026 09:02:42, ChannelId = 58d89c04, FreeBytes = 27700969472 }";
        var detail = AuditText.Detail("StorageLow", null, message);

        Assert.DoesNotContain("StorageLow {", detail);
        Assert.DoesNotContain("OccurredAt", detail);
        Assert.DoesNotContain("ChannelId", detail);
        Assert.Contains("FreeBytes 27700969472", detail);
    }

    [Fact]
    public void Un_Payload_Corrupto_No_Rompe_La_Fila()
    {
        var detail = AuditText.Detail("RecordingStopped", "{esto no es json", "RecordingStopped { Reason = Operator }");

        Assert.Contains("Reason Operator", detail);   // cae al mensaje, limpio
    }

    [Fact]
    public void El_Detalle_Se_Traduce_Con_El_Idioma()
    {
        var payload = """{"Duration":"00:00:40","Reason":6,"Files":1}""";

        Localizer.Language = AppLanguage.Spanish;
        Assert.Contains("Se cerró la aplicación", AuditText.Detail("RecordingStopped", payload, "x"));

        Localizer.Language = AppLanguage.English;
        Assert.Contains("The application was closed", AuditText.Detail("RecordingStopped", payload, "x"));
    }
}
