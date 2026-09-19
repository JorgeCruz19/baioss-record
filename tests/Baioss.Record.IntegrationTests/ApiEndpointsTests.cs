using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Storage;
using Baioss.Record.Api;
using Baioss.Record.Infrastructure;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Messaging;
using Baioss.Record.Infrastructure.Storage;
using Baioss.Record.IntegrationTests.Fakes;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Prueba la API REST de automatización hospedada de verdad (Kestrel vía TestServer en memoria):
/// despacha comandos/queries por el <see cref="IDispatcher"/> hasta un canal de prueba.
/// </summary>
public sealed class ApiEndpointsTests
{
    private static (WebApplication App, FakeChannelEngine Channel) BuildApi()
    {
        var channel = new FakeChannelEngine(Guid.NewGuid(), "A");

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IChannelEngine>(channel);
        builder.Services.AddSingleton<IChannelManager, ChannelManager>();
        builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
        builder.Services.AddSingleton<IStorageManager, StorageManager>();
        builder.Services.AddBaiossCqrs();

        var app = builder.Build();
        app.UseWebSockets();
        app.MapBaiossApi();
        return (app, channel);
    }

    [Fact]
    public async Task Rest_StartStatusStop_DispatchesThroughToChannel()
    {
        var (app, channel) = BuildApi();
        await using var _ = app;
        await app.StartAsync();
        var client = app.GetTestClient();

        // GET /channels → el canal de prueba aparece en el listado.
        var channels = await client.GetFromJsonAsync<List<JsonElement>>("/api/v1/channels");
        Assert.NotNull(channels);
        Assert.Single(channels!);
        Assert.Equal("A", channels![0].GetProperty("key").GetString());

        // POST start → 200 con sessionId y el canal recibe la orden.
        var startResponse = await client.PostAsJsonAsync(
            $"/api/v1/channels/{channel.ChannelId}/recording/start",
            new { ProfileId = Guid.NewGuid(), Operator = "tester" });
        startResponse.EnsureSuccessStatusCode();
        var startBody = await startResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(startBody.TryGetProperty("sessionId", out var sessionId));
        Assert.NotEqual(Guid.Empty, sessionId.GetGuid());
        Assert.True(channel.Started);

        // GET status → el canal está grabando.
        var status = await client.GetFromJsonAsync<JsonElement>($"/api/v1/channels/{channel.ChannelId}/status");
        Assert.Equal((int)RecordingState.Recording, status.GetProperty("recordingState").GetInt32());

        // POST stop → 204 y el canal recibe la parada.
        var stopResponse = await client.PostAsync($"/api/v1/channels/{channel.ChannelId}/recording/stop", null);
        Assert.Equal(HttpStatusCode.NoContent, stopResponse.StatusCode);
        Assert.True(channel.Stopped);

        await app.StopAsync();
    }

    [Fact]
    public async Task Status_UnknownChannel_FailsLoudly()
    {
        var (app, _) = BuildApi();
        await using var host = app;
        await app.StartAsync();
        var client = app.GetTestClient();

        // El canal no existe → el handler lanza KeyNotFoundException. Aún no hay mapeo de errores
        // (eso es Fase 2/3), así que debe fallar de forma evidente: error de servidor o excepción
        // propagada por TestServer — nunca un 2xx silencioso.
        try
        {
            var response = await client.GetAsync($"/api/v1/channels/{Guid.NewGuid()}/status");
            Assert.True((int)response.StatusCode >= 500,
                $"Se esperaba un error de servidor para un canal inexistente, pero fue {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            // TestServer propagó la excepción del handler: también es un fallo esperado.
        }

        await app.StopAsync();
    }

    // --- Preview de baja resolución para el panel web ---

    /// <summary>Instantáneas de mentira: recuerda qué se le pidió y devuelve un «JPEG» reconocible.</summary>
    private sealed class FakeSnapshots(Guid channelId) : IChannelSnapshotProvider
    {
        public static readonly byte[] Jpeg = { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9 };
        public int LastWidth { get; private set; }
        public int LastMaxAgeMs { get; private set; }
        public int Calls;

        public Task<byte[]?> GetJpegAsync(Guid id, int width, int maxAgeMs = 400, CancellationToken ct = default)
        {
            LastWidth = width;
            LastMaxAgeMs = maxAgeMs;
            Interlocked.Increment(ref Calls);
            return Task.FromResult(id == channelId ? Jpeg : null);
        }
    }

    private static WebApplication BuildPreviewApi(FakeSnapshots snapshots)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IChannelSnapshotProvider>(snapshots);
        builder.Services.AddSingleton<IChannelManager, ChannelManager>();
        builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
        builder.Services.AddSingleton<IStorageManager, StorageManager>();
        builder.Services.AddBaiossCqrs();
        var app = builder.Build();
        app.UseWebSockets();
        app.MapBaiossApi();
        return app;
    }

    private static async Task<(System.Net.WebSockets.WebSocketMessageType Type, byte[] Data)> ReceiveMessageAsync(
        System.Net.WebSockets.WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        System.Net.WebSockets.WebSocketReceiveResult r;
        do
        {
            r = await socket.ReceiveAsync(buffer, ct);
            ms.Write(buffer, 0, r.Count);
        } while (!r.EndOfMessage);
        return (r.MessageType, ms.ToArray());
    }

    [Fact]
    public async Task PreviewSocket_PushesWholeJpegFrames_AtTheRequestedWidthAndPace()
    {
        var channelId = Guid.NewGuid();
        var snapshots = new FakeSnapshots(channelId);
        await using var app = BuildPreviewApi(snapshots);
        await app.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var client = app.GetTestServer().CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri($"ws://localhost/ws/preview/{channelId}?w=240&fps=10"), timeout.Token);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 4; i++)
        {
            var (type, data) = await ReceiveMessageAsync(socket, timeout.Token);
            Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Binary, type);
            Assert.Equal(FakeSnapshots.Jpeg, data);                       // cada mensaje es un JPEG completo
        }
        clock.Stop();

        Assert.Equal(240, snapshots.LastWidth);
        Assert.InRange(snapshots.LastMaxAgeMs, 1, 99);                     // menos que el periodo: nunca la misma imagen dos veces
        // Cuatro cuadros a 10 por segundo son ~300 ms: el servidor MARCA el ritmo, no vuelca tan rápido como puede.
        Assert.True(clock.ElapsedMilliseconds >= 200, $"Cuatro cuadros a 10 fps llegaron en {clock.ElapsedMilliseconds} ms: sin ritmo.");

        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "fin", timeout.Token);
        int callsAtClose = Volatile.Read(ref snapshots.Calls);
        await Task.Delay(400);
        // Tras cerrar el cliente, el servidor deja de capturar (como mucho, el cuadro que ya estaba en vuelo).
        Assert.InRange(Volatile.Read(ref snapshots.Calls) - callsAtClose, 0, 1);

        await app.StopAsync();
    }

    [Fact]
    public async Task PreviewSocket_ChannelWithoutPicture_SaysSoOnce_InsteadOfClosing()
    {
        var snapshots = new FakeSnapshots(Guid.NewGuid());                 // otro canal: para el pedido no hay imagen
        await using var app = BuildPreviewApi(snapshots);
        await app.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var client = app.GetTestServer().CreateWebSocketClient();
        using var socket = await client.ConnectAsync(new Uri($"ws://localhost/ws/preview/{Guid.NewGuid()}?fps=5"), timeout.Token);

        var (type, data) = await ReceiveMessageAsync(socket, timeout.Token);

        Assert.Equal(System.Net.WebSockets.WebSocketMessageType.Text, type);
        Assert.Equal("unavailable", System.Text.Encoding.UTF8.GetString(data));
        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, socket.State); // sigue abierto: la imagen puede volver

        await socket.CloseAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "fin", timeout.Token);
        await app.StopAsync();
    }

    [Fact]
    public async Task Preview_ReturnsTheJpegSnapshot_UncachedAndAtTheRequestedWidth()
    {
        var channelId = Guid.NewGuid();
        var snapshots = new FakeSnapshots(channelId);
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IChannelSnapshotProvider>(snapshots);
        builder.Services.AddSingleton<IChannelManager, ChannelManager>();
        builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
        builder.Services.AddSingleton<IStorageManager, StorageManager>();
        builder.Services.AddBaiossCqrs();
        await using var app = builder.Build();
        app.MapBaiossApi();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync($"/api/v1/channels/{channelId}/preview.jpg?w=240");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);               // cada petición es un frame nuevo
        Assert.Equal(FakeSnapshots.Jpeg, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(240, snapshots.LastWidth);

        // Sin `w`: el ancho por defecto, pensado para gastar poca red.
        await client.GetAsync($"/api/v1/channels/{channelId}/preview.jpg");
        Assert.Equal(320, snapshots.LastWidth);

        // Un canal sin preview (simulado, entrada reasignándose) es un 404 limpio, no un 500.
        var missing = await client.GetAsync($"/api/v1/channels/{Guid.NewGuid()}/preview.jpg");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        await app.StopAsync();
    }

    [Fact]
    public async Task Preview_HostWithoutSnapshots_IsNotFound()
    {
        // Un host que no registra el proveedor (tests, modo sin interfaz) no debe romper el endpoint.
        var (app, channel) = BuildApi();
        await using var host = app;
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync($"/api/v1/channels/{channel.ChannelId}/preview.jpg");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await app.StopAsync();
    }
}
