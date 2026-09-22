using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Baioss.Record.Application.Network;

namespace Baioss.Record.Infrastructure.Network;

/// <summary>
/// <see cref="ApiAccessSettings"/> sobre un JSON (<c>data/api-settings.json</c>). Se lee ANTES de construir el host
/// —la dirección de escucha se fija al crear el servidor—, así que no depende del contenedor ni del logger: carga,
/// guarda (de forma atómica) y comprueba si una dirección se puede enlazar; quien llama decide qué registrar.
/// </summary>
public static class ApiAccessSettingsFile
{
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Ajustes del archivo, saneados. Si no existe se SIEMBRA con <paramref name="seed"/> (lo que venga de la
    /// configuración: <c>Api:Host</c>, <c>Api:Port</c>, <c>Api:AllowedOrigins</c>) y se escribe para que esté a la vista.
    /// Un archivo ilegible no tumba el arranque: manda la semilla y <paramref name="problem"/> cuenta por qué.
    /// </summary>
    public static ApiAccessSettings Load(string path, ApiAccessSettings seed, out string? problem)
    {
        problem = null;
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<ApiAccessSettings>(File.ReadAllText(path));
                if (loaded is not null) return loaded.Sanitized();
            }
        }
        catch (Exception ex) { problem = $"Ajustes de la API ilegibles en «{path}» ({ex.Message}); se usan los valores por defecto."; }

        var clean = seed.Sanitized();
        try { Save(path, clean); }
        catch (Exception ex) { problem ??= $"No se pudo escribir «{path}» ({ex.Message})."; }
        return clean;
    }

    public static void Save(string path, ApiAccessSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(settings.Sanitized(), WriteOpts));
        File.Move(tmp, path, overwrite: true); // atómico: nunca deja un JSON a medias
    }

    /// <summary>
    /// ¿Se puede escuchar en esa dirección y puerto AHORA? Una IP que ya no es de este equipo (cambió el DHCP, se quitó
    /// una tarjeta de red) o un puerto ocupado harían fallar el arranque del servidor… y con él el del grabador entero.
    /// Se comprueba antes, con un socket que se cierra al instante, para poder caer a una dirección segura.
    /// </summary>
    public static bool CanBind(string host, int port, out string? reason)
    {
        reason = null;
        try
        {
            var listener = new TcpListener(IPAddress.Parse(host), port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (Exception ex) { reason = ex.Message; return false; }
    }
}
