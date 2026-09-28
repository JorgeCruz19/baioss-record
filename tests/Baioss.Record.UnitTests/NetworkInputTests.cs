using System.Text.RegularExpressions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Localization;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>
/// Definición de una entrada de red SRT/RTMP (Entradas → Fuentes de red): validación campo a campo, normalización,
/// URL con la que se abre, lectura de URL pegadas de otros programas y viaje de ida y vuelta por InputSource.
/// </summary>
[Collection("Localizer")] // Describe() y las pistas para el emisor van en el idioma de la aplicación
public class NetworkInputTests : IDisposable
{
    private readonly AppLanguage _original = Localizer.Language;
    public NetworkInputTests() => Localizer.Language = AppLanguage.Spanish;
    public void Dispose() => Localizer.Language = _original;

    private static NetworkInput SrtListen(int port = 9000) => new() { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Listen, Port = port };
    private static NetworkInput SrtConnect(string host = "emisor.local", int port = 9000) => new() { Protocol = NetworkProtocol.Srt, Role = NetworkRole.Connect, Host = host, Port = port };
    private static NetworkInput RtmpListen(string path = "live/estudio1", int port = 1935) => new() { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Listen, Port = port, Path = path };
    private static NetworkInput RtmpConnect(string host = "srv.example", string path = "live/clave", int port = 1935) => new() { Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Connect, Host = host, Port = port, Path = path };

    // --- Validación: cada regla, por los dos lados del límite ---

    [Fact]
    public void TheFourBasicShapes_AreValid()
    {
        Assert.Equal(NetworkInputError.None, SrtListen().Validate());
        Assert.Equal(NetworkInputError.None, SrtConnect().Validate());
        Assert.Equal(NetworkInputError.None, RtmpListen().Validate());
        Assert.Equal(NetworkInputError.None, RtmpConnect().Validate());
        Assert.True(SrtListen().IsValid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Connect_WithoutHost_IsMissingHost_ButListen_WithoutHost_MeansEveryInterface(string host)
    {
        Assert.Equal(NetworkInputError.MissingHost, (SrtConnect(host) with { Host = host }).Validate());
        Assert.Equal(NetworkInputError.MissingHost, (RtmpConnect(host) with { Host = host }).Validate());
        Assert.Equal(NetworkInputError.None, (SrtListen() with { Host = host }).Validate());
        Assert.Equal(NetworkInput.AnyAddress, (SrtListen() with { Host = host }).Normalized().Host);
    }

    [Theory]
    [InlineData("192.168.1.50", true)]
    [InlineData("emisor.local", true)]
    [InlineData("srv-01.example.com", true)]
    [InlineData("::1", true)]
    [InlineData("[fe80::1]", true)]
    [InlineData("2001:db8::10", true)]
    [InlineData("con espacio", false)]
    [InlineData("host_con_guion_bajo", false)]
    [InlineData("-empieza-mal", false)]
    [InlineData("srt://dentro", false)]
    [InlineData("a..b", false)]
    public void Host_MustBeAnIpOrAHostName(string host, bool valid)
        => Assert.Equal(valid ? NetworkInputError.None : NetworkInputError.InvalidHost, SrtConnect(host).Validate());

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(9000, true)]
    [InlineData(65535, true)]
    [InlineData(65536, false)]
    [InlineData(-5, false)]
    public void Port_MustBeBetween1And65535(int port, bool valid)
        => Assert.Equal(valid ? NetworkInputError.None : NetworkInputError.InvalidPort, SrtListen(port).Validate());

    [Theory]
    [InlineData("", NetworkInputError.None)]  // sin aplicación ni clave: el emisor publica a rtmp://ip:puerto sin más
    [InlineData("/", NetworkInputError.None)]
    [InlineData("live", NetworkInputError.None)]
    [InlineData("live/estudio1", NetworkInputError.None)]
    [InlineData("/live/estudio-1_a.b/", NetworkInputError.None)]
    [InlineData("live/con espacio", NetworkInputError.InvalidPath)]
    [InlineData("live/clave?token=1", NetworkInputError.InvalidPath)] // en escucha, solo caracteres seguros
    public void RtmpListen_AcceptsAnEmptyOrSafeApplicationAndKey(string path, NetworkInputError expected)
        => Assert.Equal(expected, RtmpListen(path).Validate());

    [Fact]
    public void RtmpConnect_AllowsAKeyWithParameters_ButNotWhitespace()
    {
        Assert.Equal(NetworkInputError.None, RtmpConnect(path: "live/clave?token=abc&x=1").Validate());
        Assert.Equal(NetworkInputError.InvalidPath, RtmpConnect(path: "live/cla ve").Validate());
        Assert.Equal(NetworkInputError.None, RtmpConnect(path: "").Validate()); // rtmp://ip:puerto a secas: dispositivos que sirven en la raíz
    }

    [Theory]
    [InlineData(null, NetworkInputError.None)]
    [InlineData("", NetworkInputError.None)]                    // vacía = sin cifrar
    [InlineData("123456789", NetworkInputError.PassphraseLength)] // 9
    [InlineData("1234567890", NetworkInputError.None)]          // 10
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234", NetworkInputError.None)]          // 64
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345", NetworkInputError.PassphraseLength)] // 65
    public void SrtPassphrase_IsEmptyOr10To64Characters(string? passphrase, NetworkInputError expected)
        => Assert.Equal(expected, (SrtListen() with { Passphrase = passphrase }).Validate());

    [Theory]
    [InlineData(19, false)]
    [InlineData(20, true)]
    [InlineData(120, true)]
    [InlineData(8000, true)]
    [InlineData(8001, false)]
    [InlineData(-1, false)]
    public void SrtLatency_IsBetween20And8000Ms(int ms, bool valid)
        => Assert.Equal(valid ? NetworkInputError.None : NetworkInputError.LatencyRange, (SrtListen() with { LatencyMs = ms }).Validate());

    [Fact]
    public void SrtStreamId_CannotExceed512Characters()
    {
        Assert.Equal(NetworkInputError.None, (SrtConnect() with { StreamId = new string('a', 512) }).Validate());
        Assert.Equal(NetworkInputError.StreamIdTooLong, (SrtConnect() with { StreamId = new string('a', 513) }).Validate());
    }

    [Fact]
    public void Rtmp_IgnoresTheSrtOnlyFields_AndSrt_IgnoresThePath()
    {
        // Un RTMP con «latencia» absurda o contraseña corta sigue siendo válido: esos campos no son suyos.
        Assert.Equal(NetworkInputError.None, (RtmpListen() with { LatencyMs = 1, Passphrase = "x" }).Validate());
        var srt = (SrtListen() with { Path = "lo que sea" }).Normalized();
        Assert.Equal("", srt.Path);
        Assert.Null((RtmpListen() with { Passphrase = "1234567890", StreamId = "id" }).Normalized().Passphrase);
    }

    // --- Normalización y URL ---

    [Fact]
    public void Normalized_TrimsAndStripsSlashes_AndTurnsEmptyOptionalsIntoNull()
    {
        var n = (RtmpConnect(host: "  srv.example ", path: " /live/clave/ ") with { Passphrase = "", StreamId = "  " }).Normalized();
        Assert.Equal("srv.example", n.Host);
        Assert.Equal("live/clave", n.Path);
        Assert.Null(n.Passphrase);
        Assert.Null(n.StreamId);
        var s = (SrtConnect() with { StreamId = " canal1 ", Passphrase = "clave-secreta-1" }).Normalized();
        Assert.Equal("canal1", s.StreamId);
        Assert.Equal("clave-secreta-1", s.Passphrase);
    }

    [Fact]
    public void Url_IsWhatFfmpegOpens_WithBracketsForIpv6_AndRtmpsWhenSecure()
    {
        Assert.Equal("srt://0.0.0.0:9000", SrtListen().Url);
        Assert.Equal("srt://emisor.local:9000", SrtConnect().Url);
        Assert.Equal("srt://[fe80::1]:9000", SrtConnect("fe80::1").Url);
        Assert.Equal("rtmp://0.0.0.0:1935/live/estudio1", RtmpListen().Url);
        Assert.Equal("rtmp://srv.example:1935/live/clave", RtmpConnect().Url);
        Assert.Equal("rtmps://srv.example:443/live/clave", (RtmpConnect(port: 443) with { Secure = true }).Url);
        Assert.Equal("rtmp://0.0.0.0:1935/live/estudio1", (RtmpListen() with { Secure = true }).Url); // RTMPS solo al llamar
        // Sin aplicación/clave: al llamar, tal cual la da el dispositivo; al escuchar, con barra final (clave vacía para el servidor de FFmpeg).
        Assert.Equal("rtmp://srv.example:1935", RtmpConnect(path: "").Url);
        Assert.Equal("rtmp://0.0.0.0:1935/", RtmpListen("").Url);
    }

    [Fact]
    public void InputType_FollowsProtocolAndRole()
    {
        Assert.Equal(InputType.SrtListener, SrtListen().InputType);
        Assert.Equal(InputType.SrtCaller, SrtConnect().InputType);
        Assert.Equal(InputType.Rtmp, RtmpListen().InputType);
        Assert.Equal(InputType.Rtmp, RtmpConnect().InputType);
        Assert.True(NetworkInput.IsNetworkType(InputType.SrtCaller));
        Assert.True(NetworkInput.IsNetworkType(InputType.Rtmp));
        Assert.False(NetworkInput.IsNetworkType(InputType.DecklinkSdi));
        Assert.False(NetworkInput.IsNetworkType(InputType.Udp));
    }

    // --- Ida y vuelta por InputSource (lo que se persiste) ---

    [Fact]
    public void SrtListen_RoundTripsThroughInputSource_WithEveryOption()
    {
        var original = SrtListen(9010) with { Passphrase = "clave-secreta-1", LatencyMs = 250, StreamId = "ignorado-en-escucha" };
        var def = original.ToInputSource(Guid.NewGuid(), "Enlace estudio");

        Assert.Equal(InputType.SrtListener, def.Type);
        Assert.Equal("srt://0.0.0.0:9010", def.Uri);
        Assert.Equal("Enlace estudio", def.Name);
        Assert.Equal("listen", def.Parameters[NetworkInput.RoleKey]);
        Assert.Equal("250", def.Parameters[NetworkInput.LatencyKey]);
        Assert.Equal("clave-secreta-1", def.Parameters[NetworkInput.PassphraseKey]);

        var back = NetworkInput.FromInputSource(def)!;
        Assert.Equal(original.Normalized(), back);
    }

    [Fact]
    public void SrtConnect_AndRtmp_RoundTrip_AndTheRtmpRoleLivesInTheParameters()
    {
        var caller = SrtConnect("10.0.0.7", 9020) with { StreamId = "canal1" };
        Assert.Equal(caller.Normalized(), NetworkInput.FromInputSource(caller.ToInputSource(Guid.NewGuid(), "c")));

        var listen = RtmpListen("live/estudio1", 1936);
        var listenDef = listen.ToInputSource(Guid.NewGuid(), "obs");
        Assert.Equal(InputType.Rtmp, listenDef.Type);
        Assert.Equal("listen", listenDef.Parameters[NetworkInput.RoleKey]);
        Assert.Equal(listen.Normalized(), NetworkInput.FromInputSource(listenDef));

        var pull = RtmpConnect(port: 443) with { Secure = true };
        var pullDef = pull.ToInputSource(Guid.NewGuid(), "cdn");
        Assert.Equal("connect", pullDef.Parameters[NetworkInput.RoleKey]);
        Assert.Equal("1", pullDef.Parameters[NetworkInput.SecureKey]);
        Assert.Equal(pull.Normalized(), NetworkInput.FromInputSource(pullDef));
    }

    [Fact]
    public void FromInputSource_IsNullForOtherTypes_OrAnUnreadableUrl()
    {
        Assert.Null(NetworkInput.FromInputSource(new InputSource { Name = "f", Type = InputType.File, Uri = "C:/clip.mp4" }));
        Assert.Null(NetworkInput.FromInputSource(new InputSource { Name = "d", Type = InputType.DecklinkSdi, Uri = "DeckLink Duo (1)" }));
        Assert.Null(NetworkInput.FromInputSource(new InputSource { Name = "x", Type = InputType.SrtListener, Uri = "no es una url" }));
        Assert.Null(NetworkInput.FromInputSource(new InputSource { Name = "x", Type = InputType.Rtmp, Uri = null }));
    }

    [Fact]
    public void FromInputSource_ToleratesMissingOrGarbageParameters_WithDefaults()
    {
        var def = new InputSource { Name = "viejo", Type = InputType.SrtCaller, Uri = "srt://host:9000" };
        def.Parameters[NetworkInput.LatencyKey] = "no-numero";
        var input = NetworkInput.FromInputSource(def)!;
        Assert.Equal(NetworkRole.Connect, input.Role);
        Assert.Equal(NetworkInput.DefaultLatencyMs, input.LatencyMs);
        Assert.Null(input.Passphrase);
    }

    // --- URL pegadas de otros programas ---

    [Fact]
    public void ParseUrl_SrtListener_WithAllOptions_LatencyInMicroseconds()
    {
        Assert.True(NetworkInput.TryParseUrl("srt://0.0.0.0:9000?mode=listener&latency=250000&passphrase=clave-secreta-1&streamid=canal%201", out var input, out var error));
        Assert.Equal(NetworkInputError.None, error);
        Assert.Equal(NetworkProtocol.Srt, input!.Protocol);
        Assert.Equal(NetworkRole.Listen, input.Role);
        Assert.Equal(9000, input.Port);
        Assert.Equal(250, input.LatencyMs);
        Assert.Equal("clave-secreta-1", input.Passphrase);
        Assert.Equal("canal 1", input.StreamId);
    }

    [Fact]
    public void ParseUrl_SrtWithoutMode_Calls_LikeFfmpegDoes()
    {
        Assert.True(NetworkInput.TryParseUrl("SRT://emisor.local:9000", out var input, out _));
        Assert.Equal(NetworkRole.Connect, input!.Role);
        Assert.Equal("emisor.local", input.Host);
        Assert.Equal(NetworkInput.DefaultLatencyMs, input.LatencyMs);
        Assert.True(NetworkInput.TryParseUrl("srt://[fe80::1]:9000?mode=caller", out var v6, out _));
        Assert.Equal("fe80::1", v6!.Host);
        Assert.Equal("srt://[fe80::1]:9000", v6.Url);
    }

    [Fact]
    public void ParseUrl_Srt_AHashInThePassphraseIsPartOfIt_LikeFfmpeg()
    {
        // Caso real (2026-09-24): ffplay abría esta URL y el Record no. Para FFmpeg (libsrt.c) todo lo que sigue al «?» son
        // opciones y el «#» es un carácter más; System.Uri abría un fragmento en el «#», así que la contraseña llegaba sin él
        // («Televicentro2023»), el emisor rechazaba la conexión y el «mode» de detrás se perdía.
        Assert.True(NetworkInput.TryParseUrl("srt://190.5.109.106:10000?passphrase=Televicentro2023#&mode=caller", out var input, out var error));
        Assert.Equal(NetworkInputError.None, error);
        Assert.Equal("Televicentro2023#", input!.Passphrase);
        Assert.Equal(NetworkRole.Connect, input.Role);
        Assert.Equal("190.5.109.106", input.Host);
        Assert.Equal(10000, input.Port);
        Assert.Equal(NetworkInputError.None, input.Validate());

        // Lo que va detrás del «#» también cuenta: aquí, el modo escucha.
        Assert.True(NetworkInput.TryParseUrl("srt://0.0.0.0:9000?passphrase=clave#secreta&mode=listener", out var listen, out _));
        Assert.Equal("clave#secreta", listen!.Passphrase);
        Assert.Equal(NetworkRole.Listen, listen.Role);
    }

    [Theory]
    // Medido con el FFmpeg empaquetado contra un emisor con la contraseña literal «Pass#word+12%41xyz»: abre las dos primeras
    // y rechaza las dos últimas, porque lee «+» como espacio y decodifica %XX (av_find_info_tag + ff_urldecode).
    [InlineData("Pass%23word%2B12%2541xyz", "Pass#word+12%41xyz")]
    [InlineData("Pass#word%2B12%2541xyz", "Pass#word+12%41xyz")]
    [InlineData("Pass#word+12%2541xyz", "Pass#word 12%41xyz")]
    [InlineData("Pass#word+12%41xyz", "Pass#word 12Axyz")]
    public void ParseUrl_Srt_DecodesTheOptionsLikeFfmpeg(string inUrl, string expected)
    {
        Assert.True(NetworkInput.TryParseUrl($"srt://10.0.0.5:9000?passphrase={inUrl}&mode=caller", out var input, out _));
        Assert.Equal(expected, input!.Passphrase);
    }

    [Fact]
    public void ParseUrl_Srt_ARepeatedOptionKeepsTheFirst_LikeFfmpeg()
    {
        Assert.True(NetworkInput.TryParseUrl("srt://10.0.0.5:9000?passphrase=primera-clave&passphrase=segunda-clave", out var input, out _));
        Assert.Equal("primera-clave", input!.Passphrase);
    }

    [Fact]
    public void ParseUrl_Rtmp_KeepsTheKeyVerbatim_LikeFfmpeg()
    {
        // Medido: el servidor RTMP de FFmpeg recibe la clave «abc#x+y» de rtmp://…/live/abc#x+y (FFmpeg corta la ruta en el
        // primer «/», «?» o «#» tras host:puerto y la usa tal cual). .NET descartaba el «#…» y escapaba lo que no es ASCII.
        Assert.True(NetworkInput.TryParseUrl("rtmp://srv.example/live/abc#x+y", out var hash, out _));
        Assert.Equal("live/abc#x+y", hash!.Path);
        Assert.True(NetworkInput.TryParseUrl("rtmp://srv.example:1936/live/señal", out var accent, out _));
        Assert.Equal("live/señal", accent!.Path);
        Assert.Equal(1936, accent.Port);
    }

    [Fact]
    public void ParseUrl_Rtmp_DefaultsThePort_KeepsTheKeyParameters_AndReadsRtmps()
    {
        Assert.True(NetworkInput.TryParseUrl("rtmp://srv.example/live/clave?token=abc", out var input, out _));
        Assert.Equal(NetworkProtocol.Rtmp, input!.Protocol);
        Assert.Equal(NetworkRole.Connect, input.Role);
        Assert.Equal(1935, input.Port);
        Assert.Equal("live/clave?token=abc", input.Path);
        Assert.False(input.Secure);

        Assert.True(NetworkInput.TryParseUrl("rtmps://srv.example:443/live/clave/", out var secure, out _));
        Assert.True(secure!.Secure);
        Assert.Equal(443, secure.Port);
        Assert.Equal("live/clave", secure.Path);

        // Dispositivos que dan solo rtmp://ip:puerto (sin aplicación ni clave): se lee, es válida y se abre tal cual.
        Assert.True(NetworkInput.TryParseUrl("rtmp://192.4.100.90:1000", out var bare, out _));
        Assert.Equal("", bare!.Path);
        Assert.Equal(1000, bare.Port);
        Assert.Equal(NetworkInputError.None, bare.Validate());
        Assert.Equal("rtmp://192.4.100.90:1000", bare.Url);
        Assert.True(NetworkInput.TryParseUrl("rtmp://192.4.100.90:1000/", out var bareSlash, out _));
        Assert.Equal("rtmp://192.4.100.90:1000", bareSlash!.Url);
    }

    [Theory]
    [InlineData(null, NetworkInputError.InvalidUrl)]
    [InlineData("", NetworkInputError.InvalidUrl)]
    [InlineData("esto no es una url", NetworkInputError.InvalidUrl)]
    [InlineData("udp://239.0.0.1:1234", NetworkInputError.UnsupportedScheme)]
    [InlineData("http://example.com/x.m3u8", NetworkInputError.UnsupportedScheme)]
    [InlineData("srt://host", NetworkInputError.InvalidPort)]
    public void ParseUrl_RejectsWhatItCannotOpen(string? url, NetworkInputError expected)
    {
        Assert.False(NetworkInput.TryParseUrl(url, out var input, out var error));
        Assert.Null(input);
        Assert.Equal(expected, error);
    }

    [Fact]
    public void ParseUrl_SrtListenerFromOtherHost_KeepsThatHost_AndValidationCatchesTheRest()
    {
        // Una URL de escucha con contraseña corta se lee, pero Validate la rechaza: la lectura no valida por sí sola.
        Assert.True(NetworkInput.TryParseUrl("srt://192.168.1.50:9000?mode=listener&passphrase=corta", out var input, out _));
        Assert.Equal("192.168.1.50", input!.Host);
        Assert.Equal(NetworkInputError.PassphraseLength, input.Validate());
    }

    // --- Textos para el operador (español) ---

    [Fact]
    public void Describe_AndEncoderHint_TellTheOperatorWhatToConfigure()
    {
        Assert.Equal("SRT · escucha en 0.0.0.0:9000", SrtListen().Describe());
        Assert.Equal("SRT → emisor.local:9000", SrtConnect().Describe());
        Assert.Equal("RTMP · escucha en 0.0.0.0:1935/live/estudio1", RtmpListen().Describe());
        Assert.Equal("RTMP → rtmp://srv.example:1935/live/clave", RtmpConnect().Describe());
        Assert.Equal("RTMP · escucha en 0.0.0.0:1935", RtmpListen("").Describe());
        Assert.Equal("RTMP → rtmp://srv.example:1935", RtmpConnect(path: "").Describe());

        var srt = (SrtListen() with { Passphrase = "clave-secreta-1", LatencyMs = 300 }).EncoderHint("192.168.1.10")!;
        Assert.Contains("srt://192.168.1.10:9000?mode=caller&latency=300000&passphrase=clave-secreta-1", srt); // µs, como FFmpeg/OBS
        var rtmp = RtmpListen("live/estudio1").EncoderHint("192.168.1.10")!;
        Assert.Contains("rtmp://192.168.1.10:1935/live", rtmp);
        Assert.Contains("estudio1", rtmp);
        var rtmpNoKey = RtmpListen("").EncoderHint("192.168.1.10")!;
        Assert.Contains("rtmp://192.168.1.10:1935", rtmpNoKey);
        Assert.Contains("sin aplicación ni clave", rtmpNoKey);
        Assert.Null(SrtConnect().EncoderHint("192.168.1.10"));  // al llamar, el emisor no configura nada
        Assert.Null(RtmpConnect().EncoderHint("192.168.1.10"));

        Localizer.Language = AppLanguage.English;
        Assert.Equal("SRT · listening on 0.0.0.0:9000", SrtListen().Describe());
    }

    [Theory]
    [InlineData("Televicentro2023#")]
    [InlineData("a+b&c=d#e%f g")]
    public void EncoderHint_Srt_EncodesThePassphrase_SoTheSenderReadsItBackIntact(string passphrase)
    {
        // La URL que se le da al emisor la lee FFmpeg/OBS con «+» como espacio, %XX decodificado y «&» como separador: la
        // contraseña va codificada para que vuelva entera (en crudo, «#», «+», «&» o «%» la rompían).
        string hint = (SrtListen() with { Passphrase = passphrase }).EncoderHint("192.168.1.10")!;
        string url = Regex.Match(hint, @"srt://\S+").Value;
        Assert.True(NetworkInput.TryParseUrl(url, out var back, out _));
        Assert.Equal(passphrase, back!.Passphrase);
        Assert.DoesNotContain("#", url);
    }

    // --- Retardo de audio manual (ajuste fino de labios por fuente) ---

    [Fact]
    public void AudioDelay_IsOptional_Bounded_AndRoundTrips()
    {
        Assert.Equal(NetworkInputError.None, (RtmpConnect() with { AudioDelayMs = -350 }).Validate());
        Assert.Equal(NetworkInputError.None, (SrtListen() with { AudioDelayMs = 5000 }).Validate());
        Assert.Equal(NetworkInputError.AudioDelayRange, (RtmpConnect() with { AudioDelayMs = 5001 }).Validate());
        Assert.Equal(NetworkInputError.AudioDelayRange, (SrtListen() with { AudioDelayMs = -5001 }).Validate());

        var def = (SrtListen() with { AudioDelayMs = 250 }).ToInputSource(Guid.NewGuid(), "x");
        Assert.Equal("250", def.Parameters[NetworkInput.AudioDelayKey]);
        Assert.Equal(250, NetworkInput.FromInputSource(def)!.AudioDelayMs);
        var rtmp = (RtmpConnect() with { AudioDelayMs = -120 }).ToInputSource(Guid.NewGuid(), "y");
        Assert.Equal(-120, NetworkInput.FromInputSource(rtmp)!.AudioDelayMs);
        Assert.False(SrtListen().ToInputSource(Guid.NewGuid(), "z").Parameters.ContainsKey(NetworkInput.AudioDelayKey)); // 0 = no se guarda
    }

    // --- Colchón de preview por fuente (absorbe una llegada a ráfagas; no toca la grabación) ---

    [Fact]
    public void PreviewBuffer_IsOptional_Bounded_AndRoundTrips()
    {
        Assert.Equal(NetworkInputError.None, (RtmpConnect() with { PreviewBufferMs = 1000 }).Validate());
        Assert.Equal(NetworkInputError.None, (SrtListen() with { PreviewBufferMs = 5000 }).Validate());
        Assert.Equal(NetworkInputError.PreviewBufferRange, (RtmpConnect() with { PreviewBufferMs = 5001 }).Validate());
        Assert.Equal(NetworkInputError.PreviewBufferRange, (SrtListen() with { PreviewBufferMs = -1 }).Validate());

        var def = (RtmpConnect() with { PreviewBufferMs = 1500 }).ToInputSource(Guid.NewGuid(), "x");
        Assert.Equal("1500", def.Parameters[NetworkInput.PreviewBufferKey]);
        Assert.Equal(1500, NetworkInput.FromInputSource(def)!.PreviewBufferMs);
        Assert.False(SrtListen().ToInputSource(Guid.NewGuid(), "z").Parameters.ContainsKey(NetworkInput.PreviewBufferKey)); // 0 = no se guarda
        Assert.Equal(0, NetworkInput.FromInputSource(SrtListen().ToInputSource(Guid.NewGuid(), "w"))!.PreviewBufferMs);     // fuentes antiguas: sin colchón
    }
}
