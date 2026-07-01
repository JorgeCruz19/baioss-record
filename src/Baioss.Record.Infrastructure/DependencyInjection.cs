using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Presets;
using Baioss.Record.Application.Storage;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Cqrs;
using Baioss.Record.Infrastructure.Messaging;
using Baioss.Record.Infrastructure.Persistence;
using Baioss.Record.Infrastructure.Presets;
using Baioss.Record.Infrastructure.Storage;
using Baioss.Record.Infrastructure.Time;

namespace Baioss.Record.Infrastructure;

/// <summary>
/// Composición de la infraestructura: persistencia (EF Core SQLite), repositorios, bus de
/// eventos, reloj, captura, monitor de señal y localizador de FFmpeg. Es la única fuente de
/// verdad del cableado de la capa externa; la usan el host de la app, la API y los tests.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registra la infraestructura sobre SQLite. <paramref name="sqliteDbPath"/> es la ruta
    /// del archivo de base de datos; <paramref name="ffmpegDirectoryOrExe"/> ubica el binario
    /// de FFmpeg (carpeta o ruta al ejecutable). Si es <c>null</c>, no se registra el localizador.
    /// </summary>
    public static IServiceCollection AddBaiossInfrastructure(
        this IServiceCollection services, string sqliteDbPath, string? ffmpegDirectoryOrExe = null,
        long faststartMaxBytes = 4L * 1024 * 1024 * 1024)
    {
        // Factory de DbContext: contexto de corta vida por operación, seguro para servicios
        // singleton de larga vida (canales 24/7) y escrituras concurrentes entre canales.
        // «Default Timeout=30»: ante un lock momentáneo (otro canal/el scheduler escribiendo), el comando
        // espera hasta 30 s en vez de fallar al instante con «database is locked». El modo WAL se fija en
        // EnsureBaiossDatabaseCreated (permite lecturas concurrentes mientras se escribe).
        services.AddDbContextFactory<BaiossDbContext>(options =>
            options.UseSqlite($"Data Source={sqliteDbPath};Default Timeout=30"));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IEventBus, InProcessEventBus>();

        // Repositorios (stateless sobre el factory → singletons).
        services.AddSingleton<IChannelRepository, ChannelRepository>();
        services.AddSingleton<IInputSourceRepository, InputSourceRepository>();
        services.AddSingleton<IRecordingProfileRepository, RecordingProfileRepository>();
        services.AddSingleton<IRecordingSessionRepository, RecordingSessionRepository>();
        services.AddSingleton<IScheduledJobRepository, ScheduledJobRepository>();
        services.AddSingleton<IRetentionPolicyRepository, RetentionPolicyRepository>();
        services.AddSingleton<IUserRepository, UserRepository>();
        services.AddSingleton<IEventLogRepository, EventLogRepository>();
        services.AddSingleton<IRepository<Segment>, EfRepository<Segment>>();

        services.AddSingleton<IStorageManager, StorageManager>();

        // Presets de encoding: built-in + personalizados en JSON, junto a la base de datos.
        var presetsPath = Path.Combine(Path.GetDirectoryName(sqliteDbPath) ?? ".", "presets.json");
        services.AddSingleton<IPresetStore>(_ => new JsonPresetStore(presetsPath));

        // Captura y monitoreo de señal: una fábrica por protocolo + resolver que elige por tipo.
        services.AddSingleton<ICaptureSourceFactory, FileCaptureSourceFactory>();
        services.AddSingleton<ICaptureSourceFactory, DecklinkCaptureSourceFactory>();
        services.AddSingleton<ICaptureSourceFactory, DirectShowCaptureSourceFactory>();
        services.AddSingleton<ICaptureSourceFactory, NdiCaptureSourceFactory>();
        services.AddSingleton<CaptureSourceResolver>();
        services.AddTransient<ISignalMonitor, SignalMonitor>();
        services.AddSingleton<IDeviceEnumerator, NoOpDeviceEnumerator>(); // fallback (simulado / sin FFmpeg)

        if (ffmpegDirectoryOrExe is not null)
        {
            services.AddSingleton<IFfmpegLocator>(_ => new FfmpegLocator(ffmpegDirectoryOrExe) { FaststartMaxBytes = faststartMaxBytes });
            // Enumeración real de dispositivos (DeckLink/DirectShow) vía FFmpeg: sustituye al fallback.
            services.AddSingleton<IDeviceEnumerator, FfmpegDeviceEnumerator>();
        }

        return services;
    }

    /// <summary>
    /// Registra el despachador CQRS y descubre automáticamente todos los handlers de comandos y
    /// queries de la capa Application. La API y la UI despachan los mismos casos de uso a través de él.
    /// </summary>
    public static IServiceCollection AddBaiossCqrs(this IServiceCollection services)
    {
        services.AddSingleton<IDispatcher, Dispatcher>();

        var applicationAssembly = typeof(IDispatcher).Assembly;
        foreach (var type in applicationAssembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface) continue;
            foreach (var contract in type.GetInterfaces())
            {
                if (!contract.IsGenericType) continue;
                var definition = contract.GetGenericTypeDefinition();
                if (definition == typeof(ICommandHandler<,>) || definition == typeof(IQueryHandler<,>))
                    services.AddTransient(contract, type);
            }
        }

        return services;
    }

    /// <summary>Crea el esquema de la base de datos si aún no existe (MVP single-box, sin migraciones).</summary>
    public static void EnsureBaiossDatabaseCreated(this IServiceProvider services)
    {
        var factory = services.GetRequiredService<IDbContextFactory<BaiossDbContext>>();
        using var db = factory.CreateDbContext();
        db.Database.EnsureCreated();
        // WAL (Write-Ahead Logging): persistente en el archivo. Permite que el scheduler (lee cada 1 s) y la
        // UI lean mientras un canal escribe segmentos, en vez del bloqueo total del modo rollback por defecto
        // → evita el «database is locked» con varios canales grabando a la vez.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
        // EnsureCreated NO altera una BD ya existente: crea los índices de historial/retención de forma
        // idempotente para que apliquen también a bases creadas antes de añadirlos al modelo. Los nombres
        // coinciden con la convención de EF, así que en una BD NUEVA «IF NOT EXISTS» los salta. (Auditoría #22.)
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_Sessions_ChannelId_StartedAt\" ON \"Sessions\" (\"ChannelId\", \"StartedAt\");");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_Sessions_ChannelId_EndedAt\" ON \"Sessions\" (\"ChannelId\", \"EndedAt\");");
        db.Database.ExecuteSqlRaw("CREATE INDEX IF NOT EXISTS \"IX_Sessions_StartedAt\" ON \"Sessions\" (\"StartedAt\");");
    }
}
