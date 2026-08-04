using System.IO;
using System.Linq;
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

    private Mutex? _instanceMutex;

    // Cierre ordenado (N2): _shuttingDown = finalización en curso (bloquea nuevos intentos de cerrar mientras
    // se cierran los archivos); _shutdownComplete = host parado y canales finalizados (idempotencia + salta el
    // backstop de OnExit). Un cierre con grabaciones en curso NO debe dejar morir el proceso de golpe: el Job
    // Object mataría los FFmpeg sin flush → MP4 sin moov (sobre todo con el contenedor MP4 estándar).
    private bool _shuttingDown;
    private bool _shutdownComplete;
    /// <summary>Tope del apagado ordenado: si se excede (un FFmpeg/servicio atascado) se fuerza la salida, para
    /// que el proceso NUNCA quede vivo bloqueando el mutex de instancia única («ya está en ejecución» al reabrir).</summary>
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WireGlobalExceptionHandlers();

        // Modo de servicio para el INSTALADOR: «--machine-code <archivo>» escribe el código de este equipo en el
        // archivo indicado y sale. El asistente lo usa para MOSTRAR el código en la página de licencia, sin
        // duplicar el cálculo de la huella (que debe ser idéntico bit a bit al de la app). Se resuelve ANTES del
        // mutex de instancia única: es una consulta de un segundo y debe funcionar aunque la app ya esté abierta.
        if (TryHandleMachineCodeRequest(e.Args)) { Shutdown(0); return; }

        // Instancia única: una 2ª instancia chocaría al enlazar el puerto 5005 (Kestrel) y al abrir la BD.
        // Mejor avisar y salir limpio que cerrar el proceso con un error opaco. (Auditoría A2/#5.)
        _instanceMutex = new Mutex(initiallyOwned: true, @"Local\Baioss.Record.App.SingleInstance", out bool isFirst);
        if (!isFirst)
        {
            MessageBox.Show("Baioss Record ya está en ejecución.", "Baioss Record",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // El cuerpo del arranque va en su propio método para envolverlo en try/catch: un fallo (puerto 5005
        // ocupado, DI mal cableado, appsettings corrupto) deja un diagnóstico y un cierre limpio en vez de
        // cerrar el proceso sin rastro (OnStartup es async void). (Auditoría A2/#5.)
        try { await StartHostAsync(); }
        catch (Exception ex)
        {
            Serilog.Log.Fatal(ex, "Fallo de arranque de Baioss Record.");
            MessageBox.Show($"No se pudo iniciar Baioss Record:\n{ex.Message}", "Baioss Record",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    /// <summary>
    /// «--machine-code &lt;archivo&gt;»: escribe el código de equipo y termina. Lo invoca el instalador para poder
    /// enseñárselo al cliente en el asistente. Devuelve true si la app debe salir sin abrir la interfaz.
    /// </summary>
    private static bool TryHandleMachineCodeRequest(string[] args)
    {
        int i = Array.FindIndex(args, a => string.Equals(a, "--machine-code", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return false;
        try
        {
            var fingerprint = new Baioss.Record.Infrastructure.Licensing.WindowsMachineFingerprint(
                Microsoft.Extensions.Logging.Abstractions.NullLogger<Baioss.Record.Infrastructure.Licensing.WindowsMachineFingerprint>.Instance);
            if (i + 1 < args.Length) File.WriteAllText(args[i + 1], fingerprint.Code);
        }
        catch { /* el instalador simplemente no mostrará el código; no es motivo para fallar la instalación */ }
        return true;
    }

    /// <summary>
    /// Recoge la licencia que el INSTALADOR dejó preparada («pending-license.txt») y la activa en el primer
    /// arranque. El instalador no puede escribir directamente el estado de licencia porque va firmado con una
    /// clave derivada de la huella del equipo; deja la clave en claro y es la app quien la valida y la guarda.
    /// Best-effort: si la clave no fuera válida, el archivo se retira igualmente y la app queda en periodo de
    /// prueba (el operador puede activarla luego desde la ventana de Licencia).
    /// </summary>
    private static void ConsumePendingLicense(IServiceProvider services)
    {
        var pending = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Baioss", "Record", "pending-license.txt");
        try
        {
            if (!File.Exists(pending)) return;
            var key = File.ReadAllText(pending).Trim();
            var license = services.GetService<Baioss.Record.Application.Licensing.ILicenseService>();
            if (license is not null && !string.IsNullOrWhiteSpace(key))
            {
                var result = license.Activate(key);
                if (result.Success) Serilog.Log.Information("Licencia aplicada desde la instalación.");
                else Serilog.Log.Warning("La licencia indicada en la instalación no es válida: {Message}", result.Message);
            }
            File.Delete(pending); // no se reintenta en cada arranque
        }
        catch (Exception ex) { Serilog.Log.Warning(ex, "No se pudo aplicar la licencia dejada por el instalador."); }
    }

    private async Task StartHostAsync()
    {
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
        // Resiliencia de los servicios de fondo: en .NET 8 una excepción que ESCAPE de un BackgroundService
        // detiene TODO el host por defecto (BackgroundServiceExceptionBehavior.StopHost) → tumbaría las
        // grabaciones de todos los canales. Con Ignore, un fallo del scheduler/retención/auditoría queda en el
        // log pero NO mata el host (las grabaciones siguen). Complementa los handlers globales de C1. (Auditoría #34.)
        s.Configure<HostOptions>(o => o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);
        // Límite de optimización faststart (GB): al detener una grabación de archivo único por encima de este
        // tamaño, NO se reescribe para optimizar la búsqueda (el remux copia el archivo entero y saturaría el
        // disco, compitiendo con las grabaciones activas). 0 = sin límite. Para archivos largos con búsqueda
        // óptima sin reescribir: preset MKV o segmentación. (Rendimiento del faststart en grabaciones >4 GB.)
        double faststartGb = builder.Configuration.GetValue("Recording:FaststartMaxGB", 4.0);
        long faststartCap = faststartGb > 0 ? (long)(faststartGb * 1024 * 1024 * 1024) : 0;

        // Bus de eventos y storage (que la API necesita) se registran en ambos modos; en modo real
        // se añaden además repos/captura/locator. En simulado, la BD queda registrada pero sin usar.
        s.AddBaiossInfrastructure(dbPath, real ? ffmpegDir : null, faststartCap);
        s.AddBaiossCqrs(); // IDispatcher + handlers de comandos/queries que despacha la API
        s.AddSingleton(new RecordingCapabilities { GpuEncoders = gpuEncoders });
        s.AddSingleton<PreviewCatalog>();
        // Contenedor MP4/MOV: true (por defecto) fMP4 fragmentado ROBUSTO ante corte eléctrico/kill (+ remux a
        // faststart al detener); false = MP4 ESTÁNDAR con el moov al final (100% seekable en local, cierre rápido
        // y SIN remux ni saturación de disco, pero un corte antes del cierre limpio lo deja sin índice → poner
        // false SOLO en máquinas con SAI/UPS, como pidió el operador). (Recording:FragmentedMp4.)
        bool fragmentedMp4 = builder.Configuration.GetValue("Recording:FragmentedMp4", true);
        // El ChannelHost compone los canales y permite reasignarles la entrada en caliente.
        s.AddSingleton(new ChannelCompositionContext(real, root, ffmpegDir, clipPath, codec, channelCount, fragmentedMp4));
        s.AddSingleton<ChannelHost>();
        s.AddSingleton<IChannelManager>(sp => sp.GetRequiredService<ChannelHost>());
        // Scheduler de grabación automática (BackgroundService): dispara start/stop por hora/calendario.
        s.AddSingleton<SchedulerService>();
        s.AddSingleton<ISchedulerService>(sp => sp.GetRequiredService<SchedulerService>());
        s.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
        // Almacén de ajustes de almacenamiento EDITABLE (Fase 4c): fuente de verdad ÚNICA de retención + alertas +
        // emergencia, persistido en data/storage-settings.json. La PRIMERA vez se SIEMBRA desde la config heredada
        // (sección «Retention» + claves «Disk:*»); a partir de ahí manda el JSON (la UI/API lo editan y los
        // servicios lo leen en vivo). Las variables de entorno solo influyen en el sembrado inicial.
        var retention = builder.Configuration.GetSection("Retention").Get<RetentionOptions>() ?? new RetentionOptions();
        var settingsSeed = new Baioss.Record.Application.Storage.StorageSettings
        {
            RetentionEnabled = retention.Enabled,
            RetentionDays = retention.Days,
            MinFreeGB = retention.MinFreeGB,
            MinFreePercent = retention.MinFreePercent,
            IntervalMinutes = retention.IntervalMinutes > 0 ? retention.IntervalMinutes : retention.IntervalHours * 60,
            Action = retention.Action,
            ArchivePath = retention.ArchivePath,
            WarnPercent = builder.Configuration.GetValue("Disk:WarnPercent", 80),
            CriticalPercent = builder.Configuration.GetValue("Disk:CriticalPercent", 90),
            EmergencyPercent = builder.Configuration.GetValue("Disk:EmergencyPercent", 95),
            AutoCleanupOnEmergency = builder.Configuration.GetValue("Disk:AutoCleanupOnEmergency", false),
            StopNewRecordingsOnEmergency = builder.Configuration.GetValue("Disk:StopNewRecordingsOnEmergency", false),
        };
        var settingsPath = Path.Combine(root, "data", "storage-settings.json");
        s.AddSingleton<Baioss.Record.Application.Storage.IStorageSettingsStore>(sp =>
            new Baioss.Record.Infrastructure.Storage.JsonStorageSettingsStore(settingsPath, settingsSeed,
                sp.GetRequiredService<ILogger<Baioss.Record.Infrastructure.Storage.JsonStorageSettingsStore>>()));

        // Retención automática de grabaciones viejas (opt-in; lee el almacén de ajustes EN VIVO). Aplica también
        // políticas por canal. RecordingsRoot = volumen sobre el que la retención por ESPACIO libera hueco.
        s.AddHostedService(sp => new RetentionService(
            sp.GetRequiredService<Baioss.Record.Application.Storage.IStorageManager>(),
            sp.GetRequiredService<Baioss.Record.Application.Persistence.IChannelRepository>(),
            sp.GetRequiredService<Baioss.Record.Application.Persistence.IRetentionPolicyRepository>(),
            sp.GetRequiredService<Baioss.Record.Application.Storage.IStorageSettingsStore>(),
            sp.GetRequiredService<ILogger<RetentionService>>())
        {
            RecordingsRoot = Path.Combine(root, "recordings"),
            RecordingRootsProvider = ChannelDestinations(sp), // MULTI-DISCO: limpia en el disco de cada canal
        });
        // Coordinador GLOBAL de emergencia de almacenamiento (Fase 3b): vigila el volumen de grabación de forma
        // continua —también en IDLE, que la guarda por-canal (solo activa grabando) NO cubre— y, al entrar en
        // emergencia, AUDITA el evento y —opt-in— auto-limpia y/o BLOQUEA nuevas grabaciones (vía IStorageGate en
        // el pre-vuelo). Umbrales + opt-in se leen del almacén de ajustes EN VIVO (Fase 4c). Seguro por defecto.
        s.AddSingleton(sp => new StorageEmergencyCoordinator(
            sp.GetRequiredService<Baioss.Record.Application.Storage.IStorageManager>(),
            sp.GetRequiredService<Baioss.Record.Application.Abstractions.IEventBus>(),
            sp.GetRequiredService<Baioss.Record.Application.Storage.IStorageSettingsStore>(),
            sp.GetRequiredService<ILogger<StorageEmergencyCoordinator>>())
        {
            RecordingsRoot = Path.Combine(root, "recordings"),
            RecordingRootsProvider = ChannelDestinations(sp), // MULTI-DISCO: vigila el disco de destino de cada canal
        });
        s.AddSingleton<Baioss.Record.Application.Storage.IStorageGate>(sp => sp.GetRequiredService<StorageEmergencyCoordinator>());
        s.AddSingleton<Baioss.Record.Application.Storage.IStorageStatusProvider>(sp => sp.GetRequiredService<StorageEmergencyCoordinator>());
        s.AddHostedService(sp => sp.GetRequiredService<StorageEmergencyCoordinator>());
        // Auditoría 24/7: persiste los eventos de dominio (señal, grabación, disco, encoder) en la tabla
        // EventLog para trazabilidad post-incidente. (Auditoría #42.) Poda las entradas más antiguas que
        // «EventLog:RetentionDays» días (30 por defecto; ≤0 = conservar siempre) para que la tabla no crezca
        // sin límite en 24/7. Es higiene de la BD, distinta de la retención de grabaciones. (Auditoría N11.)
        int eventLogDays = builder.Configuration.GetValue<int?>("EventLog:RetentionDays") ?? 30;
        s.AddHostedService(sp => new Baioss.Record.Infrastructure.Messaging.EventLogWriter(
            sp.GetRequiredService<Baioss.Record.Application.Abstractions.IEventBus>(),
            sp.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<Baioss.Record.Infrastructure.Persistence.BaiossDbContext>>(),
            sp.GetRequiredService<ILogger<Baioss.Record.Infrastructure.Messaging.EventLogWriter>>())
        { RetentionDays = eventLogDays });
        // Telemetría de salud por canal (fps real vs objetivo, frames perdidos y escritura REAL al disco):
        // diagnostica «cortes» en grabación multicanal mostrando qué canal se queda atrás. (Fiabilidad 4 canales.)
        s.AddHostedService(sp => new Baioss.Record.Infrastructure.Diagnostics.ChannelHealthMonitor(
            sp.GetRequiredService<IChannelManager>(),
            sp.GetRequiredService<ILogger<Baioss.Record.Infrastructure.Diagnostics.ChannelHealthMonitor>>()));
        // --- Licenciamiento: prueba de 14 días + licencia PERPETUA atada a este equipo. ---
        // REGLA DURA: nada de esto puede impedir que la app ARRANQUE ni que grabe por un fallo propio. Por eso la
        // huella y el servicio se construyen dentro de try/catch y, si algo falla, sencillamente NO se registra la
        // compuerta: el canal la recibe como null y graba sin restricción (fail-open). Un impago cuesta dinero;
        // dejar sin grabar a una emisora por un bug nuestro cuesta mucho más.
        try
        {
            s.AddSingleton<Baioss.Record.Application.Licensing.IMachineFingerprint>(sp =>
                new Baioss.Record.Infrastructure.Licensing.WindowsMachineFingerprint(
                    sp.GetRequiredService<ILogger<Baioss.Record.Infrastructure.Licensing.WindowsMachineFingerprint>>()));
            s.AddSingleton(sp => new Baioss.Record.Infrastructure.Licensing.LicenseStore(
                sp.GetRequiredService<Baioss.Record.Application.Licensing.IMachineFingerprint>(),
                sp.GetRequiredService<ILogger<Baioss.Record.Infrastructure.Licensing.LicenseStore>>()));
            s.AddSingleton<Baioss.Record.Infrastructure.Licensing.LicenseService>();
            s.AddSingleton<Baioss.Record.Application.Licensing.ILicenseService>(sp =>
                sp.GetRequiredService<Baioss.Record.Infrastructure.Licensing.LicenseService>());
            s.AddSingleton<Baioss.Record.Application.Licensing.ILicenseGate>(sp =>
                sp.GetRequiredService<Baioss.Record.Infrastructure.Licensing.LicenseService>());
            s.AddHostedService(sp => sp.GetRequiredService<Baioss.Record.Infrastructure.Licensing.LicenseService>());
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Licenciamiento: no se pudo componer el subsistema; la app funcionará SIN restricciones.");
        }

        s.AddSingleton<ShellViewModel>();
        s.AddSingleton<MainWindow>();

        var app = builder.Build();
        // KeepAlive de servidor: detecta clientes WS caídos en half-open (cable cortado sin frame Close) y
        // libera su suscripción al bus, evitando suscripciones zombi en operación 24/7. (Auditoría 24/7, #28.)
        app.UseWebSockets(new Microsoft.AspNetCore.Builder.WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });   // necesario para el endpoint /ws/events
        app.MapBaiossApi();    // REST de automatización + WebSocket de eventos
        _host = app;

        // Construye los canales (I/O de FFmpeg/NDI por canal) FUERA del hilo de UI y EN PARALELO, pre-resolviendo
        // el singleton en un hilo de fondo ANTES de StartAsync. Si no, el ChannelHost se construiría dentro de
        // app.StartAsync (al crear el scheduler) sobre el hilo de UI, congelándolo mientras las fuentes abren —una
        // NDI sin señal tarda ~8 s en su timeout y, en serie, 4 canales sumaban ~32 s—. (Auditoría 24/7, #17.)
        await Task.Run(() => app.Services.GetRequiredService<ChannelHost>());

        await app.StartAsync();

        // Si el instalador dejó una licencia preparada, se aplica ahora (una sola vez).
        ConsumePendingLicense(app.Services);

        // Recovery tras un cierre ABRUPTO: cierra las sesiones que quedaron «grabando» de una ejecución
        // anterior (crash/kill) para que la BD no arrastre grabaciones colgadas. La BD ya está compuesta (la
        // creó el ChannelHost al pre-resolverlo, justo arriba). Solo en modo real (en simulado no
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

            // Reconcilia los segmentos que quedaron en disco SIN registro en la BD por un corte eléctrico /
            // cierre abrupto durante una grabación segmentada (el último segmento no llegó a emitirse). Se da
            // de alta el material para que aparezca en el historial. Antes del barrido .faststart, que solo
            // borra temporales de remux. Best-effort. (Auditoría 24/7, #23.)
            try
            {
                var dbFactory = app.Services.GetRequiredService<
                    Microsoft.EntityFrameworkCore.IDbContextFactory<Baioss.Record.Infrastructure.Persistence.BaiossDbContext>>();
                var recLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Recovery.Segments");
                await OrphanSegmentReconciler.ReconcileAsync(dbFactory, Path.Combine(root, "recordings"), recLog);
            }
            catch (Exception ex) { Serilog.Log.Error(ex, "Reconciliación de segmentos huérfanos falló."); }

            // Barre temporales de remux (.faststart) huérfanos de una caída durante el remux: nunca deben
            // sobrevivir a un reinicio (pueden ocupar varios GB de un archivo grande). (Auditoría #43.)
            try
            {
                var recDir = Path.Combine(root, "recordings");
                if (Directory.Exists(recDir))
                    foreach (var pat in new[] { "*.faststart.mp4", "*.faststart.mov" })
                        foreach (var f in Directory.EnumerateFiles(recDir, pat, SearchOption.AllDirectories))
                            try { File.Delete(f); } catch { /* best-effort */ }
            }
            catch (Exception ex) { Serilog.Log.Debug(ex, "Barrido de temporales .faststart falló."); }
        }

        Serilog.Log.Information("API REST + WebSocket escuchando en {Url} (solo loopback).", ApiUrl);
        Serilog.Log.Information("Canales configurados (Channels:Count): {Count}.", channelCount);
        // Cascada GPU dedicada (NVENC) → GPU integrada (QSV/AMF) → CPU (libx264): por qué se descartó cada
        // GPU (p. ej. driver NVIDIA viejo para este FFmpeg) y cuál quedó como encoder por defecto.
        Serilog.Log.Information("Cascada de encoder: {Count} GPU(s) descartada(s) antes del elegido.", encoderNotes.Count);
        foreach (var note in encoderNotes) Serilog.Log.Information(" - {Note}", note);
        Serilog.Log.Information("Encoder de video por defecto: {Codec} ({Mode}).", codec, real ? "real" : "simulado");
        var mainWindow = app.Services.GetRequiredService<MainWindow>();
        mainWindow.Closing += OnMainWindowClosing; // finaliza las grabaciones ANTES de cerrar la app (N2)
        mainWindow.Show();
    }

    /// <summary>Canales que están grabando ahora mismo (estado autoritativo del motor, no de la UI), para
    /// avisar al cerrar y finalizar sus archivos antes de que el proceso muera. (Auditoría grabación N2.)</summary>
    private IReadOnlyList<string> ChannelsRecording()
    {
        try
        {
            return _host!.Services.GetRequiredService<IChannelManager>().Channels
                .Select(c => c.Status)
                .Where(st => st.RecordingState is RecordingState.Recording or RecordingState.Paused
                          or RecordingState.Starting or RecordingState.Stopping)
                .Select(st => st.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
        }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>
    /// Cierre ORDENADO del host: para los servicios de fondo (para que el scheduler no toque los canales
    /// mientras finalizan), dispone los canales EN PARALELO (cada FFmpeg cierra su contenedor y finaliza el
    /// archivo) y libera el host. Idempotente. Lo usan el handler de Closing y el backstop de OnExit. (N2.)
    /// </summary>
    private async Task ShutdownHostAsync()
    {
        var host = _host;
        if (host is null || _shutdownComplete) return;
        // ConfigureAwait(false) OBLIGATORIO en toda la cadena: OnExit invoca esto con GetResult() bloqueando el
        // hilo de UI; sin esto, las continuaciones intentarían volver a ese hilo bloqueado → deadlock al cerrar.

        // 1) Para los servicios de fondo (el scheduler no debe disparar start/stop mientras cerramos).
        try { await host.StopAsync().ConfigureAwait(false); }
        catch (Exception ex) { Serilog.Log.Error(ex, "Cierre: fallo al parar los servicios de fondo."); }

        // 2) Detén ORDENADAMENTE las grabaciones en curso: finaliza el archivo Y cierra la sesión en BD
        //    (EndedAt/estado) + publica RecordingStopped. Disponer el canal por sí solo finaliza el archivo pero
        //    dejaría la sesión abierta → el recovery del próximo arranque la marcaría «error». (Auditoría N2.)
        try
        {
            var mgr = host.Services.GetRequiredService<IChannelManager>();
            await Task.WhenAll(mgr.Channels
                .Where(c => c.Status.RecordingState is RecordingState.Recording or RecordingState.Paused or RecordingState.Starting)
                .Select(async c =>
                {
                    try { await c.StopRecordingAsync().ConfigureAwait(false); }
                    catch (Exception ex) { Serilog.Log.Error(ex, "Cierre: fallo al detener el canal {Ch}.", c.ChannelId); }
                })).ConfigureAwait(false);
        }
        catch (Exception ex) { Serilog.Log.Error(ex, "Cierre: fallo al detener las grabaciones en curso."); }

        // 3) Dispone los canales (ya inactivos): libera el FFmpeg de preview, los sockets y las fuentes.
        try { await host.Services.GetRequiredService<ChannelHost>().DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { Serilog.Log.Error(ex, "Cierre: fallo al finalizar los canales."); }
        try { host.Dispose(); } catch { /* noop */ }
        _shutdownComplete = true;
    }

    /// <summary>
    /// Al cerrar la ventana con grabaciones en curso, NO dejar morir el proceso de golpe: el Job Object mataría
    /// los FFmpeg sin cerrar el contenedor (MP4 sin moov = ilegible). Se cancela el cierre, se confirma con el
    /// operador, se finalizan los archivos ordenadamente y solo entonces se cierra de verdad. (Auditoría N2.)
    /// </summary>
    private async void OnMainWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_shutdownComplete) return;                  // ya finalizado: deja cerrar de verdad
        if (_shuttingDown) { e.Cancel = true; return; } // finalización en curso: no re-entrar

        // SIEMPRE tomamos el control del cierre para hacerlo aquí ORDENADO Y ASÍNCRONO, en vez de dejar que OnExit
        // lo haga BLOQUEANDO el hilo de UI con un GetResult(): si algún FFmpeg/servicio se atascaba, ese bloqueo
        // dejaba el PROCESO vivo sin salir → el mutex de instancia única quedaba tomado → «ya está en ejecución»
        // al reabrir. Con grabaciones en curso se confirma antes (se finalizarán los archivos). (Cierre robusto.)
        // Confirmación SIEMPRE al cerrar (con o sin grabación): evita cierres accidentales de una app de
        // grabación 24/7. Si hay grabaciones en curso, el mensaje avisa de que se finalizarán los archivos.
        var recording = ChannelsRecording();
        e.Cancel = true; // tomamos el control; solo se cierra si el operador CONFIRMA
        var confirm = recording.Count > 0
            ? MessageBox.Show(
                $"Hay {recording.Count} grabación(es) en curso (canal {string.Join(", ", recording)}).\n\n" +
                "¿Detenerlas y salir? Se finalizarán los archivos correctamente antes de cerrar.",
                "Cerrar Baioss Record", MessageBoxButton.YesNo, MessageBoxImage.Warning)
            : MessageBox.Show(
                "¿Seguro que quieres cerrar Baioss Record?",
                "Cerrar Baioss Record", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return; // no cerrar (e.Cancel sigue en true)

        _shuttingDown = true;
        if (sender is Window w) w.Title = "Baioss Record — cerrando…";
        // Apagado ACOTADO: si se atasca (FFmpeg colgado, servicio que no cancela) NO colgamos el cierre —
        // ForceExit fuerza la salida igualmente para que el proceso nunca quede vivo.
        try { await ShutdownHostAsync().WaitAsync(ShutdownTimeout); }
        catch (TimeoutException) { Serilog.Log.Warning("Cierre: el apagado ordenado excedió {T:0} s; se fuerza la salida.", ShutdownTimeout.TotalSeconds); }
        catch (Exception ex) { Serilog.Log.Error(ex, "Cierre: error en el apagado ordenado; se fuerza la salida."); }
        ForceExit();
    }

    /// <summary>
    /// Libera el mutex de instancia única y TERMINA el proceso de forma garantizada, aunque el apagado dejara
    /// algún <c>await</c>/hilo colgado (que, si no, mantendría el proceso vivo bloqueando el mutex → «ya está en
    /// ejecución» al reabrir). El Job Object mata los FFmpeg hijos al morir el proceso. (Cierre robusto.)
    /// </summary>
    private void ForceExit()
    {
        try { _instanceMutex?.ReleaseMutex(); } catch { /* no somos el dueño o ya liberado */ }
        try { _instanceMutex?.Dispose(); } catch { /* noop */ }
        _instanceMutex = null;
        Environment.Exit(0);
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

    /// <summary>Provee las carpetas de destino VIGENTES de los canales para la vigilancia MULTI-DISCO de
    /// almacenamiento (coordinador de emergencia + retención). PEREZOSO: resuelve el <see cref="IChannelManager"/>
    /// al invocarse (en cada sondeo, ya con los canales compuestos), NO al registrar los servicios —así no fuerza
    /// construir los canales antes de tiempo—. Devuelve las rutas resueltas (cada canal en su disco).</summary>
    private static Func<IReadOnlyCollection<string>> ChannelDestinations(IServiceProvider sp) => () =>
    {
        var mgr = sp.GetService<IChannelManager>();
        if (mgr is null) return Array.Empty<string>();
        return mgr.Channels
            .Select(c => (c as IConfigurableRecording)?.OutputDirectory)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!)
            .ToList();
    };

    protected override void OnExit(ExitEventArgs e)
    {
        // Backstop para cierres que NO pasaron por el handler de Closing (p. ej. Shutdown() directo de una 2ª
        // instancia o de un fallo de arranque). Con host, apagado ordenado ACOTADO (Wait con tope): NUNCA bloquea
        // indefinidamente el hilo de UI —ese GetResult() sin tope era la causa del proceso «colgado» tras cerrar—;
        // sin host (arranque fallido) retorna al instante. Además libera el mutex de instancia única.
        if (!_shutdownComplete && _host is not null)
            try { ShutdownHostAsync().Wait(ShutdownTimeout); } catch { /* ya registrado dentro / timeout */ }
        try { _instanceMutex?.ReleaseMutex(); } catch { /* no somos el dueño */ }
        try { _instanceMutex?.Dispose(); } catch { /* noop */ }
        base.OnExit(e);
    }
}
