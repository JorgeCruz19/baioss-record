using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Recording;
using Baioss.Record.Application.UseCases.Recording;
using Baioss.Record.Domain;
using Baioss.Record.Domain.ValueObjects;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Detener DESDE LA API (panel web, automatización) poniéndole nombre al archivo, como hace el diálogo de la aplicación al
/// detener una grabación manual. Lo que se blinda aquí es cuándo NO se renombra: sin grabación en curso «la última
/// grabación» sería una anterior (quizá ya nombrada por otro), y una programada ya tiene su nombre.
/// </summary>
public class StopRecordingWithNameTests
{
    private static (StopRecordingHandler Handler, RenamingChannel Channel) Build(RenamingChannel? channel = null, TimeSpan? wait = null)
    {
        channel ??= new RenamingChannel();
        var handler = new StopRecordingHandler(new OneChannel(channel)) { RenameWait = wait ?? TimeSpan.FromSeconds(4) };
        return (handler, channel);
    }

    [Fact]
    public async Task Sin_Nombre_Se_Detiene_Como_Siempre_Y_No_Se_Renombra_Nada()
    {
        var (handler, channel) = Build();
        channel.Record(RecordingTrigger.Api);

        var result = await handler.HandleAsync(new StopRecordingCommand(channel.ChannelId));

        Assert.Equal(RecordingNameOutcome.NotRequested, result.Name);
        Assert.Equal(RecordingStopReason.Api, channel.LastStopReason);
        Assert.Null(channel.RenamedTo);
    }

    [Theory]
    [InlineData(RecordingTrigger.Manual)]
    [InlineData(RecordingTrigger.Api)]
    public async Task Con_Nombre_El_Archivo_Se_Guarda_Con_Ese_Nombre_Y_Se_Sabe_Quien_Lo_Puso(RecordingTrigger trigger)
    {
        var (handler, channel) = Build();
        channel.Record(trigger);

        var result = await handler.HandleAsync(new StopRecordingCommand(channel.ChannelId, "  Noticias del mediodía  ", "jcruz"));

        Assert.Equal(RecordingNameOutcome.Renamed, result.Name);
        Assert.Equal("Noticias del mediodía.mp4", result.FileName);      // solo el nombre: la ruta del servidor no sale por la API
        Assert.Equal("Noticias del mediodía", channel.RenamedTo);        // sin los espacios de los extremos
        Assert.Equal("jcruz", channel.RenamedBy);
        Assert.True(channel.StoppedBeforeRename, "Se renombra el archivo YA cerrado, nunca antes de detener.");
    }

    [Fact]
    public async Task Una_Grabacion_Programada_Se_Detiene_Pero_Conserva_Su_Nombre()
    {
        var (handler, channel) = Build();
        channel.Record(RecordingTrigger.Scheduled);

        var result = await handler.HandleAsync(new StopRecordingCommand(channel.ChannelId, "Otro nombre"));

        Assert.Equal(RecordingNameOutcome.Ignored, result.Name);
        Assert.Equal(RecordingNameIgnored.Scheduled, result.Detail);
        Assert.Equal(1, channel.StopCount);                                // se detuvo igual
        Assert.Null(channel.RenamedTo);                                    // su archivo ya se llama fecha_Título
    }

    [Fact]
    public async Task Sin_Grabacion_En_Curso_No_Se_Toca_La_Grabacion_Anterior()
    {
        var (handler, channel) = Build();                                 // en reposo

        var result = await handler.HandleAsync(new StopRecordingCommand(channel.ChannelId, "Nombre suelto"));

        Assert.Equal(RecordingNameOutcome.Ignored, result.Name);
        Assert.Equal(RecordingNameIgnored.NotRecording, result.Detail);
        Assert.Null(channel.RenamedTo);
    }

    [Fact]
    public async Task Un_Motor_Que_No_Renombra_Lo_Dice_En_Vez_De_Fallar()
    {
        var plain = new PlainChannel();
        var handler = new StopRecordingHandler(new OneChannel(plain));

        var result = await handler.HandleAsync(new StopRecordingCommand(plain.ChannelId, "Da igual"));

        Assert.Equal(RecordingNameOutcome.Ignored, result.Name);
        Assert.Equal(RecordingNameIgnored.Unsupported, result.Detail);
        Assert.True(plain.Stopped);
    }

    [Fact]
    public async Task Un_Nombre_Que_No_Deja_Nada_Valido_Se_Avisa_Y_El_Archivo_Conserva_El_Temporal()
    {
        var (handler, channel) = Build(new RenamingChannel { RenameResult = null });
        channel.Record(RecordingTrigger.Api);

        var result = await handler.HandleAsync(new StopRecordingCommand(channel.ChannelId, "???"));

        Assert.Equal(RecordingNameOutcome.Ignored, result.Name);
        Assert.Equal(RecordingNameIgnored.NothingRenamed, result.Detail);
    }

    [Fact]
    public async Task Si_El_Archivo_Aun_Se_Esta_Optimizando_Se_Contesta_Pendiente_Y_El_Renombrado_Termina_Solo()
    {
        // Mover el archivo exige esperar a su optimización (remux), que en una grabación larga son decenas de segundos:
        // la petición HTTP no se queda colgada hasta entonces.
        var gate = new TaskCompletionSource();
        var (handler, channel) = Build(new RenamingChannel { RenameGate = gate.Task }, wait: TimeSpan.FromMilliseconds(150));
        channel.Record(RecordingTrigger.Api);

        var result = await handler.HandleAsync(new StopRecordingCommand(channel.ChannelId, "Largo"));

        Assert.Equal(RecordingNameOutcome.Pending, result.Name);
        Assert.False(channel.RenameFinished);

        gate.SetResult();                                                  // termina la optimización…
        await channel.RenameDone.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(channel.RenameFinished);                               // …y el renombrado se completa sin nadie esperándolo
        Assert.Equal("Largo", channel.RenamedTo);
    }

    [Fact]
    public async Task Un_Cliente_Que_Se_Va_No_Deja_El_Renombrado_A_Medias()
    {
        var (handler, channel) = Build();
        channel.Record(RecordingTrigger.Api);

        await handler.HandleAsync(new StopRecordingCommand(channel.ChannelId, "Clip"));

        Assert.False(channel.RenameToken.CanBeCanceled, "El renombrado no debe depender del token de la petición HTTP.");
    }

    [Fact]
    public async Task Un_Nombre_Desmedido_Se_Recorta()
    {
        var (handler, channel) = Build();
        channel.Record(RecordingTrigger.Api);

        await handler.HandleAsync(new StopRecordingCommand(channel.ChannelId, new string('x', 400)));

        Assert.Equal(StopRecordingHandler.MaxNameLength, channel.RenamedTo!.Length);
    }

    // ------------------------------------------------------------------ dobles de prueba

    private sealed class OneChannel(IChannelEngine channel) : IChannelManager
    {
        public IReadOnlyCollection<IChannelEngine> Channels => new[] { channel };
        public IChannelEngine Get(Guid channelId) => channel;
        public bool TryGet(Guid channelId, out IChannelEngine? engine) { engine = channel; return true; }
    }

    private class PlainChannel : IChannelEngine
    {
        private Guid? _sessionId = Guid.NewGuid();
        private RecordingTrigger? _trigger = RecordingTrigger.Api;

        public Guid ChannelId { get; } = Guid.NewGuid();
        public bool Stopped { get; private set; }
        public int StopCount { get; private set; }
        public RecordingStopReason? LastStopReason { get; private set; }

        public void Record(RecordingTrigger trigger) { _sessionId = Guid.NewGuid(); _trigger = trigger; }
        protected void Idle() { _sessionId = null; _trigger = null; }

        public ChannelStatus Status => new(ChannelId, "A",
            _sessionId is null ? RecordingState.Idle : RecordingState.Recording,
            SignalInfo.None, RecorderStats.Empty, _sessionId, SessionTrigger: _trigger);

        public event EventHandler<ChannelStatus>? StatusChanged { add { } remove { } }
        public Task BindSourceAsync(Guid sourceId, CancellationToken ct = default) => Task.CompletedTask;
        public Task StartPreviewAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StartRecordingAsync(Guid profileId, RecordingOrigin origin, string? recordingName = null, CancellationToken ct = default) => Task.CompletedTask;

        public Task StopRecordingAsync(RecordingStopReason reason, CancellationToken ct = default)
        {
            Stopped = true; StopCount++; LastStopReason = reason;
            Idle();
            return Task.CompletedTask;
        }

        public Task EnableContinuousModeAsync(bool enabled, CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RenamingChannel : PlainChannel, IPostRecordingRename
    {
        private readonly TaskCompletionSource _done = new();

        public RenamingChannel() => Idle();                                // nace en reposo; Record() lo pone a grabar

        /// <summary>Lo que devuelve el motor: la ruta nueva, o null si no renombró nada. "" = ruta con el nombre pedido.</summary>
        public string? RenameResult { get; init; } = "";
        public Task? RenameGate { get; init; }

        public string? RenamedTo { get; private set; }
        public string? RenamedBy { get; private set; }
        public bool StoppedBeforeRename { get; private set; }
        public bool RenameFinished { get; private set; }
        public CancellationToken RenameToken { get; private set; }
        public Task RenameDone => _done.Task;

        public async Task<string?> RenameLastRecordingAsync(string baseName, string? operatorName = null, CancellationToken ct = default)
        {
            StoppedBeforeRename = Stopped;
            RenameToken = ct;
            if (RenameGate is not null) await RenameGate;
            RenamedTo = baseName;
            RenamedBy = operatorName;
            RenameFinished = true;
            _done.TrySetResult();
            return RenameResult is null ? null : Path.Combine(@"D:\grabaciones", baseName + ".mp4");
        }
    }
}
