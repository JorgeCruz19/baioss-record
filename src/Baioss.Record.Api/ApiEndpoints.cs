using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Storage;
using Baioss.Record.Application.UseCases.Queries;
using Baioss.Record.Application.UseCases.Recording;
using Baioss.Record.Domain.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
            Results.Ok(await d.SendAsync(new StartRecordingCommand(id, body.ProfileId, body.Operator), ct)));

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

        api.MapGet("/storage", async (string volume, IStorageManager s, CancellationToken ct) =>
            Results.Ok(await s.GetStatusAsync(volume, ct)));

        // GET /inputs y /recordings se mapean igual (omitido por brevedad en el scaffold).

        // --- WebSocket de eventos ---
        app.Map("/ws/events", async (HttpContext ctx, IEventBus bus) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            using var sub = bus.Subscribe<IDomainEvent>(async (e, _) =>
            {
                var json = JsonSerializer.SerializeToUtf8Bytes(e, e.GetType());
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(json, WebSocketMessageType.Text, true, CancellationToken.None);
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
}
