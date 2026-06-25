using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Channels;
using Baioss.Record.Application.Persistence;
using Baioss.Record.App.Demo;
using Baioss.Record.App.Preview;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Channels;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App;

/// <summary>Entorno de composición (modo real, rutas, códec demo y nº de canales), calculado al arrancar.</summary>
public sealed record ChannelCompositionContext(
    bool Real, string Root, string? FfmpegDir, string? ClipPath, VideoCodec Codec, int ChannelCount);

/// <summary>
/// Compone y MANTIENE el runtime de cada canal (fuente + grabador + monitor + preview + motor), y
/// permite RECONSTRUIR uno en caliente cuando se le cambia la entrada asignada, sin reiniciar la app.
/// Es el dueño del ciclo de vida de los canales e implementa <see cref="IChannelManager"/> para que
/// UI y API vean siempre el motor vigente del canal.
/// </summary>
public sealed class ChannelHost : IChannelManager, IAsyncDisposable, IDisposable
{
    /// <summary>Keys de los canales (A, B, C, …) según el nº configurado; nombran y siembran cada canal.</summary>
    private readonly string[] _channelKeys;

    private readonly IServiceProvider _sp;
    private readonly PreviewCatalog _previews;
    private readonly ChannelCompositionContext _ctx;
    private readonly Dictionary<Guid, IChannelEngine> _engines = new();
    private readonly Dictionary<Guid, string> _keys = new();
    // Entrada vigente de cada canal: para impedir que dos canales reciban el MISMO dispositivo de captura
    // exclusivo (cámara DirectShow / tarjeta DeckLink), que haría fallar al segundo al abrir.
    private readonly Dictionary<Guid, InputSource> _sources = new();

    /// <summary>Se eleva (channelId) tras reconstruir un canal; la UI reemplaza su ViewModel.</summary>
    public event Action<Guid>? ChannelRebound;

    public ChannelHost(IServiceProvider sp, PreviewCatalog previews, ChannelCompositionContext ctx)
    {
        _sp = sp;
        _previews = previews;
        _ctx = ctx;
        // A, B, C, … hasta el nº de canales configurado (ya acotado en el arranque): 'A' + índice.
        _channelKeys = Enumerable.Range(0, ctx.ChannelCount).Select(i => ((char)('A' + i)).ToString()).ToArray();
        Initialize();
    }

    /// <summary>True si los canales corren en modo real (hay FFmpeg): la reasignación de entradas aplica.</summary>
    public bool CanRebind => _ctx.Real && _engines.Values.All(e => e is StandaloneChannelEngine);

    /// <summary>Ruta del clip de prueba, para ofrecer "volver al archivo demo" como entrada.</summary>
    public string? DemoClipPath => _ctx.ClipPath;

    public IReadOnlyCollection<IChannelEngine> Channels => _engines.Values;

    public IChannelEngine Get(Guid channelId) =>
        _engines.TryGetValue(channelId, out var e) ? e : throw new KeyNotFoundException($"Canal {channelId} no registrado.");

    public bool TryGet(Guid channelId, out IChannelEngine? engine) => _engines.TryGetValue(channelId, out engine);

    private void Initialize()
    {
        if (!_ctx.Real || _ctx.FfmpegDir is null || _ctx.ClipPath is null) { BuildSimulated(); return; }
        try
        {
            _sp.EnsureBaiossDatabaseCreated();
            foreach (var key in _channelKeys)
            {
                var (channelId, def, profile) = SeedAndResolve(key);
                var (engine, preview) = BuildRuntime(key, channelId, def, profile);
                _engines[channelId] = engine;
                _keys[channelId] = key;
                _previews.Add(channelId, preview);
            }
        }
        catch (Exception ex)
        {
            // Cualquier fallo de composición (esquema, dispositivo) cae a simulado, pero queda registrado.
            Serilog.Log.Error(ex, "No se pudieron construir los canales reales; se usa modo simulado.");
            _engines.Clear();
            _keys.Clear();
            BuildSimulated();
        }
    }

    private void BuildSimulated()
    {
        foreach (var key in _channelKeys)
        {
            var sim = new SimulatedChannelEngine(key);
            _engines[sim.ChannelId] = sim;
            _keys[sim.ChannelId] = key;
        }
    }

    /// <summary>
    /// Reasigna la entrada de un canal EN CALIENTE: persiste la fuente y el vínculo, derriba el runtime
    /// viejo (libera el dispositivo, que en vivo es exclusivo) y construye el nuevo con la entrada elegida.
    /// </summary>
    public async Task RebindAsync(Guid channelId, InputSource newDef, CancellationToken ct = default)
    {
        if (!CanRebind) throw new InvalidOperationException("La reasignación de entrada no está disponible (modo simulado).");
        if (!_keys.TryGetValue(channelId, out var key)) throw new KeyNotFoundException($"Canal {channelId} no registrado.");
        if (_engines.TryGetValue(channelId, out var current) &&
            current.Status.RecordingState is RecordingState.Recording or RecordingState.Paused)
            throw new InvalidOperationException("Detén la grabación antes de cambiar la entrada del canal.");

        // Exclusividad: una cámara DirectShow o una tarjeta DeckLink no admiten dos canales a la vez. Si otro
        // canal ya usa ese dispositivo, no reasignar (el segundo fallaría a abrir y se quedaría sin grabar).
        foreach (var (otherId, otherDef) in _sources)
            if (otherId != channelId && DeviceExclusivity.Conflicts(newDef, otherDef))
                throw new InvalidOperationException(
                    $"«{newDef.Name}» ya está asignada al Canal {_keys.GetValueOrDefault(otherId, "?")}. " +
                    "Un dispositivo de captura no admite dos canales a la vez.");

        var sources = _sp.GetRequiredService<IInputSourceRepository>();
        var channels = _sp.GetRequiredService<IChannelRepository>();
        var profiles = _sp.GetRequiredService<IRecordingProfileRepository>();

        // 1) Persiste la fuente y el vínculo Channel→InputSource (sobrevive a reinicios).
        if (await sources.GetAsync(newDef.Id, ct).ConfigureAwait(false) is null)
            await sources.AddAsync(newDef, ct).ConfigureAwait(false);
        else
            await sources.UpdateAsync(newDef, ct).ConfigureAwait(false);

        var channel = await channels.GetAsync(channelId, ct).ConfigureAwait(false);
        if (channel is not null)
        {
            channel.InputSourceId = newDef.Id;
            await channels.UpdateAsync(channel, ct).ConfigureAwait(false);
        }

        // 2) Derriba el runtime viejo ANTES de abrir el nuevo (un dispositivo en vivo no admite dos dueños).
        //    Disponer el motor del canal ya detiene su captura unificada (preview+grabación); el catálogo
        //    solo mantiene una referencia no-propietaria, así que basta quitarla.
        if (_engines.Remove(channelId, out var old)) await old.DisposeAsync().ConfigureAwait(false);
        _previews.Remove(channelId);

        // 3) Construye el runtime nuevo con la entrada asignada y el perfil vigente del canal.
        var profile = (channel?.ProfileId is { } pid ? await profiles.GetAsync(pid, ct).ConfigureAwait(false) : null)
                      ?? DemoProfile(key);
        var (engine, preview) = BuildRuntime(key, channelId, newDef, profile);
        _engines[channelId] = engine;
        _previews.Add(channelId, preview);

        // 4) La UI reconstruye el ViewModel del canal (re-enlaza el preview).
        ChannelRebound?.Invoke(channelId);
    }

    private (Guid ChannelId, InputSource Def, RecordingProfile Profile) SeedAndResolve(string key)
    {
        var channels = _sp.GetRequiredService<IChannelRepository>();
        var sources = _sp.GetRequiredService<IInputSourceRepository>();
        var profiles = _sp.GetRequiredService<IRecordingProfileRepository>();

        var clip = DemoClip(key);
        var profile = DemoProfile(key);
        var channelId = StableGuid($"channel:{key}");
        var channel = new Channel { Id = channelId, Key = key, Name = $"Canal {key}", InputSourceId = clip.Id, ProfileId = profile.Id };

        SeedIfAbsent(sources, clip.Id, clip);
        SeedIfAbsent(profiles, profile.Id, profile);
        SeedIfAbsent(channels, channelId, channel);

        // Estado vigente persistido: la entrada y el perfil pudieron cambiarse en sesiones previas.
        var persisted = channels.GetAsync(channelId).GetAwaiter().GetResult() ?? channel;
        var def = (persisted.InputSourceId is { } sid ? sources.GetAsync(sid).GetAwaiter().GetResult() : null) ?? clip;
        var activeProfile = (persisted.ProfileId is { } pid ? profiles.GetAsync(pid).GetAwaiter().GetResult() : null) ?? profile;
        return (channelId, def, activeProfile);
    }

    private (IChannelEngine Engine, IChannelPreviewSource Preview) BuildRuntime(
        string key, Guid channelId, InputSource def, RecordingProfile profile)
    {
        var locator = _sp.GetRequiredService<IFfmpegLocator>();
        var bus = _sp.GetRequiredService<IEventBus>();
        var loggers = _sp.GetRequiredService<ILoggerFactory>();
        var sessions = _sp.GetRequiredService<IRecordingSessionRepository>();
        var segments = _sp.GetRequiredService<IRepository<Segment>>();
        var profilesRepo = _sp.GetRequiredService<IRecordingProfileRepository>();
        var resolver = _sp.GetRequiredService<CaptureSourceResolver>();

        var source = resolver.Create(def);
        source.OpenAsync().GetAwaiter().GetResult();

        // Motor de captura UNIFICADO: un solo proceso abre la fuente y da preview + grabación a la vez.
        var capture = new FfmpegChannelEngine(locator, loggers.CreateLogger<FfmpegChannelEngine>())
        {
            OutputRoot = Path.Combine(_ctx.Root, "recordings"),
        };
        capture.StartPreviewAsync(source, profile, key).GetAwaiter().GetResult(); // preview siempre activo

        var monitor = new SignalMonitor(bus, loggers.CreateLogger<SignalMonitor>()) { ChannelId = channelId };
        var diskGuard = new Baioss.Record.Infrastructure.Storage.DiskSpaceGuard(loggers.CreateLogger<ChannelHost>());

        var engine = new StandaloneChannelEngine(
            key, source, profile, capture, channelId,
            sessions, segments, bus, monitor, loggers.CreateLogger<StandaloneChannelEngine>(), profilesRepo, diskGuard);
        _sources[channelId] = def; // entrada vigente del canal (para el chequeo de exclusividad al reasignar)
        return (engine, capture);
    }

    private InputSource DemoClip(string key) => new()
    {
        Id = StableGuid($"source:{key}"),
        Name = $"Clip {key}", Type = InputType.File, Uri = _ctx.ClipPath!,
        Parameters = { ["loop"] = "1", ["realtime"] = "1" },
        ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
    };

    private RecordingProfile DemoProfile(string key) => new()
    {
        Id = StableGuid($"profile:{key}"),
        Name = "MP4 (demo)", VideoCodec = _ctx.Codec, HwAccel = HwAccel.None,
        VideoBitrate = Bitrate.FromMbps(8), GopSize = 50,
        AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
    };

    private static void SeedIfAbsent<T>(IRepository<T> repo, Guid id, T entity) where T : class
    {
        if (repo.GetAsync(id).GetAwaiter().GetResult() is null)
            repo.AddAsync(entity).GetAwaiter().GetResult();
    }

    private static Guid StableGuid(string seed) => new(MD5.HashData(Encoding.UTF8.GetBytes(seed)));

    public async ValueTask DisposeAsync()
    {
        foreach (var e in _engines.Values) await e.DisposeAsync().ConfigureAwait(false);
        _engines.Clear();
    }

    /// <summary>El contenedor DI dispone los singletons de forma síncrona al cerrar; delega en el async.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
