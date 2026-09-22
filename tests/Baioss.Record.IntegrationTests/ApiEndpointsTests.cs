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

    // --- Detener poniéndole nombre al archivo (lo que el panel web ofrece en su diálogo de «Detener») ---

    [Fact]
    public async Task Stop_WithAName_SavesTheFileUnderThatName_AndSaysSo()
    {
        var (app, channel) = BuildApi();
        await using var _ = app;
        await app.StartAsync();
        var client = app.GetTestClient();
        (await client.PostAsJsonAsync($"/api/v1/channels/{channel.ChannelId}/recording/start",
            new { ProfileId = Guid.Empty, Operator = "tester" })).EnsureSuccessStatusCode();

        var response = await client.PostAsJsonAsync($"/api/v1/channels/{channel.ChannelId}/recording/stop",
            new { name = "Noticias del mediodía", @operator = "jcruz" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("renamed").GetBoolean());
        Assert.False(body.GetProperty("pending").GetBoolean());
        Assert.Equal("Noticias del mediodía.mp4", body.GetProperty("fileName").GetString()); // el nombre, no la ruta del servidor
        Assert.True(channel.Stopped);
        Assert.Equal("Noticias del mediodía", channel.RenamedTo);
        Assert.Equal("jcruz", channel.RenamedBy);
        await app.StopAsync();
    }

    // «Detener» nunca tuvo cuerpo y aceptaba cualquier POST. Al añadirle el nombre opcional con el enlace automático de
    // ASP.NET, un formulario vacío (curl -d '', Invoke-WebRequest -Method Post) pasó a dar 415 y un JSON roto 400: un
    // cliente de siempre ya no podía DETENER. Se vio en la prueba en vivo; esto lo deja fijado.
    [Theory]
    [InlineData("application/json", "{}")]                                  // cuerpo sin nombre
    [InlineData("application/json", "{\"name\":\"   \"}")]                   // nombre en blanco
    [InlineData("application/json", "{\"name\":null}")]
    [InlineData("application/json", "null")]
    [InlineData("application/json", "")]                                    // JSON declarado, cuerpo vacío
    [InlineData("application/json", "{esto no es json")]                    // JSON mal formado
    [InlineData("application/x-www-form-urlencoded", "")]                   // lo que envía curl -d '' / PowerShell
    [InlineData("application/x-www-form-urlencoded", "name=Nombre")]        // un formulario NO pone nombre: no es el contrato
    [InlineData("text/plain", "hola")]
    public async Task Stop_WithAnythingThatIsNotAValidName_BehavesExactlyAsBefore(string contentType, string json)
    {
        var (app, channel) = BuildApi();
        await using var _ = app;
        await app.StartAsync();
        var client = app.GetTestClient();
        (await client.PostAsJsonAsync($"/api/v1/channels/{channel.ChannelId}/recording/start",
            new { ProfileId = Guid.Empty, Operator = "tester" })).EnsureSuccessStatusCode();

        var response = await client.PostAsync($"/api/v1/channels/{channel.ChannelId}/recording/stop",
            new StringContent(json, System.Text.Encoding.UTF8, contentType));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);       // 204, como sin cuerpo
        Assert.True(channel.Stopped);
        Assert.Null(channel.RenamedTo);
        await app.StopAsync();
    }

    [Fact]
    public async Task Stop_WithAName_WhenNothingIsRecording_DoesNotRenameAnOlderRecording()
    {
        var (app, channel) = BuildApi();
        await using var _ = app;
        await app.StartAsync();

        var response = await app.GetTestClient().PostAsJsonAsync(
            $"/api/v1/channels/{channel.ChannelId}/recording/stop", new { name = "Nombre suelto" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("renamed").GetBoolean());
        Assert.Equal("not-recording", body.GetProperty("detail").GetString());
        Assert.Null(channel.RenamedTo);
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

    // --- CORS: el panel web apuntando a la IP y el puerto de este Record desde OTRO origen ---

    private static WebApplication BuildApiWithCors(string allowedOrigins)
    {
        var settings = new Baioss.Record.Application.Network.ApiAccessSettings { AllowedOrigins = allowedOrigins }.Sanitized();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IChannelEngine>(new FakeChannelEngine(Guid.NewGuid(), "A"));
        builder.Services.AddSingleton<IChannelManager, ChannelManager>();
        builder.Services.AddSingleton<IEventBus, InProcessEventBus>();
        builder.Services.AddSingleton<IStorageManager, StorageManager>();
        builder.Services.AddBaiossCqrs();
        builder.Services.AddBaiossApiCors(settings);
        var app = builder.Build();
        app.UseBaiossApiCors(settings);
        app.MapBaiossApi();
        return app;
    }

    private static async Task<HttpResponseMessage> GetChannelsFrom(WebApplication app, string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/channels");
        request.Headers.Add("Origin", origin);
        return await app.GetTestClient().SendAsync(request);
    }

    [Fact]
    public async Task Cors_WithoutAllowedOrigins_TheApiBehavesAsAlways_NoCorsHeaders()
    {
        await using var app = BuildApiWithCors("");
        await app.StartAsync();

        var response = await GetChannelsFrom(app, "http://192.168.1.50:5173");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin")); // el navegador de otro origen no podrá leerla
        await app.StopAsync();
    }

    [Fact]
    public async Task Cors_AllowedOrigin_CanCallTheApi_AndOthersCannot()
    {
        await using var app = BuildApiWithCors("http://192.168.1.50:5173/");
        await app.StartAsync();

        var allowed = await GetChannelsFrom(app, "http://192.168.1.50:5173");
        Assert.Equal("http://192.168.1.50:5173", allowed.Headers.GetValues("Access-Control-Allow-Origin").Single());

        var other = await GetChannelsFrom(app, "http://malicioso.example");
        Assert.False(other.Headers.Contains("Access-Control-Allow-Origin"));

        // Preflight del POST con JSON (iniciar grabación): el navegador pregunta antes; debe obtener permiso.
        var preflight = new HttpRequestMessage(HttpMethod.Options, $"/api/v1/channels/{Guid.NewGuid()}/recording/start");
        preflight.Headers.Add("Origin", "http://192.168.1.50:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        var answer = await app.GetTestClient().SendAsync(preflight);
        Assert.Equal(HttpStatusCode.NoContent, answer.StatusCode);
        Assert.Contains("POST", string.Join(",", answer.Headers.GetValues("Access-Control-Allow-Methods")));
        await app.StopAsync();
    }

    [Fact]
    public async Task Cors_Star_AllowsAnyPanel()
    {
        await using var app = BuildApiWithCors("*");
        await app.StartAsync();

        var response = await GetChannelsFrom(app, "http://cualquier-panel:8080");

        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
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

    /// <summary>Lee hasta que el socket se acaba (Close o conexión cortada). Devuelve false si venció el plazo.</summary>
    private static async Task<bool> EndsBeforeAsync(System.Net.WebSockets.WebSocket socket, TimeSpan limit)
    {
        using var timeout = new CancellationTokenSource(limit);
        var buffer = new byte[64 * 1024];
        try
        {
            while (socket.State == System.Net.WebSockets.WebSocketState.Open)
            {
                var r = await socket.ReceiveAsync(buffer, timeout.Token);
                if (r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close) break;
            }
        }
        catch when (!timeout.IsCancellationRequested) { /* cortado por el servidor: también es «se acabó» */ }
        catch { return false; }
        return !timeout.IsCancellationRequested;
    }

    [Fact]
    public async Task Sockets_AreReleasedAsSoonAsTheApplicationStartsStopping_EvenIfTheClientNeverCloses()
    {
        // Un panel web abierto NO cierra sus WebSocket porque la aplicación se vaya a cerrar. Si el servidor los espera,
        // el apagado de Kestrel se alarga hasta su plazo y el cierre de la aplicación agota su tope ANTES de finalizar
        // las grabaciones (medido en vivo: salida forzada a los 20 s, sesión sin cerrar en BD).
        var channelId = Guid.NewGuid();
        await using var app = BuildPreviewApi(new FakeSnapshots(channelId));
        await app.StartAsync();
        using var connect = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var client = app.GetTestServer().CreateWebSocketClient();
        using var events = await client.ConnectAsync(new Uri("ws://localhost/ws/events"), connect.Token);
        using var preview = await client.ConnectAsync(new Uri($"ws://localhost/ws/preview/{channelId}?fps=5"), connect.Token);
        await ReceiveMessageAsync(preview, connect.Token);                 // la vista previa ya está emitiendo

        app.Services.GetRequiredService<Microsoft.Extensions.Hosting.IHostApplicationLifetime>().StopApplication();

        var ended = await Task.WhenAll(EndsBeforeAsync(events, TimeSpan.FromSeconds(5)), EndsBeforeAsync(preview, TimeSpan.FromSeconds(5)));
        Assert.True(ended[0], "El WebSocket de eventos siguió abierto tras empezar el apagado.");
        Assert.True(ended[1], "El WebSocket de vista previa siguió abierto tras empezar el apagado.");
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
