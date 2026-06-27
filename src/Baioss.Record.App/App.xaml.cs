using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
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
using Baioss.Record.Application.Scheduling;
using Baioss.Record.Infrastructure.Scheduling;
using Baioss.Record.App.Demo;
using Baioss.Record.App.Preview;
using Baioss.Record.App.Recording;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Preview;
using Baioss.Record.Infrastructure.Storage;

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
        WireGlobalExceptionHandlers();

        // Raíz del repositorio (carpeta que contiene tools/), localizada hacia arriba desde el
        // ejecutable; ancla datos/grabaciones/logs con independencia del working directory.
        var root = FindUpwards("tools") is { } toolsDir ? Path.GetDirectoryName(toolsDir)! : Directory.GetCurrentDirectory();
        var ffmpegDir = FindUpwards(Path.Combine("tools", "ffmpeg", "ffmpeg.exe")) is { } ffmpegExe
            ? Path.GetDirectoryName(ffmpegExe)
            : null;
        var clipPath = FindUpwards(Path.Combine("tools", "test", "clip.mp4"));
        var dbPath = Path.Combine(root, "data", "baioss.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        // Selección de encoder (una sola vez): GPU dedicada (NVENC) → GPU integrada (QSV/AMF) → CPU (libx264).
        var (real, codec, encoderNotes) = await ProbeEngineAsync(ffmpegDir, clipPath);
        var gpuEncoders = real && codec == VideoCodec.H264Nvenc; // NVENC utilizable → ofrecer códecs GPU en la UI

        // El host es una WebApplication (Kestrel) que además levanta la UI y los servicios de fondo:
        // un único contenedor DI compartido por la UI, los canales y la API de automatización.
        var builder = WebApplication.CreateBuilder();
        builder.Host.UseSerilog((ctx, cfg) => cfg
            // El scheduler consulta SQLite cada segundo: silencia el log de cada comando SQL de EF (solo avisos).
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(root, "logs", "baioss-.log"), rollingInterval: RollingInterval.Day)
            .Enrich.FromLogContext());
        builder.WebHost.UseUrls(ApiUrl);

        // Nº de canales a crear (1-8). Se lee del appsettings EMBEBIDO en el binario: el release publicado lo
        // lleva FIJO. Se añade DESPUÉS de cualquier appsettings externo, así que PREVALECE → el operador no
        // puede cambiar los canales editando archivos junto al .exe.
        using (var embedded = typeof(App).Assembly.GetManifestResourceStream("appsettings.json"))
            if (embedded is not null) builder.Configuration.AddJsonStream(embedded);
#if DEBUG
        // Solo en DESARROLLO: permite override con un appsettings.json junto al .exe (probar 2/4 sin recompilar).
        builder.Configuration.AddJsonFile(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false);
#endif
        int channelCount = Math.Clamp(builder.Configuration.GetValue("Channels:Count", 4), 1, 8);

        var s = builder.Services;
        // Bus de eventos y storage (que la API necesita) se registran en ambos modos; en modo real
        // se añaden además repos/captura/locator. En simulado, la BD queda registrada pero sin usar.
        s.AddBaiossInfrastructure(dbPath, real ? ffmpegDir : null);
        s.AddBaiossCqrs(); // IDispatcher + handlers de comandos/queries que despacha la API
        s.AddSingleton(new RecordingCapabilities { GpuEncoders = gpuEncoders });
        s.AddSingleton<PreviewCatalog>();
        // El ChannelHost compone los canales y permite reasignarles la entrada en caliente.
        s.AddSingleton(new ChannelCompositionContext(real, root, ffmpegDir, clipPath, codec, channelCount));
        s.AddSingleton<ChannelHost>();
        s.AddSingleton<IChannelManager>(sp => sp.GetRequiredService<ChannelHost>());
        // Scheduler de grabación automática (BackgroundService): dispara start/stop por hora/calendario.
        s.AddSingleton<SchedulerService>();
        s.AddSingleton<ISchedulerService>(sp => sp.GetRequiredService<SchedulerService>());
        s.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
        // Retención automática de grabaciones viejas (opt-in vía appsettings «Retention»; deshabilitada por
        // defecto para no borrar nada sin que el operador lo pida). Aplica también políticas por canal.
        var retention = builder.Configuration.GetSection("Retention").Get<RetentionOptions>() ?? new RetentionOptions();
        s.AddSingleton(retention);
        s.AddHostedService<RetentionService>();
        s.AddSingleton<ShellViewModel>();
        s.AddSingleton<MainWindow>();

        var app = builder.Build();
        // KeepAlive de servidor: detecta clientes WS caídos en half-open (cable cortado sin frame Close) y
        // libera su suscripción al bus, evitando suscripciones zombi en operación 24/7. (Auditoría 24/7, #28.)
        app.UseWebSockets(new Microsoft.AspNetCore.Builder.WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });   // necesario para el endpoint /ws/events
        app.MapBaiossApi();    // REST de automatización + WebSocket de eventos
        _host = app;

        await app.StartAsync();

        // Recovery tras un cierre ABRUPTO: cierra las sesiones que quedaron «grabando» de una ejecución
        // anterior (crash/kill) para que la BD no arrastre grabaciones colgadas. La BD ya está compuesta (la
        // creó el ChannelHost al instanciarse el scheduler en StartAsync). Solo en modo real (en simulado no
        // se graba ni se persiste). Best-effort: un fallo aquí no impide arrancar.
        if (real)
        {
            try
            {
                int closed = await app.Services.GetRequiredService<IRecordingSessionRepository>()
                    .CloseOrphanedAsync(DateTimeOffset.UtcNow);
                if (closed > 0)
                    Serilog.Log.Warning("Recovery: {Count} sesión(es) huérfana(s) de un cierre previo cerradas como error.", closed);
            }
            catch (Exception ex) { Serilog.Log.Error(ex, "Recovery de sesiones huérfanas falló."); }
        }

        Serilog.Log.Information("API REST + WebSocket escuchando en {Url} (solo loopback).", ApiUrl);
        Serilog.Log.Information("Canales configurados (Channels:Count): {Count}.", channelCount);
        // Cascada GPU dedicada (NVENC) → GPU integrada (QSV/AMF) → CPU (libx264): por qué se descartó cada
        // GPU (p. ej. driver NVIDIA viejo para este FFmpeg) y cuál quedó como encoder por defecto.
        Serilog.Log.Information("Cascada de encoder: {Count} GPU(s) descartada(s) antes del elegido.", encoderNotes.Count);
        foreach (var note in encoderNotes) Serilog.Log.Information(" - {Note}", note);
        Serilog.Log.Information("Encoder de video por defecto: {Codec} ({Mode}).", codec, real ? "real" : "simulado");
        app.Services.GetRequiredService<MainWindow>().Show();
    }

    /// <summary>
    /// Red de seguridad 24/7: una excepción no observada en el hilo de UI o en una tarea de fondo NO debe
    /// tumbar el proceso (cerraría la grabación de TODOS los canales a la vez). Se registran los tres handlers
    /// globales y se loguea la causa; los fallos del hilo de UI se marcan como gestionados para no cerrar la app.
    /// </summary>
    private void WireGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += (_, ev) =>
        {
            Serilog.Log.Error(ev.Exception, "Excepción no controlada en el hilo de UI (Dispatcher); la app sigue activa.");
            ev.Handled = true; // un binding o un handler de canal no debe tumbar la UI ni cortar las grabaciones
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
            Serilog.Log.Fatal(ev.ExceptionObject as Exception, "Excepción no controlada de dominio (IsTerminating={Terminating}).", ev.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            Serilog.Log.Error(ev.Exception, "Excepción no observada en una tarea de fondo; observada para no escalar.");
            ev.SetObserved();
        };
    }

    /// <summary>
    /// Determina si se puede grabar de verdad y con qué encoder (cascada NVENC → QSV → AMF → CPU);
    /// <c>Notes</c> lleva el motivo por el que se descartó cada GPU, para registrarlo al arrancar.
    /// </summary>
    private static async Task<(bool Real, VideoCodec Codec, IReadOnlyList<string> Notes)> ProbeEngineAsync(string? ffmpegDir, string? clipPath)
    {
        var notes = new List<string>();
        if (ffmpegDir is null || clipPath is null) return (false, VideoCodec.H264x264, notes);
        try
        {
            var locator = new FfmpegLocator(ffmpegDir);
            // Cascada: GPU dedicada (NVIDIA NVENC) → GPU integrada (Intel QSV → AMD AMF) → CPU (libx264).
            var cascade = new[]
            {
                ("h264_nvenc", VideoCodec.H264Nvenc),
                ("h264_qsv",   VideoCodec.H264Qsv),
                ("h264_amf",   VideoCodec.H264Amf),
            };
            foreach (var (name, codecOption) in cascade)
            {
                var (ok, detail) = await locator.ProbeVideoEncoderAsync(name);
                if (ok) return (true, codecOption, notes);
                notes.Add($"Encoder GPU «{name}» no utilizable: {detail}");
            }
            return (true, VideoCodec.H264x264, notes); // ninguna GPU utilizable → software (CPU)
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "FFmpeg no utilizable; los canales caerán a modo simulado.");
            return (false, VideoCodec.H264x264, notes); // sin FFmpeg utilizable → simulado
        }
    }

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
            // Dispone los canales (cierre ordenado de FFmpeg → finaliza archivos) antes de parar el host.
            try { await _host.Services.GetRequiredService<ChannelHost>().DisposeAsync(); } catch { /* noop */ }
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
