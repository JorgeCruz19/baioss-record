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
/// Autodetección del modo de vídeo de una DeckLink (2026-09-22, «la autodetección no funciona bien»): el motor lee
/// lo que FFmpeg dice al abrir la tarjeta, la fuente lo publica en su señal y el gestor de entradas lo casa con la
/// lista de modos. Sin tarjeta a mano: todo lo que aquí se fija es lo que NO depende del hardware.
/// </summary>
[Collection("Localizer")] // «Automático (autodetección)» y las etiquetas siguen el idioma de la aplicación
public class DecklinkAutodetectTests : IDisposable
{
    private readonly AppLanguage _original = Localizer.Language;
    public DecklinkAutodetectTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _original;

    [Theory]
    [InlineData("[decklink @ 000001f4c2a0] Found Decklink mode 1920 x 1080 with rate 29.97(i)", 1920, 1080, 30000, 1001, true, "1920×1080 · 59.94i")]
    [InlineData("Found Decklink mode 1280 x 720 with rate 59.94", 1280, 720, 60000, 1001, false, "1280×720 · 59.94p")]
    [InlineData("Found Decklink mode 1920 x 1080 with rate 25.00(i)", 1920, 1080, 25, 1, true, "1920×1080 · 50i")]
    [InlineData("Found Decklink mode 1920 x 1080 with rate 23.98", 1920, 1080, 24000, 1001, false, "1920×1080 · 23.98p")]
    [InlineData("Found Decklink mode 3840 x 2160 with rate 50.00", 3840, 2160, 50, 1, false, "3840×2160 · 50p")]
    [InlineData("Found Decklink mode 720 x 486 with rate 29.97(i)", 720, 486, 30000, 1001, true, "720×486 · 59.94i")]
    public void FoundModeLine_GivesTheExactRate_AndTheSameLabelAsTheInputsManager(
        string line, int w, int h, int num, int den, bool interlaced, string label)
    {
        var mode = DecklinkModeParser.TryParseFoundMode(line)!;

        Assert.Equal(new Resolution(w, h), mode.Resolution);
        Assert.Equal(new FrameRate(num, den), mode.FrameRate); // 29.97 impreso → 30000/1001 exacto (timecode, encoder)
        Assert.Equal(interlaced, mode.Interlaced);
        Assert.Equal(label, mode.Label);
    }

    [Theory]
    [InlineData("Stream #0:1: Video: rawvideo (UYVY / 0x59565955), uyvy422(top first), 1920x1080, 29.97 fps")]
    [InlineData("Autodetected the input mode")]
    [InlineData("Supported formats for 'DeckLink Duo (1)':")]
    public void OtherLines_AreNotAMode(string line) => Assert.Null(DecklinkModeParser.TryParseFoundMode(line));

    [Fact]
    public void TheAutodetectFailure_IsRecognised_AndTheRestIsNot()
    {
        Assert.True(DecklinkModeParser.IsAutodetectFailure("[decklink @ 0x1] Cannot Autodetect input stream or No signal"));
        Assert.False(DecklinkModeParser.IsAutodetectFailure("[decklink @ 0x1] Setting signal_loss_action to none because draw_bars is false"));
    }

    [Fact]
    public void ParseOutput_FindsTheModeAmongTheNoise()
    {
        const string output = """
            [decklink @ 000001] Setting signal_loss_action to none because draw_bars is false
            [decklink @ 000001] Autodetected the input mode
            [decklink @ 000001] Found Decklink mode 1920 x 1080 with rate 29.97(i)
            Input #0, decklink, from 'DeckLink Duo (1)':
              Stream #0:0: Audio: pcm_s16le, 48000 Hz, 2 channels, s16, 1536 kb/s
            """;
        Assert.Equal("1920×1080 · 59.94i", DecklinkModeParser.ParseOutput(output)!.Label);
        Assert.Null(DecklinkModeParser.ParseOutput("[decklink @ 000001] Cannot Autodetect input stream or No signal\r\nDeckLink Duo (1): Input/output error\r\n"));
    }

    // Salida REAL de `ffmpeg -loglevel verbose -f decklink -draw_bars false -t 2 -i "DeckLink Duo (1)" -f null -` con la
    // señal del usuario (2026-09-22): la tarjeta autodetecta 1080i59.94 (aviso de draw_bars deprecado incluido).
    private const string Duo2AutodetectOutput = """
        [Blackmagic DeckLink indev @ 0000022cb23e7940] The "draw_bars" option is deprecated: use option signal_loss_action instead
        [in#0 @ 0000022cb23e7200] Setting signal_loss_action to none because draw_bars is false
        [in#0 @ 0000022cb23e7200] Autodetected the input mode
        [in#0 @ 0000022cb23e7200] Found Decklink mode 1920 x 1080 with rate 29.97(i)
        [in#0 @ 0000022cb23e7200] Using 2 input audio channels
        [aist#0:0/pcm_s16le @ 0000022cb2c25b40] Guessed Channel Layout: stereo
        """;

    [Fact]
    public void TheRealDuo2Output_GivesHi59_InTheRealModeList()
    {
        var mode = DecklinkModeParser.ParseOutput(Duo2AutodetectOutput)!;
        Assert.Equal("1920×1080 · 59.94i", mode.Label);

        var list = FfmpegDeviceEnumerator.ParseDecklinkFormats("""
            [in#0 @ 000002757f666f40] Supported formats for 'DeckLink Duo (1)':
                    format_code     description
                    Hp29            1920x1080 at 30000/1001 fps
                    Hi59            1920x1080 at 30000/1001 fps (interlaced, upper field first)
                    Hi60            1920x1080 at 30000/1000 fps (interlaced, upper field first)
            """);
        Assert.Equal("Hi59", VideoModes.FindMatch(list, mode)!.Code);
    }

    // Salidas REALES (2026-09-22) con la aplicación CAPTURANDO en la entrada (1): en autodetección el fallo genérico;
    // con modo fijo, «Found Decklink mode…» (el modo pedido) y luego el error de tarjeta ocupada.
    private const string BusyAutodetectOutput = """
        [Blackmagic DeckLink indev @ 000001a8411b7840] The "draw_bars" option is deprecated: use option signal_loss_action instead
        [in#0 @ 000001a8411b7100] Setting signal_loss_action to none because draw_bars is false
        [in#0 @ 000001a8411b7100] Cannot Autodetect input stream or No signal
        [in#0 @ 000001a8411b6e40] Error opening input: I/O error
        Error opening input file DeckLink Duo (1).
        Error opening input files: I/O error
        """;
    private const string BusyFixedModeOutput = """
        [Blackmagic DeckLink indev @ 000002a473257c80] The "draw_bars" option is deprecated: use option signal_loss_action instead
        [in#0 @ 000002a473257300] Setting signal_loss_action to none because draw_bars is false
        [in#0 @ 000002a473257300] Found Decklink mode 1920 x 1080 with rate 29.97(i)
        [in#0 @ 000002a473257300] Cannot enable video input
        [in#0 @ 000002a473256f40] Error opening input: I/O error
        Error opening input file DeckLink Duo (1).
        Error opening input files: I/O error
        """;

    [Fact]
    public void TheRealBusyOutputs_MeanTheCardIsInUse_NotThatThereIsNoSignal()
    {
        Assert.Null(DecklinkModeParser.ParseOutput(BusyAutodetectOutput));
        var result = DecklinkModeParser.Classify(BusyAutodetectOutput, BusyFixedModeOutput);
        Assert.Equal(VideoModeDetectionOutcome.DeviceBusy, result.Outcome);
        Assert.Null(result.Mode); // el «Found Decklink mode» del intento fijo es el modo PEDIDO, no la señal
    }

    [Fact]
    public void Classify_TellsFreeButNoSignal_FromOpeningInAFixedMode_AndFailedWhenNothingOpened()
    {
        const string opened = """
            [in#0 @ 0000] Found Decklink mode 720 x 486 with rate 29.97(i)
            Input #0, decklink, from 'DeckLink Duo (1)':
              Stream #0:0: Audio: pcm_s16le, 48000 Hz, stereo, s16, 1536 kb/s
            """;
        Assert.Equal(VideoModeDetectionOutcome.NoSignal, DecklinkModeParser.Classify(BusyAutodetectOutput, opened).Outcome);
        Assert.Equal(VideoModeDetectionOutcome.Failed, DecklinkModeParser.Classify(BusyAutodetectOutput, "").Outcome);
        Assert.Equal(VideoModeDetectionOutcome.Failed, DecklinkModeParser.Classify(BusyAutodetectOutput, null).Outcome);

        // Con modo detectado, el segundo intento ni se mira.
        var detected = DecklinkModeParser.Classify(Duo2AutodetectOutput, null);
        Assert.Equal(VideoModeDetectionOutcome.Detected, detected.Outcome);
        Assert.Equal("1920×1080 · 59.94i", detected.Mode!.Label);
    }

    [Fact]
    public void BusyAndOpenedLines_AreRecognised()
    {
        Assert.True(DecklinkModeParser.IsDeviceBusy("[in#0 @ 000002a473257300] Cannot enable video input"));
        Assert.True(DecklinkModeParser.IsInputOpened("Input #0, decklink, from 'DeckLink Duo (1)':"));
        Assert.False(DecklinkModeParser.IsInputOpened("Input #0, lavfi, from 'smptebars=size=1920x1080:rate=30':"));
        Assert.False(DecklinkModeParser.IsDeviceBusy("[in#0 @ 0000] Cannot enable audio input"));
    }

    [Fact]
    public async Task WhileAnotherProcessHoldsTheCard_TheSourceSaysNoSignal_InAnyMode_AndRecoversWhenItOpens()
    {
        // Modo fijo: antes el panel fingía «SEÑAL OK» con el preview en negro mientras otro programa tenía la tarjeta.
        var fixedMode = Source("Hi59");
        await fixedMode.OpenAsync();
        int changes = 0;
        fixedMode.SignalChanged += (_, _) => changes++;

        fixedMode.ReportDeviceOpen(false);
        Assert.Equal(SignalState.NoSignal, fixedMode.CurrentSignal.State);
        Assert.Equal("1920×1080 · 59.94i", fixedMode.CurrentSignal.FormatLabel); // se sigue viendo con qué modo se intenta
        fixedMode.ReportDeviceOpen(false);
        Assert.Equal(1, changes);
        fixedMode.ReportDeviceOpen(true);
        Assert.Equal(SignalState.Locked, fixedMode.CurrentSignal.State);
        Assert.Equal(2, changes);
        fixedMode.ReportDeviceOpen(true);
        Assert.Equal(2, changes);

        // Autodetección: también, y el modo detectado después la deja bloqueada con su formato.
        var auto = Source();
        await auto.OpenAsync();
        auto.ReportDeviceOpen(false);
        Assert.Equal(SignalState.NoSignal, auto.CurrentSignal.State);
        auto.ReportDetectedMode(new DetectedVideoMode(new Resolution(1920, 1080), new FrameRate(30000, 1001), true));
        Assert.Equal(SignalState.Locked, auto.CurrentSignal.State);
        auto.ReportDeviceOpen(true); // «Input #0» tras el modo: sin cambios
        Assert.Equal("1920×1080 · 59.94i", auto.CurrentSignal.FormatLabel);
    }

    [Theory]
    [InlineData(29.97, 30000, 1001)]
    [InlineData(59.94, 60000, 1001)]
    [InlineData(23.98, 24000, 1001)]
    [InlineData(47.95, 48000, 1001)]
    [InlineData(25.00, 25, 1)]
    [InlineData(24.00, 24, 1)]
    [InlineData(60.00, 60, 1)]
    public void TheRatePrintedWithTwoDecimals_GoesBackToItsExactFraction(double shown, int num, int den)
        => Assert.Equal(new FrameRate(num, den), VideoModes.RateFromDisplay(shown));

    // Lista de modos de una DeckLink (salida de -list_formats): 1080p29.97 y 1080i59.94 comparten tasa de CUADRO.
    private const string Formats = """
        Supported formats for 'DeckLink Duo (1)':
                format_code     description
                ntsc            720x486 at 30000/1001 fps (interlaced, lower field first)
                Hp29            1920x1080 at 30000/1001 fps
                Hi59            1920x1080 at 30000/1001 fps (interlaced, upper field first)
                Hp50            1920x1080 at 50000/1000 fps
                hp59            1280x720 at 60000/1001 fps
        """;

    [Fact]
    public void FindMatch_PicksTheListedMode_WithTheSameScanAndRate()
    {
        var list = FfmpegDeviceEnumerator.ParseDecklinkFormats(Formats);

        var interlaced = DecklinkModeParser.TryParseFoundMode("Found Decklink mode 1920 x 1080 with rate 29.97(i)")!;
        Assert.Equal("Hi59", VideoModes.FindMatch(list, interlaced)!.Code);       // el barrido distingue Hi59 de Hp29

        var progressive = DecklinkModeParser.TryParseFoundMode("Found Decklink mode 1920 x 1080 with rate 29.97")!;
        Assert.Equal("Hp29", VideoModes.FindMatch(list, progressive)!.Code);

        var hd720 = DecklinkModeParser.TryParseFoundMode("Found Decklink mode 1280 x 720 with rate 59.94")!;
        Assert.Equal("hp59", VideoModes.FindMatch(list, hd720)!.Code);

        var unlisted = DecklinkModeParser.TryParseFoundMode("Found Decklink mode 1920 x 1080 with rate 60.00")!;
        Assert.Null(VideoModes.FindMatch(list, unlisted));                         // la lista no lo tiene: se queda en Automático
    }

    [Fact]
    public void TheListedModes_CarryTheSameLabelAsADetectedMode()
    {
        // Lo que enseña el desplegable y lo que enseña «Detectar señal» / el panel del canal tienen que coincidir letra a letra.
        var list = FfmpegDeviceEnumerator.ParseDecklinkFormats(Formats);
        Assert.Equal("1920×1080 · 59.94i", list.Single(f => f.Code == "Hi59").Description);
        Assert.Equal("1920×1080 · 29.97p", list.Single(f => f.Code == "Hp29").Description);
        Assert.Equal("720×486 · 59.94i", list.Single(f => f.Code == "ntsc").Description);
    }

    private static DecklinkCaptureSource Source(string? formatCode = null)
    {
        var def = new InputSource { Name = "DeckLink — DeckLink Duo (1)", Type = InputType.DecklinkSdi, Uri = "DeckLink Duo (1)" };
        if (formatCode is not null) { def.Parameters["format_code"] = formatCode; def.Parameters["format_label"] = "1920×1080 · 59.94i"; }
        return new DecklinkCaptureSource(def);
    }

    [Fact]
    public async Task InAutodetect_TheSourcePublishesWhatTheCardDetected_AndNoSignalWhenItDetectsNothing()
    {
        var source = Source();
        await source.OpenAsync();
        int changes = 0;
        source.SignalChanged += (_, _) => changes++;
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State); // lock optimista de siempre al asignar
        Assert.Null(source.CurrentSignal.FormatLabel);                 // …pero sin saber el formato (el panel decía «—»)

        source.ReportDetectedMode(new DetectedVideoMode(new Resolution(1920, 1080), new FrameRate(30000, 1001), true));
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        Assert.Equal("1920×1080 · 59.94i", source.CurrentSignal.FormatLabel);
        Assert.Equal(new Resolution(1920, 1080), source.CurrentSignal.Resolution);
        Assert.Equal(new FrameRate(30000, 1001), source.CurrentSignal.FrameRate);
        Assert.Equal(1, changes);

        // La misma detección otra vez (el supervisor relanzó el proceso): nada que contar.
        source.ReportDetectedMode(new DetectedVideoMode(new Resolution(1920, 1080), new FrameRate(30000, 1001), true));
        Assert.Equal(1, changes);

        // «Cannot Autodetect input stream or No signal»: SIN SEÑAL de verdad, no «SEÑAL OK» con el preview en negro.
        source.ReportDetectedMode(null);
        Assert.Equal(SignalState.NoSignal, source.CurrentSignal.State);
        Assert.Null(source.CurrentSignal.FormatLabel);
        Assert.Null(source.CurrentSignal.Resolution);
        Assert.Equal(2, changes);
        source.ReportDetectedMode(null);
        Assert.Equal(2, changes);

        // Vuelve la señal (otro modo): bloqueada otra vez con el formato nuevo.
        source.ReportDetectedMode(new DetectedVideoMode(new Resolution(1280, 720), new FrameRate(60000, 1001), false));
        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        Assert.Equal("1280×720 · 59.94p", source.CurrentSignal.FormatLabel);
        Assert.Equal(3, changes);
    }

    [Fact]
    public async Task WithAFixedMode_WhatFfmpegSaysAboutTheMode_IsIgnored()
    {
        // Con -format_code FFmpeg no autodetecta: «Found Decklink mode…» es el modo PEDIDO, se corresponda o no con la
        // señal, y el fallo de autodetección no puede llegar. La fuente se queda con lo que el operador fijó.
        var source = Source("Hi59");
        await source.OpenAsync();
        int changes = 0;
        source.SignalChanged += (_, _) => changes++;

        source.ReportDetectedMode(new DetectedVideoMode(new Resolution(1280, 720), new FrameRate(60000, 1001), false));
        source.ReportDetectedMode(null);

        Assert.Equal(SignalState.Locked, source.CurrentSignal.State);
        Assert.Equal("1920×1080 · 59.94i", source.CurrentSignal.FormatLabel);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void TheAutomaticOption_FollowsTheApplicationLanguage()
    {
        Assert.Equal("Automático (autodetección)", DeviceFormat.Auto.ToString());
        Localizer.Language = AppLanguage.English;
        Assert.Equal("Automatic (autodetect)", DeviceFormat.Auto.ToString());
        Assert.Equal("", DeviceFormat.Auto.Code); // sigue siendo «sin -format_code»
    }
}
