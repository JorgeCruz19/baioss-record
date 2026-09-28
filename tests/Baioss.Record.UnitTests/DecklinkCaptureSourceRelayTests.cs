using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Localization;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// La fuente DeckLink en modo dispositivo persistente: la señal la dicta el receptor permanente (abriendo → abierta con
/// el formato real → sin señal en la entrada / tarjeta en uso / sin detección / reabriendo), el proceso del canal lee
/// del relé (con reserva) y el sondeo de recuperación no reserva; el audio en «auto» baja de escalón y reabre la
/// tarjeta; y la captura directa sigue como siempre. Con un receptor falso, sin FFmpeg ni tarjeta.
/// </summary>
public class DecklinkCaptureSourceRelayTests
{
    private sealed class FakeReceiver : IRawCaptureReceiver
    {
        public int ConsumerPort => 12345;
        public int Starts, Restarts, Reservations;
        public bool Disposed;
        public bool Live = true;

        public event EventHandler<FfmpegInputDescription>? Opened;
        public event EventHandler? Lost;
        public event EventHandler<DetectedVideoMode?>? ModeDetected;
        public event EventHandler? DeviceBusy;
        public event EventHandler<bool>? SignalPresence;
        public event EventHandler? AudioChannelsRejected;

        public RelayReservation? ReserveConsumer() { Reservations++; return new RelayReservation(0, () => Live); }
        public Task StartAsync(CancellationToken ct = default) { Starts++; return Task.CompletedTask; }
        public Task RestartAsync(CancellationToken ct = default) { Restarts++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed = true; return default; }

        public void RaiseOpened(FfmpegInputDescription d) => Opened?.Invoke(this, d);
        public void RaiseLost() => Lost?.Invoke(this, EventArgs.Empty);
        public void RaiseMode(DetectedVideoMode? m) => ModeDetected?.Invoke(this, m);
        public void RaiseBusy() => DeviceBusy?.Invoke(this, EventArgs.Empty);
        public void RaisePresence(bool present) => SignalPresence?.Invoke(this, present);
        public void RaiseAudioRejected() => AudioChannelsRejected?.Invoke(this, EventArgs.Empty);
    }

    private static readonly DetectedVideoMode Mode1080i = new(new Resolution(1920, 1080), VideoModes.RateFromDisplay(29.97), true);
    private static readonly FfmpegInputDescription Description = new("DeckLink Duo (1)", Mode1080i, HasAudio: true);

    private static InputSource Definition(params (string Key, string Value)[] parameters)
    {
        var source = new InputSource { Name = "DeckLink Duo (1)", Type = InputType.DecklinkSdi, Uri = "DeckLink Duo (1)" };
        foreach (var (k, v) in parameters) source.Parameters[k] = v;
        return source;
    }

    private static (DecklinkCaptureSource Source, FakeReceiver Receiver) RelaySource(params (string Key, string Value)[] parameters)
    {
        var receiver = new FakeReceiver();
        var source = new DecklinkCaptureSource(Definition(parameters), _ => receiver);
        return (source, receiver);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition()) { Assert.True(DateTime.UtcNow < deadline, "tiempo agotado"); await Task.Delay(10); }
    }

    [Fact]
    public async Task Open_StartsTheReceiver_AndStaysWithoutSignalUntilTheCardOpens()
    {
        var (source, receiver) = RelaySource();
        var signals = new List<SignalInfo>();
        source.SignalChanged += (_, s) => signals.Add(s);
        await source.OpenAsync();
        Assert.Equal(1, receiver.Starts);
        Assert.True(source.IsOpen);
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        Assert.Equal(Localizer.T("Dl_State_Opening"), source.CurrentSignal.FormatLabel);
        Assert.Single(signals);
        Assert.Throws<InvalidOperationException>(() => source.BuildInputArguments());
        Assert.Throws<InvalidOperationException>(() => source.BuildProbeArguments());
        Assert.True(source.SupportsOverlappingProcesses);
        Assert.False(((ICaptureSource)source).DeliversPreroll); // arranca en el directo: el relevo no espera a «al día»
        Assert.True(source.WaitsForPeer);
        Assert.True(source.RestartsAfterEndOfStream);
        Assert.False(((ICaptureSource)source).SelfReportsRecovery);
        Assert.Equal(0, source.PreviewFramesToSkip);
        await source.OpenAsync(); // idempotente: el bucle de espera del motor reabre cada pocos segundos
        Assert.Equal(1, receiver.Starts);
    }

    [Fact]
    public async Task CardOpened_PublishesTheRealFormat_AndTheChannelProcessReadsTheRelayWithAReservation()
    {
        var (source, receiver) = RelaySource();
        await source.OpenAsync();
        receiver.RaiseMode(Mode1080i); // llega antes del volcado de la entrada
        receiver.RaiseOpened(Description);
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        Assert.Equal(new Resolution(1920, 1080), source.CurrentSignal.Resolution);
        Assert.Equal(Mode1080i.Label, source.CurrentSignal.FormatLabel);
        Assert.True(source.CurrentSignal.HasAudio);

        var args = source.BuildInputArguments();
        Assert.Equal(new[] { "-f", "nut", "-analyzeduration", "200000", "-probesize", "20000000", "-i", "tcp://127.0.0.1:12345" }, args);
        Assert.Equal(1, receiver.Reservations);
        Assert.True(source.NewestConsumerIsLive);
        receiver.Live = false;
        Assert.False(source.NewestConsumerIsLive);

        Assert.Equal(args, source.BuildProbeArguments());
        Assert.Equal(1, receiver.Reservations); // el sondeo no reserva
    }

    [Fact]
    public async Task FixedMode_KeepsTheSavedLabel()
    {
        var (source, receiver) = RelaySource(("format_code", "Hi59"), ("format_label", "1920×1080 · 59.94i"));
        await source.OpenAsync();
        receiver.RaiseMode(Mode1080i); // con modo fijo FFmpeg dice el pedido
        receiver.RaiseOpened(Description);
        Assert.Equal("1920×1080 · 59.94i", source.CurrentSignal.FormatLabel);
        Assert.Equal(new Resolution(1920, 1080), source.CurrentSignal.Resolution);
        Assert.Contains("-format_code", source.BuildDeviceArguments());
    }

    [Fact]
    public async Task AutodetectFailure_ThenProcessLost_KeepsTheReason_UntilTheCardOpens()
    {
        var (source, receiver) = RelaySource();
        await source.OpenAsync();
        receiver.RaiseMode(null);
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        Assert.Equal(Localizer.T("Dl_State_NoDetect"), source.CurrentSignal.FormatLabel);
        receiver.RaiseLost(); // FFmpeg salió; el receptor reintenta
        Assert.Equal(Localizer.T("Dl_State_NoDetect"), source.CurrentSignal.FormatLabel);
        receiver.RaiseMode(Mode1080i);
        receiver.RaiseOpened(Description);
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
    }

    [Fact]
    public async Task LostAfterCapturing_SaysReopening_AndBusySaysBusy()
    {
        var (source, receiver) = RelaySource();
        await source.OpenAsync();
        receiver.RaiseOpened(Description);
        receiver.RaiseLost();
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        Assert.Equal(Localizer.T("Dl_State_Reopening"), source.CurrentSignal.FormatLabel);
        Assert.Throws<InvalidOperationException>(() => source.BuildInputArguments());
        receiver.RaiseBusy();
        Assert.Equal(Localizer.T("Dl_State_Busy"), source.CurrentSignal.FormatLabel);
        receiver.RaiseLost(); // sigue en uso: el motivo se conserva
        Assert.Equal(Localizer.T("Dl_State_Busy"), source.CurrentSignal.FormatLabel);
    }

    [Fact]
    public async Task InputSignalLossAndReturn_ToggleTheState_KeepingTheFormat()
    {
        var (source, receiver) = RelaySource();
        var states = new List<SignalState>();
        source.SignalChanged += (_, s) => states.Add(s.State);
        await source.OpenAsync();
        receiver.RaiseMode(Mode1080i);
        receiver.RaiseOpened(Description);
        receiver.RaisePresence(false);
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        Assert.Equal(Mode1080i.Label, source.CurrentSignal.FormatLabel); // la tarjeta sigue abierta con ese modo
        Assert.Throws<InvalidOperationException>(() => source.BuildProbeArguments()); // el sondeo no da por buena una entrada sin señal
        receiver.RaisePresence(false); // repetido: sin ruido
        receiver.RaisePresence(true);
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        Assert.Equal(new[] { SignalState.NoSignal, SignalState.Locked, SignalState.NoSignal, SignalState.Locked }, states);
    }

    [Fact]
    public async Task AudioRejected_InAuto_StepsDownAndReopensTheCard()
    {
        var (source, receiver) = RelaySource((AudioSelection.ChannelsKey, AudioSelection.AutoValue));
        await source.OpenAsync();
        Assert.Equal(16, source.AudioChannelCount);
        Assert.Contains("16", source.BuildDeviceArguments());
        receiver.RaiseAudioRejected();
        await WaitUntilAsync(() => receiver.Restarts == 1);
        Assert.Equal(8, source.AudioChannelCount);
        Assert.Contains("8", source.BuildDeviceArguments());
        receiver.RaiseOpened(Description);
        Assert.Equal(8, source.CurrentSignal.AudioChannels);
    }

    [Fact]
    public async Task AudioRejected_WithAFixedCount_DoesNotTouchAnything()
    {
        var (source, receiver) = RelaySource((AudioSelection.ChannelsKey, "16"));
        await source.OpenAsync();
        receiver.RaiseAudioRejected();
        await Task.Delay(100);
        Assert.Equal(16, source.AudioChannelCount);
        Assert.Equal(0, receiver.Restarts);
    }

    [Fact]
    public async Task Close_DisposesTheReceiver_AndIgnoresItsLateEvents()
    {
        var (source, receiver) = RelaySource();
        await source.OpenAsync();
        receiver.RaiseOpened(Description);
        await source.CloseAsync();
        Assert.True(receiver.Disposed);
        Assert.False(source.IsOpen);
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        receiver.RaiseOpened(Description); // un receptor ya cerrado no manda
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
    }

    [Fact]
    public async Task EngineReports_AreIgnoredInRelayMode()
    {
        var (source, receiver) = RelaySource();
        await source.OpenAsync();
        receiver.RaiseOpened(Description);
        source.ReportDeviceOpen(false);
        source.ReportDetectedMode(null);
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
    }

    [Fact]
    public async Task DirectMode_IsUnchanged()
    {
        var source = new DecklinkCaptureSource(Definition(("format_code", "Hp25")));
        await source.OpenAsync();
        Assert.False(source.UsesRelay);
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        Assert.False(source.SupportsOverlappingProcesses);
        Assert.False(source.WaitsForPeer);
        Assert.Equal(new[] { "-f", "decklink", "-draw_bars", "false", "-format_code", "Hp25", "-channels", "2", "-i", "DeckLink Duo (1)" }, source.BuildInputArguments());
        Assert.Equal(source.BuildInputArguments(), source.BuildProbeArguments());
        source.ReportDeviceOpen(false);
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
    }

    [Fact]
    public void Factory_WithoutFfmpegOrWithRelayOff_GivesDirectCapture()
    {
        // Un «FFmpeg» de mentira (el localizador solo comprueba que los ejecutables existan): la fábrica no lo lanza.
        string dir = Path.Combine(Path.GetTempPath(), $"baioss-fake-ffmpeg-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "ffmpeg.exe"), Array.Empty<byte>());
            File.WriteAllBytes(Path.Combine(dir, "ffprobe.exe"), Array.Empty<byte>());
            var ffmpeg = new FfmpegLocator(dir);
            Assert.False(((DecklinkCaptureSource)new DecklinkCaptureSourceFactory().Create(Definition())).UsesRelay);
            Assert.False(((DecklinkCaptureSource)new DecklinkCaptureSourceFactory(ffmpeg, relay: false).Create(Definition())).UsesRelay);
            Assert.True(((DecklinkCaptureSource)new DecklinkCaptureSourceFactory(ffmpeg).Create(Definition())).UsesRelay);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReceiverArguments_CopyTheDeviceStreamsToNutOverLoopback()
    {
        var args = RawCaptureReceiver.ArgumentsFor(new[] { "-f", "decklink", "-i", "DeckLink Duo (1)" }, copyStreams: true, 4567);
        Assert.Equal(new[]
        {
            "-hide_banner", "-progress", "pipe:1", "-stats_period", "1", "-f", "decklink", "-i", "DeckLink Duo (1)",
            "-map", "0:v:0", "-map", "0:a:0?", "-c", "copy", "-f", "nut", "-flush_packets", "1", "tcp://127.0.0.1:4567?tcp_nodelay=1",
        }, args);
        var synthetic = RawCaptureReceiver.ArgumentsFor(new[] { "-f", "lavfi", "-i", "testsrc2" }, copyStreams: false, 1);
        Assert.Contains("rawvideo", synthetic);
        Assert.Contains("pcm_s16le", synthetic);
    }

    [Theory]
    [InlineData("[decklink @ 0x1] Frame received (#1) - No input signal detected - Frames dropped 1", false)]
    [InlineData("[decklink @ 0x1] Frame received (#11) - Input returned - Frames dropped 1", true)]
    public void DecklinkSignalLines_AreRecognised(string line, bool returned)
    {
        Assert.Equal(!returned, DecklinkModeParser.IsNoInputSignal(line));
        Assert.Equal(returned, DecklinkModeParser.IsInputReturned(line));
        Assert.True(DecklinkModeParser.IsAudioInputRejected("[decklink @ 0x1] Cannot enable audio input"));
    }
}
