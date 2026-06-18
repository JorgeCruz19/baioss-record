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
}
