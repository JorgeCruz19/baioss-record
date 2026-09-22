using Baioss.Record.Application.Network;
using Baioss.Record.Infrastructure.Network;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Desde dónde se llega a la API del Record: dirección, puerto y webs permitidas (para el panel web remoto).</summary>
public class ApiAccessSettingsTests
{
    [Fact]
    public void Defaults_AreTheBehaviourOfAlways_LoopbackOnly5005NoForeignWebsites()
    {
        var s = new ApiAccessSettings().Sanitized();

        Assert.Equal("http://127.0.0.1:5005", s.ListenUrl);
        Assert.False(s.ListensOnNetwork);
        Assert.Empty(s.Origins);
    }

    [Theory]
    [InlineData("0.0.0.0", "0.0.0.0", true)]
    [InlineData("*", "0.0.0.0", true)]
    [InlineData("192.168.1.10", "192.168.1.10", true)]
    [InlineData("127.0.0.1", "127.0.0.1", false)]
    [InlineData("127.0.0.2", "127.0.0.2", false)]        // todo 127.x es este equipo
    [InlineData("localhost", "127.0.0.1", false)]
    [InlineData("", "127.0.0.1", false)]
    [InlineData("grabador-01", "127.0.0.1", false)]      // un nombre no es una dirección de escucha: a lo seguro
    [InlineData("::1", "127.0.0.1", false)]              // solo IPv4
    [InlineData("999.1.1.1", "127.0.0.1", false)]
    public void Host_WhatIsNotUnderstoodFallsBackToThisComputerOnly(string host, string expected, bool network)
    {
        var s = new ApiAccessSettings { Host = host }.Sanitized();

        Assert.Equal(expected, s.Host);
        Assert.Equal(network, s.ListensOnNetwork);
    }

    [Theory]
    [InlineData(5005, 5005)]
    [InlineData(8080, 8080)]
    [InlineData(1, 1)]
    [InlineData(65535, 65535)]
    [InlineData(0, 5005)]
    [InlineData(-3, 5005)]
    [InlineData(70000, 5005)]
    public void Port_OutOfRange_GoesBackToTheDefault(int port, int expected)
        => Assert.Equal(expected, new ApiAccessSettings { Port = port }.Sanitized().Port);

    [Fact]
    public void Origins_AreNormalised_DeduplicatedAndInvalidOnesDropped()
    {
        var origins = ApiAccessSettings.ParseOrigins(
            " http://192.168.1.50:5173/ ; HTTP://Panel.Local:80/ruta , https://panel.tv:443, ftp://x, no-es-una-url, http://192.168.1.50:5173 ");

        // Sin ruta ni barra final, en minúsculas, sin el puerto por defecto del esquema y sin repetidos.
        Assert.Equal(new[] { "http://192.168.1.50:5173", "http://panel.local", "https://panel.tv" }, origins);
    }

    [Fact]
    public void Origins_Star_MeansAnyWebsite()
    {
        var s = new ApiAccessSettings { AllowedOrigins = "*, http://a:1" }.Sanitized();

        Assert.True(s.AllowsAnyOrigin);
        Assert.Equal("*, http://a:1", s.AllowedOrigins);
    }

    [Fact]
    public void File_IsSeededTheFirstTime_ThenWhatIsSavedWins_AndGarbageDoesNotBreakStartup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "baioss-api-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "api-settings.json");
        try
        {
            // Primera vez: se siembra desde la configuración y el archivo queda a la vista.
            var seeded = ApiAccessSettingsFile.Load(path, new ApiAccessSettings { Port = 6001 }, out var problem);
            Assert.Null(problem);
            Assert.Equal(6001, seeded.Port);
            Assert.True(File.Exists(path));

            // Lo guardado manda sobre la semilla.
            ApiAccessSettingsFile.Save(path, new ApiAccessSettings { Host = "0.0.0.0", Port = 7002, AllowedOrigins = "*" });
            var loaded = ApiAccessSettingsFile.Load(path, new ApiAccessSettings(), out _);
            Assert.Equal("http://0.0.0.0:7002", loaded.ListenUrl);
            Assert.True(loaded.AllowsAnyOrigin);

            // Un archivo roto no tumba el arranque: vuelve a lo seguro y cuenta por qué.
            File.WriteAllText(path, "{ esto no es json");
            var recovered = ApiAccessSettingsFile.Load(path, new ApiAccessSettings(), out var why);
            Assert.NotNull(why);
            Assert.Equal("http://127.0.0.1:5005", recovered.ListenUrl);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* temporal */ } }
    }

    [Fact]
    public void CanBind_TellsAnUnusableAddressApart_SoStartupCanFallBack()
    {
        // Una IP que no es de este equipo (TEST-NET-1, reservada para documentación): no se puede escuchar en ella.
        Assert.False(ApiAccessSettingsFile.CanBind("192.0.2.1", 5999, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));

        // Loopback con puerto 0 (el sistema elige uno libre) siempre se puede.
        Assert.True(ApiAccessSettingsFile.CanBind("127.0.0.1", 0, out _));
    }
}
