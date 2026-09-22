using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

namespace Baioss.Record.Application.Network;

/// <summary>
/// Desde dónde se puede llegar a la API del Record (REST + WebSocket): en qué dirección y puerto escucha y qué páginas
/// web pueden llamarla desde un navegador (CORS). Por defecto es lo de siempre —solo este equipo, puerto 5005, ninguna
/// web ajena—; abrirla a la red es una decisión explícita del administrador (🛠 Configuración → Panel web y API, o
/// <c>data/api-settings.json</c>) y se aplica al REINICIAR, porque el servidor enlaza su dirección al arrancar.
/// <para>OJO: la API no tiene autenticación. Abrirla a la red deja ver los canales y grabar/detener a cualquiera que
/// llegue al puerto: solo en redes de confianza.</para>
/// </summary>
public sealed class ApiAccessSettings
{
    public const int DefaultPort = 5005;
    public const string Loopback = "127.0.0.1";
    public const string AnyAddress = "0.0.0.0";

    /// <summary>Texto de ayuda que viaja en el propio JSON, para quien lo edite a mano. No se lee.</summary>
    [JsonPropertyName("_ayuda")]
    public string Help =>
        "Host: 127.0.0.1 = solo este equipo; 0.0.0.0 = toda la red; o una IP concreta de este equipo. Port: 1-65535. " +
        "AllowedOrigins: webs que pueden llamar a la API desde un navegador, separadas por comas (* = cualquiera; vacio = ninguna). " +
        "Se aplica al reiniciar Baioss Record. AVISO: la API no tiene contrasena; abrela solo en redes de confianza.";

    /// <summary>Dirección IPv4 en la que escucha: <see cref="Loopback"/>, <see cref="AnyAddress"/> o una IP de este equipo.</summary>
    public string Host { get; set; } = Loopback;

    public int Port { get; set; } = DefaultPort;

    /// <summary>Orígenes web permitidos (CORS), separados por comas: «*» = cualquiera; vacío = ninguno (como siempre).</summary>
    public string AllowedOrigins { get; set; } = "";

    [JsonIgnore] public bool ListensOnNetwork => !IsLoopback(Host);
    [JsonIgnore] public string ListenUrl => $"http://{Host}:{Port}";
    [JsonIgnore] public IReadOnlyList<string> Origins => ParseOrigins(AllowedOrigins);
    [JsonIgnore] public bool AllowsAnyOrigin => Origins.Contains("*");

    /// <summary>Copia con valores válidos: lo que no se entienda vuelve a lo SEGURO (solo este equipo, 5005, ninguna web).</summary>
    public ApiAccessSettings Sanitized() => new()
    {
        Host = NormalizeHost(Host),
        Port = Port is >= 1 and <= 65535 ? Port : DefaultPort,
        AllowedOrigins = string.Join(", ", ParseOrigins(AllowedOrigins)),
    };

    public static bool IsLoopback(string? host)
        => IPAddress.TryParse(host, out var ip) ? IPAddress.IsLoopback(ip) : string.Equals(host?.Trim(), "localhost", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeHost(string? host)
    {
        var h = (host ?? "").Trim();
        if (h.Length == 0 || h.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return Loopback;
        if (h is "*" or "+" || h.Equals("any", StringComparison.OrdinalIgnoreCase)) return AnyAddress;
        // Solo IPv4 literal: un nombre de equipo o una IPv6 no son direcciones de escucha que queramos adivinar.
        return IPAddress.TryParse(h, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip.ToString() : Loopback;
    }

    /// <summary>
    /// Orígenes válidos, normalizados a <c>esquema://host[:puerto]</c> (sin ruta ni barra final, en minúsculas) y sin
    /// repetidos. Se admiten comas, punto y coma, espacios o saltos de línea como separador. Lo inválido se descarta.
    /// </summary>
    public static IReadOnlyList<string> ParseOrigins(string? text)
    {
        var result = new List<string>();
        foreach (var raw in (text ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var item = raw.Trim();
            if (item == "*") { if (!result.Contains("*")) result.Add("*"); continue; }
            if (!Uri.TryCreate(item, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) continue;
            var origin = uri.IsDefaultPort ? $"{uri.Scheme}://{uri.Host}" : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
            origin = origin.ToLowerInvariant();
            if (!result.Contains(origin)) result.Add(origin);
        }
        return result;
    }
}
