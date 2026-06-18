using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Baioss.Record.Api;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Persistence;
using Baioss.Record.App.Demo;
using Baioss.Record.App.Preview;
using Baioss.Record.App.Recording;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App;

/// <summary>
/// Composition root. Construye los canales con el motor FFmpeg real (grabación a MP4) usando
/// un clip como "fuente en vivo" y persistiendo sesiones/segmentos en SQLite. El binario de
/// FFmpeg y el clip se localizan junto al repositorio (carpeta <c>tools/</c>); si no se
/// encuentran, los canales caen a modo simulado para que la app siga abriendo.
/// </summary>
public partial class App : System.Windows.Application
{
    // API local de automatización: solo loopback (sin exposición a la red; la autenticación es Fase 3).
    private const string ApiUrl = "http://127.0.0.1:5005";

    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Raíz del repositorio (carpeta que contiene tools/), localizada hacia arriba desde el
        // ejecutable; ancla datos/grabaciones/logs con independencia del working directory.
        var root = FindUpwards("tools") is { } toolsDir ? Path.GetDirectoryName(toolsDir)! : Directory.GetCurrentDirectory();
        var ffmpegDir = FindUpwards(Path.Combine("tools", "ffmpeg", "ffmpeg.exe")) is { } ffmpegExe
            ? Path.GetDirectoryName(ffmpegExe)
            : null;
        var clipPath = FindUpwards(Path.Combine("tools", "test", "clip.mp4"));
        var dbPath = Path.Combine(root, "data", "baioss.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // Selección de encoder (una sola vez): NVENC si el driver responde, si no libx264.
        var (real, codec) = await ProbeEngineAsync(ffmpegDir, clipPath);
        var gpuEncoders = real && codec == VideoCodec.H264Nvenc; // hay NVENC utilizable → ofrecer códecs GPU en la UI

        // El host es una WebApplication (Kestrel) que además levanta la UI y los servicios de fondo:
        // un único contenedor DI compartido por la UI, los canales y la API de automatización.
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseSerilog((ctx, cfg) => cfg
            .WriteTo.File(Path.Combine(root, "logs", "baioss-.log"), rollingInterval: RollingInterval.Day)
            .Enrich.FromLogContext());
        builder.WebHost.UseUrls(ApiUrl);

        var s = builder.Services;
        // Bus de eventos y storage (que la API necesita) se registran en ambos modos; en modo real
        // se añaden además repos/captura/locator. En simulado, la BD queda registrada pero sin usar.
        s.AddBaiossInfrastructure(dbPath, real ? ffmpegDir : null);
        s.AddBaiossCqrs(); // IDispatcher + handlers de comandos/queries que despacha la API
        s.AddSingleton(new RecordingCapabilities { GpuEncoders = gpuEncoders });
        s.AddSingleton<PreviewCatalog>();
        s.AddSingleton<IReadOnlyList<IChannelEngine>>(sp =>
            BuildChannels(sp, real, root, ffmpegDir, clipPath, codec));
        s.AddSingleton<IChannelManager>(sp =>
            new ChannelManager(sp.GetRequiredService<IReadOnlyList<IChannelEngine>>()));
        s.AddSingleton<ShellViewModel>();
        s.AddSingleton<MainWindow>();

        var app = builder.Build();
        app.UseWebSockets();   // necesario para el endpoint /ws/events
        app.MapBaiossApi();    // REST de automatización + WebSocket de eventos
        _host = app;

        await app.StartAsync();
        Serilog.Log.Information("API REST + WebSocket escuchando en {Url} (solo loopback).", ApiUrl);
        app.Services.GetRequiredService<MainWindow>().Show();
    }

    /// <summary>Determina si se puede grabar de verdad y con qué encoder de video.</summary>
    private static async Task<(bool Real, VideoCodec Codec)> ProbeEngineAsync(string? ffmpegDir, string? clipPath)
    {
        if (ffmpegDir is null || clipPath is null) return (false, VideoCodec.H264x264);
        try
        {
            var locator = new FfmpegLocator(ffmpegDir);
            var nvenc = await locator.IsVideoEncoderUsableAsync("h264_nvenc");
            return (true, nvenc ? VideoCodec.H264Nvenc : VideoCodec.H264x264);
        }
        catch
        {
            return (false, VideoCodec.H264x264); // sin FFmpeg utilizable → simulado
        }
    }

    /// <summary>
    /// Construye los canales A y B con grabación real y persistencia. Hace seed idempotente de
    /// canal/fuente/perfil en SQLite (GUIDs deterministas) y arranca el monitor de señal por canal.
    /// Cualquier fallo de inicialización cae a canales simulados.
    /// </summary>
    private static IReadOnlyList<IChannelEngine> BuildChannels(
        IServiceProvider sp, bool real, string root, string? ffmpegDir, string? clipPath, VideoCodec codec)
    {
        if (!real || ffmpegDir is null || clipPath is null)
            return new IChannelEngine[] { new SimulatedChannelEngine("A"), new SimulatedChannelEngine("B") };

        try
        {
            sp.EnsureBaiossDatabaseCreated();

            var locator = sp.GetRequiredService<IFfmpegLocator>();
            var bus = sp.GetRequiredService<IEventBus>();
            var loggers = sp.GetRequiredService<ILoggerFactory>();
            var sessions = sp.GetRequiredService<IRecordingSessionRepository>();
            var segments = sp.GetRequiredService<IRepository<Segment>>();
            var channelsRepo = sp.GetRequiredService<IChannelRepository>();
            var sourcesRepo = sp.GetRequiredService<IInputSourceRepository>();
            var profilesRepo = sp.GetRequiredService<IRecordingProfileRepository>();
            var previews = sp.GetRequiredService<PreviewCatalog>();

            var result = new List<IChannelEngine>();
            foreach (var key in new[] { "A", "B" })
            {
                var def = new InputSource
                {
                    Id = StableGuid($"source:{key}"),
                    Name = $"Clip {key}", Type = InputType.File, Uri = clipPath,
                    Parameters = { ["loop"] = "1", ["realtime"] = "1" }, // bucle a velocidad real = fuente en vivo
                    ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
                };
                var profile = new RecordingProfile
                {
                    Id = StableGuid($"profile:{key}"),
                    Name = "MP4 (demo)", VideoCodec = codec, HwAccel = HwAccel.None,
                    VideoBitrate = Bitrate.FromMbps(8), GopSize = 50,
                    AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
                };
                var channelId = StableGuid($"channel:{key}");
                var channel = new Channel
                {
                    Id = channelId, Key = key, Name = $"Canal {key}",
                    InputSourceId = def.Id, ProfileId = profile.Id,
                };
                SeedIfAbsent(sourcesRepo, def.Id, def);
                SeedIfAbsent(profilesRepo, profile.Id, profile);
                SeedIfAbsent(channelsRepo, channelId, channel);

                // Perfil vigente: el persistido (última configuración elegida por el operador) si existe.
                var activeProfile = profilesRepo.GetAsync(profile.Id).GetAwaiter().GetResult() ?? profile;

                var source = new FileCaptureSource(def);
                source.OpenAsync().GetAwaiter().GetResult();

                var recorder = new FfmpegRecorderEngine(locator, loggers.CreateLogger<FfmpegRecorderEngine>())
                {
                    OutputRoot = Path.Combine(root, "recordings"),
                    ProxyRoot = Path.Combine(root, "proxies"),
                };
                var monitor = new SignalMonitor(bus, loggers.CreateLogger<SignalMonitor>()) { ChannelId = channelId };

                // Preview de baja latencia: proceso FFmpeg independiente (no acopla al encoder).
                var preview = new FfmpegPreviewEngine(locator, loggers.CreateLogger<FfmpegPreviewEngine>());
                _ = preview.StartAsync(source);
                previews.Add(channelId, preview);

                result.Add(new StandaloneChannelEngine(
                    key, source, activeProfile, recorder, channelId,
                    sessions, segments, bus, monitor, loggers.CreateLogger<StandaloneChannelEngine>(), profilesRepo));
            }
            return result;
        }
        catch
        {
            return new IChannelEngine[] { new SimulatedChannelEngine("A"), new SimulatedChannelEngine("B") };
        }
    }

    private static void SeedIfAbsent<T>(IRepository<T> repo, Guid id, T entity) where T : class
    {
        if (repo.GetAsync(id).GetAwaiter().GetResult() is null)
            repo.AddAsync(entity).GetAwaiter().GetResult();
    }

    /// <summary>GUID determinista (v3-like, MD5) para que el seed sea idempotente entre ejecuciones.</summary>
    private static Guid StableGuid(string seed)
        => new(MD5.HashData(Encoding.UTF8.GetBytes(seed)));

    /// <summary>Busca un archivo/carpeta relativo subiendo desde el ejecutable y el working directory.</summary>
    private static string? FindUpwards(string relative)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            try { await _host.Services.GetRequiredService<PreviewCatalog>().DisposeAllAsync(); } catch { /* noop */ }
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
