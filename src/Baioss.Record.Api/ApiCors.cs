using Baioss.Record.Application.Network;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Baioss.Record.Api;

/// <summary>
/// CORS de la API según <see cref="ApiAccessSettings.AllowedOrigins"/>: qué páginas web pueden llamarla DESDE UN
/// NAVEGADOR cuando no se sirven desde el mismo origen (el panel web apuntando a la IP y el puerto de este Record).
/// Sin orígenes configurados no se añade nada: la API se comporta exactamente como siempre (solo mismo origen/proxy).
/// <para>CORS no es seguridad —un cliente que no sea un navegador lo ignora, y la API no tiene autenticación—: solo
/// evita que cualquier página abierta en un navegador de la red pueda leer y manejar el grabador.</para>
/// Los WebSocket (<c>/ws/…</c>) no pasan por CORS: los navegadores no lo aplican a la conexión.
/// </summary>
public static class ApiCors
{
    private const string PolicyName = "baioss-panel";

    public static IServiceCollection AddBaiossApiCors(this IServiceCollection services, ApiAccessSettings settings)
    {
        var origins = settings.Origins;
        if (origins.Count == 0) return services;
        return services.AddCors(o => o.AddPolicy(PolicyName, p =>
        {
            if (settings.AllowsAnyOrigin) p.AllowAnyOrigin();
            else p.WithOrigins(origins.ToArray());
            p.AllowAnyHeader().AllowAnyMethod();
        }));
    }

    /// <summary>Debe ir ANTES de mapear los endpoints.</summary>
    public static IApplicationBuilder UseBaiossApiCors(this IApplicationBuilder app, ApiAccessSettings settings)
        => settings.Origins.Count == 0 ? app : app.UseCors(PolicyName);
}
