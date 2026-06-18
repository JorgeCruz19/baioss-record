using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Cqrs;
using Baioss.Record.Infrastructure.Messaging;
using Baioss.Record.Infrastructure.Persistence;
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
        this IServiceCollection services, string sqliteDbPath, string? ffmpegDirectoryOrExe = null)
    {
        // Factory de DbContext: contexto de corta vida por operación, seguro para servicios
        // singleton de larga vida (canales 24/7) y escrituras concurrentes entre canales.
        services.AddDbContextFactory<BaiossDbContext>(options =>
            options.UseSqlite($"Data Source={sqliteDbPath}"));

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

        // Captura y monitoreo de señal.
        services.AddSingleton<ICaptureSourceFactory, FileCaptureSourceFactory>();
        services.AddTransient<ISignalMonitor, SignalMonitor>();

        if (ffmpegDirectoryOrExe is not null)
            services.AddSingleton<IFfmpegLocator>(_ => new FfmpegLocator(ffmpegDirectoryOrExe));

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
    }
}
