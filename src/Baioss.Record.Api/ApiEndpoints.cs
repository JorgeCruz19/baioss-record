using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;
using Baioss.Record.Application.UseCases.Queries;
using Baioss.Record.Application.UseCases.Recording;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Baioss.Record.Api;

/// <summary>
/// Mapea la REST API de automatización y el WebSocket de eventos.
/// Llamar desde el host de la app: <c>app.MapBaiossApi();</c>.
/// </summary>
public static class ApiEndpoints
{
    public static IEndpointRouteBuilder MapBaiossApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1"); // .RequireAuthorization() en producción

        // --- Grabación ---
        api.MapPost("/channels/{id:guid}/recording/start", async (Guid id, StartBody body, IDispatcher d, CancellationToken ct) =>
        {
            try { return Results.Ok(await d.SendAsync(new StartRecordingCommand(id, body.ProfileId, body.Operator), ct)); }
            // El canal ya está grabando (doble START): conflicto claro en vez de un 500. (Auditoría 24/7, A9.)
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });

        api.MapPost("/channels/{id:guid}/recording/stop", async (Guid id, IDispatcher d, CancellationToken ct) =>
        {
            await d.SendAsync(new StopRecordingCommand(id), ct);
            return Results.NoContent();
        });

        // --- Estado / consultas ---
        api.MapGet("/channels/{id:guid}/status", async (Guid id, IDispatcher d, CancellationToken ct) =>
            Results.Ok(await d.QueryAsync(new GetChannelStatusQuery(id), ct)));

        api.MapGet("/channels", (IChannelManager m) =>
            Results.Ok(m.Channels.Select(c => c.Status)));

        api.MapGet("/storage", async (string? volume, IStorageManager s, CancellationToken ct) =>
        {
            // Seguridad: solo se permite consultar el VOLUMEN donde corre la app (donde se graba), no una ruta
            // arbitraria del sistema que un proceso local cualquiera pase por el parámetro. La consulta es por
            // volumen (DriveInfo), así que basta comparar la raíz; si se omite, se usa la del propio proceso.
            // (Auditoría 24/7, #57.)
            var appVolume = Path.GetPathRoot(AppContext.BaseDirectory);
            string? requested;
            try { requested = string.IsNullOrWhiteSpace(volume) ? appVolume : Path.GetPathRoot(Path.GetFullPath(volume)); }
            catch (Exception ex) { return Results.BadRequest(new { error = "Volumen inválido: " + ex.Message }); }

            if (appVolume is null || !string.Equals(requested, appVolume, StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Solo se permite consultar el volumen de grabación." });

            try { return Results.Ok(await s.GetStatusAsync(requested!, ct)); } // no-nulo tras la guarda (== appVolume)
            catch (Exception ex) { return Results.Problem("No se pudo consultar el volumen: " + ex.Message, statusCode: 404); }
        });

        // --- Grabaciones / retención (gestión de almacenamiento — Fase 1) ---
        // Lista el historial reciente con su estado de PROTECCIÓN, para que el operador (o una UI/automatización)
        // sepa qué hay y pueda marcarlo. `channel` filtra por canal; `days` acota la ventana (30 por defecto).
        api.MapGet("/recordings", async (Guid? channel, int? days, [FromServices] IRecordingSessionRepository sessions, CancellationToken ct) =>
        {
            var to = DateTimeOffset.UtcNow;
            var from = to - TimeSpan.FromDays(days is > 0 ? days.Value : 30);
            var list = await sessions.GetHistoryAsync(channel, from, to, skip: 0, take: 500, ct);
            return Results.Ok(list.Select(s => new
            {
                s.Id, s.ChannelId, s.StartedAt, s.EndedAt,
                DurationSeconds = (int)s.Duration.TotalSeconds,
                s.TotalBytes, Files = s.Segments.Count,
                Protection = s.Protection.ToString(), s.Operator,
            }));
        });

        // Marca (o quita) la protección de una grabación frente a la limpieza automática. Body: {"level":"Protected"}
        // (None | Important | Protected). Una grabación protegida NUNCA se borra ni archiva automáticamente.
        api.MapPost("/recordings/{id:guid}/protection", async (Guid id, ProtectionBody body, [FromServices] IRecordingSessionRepository sessions, CancellationToken ct) =>
        {
            if (!Enum.TryParse<RecordingProtection>(body.Level, ignoreCase: true, out var level))
                return Results.BadRequest(new { error = "Nivel inválido. Usa None, Important o Protected." });
            var ok = await sessions.SetProtectionAsync(id, level, ct);
            return ok ? Results.Ok(new { id, protection = level.ToString() }) : Results.NotFound();
        });

        // --- Ajustes de almacenamiento (Fase 4c): retención + alertas por % + modo emergencia, editables en
        //     caliente. GET devuelve los vigentes; PUT los guarda (se SANEAN) y los servicios de fondo los aplican
        //     en su próximo ciclo SIN reiniciar. El body de PUT es un objeto StorageSettings completo. ---
        // Estado GLOBAL del almacenamiento tal como lo ve el indicador de la UI (MULTI-DISCO): peor disco +
        // nº de discos vigilados + desglose por disco + emergencia. Lo publica el coordinador de emergencia.
        api.MapGet("/storage/status", ([FromServices] IStorageStatusProvider provider) => Results.Ok(provider.Current));
        api.MapGet("/storage/settings", ([FromServices] IStorageSettingsStore store) => Results.Ok(store.Current));
        // [FromServices] EXPLÍCITO en `store`: sin él, ASP.NET lo infiere como un 2º body (junto a `body`) y falla
        // al construir el endpoint —rompiendo TODA la API— si el test/DI no lo tiene registrado. (Como en /recordings.)
        api.MapPut("/storage/settings", (StorageSettings body, [FromServices] IStorageSettingsStore store) =>
        {
            store.Save(body);
            return Results.Ok(store.Current); // la versión SANEADA realmente aplicada
        });

        // --- WebSocket de eventos ---
        app.Map("/ws/events", async (HttpContext ctx, IEventBus bus) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            // Un WebSocket NO admite envíos solapados: con varios canales publicando a la vez, dos SendAsync
            // concurrentes lanzan InvalidOperationException y dejan el stream de eventos en estado Aborted. Se
            // serializan con un semáforo por conexión, y cada envío lleva timeout para que un cliente lento no
            // bloquee el bus de eventos (que está en la ruta de algunos start/stop). (Auditoría 24/7, A1/#27.)
            using var sendGate = new SemaphoreSlim(1, 1);
            using var sub = bus.Subscribe<IDomainEvent>(async (e, _) =>
            {
                if (socket.State != WebSocketState.Open) return;
                var json = JsonSerializer.SerializeToUtf8Bytes(e, e.GetType());
                await sendGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await socket.SendAsync(json, WebSocketMessageType.Text, true, timeout.Token).ConfigureAwait(false);
                }
                catch { try { socket.Abort(); } catch { /* cliente caído: WaitUntilClosed cerrará la suscripción */ } }
                finally { sendGate.Release(); }
            });
            await WaitUntilClosedAsync(socket);
        });

        return app;
    }

    private static async Task WaitUntilClosedAsync(WebSocket socket)
    {
        var buffer = new byte[256];
        while (socket.State == WebSocketState.Open)
        {
            var r = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (r.MessageType == WebSocketMessageType.Close)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        }
    }

    public sealed record StartBody(Guid ProfileId, string? Operator);
    public sealed record ProtectionBody(string Level);
}
