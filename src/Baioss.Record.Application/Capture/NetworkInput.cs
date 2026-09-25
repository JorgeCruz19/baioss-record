using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Baioss.Record.Application.Localization;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Application.Capture;

/// <summary>Protocolo de una entrada de red que se GRABA (no se emite): SRT o RTMP.</summary>
public enum NetworkProtocol { Srt, Rtmp }

/// <summary>
/// Quién inicia la conexión. <see cref="Listen"/>: el Record espera al emisor (SRT en modo <c>listener</c>; RTMP como
/// servidor al que el codificador publica). <see cref="Connect"/>: el Record llama (SRT <c>caller</c>; RTMP tira de un
/// servidor o de un emisor que escucha).
/// </summary>
public enum NetworkRole { Listen, Connect }

/// <summary>Por qué una definición de entrada de red no vale. <see cref="None"/> = válida.</summary>
public enum NetworkInputError
{
    None,
    MissingHost,
    InvalidHost,
    InvalidPort,
    InvalidPath,
    PassphraseLength,
    LatencyRange,
    StreamIdTooLong,
    InvalidUrl,
    UnsupportedScheme,
    AudioDelayRange,
}

/// <summary>
/// Definición de una entrada de red (SRT o RTMP) tal como la configura el operador en Entradas → Fuentes de red: el
/// protocolo, quién llama a quién, dirección y puerto, y las opciones del protocolo. Es un valor PURO: valida, se
/// convierte a/desde <see cref="InputSource"/> (lo que se persiste) y a/desde una URL (lo que se pega desde OBS o
/// vMix). Los argumentos de FFmpeg los construye la fuente de captura, en Infraestructura.
/// </summary>
public sealed partial record NetworkInput
{
    public const string AnyAddress = "0.0.0.0";
    public const int DefaultLatencyMs = 120, MinLatencyMs = 20, MaxLatencyMs = 8000;
    public const int MinPassphraseLength = 10, MaxPassphraseLength = 64;   // lo que admite el SRT de FFmpeg
    public const int MaxStreamIdLength = 512;                              // límite del protocolo SRT
    public const int DefaultRtmpPort = 1935;
    /// <summary>Tope del retardo de audio manual (±5 s): más allá no es un ajuste de labios, es otro problema.</summary>
    public const int MaxAudioDelayMs = 5000;

    // Claves con las que viaja en InputSource.Parameters (el tipo y la URL van en Type y Uri).
    public const string RoleKey = "net_role", LatencyKey = "latency_ms", PassphraseKey = "passphrase",
        StreamIdKey = "streamid", SecureKey = "secure", AudioDelayKey = "audio_delay_ms";

    public NetworkProtocol Protocol { get; init; }
    public NetworkRole Role { get; init; }
    /// <summary>Con <see cref="NetworkRole.Connect"/>, el servidor o emisor al que se llama; con
    /// <see cref="NetworkRole.Listen"/>, la dirección local donde se escucha (<see cref="AnyAddress"/> = todas).</summary>
    public string Host { get; init; } = AnyAddress;
    public int Port { get; init; }
    /// <summary>RTMP: «aplicación/clave» (por ejemplo <c>live/estudio1</c>), OPCIONAL: vacía para los dispositivos que solo
    /// dan <c>rtmp://ip:puerto</c> (sirven o publican en la raíz). Al escuchar es lo que se le dice al emisor que use; el
    /// servidor RTMP de FFmpeg acepta al emisor con cualquier aplicación/clave (solo avisa si no coincide; medido).</summary>
    public string Path { get; init; } = "";
    /// <summary>SRT: identificador que el que llama presenta al que escucha (opcional).</summary>
    public string? StreamId { get; init; }
    /// <summary>SRT: contraseña de cifrado (opcional; de 10 a 64 caracteres). Ambos extremos deben llevar la misma.</summary>
    public string? Passphrase { get; init; }
    /// <summary>SRT: latencia del receptor en milisegundos (margen para retransmitir paquetes perdidos).</summary>
    public int LatencyMs { get; init; } = DefaultLatencyMs;
    /// <summary>RTMP con <see cref="NetworkRole.Connect"/>: usar RTMPS (TLS).</summary>
    public bool Secure { get; init; }
    /// <summary>Retardo de audio manual en ms (positivo = el audio suena más tarde; 0 = sin ajuste): el ajuste fino de labios
    /// de esta fuente, para el residuo que solo un operador puede juzgar (el relé ya alinea relojes distintos por llegada).</summary>
    public int AudioDelayMs { get; init; }

    /// <summary>El tipo persistido: SRT lleva el rol en el tipo; RTMP lo lleva en los parámetros.</summary>
    public InputType InputType => Protocol switch
    {
        NetworkProtocol.Srt => Role == NetworkRole.Listen ? InputType.SrtListener : InputType.SrtCaller,
        _ => InputType.Rtmp,
    };

    public static bool IsNetworkType(InputType type) => type is InputType.SrtCaller or InputType.SrtListener or InputType.Rtmp;

    public NetworkInputError Validate()
    {
        string host = Host.Trim();
        if (host.Length == 0) return Role == NetworkRole.Listen ? NetworkInputError.None : NetworkInputError.MissingHost;
        if (!IsValidHost(host)) return NetworkInputError.InvalidHost;
        if (Port is < 1 or > 65535) return NetworkInputError.InvalidPort;
        if (Math.Abs(AudioDelayMs) > MaxAudioDelayMs) return NetworkInputError.AudioDelayRange;
        if (Protocol == NetworkProtocol.Rtmp)
        {
            string path = NormalizePath(Path);
            if (path.Length > 0 && !IsValidPath(path, Role)) return NetworkInputError.InvalidPath;
        }
        else
        {
            if (!string.IsNullOrEmpty(Passphrase) && Passphrase.Length is < MinPassphraseLength or > MaxPassphraseLength)
                return NetworkInputError.PassphraseLength;
            if (LatencyMs is < MinLatencyMs or > MaxLatencyMs) return NetworkInputError.LatencyRange;
            if (StreamId is { Length: > MaxStreamIdLength }) return NetworkInputError.StreamIdTooLong;
        }
        return NetworkInputError.None;
    }

    public bool IsValid => Validate() == NetworkInputError.None;

    /// <summary>Copia con host, ruta y textos opcionales normalizados (recortes, barras, vacíos → null).</summary>
    public NetworkInput Normalized() => this with
    {
        Host = Host.Trim().Length == 0 ? AnyAddress : Host.Trim(),
        Path = Protocol == NetworkProtocol.Rtmp ? NormalizePath(Path) : "",
        StreamId = Protocol == NetworkProtocol.Srt && !string.IsNullOrWhiteSpace(StreamId) ? StreamId.Trim() : null,
        Passphrase = Protocol == NetworkProtocol.Srt && !string.IsNullOrEmpty(Passphrase) ? Passphrase : null,
        Secure = Protocol == NetworkProtocol.Rtmp && Role == NetworkRole.Connect && Secure,
    };

    /// <summary>La URL que se le da a FFmpeg en <c>-i</c> (sin opciones: esas van como argumentos aparte).</summary>
    public string Url
    {
        get
        {
            var n = Normalized();
            string authority = $"{BracketIfIpv6(n.Host)}:{n.Port.ToString(CultureInfo.InvariantCulture)}";
            if (n.Protocol == NetworkProtocol.Srt) return $"srt://{authority}";
            // Sin aplicación/clave: al llamar, la URL tal cual la da el dispositivo (rtmp://ip:puerto); al escuchar, con la
            // barra final, que es como el servidor de FFmpeg entiende «clave vacía» (sin ella toma «ip:puerto» como clave).
            string path = n.Path.Length > 0 ? $"/{n.Path}" : n.Role == NetworkRole.Listen ? "/" : "";
            return $"{(n.Secure ? "rtmps" : "rtmp")}://{authority}{path}";
        }
    }

    /// <summary>«SRT · escucha en 0.0.0.0:9000», «RTMP → rtmp://srv/live/a»… en el idioma de la aplicación.</summary>
    public string Describe()
    {
        var n = Normalized();
        return (n.Protocol, n.Role) switch
        {
            (NetworkProtocol.Srt, NetworkRole.Listen) => Localizer.F("Net_Describe_SrtListen", $"{n.Host}:{n.Port}"),
            (NetworkProtocol.Srt, _) => Localizer.F("Net_Describe_SrtConnect", $"{BracketIfIpv6(n.Host)}:{n.Port}"),
            (_, NetworkRole.Listen) => Localizer.F("Net_Describe_RtmpListen", n.Path.Length > 0 ? $"{n.Host}:{n.Port}/{n.Path}" : $"{n.Host}:{n.Port}"),
            _ => Localizer.F("Net_Describe_RtmpConnect", n.Url),
        };
    }

    /// <summary>
    /// Con <see cref="NetworkRole.Listen"/>: lo que hay que configurar EN EL EMISOR para llegar a este Record
    /// (<paramref name="thisHost"/> = la IP de este equipo tal como la ve el emisor). SRT en una sola URL, como la
    /// piden OBS o vMix (la latencia en microsegundos, que es como la entiende FFmpeg/libsrt); RTMP como servidor +
    /// clave. Null con <see cref="NetworkRole.Connect"/>: ahí el emisor no tiene que hacer nada especial.
    /// </summary>
    public string? EncoderHint(string thisHost)
    {
        var n = Normalized();
        if (n.Role != NetworkRole.Listen) return null;
        if (n.Protocol == NetworkProtocol.Rtmp)
        {
            if (n.Path.Length == 0) return Localizer.F("Net_Hint_RtmpNoKey", $"rtmp://{thisHost}:{n.Port}");
            int slash = n.Path.LastIndexOf('/');
            string app = slash < 0 ? n.Path : n.Path[..slash];
            string key = slash < 0 ? "" : n.Path[(slash + 1)..];
            return Localizer.F("Net_Hint_Rtmp", $"rtmp://{thisHost}:{n.Port}/{app}", key);
        }
        var query = new List<string> { "mode=caller", $"latency={(n.LatencyMs * 1000L).ToString(CultureInfo.InvariantCulture)}" };
        // Codificada, como el streamid: FFmpeg y OBS leen «+» como espacio, decodifican %XX y cortan en «&», así que una
        // contraseña con «#», «+», «&» o «%» en crudo no les llegaría entera (ver SrtOptions).
        if (n.Passphrase is not null) query.Add($"passphrase={Uri.EscapeDataString(n.Passphrase)}");
        if (n.StreamId is not null) query.Add($"streamid={Uri.EscapeDataString(n.StreamId)}");
        return Localizer.F("Net_Hint_Srt", $"srt://{thisHost}:{n.Port}?{string.Join('&', query)}");
    }

    /// <summary>La entrada tal como se persiste: el tipo y la URL en su sitio, el resto en los parámetros.</summary>
    public InputSource ToInputSource(Guid id, string name)
    {
        var n = Normalized();
        var def = new InputSource { Id = id, Name = name, Type = n.InputType, Uri = n.Url };
        def.Parameters[RoleKey] = n.Role == NetworkRole.Listen ? "listen" : "connect";
        if (n.Protocol == NetworkProtocol.Srt)
        {
            def.Parameters[LatencyKey] = n.LatencyMs.ToString(CultureInfo.InvariantCulture);
            if (n.Passphrase is not null) def.Parameters[PassphraseKey] = n.Passphrase;
            if (n.StreamId is not null) def.Parameters[StreamIdKey] = n.StreamId;
        }
        else if (n.Secure) def.Parameters[SecureKey] = "1";
        if (n.AudioDelayMs != 0) def.Parameters[AudioDelayKey] = n.AudioDelayMs.ToString(CultureInfo.InvariantCulture);
        return def;
    }

    /// <summary>La definición guardada en una <see cref="InputSource"/>, o null si no es una entrada de red o su URL no se entiende.</summary>
    public static NetworkInput? FromInputSource(InputSource source)
    {
        if (!IsNetworkType(source.Type) || string.IsNullOrWhiteSpace(source.Uri)) return null;
        if (!TryParseUrl(source.Uri, out var parsed, out _) || parsed is null) return null;
        var p = source.Parameters;
        // El rol: en SRT manda el tipo (SrtListener/SrtCaller); en RTMP, el parámetro.
        var role = source.Type switch
        {
            InputType.SrtListener => NetworkRole.Listen,
            InputType.SrtCaller => NetworkRole.Connect,
            _ => p.GetValueOrDefault(RoleKey) == "listen" ? NetworkRole.Listen : NetworkRole.Connect,
        };
        var result = parsed with { Role = role };
        if (result.Protocol == NetworkProtocol.Srt)
        {
            result = result with
            {
                LatencyMs = int.TryParse(p.GetValueOrDefault(LatencyKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) ? ms : parsed.LatencyMs,
                Passphrase = p.GetValueOrDefault(PassphraseKey) ?? parsed.Passphrase,
                StreamId = p.GetValueOrDefault(StreamIdKey) ?? parsed.StreamId,
            };
        }
        else result = result with { Secure = parsed.Secure || p.GetValueOrDefault(SecureKey) == "1" };
        if (int.TryParse(p.GetValueOrDefault(AudioDelayKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var delay))
            result = result with { AudioDelayMs = delay };
        return result.Normalized();
    }

    /// <summary>
    /// Entiende una URL pegada desde otro programa: <c>srt://host:puerto[?mode=listener|caller&amp;latency=µs&amp;passphrase=…&amp;streamid=…]</c>
    /// (sin <c>mode</c>, llama: es lo que hace FFmpeg), <c>rtmp://host[:puerto][/aplicación/clave]</c> (la aplicación/clave
    /// es opcional: hay dispositivos que dan solo <c>rtmp://ip:puerto</c>) o <c>rtmps://…</c>.
    /// La latencia de una URL SRT va en MICROSEGUNDOS, como la escriben FFmpeg, OBS y vMix. Devuelve false con el
    /// motivo en <paramref name="error"/>; la definición devuelta puede necesitar aún <see cref="Validate"/>.
    /// System.Uri solo da el esquema, el host y el puerto: lo que va detrás (opciones SRT, aplicación/clave RTMP) se lee
    /// como FFmpeg, que no trata el «#» como fragmento (ver <see cref="AfterAuthority"/> y <see cref="SrtOptions"/>). Con
    /// Uri, una contraseña acabada en «#» llegaba recortada y el emisor rechazaba la URL que en ffplay sí abría.
    /// </summary>
    public static bool TryParseUrl(string? url, out NetworkInput? input, out NetworkInputError error)
    {
        input = null;
        error = NetworkInputError.InvalidUrl;
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        string scheme = uri.Scheme.ToLowerInvariant();
        if (scheme is not ("srt" or "rtmp" or "rtmps")) { error = NetworkInputError.UnsupportedScheme; return false; }
        if (string.IsNullOrEmpty(uri.Host)) { error = NetworkInputError.MissingHost; return false; }
        string host = uri.HostNameType == UriHostNameType.IPv6 ? uri.Host.Trim('[', ']') : uri.Host;
        string rest = AfterAuthority(url.Trim());

        if (scheme == "srt")
        {
            if (uri.Port < 0) { error = NetworkInputError.InvalidPort; return false; }
            var query = SrtOptions(rest);
            var candidate = new NetworkInput
            {
                Protocol = NetworkProtocol.Srt,
                Role = query.GetValueOrDefault("mode") is "listener" ? NetworkRole.Listen : NetworkRole.Connect,
                Host = host, Port = uri.Port,
                LatencyMs = long.TryParse(query.GetValueOrDefault("latency"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us)
                    ? (int)Math.Clamp(us / 1000, 0, int.MaxValue) : DefaultLatencyMs,
                Passphrase = query.GetValueOrDefault("passphrase"),
                StreamId = query.GetValueOrDefault("streamid"),
            };
            input = candidate.Normalized();
            error = NetworkInputError.None;
            return true;
        }

        string path = NormalizePath(rest);
        input = new NetworkInput
        {
            Protocol = NetworkProtocol.Rtmp, Role = NetworkRole.Connect, Host = host,
            Port = uri.Port < 0 ? DefaultRtmpPort : uri.Port, Path = path, Secure = scheme == "rtmps",
        }.Normalized();
        error = NetworkInputError.None;
        return true;
    }

    /// <summary>Recorta y quita las barras de los extremos: «/live/estudio/» → «live/estudio».</summary>
    public static string NormalizePath(string? path) => (path ?? "").Trim().Trim('/');

    private static bool IsValidHost(string host)
    {
        string bare = host.Length > 2 && host[0] == '[' && host[^1] == ']' ? host[1..^1] : host;
        if (IPAddress.TryParse(bare, out _)) return true;
        return host.Length <= 253 && HostNameRegex().IsMatch(host);
    }

    // En escucha, la ruta la compara FFmpeg con lo que publica el emisor: solo caracteres «seguros». Al llamar a un
    // servidor, la clave puede llevar parámetros (?...): cualquier cosa sin espacios ni caracteres de control.
    private static bool IsValidPath(string path, NetworkRole role) => role == NetworkRole.Listen
        ? ListenPathRegex().IsMatch(path)
        : ConnectPathRegex().IsMatch(path);

    private static string BracketIfIpv6(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    /// <summary>
    /// Lo que sigue a host:puerto TAL CUAL, cortado como lo corta FFmpeg (<c>av_url_split</c>): desde el primer «/», «?» o
    /// «#». Es la aplicación/clave RTMP (medido: el servidor RTMP de FFmpeg recibe «abc#x+y» de <c>…/live/abc#x+y</c>) y
    /// contiene las opciones SRT. System.Uri, en cambio, descarta el «#…» como fragmento y escapa lo que no es ASCII.
    /// </summary>
    private static string AfterAuthority(string url)
    {
        int start = url.IndexOf("://", StringComparison.Ordinal);
        int end = start < 0 ? -1 : url.IndexOfAny(AuthorityEnd, start + 3);
        return end < 0 ? "" : url[end..];
    }

    private static readonly char[] AuthorityEnd = { '/', '?', '#' };

    /// <summary>
    /// Las opciones de una URL <c>srt://</c> leídas como las lee FFmpeg (libsrt.c: <c>av_find_info_tag</c> +
    /// <c>ff_urldecode</c>): todo lo que sigue al primer «?», separado por «&amp;», con el «#» como un carácter más, «+»
    /// como espacio y %XX decodificado; si una opción se repite, vale la primera. Medido con el FFmpeg empaquetado contra
    /// un emisor con contraseña: así, la URL que abre ffplay abre aquí igual.
    /// </summary>
    private static Dictionary<string, string> SrtOptions(string afterAuthority)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int q = afterAuthority.IndexOf('?');
        if (q < 0) return result;
        foreach (var part in afterAuthority[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            string key = eq < 0 ? part : part[..eq];
            string value = eq < 0 ? "" : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            result.TryAdd(key, value);
        }
        return result;
    }

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*$")]
    private static partial Regex ListenPathRegex();

    [GeneratedRegex(@"^[^\s\p{C}]+$")]
    private static partial Regex ConnectPathRegex();
}
