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
    bool Real, string Root, string? FfmpegDir, string? ClipPath, VideoCodec Codec, int ChannelCount, bool FragmentedMp4 = true);

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

    // Registro compartido del ritmo de escritura por volumen: la guarda de disco de cada canal vigila el
    // caudal AGREGADO de todos los canales que escriben en el mismo disco. (Auditoría 24/7, A7/#10.)
    private readonly Baioss.Record.Infrastructure.Storage.DiskUsageRegistry _diskUsage = new();

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

        // La base de datos es un prerequisito GLOBAL: si no se puede preparar, no hay persistencia → todo simulado.
        try { _sp.EnsureBaiossDatabaseCreated(); }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "No se pudo preparar la base de datos; se usan canales simulados.");
            BuildSimulated();
            return;
        }

        // Cada canal se compone de forma INDEPENDIENTE (principio de diseño: la caída de uno nunca afecta a
        // los demás) y EN PARALELO. Antes se construían EN SERIE con I/O bloqueante (.GetAwaiter().GetResult()):
        // con N fuentes NDI sin señal, sus timeouts de apertura de 8 s se ENCADENABAN (~32 s con 4 canales)
        // congelando el arranque. Ahora se construyen a la vez, cada uno con un TIMEOUT propio, y los resultados
        // se MEZCLAN en este hilo (sin escrituras concurrentes en los diccionarios). La construcción corre fuera
        // del hilo de UI porque App la pre-resuelve en un hilo de fondo antes de StartAsync. Si un canal falla o
        // se cuelga —p. ej. su fuente sin señal o un tipo sin fábrica— cae a simulado ÉL SOLO. (Auditoría 24/7, #17.)
        var built = Task.WhenAll(_channelKeys.Select(BuildChannelGuardedAsync)).GetAwaiter().GetResult();
        foreach (var b in built)
        {
            _engines[b.ChannelId] = b.Engine;
            _keys[b.ChannelId] = b.Key;
            if (b.Preview is not null) _previews.Add(b.ChannelId, b.Preview);
            if (b.Def is not null) _sources[b.ChannelId] = b.Def; // entrada vigente (para el chequeo de exclusividad al reasignar)
        }
    }

    /// <summary>Resultado de construir un canal (real o, si falló/expiró, su sustituto simulado).</summary>
    private readonly record struct ChannelBuild(
        string Key, Guid ChannelId, IChannelEngine Engine, IChannelPreviewSource? Preview, InputSource? Def);

    /// <summary>Margen máximo para construir UN canal (abrir la fuente + arrancar el preview). Holgado: una NDI
    /// sin señal tarda ~8 s en su propio timeout; este solo ataja una fuente realmente colgada. Al expirar, el
    /// canal cae a simulado en vez de bloquear el arranque indefinidamente. (Auditoría #17.)</summary>
    private static readonly TimeSpan ChannelBuildTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Construye un canal con timeout y captura de fallos: NUNCA lanza (la caída de uno no afecta al resto).</summary>
    private async Task<ChannelBuild> BuildChannelGuardedAsync(string key)
    {
        using var cts = new CancellationTokenSource(ChannelBuildTimeout);
        try
        {
            return await BuildChannelAsync(key, cts.Token).WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Serilog.Log.Error("Canal {Key}: la construcción superó {T:0}s (fuente colgada o sin respuesta al abrir); queda en simulado.", key, ChannelBuildTimeout.TotalSeconds);
            return SimulatedBuild(key);
        }
        catch (NotSupportedException ex)
        {
            // Tipo de entrada declarado en el dominio pero SIN fábrica de captura (SRT/RTMP/UDP/RTP/MpegTs/
            // MediaFoundation): NO es «sin señal». Se avisa con la causa real para que el operador sepa que ese
            // canal NUNCA grabará su fuente (queda en simulado para que la app siga abriendo). (Auditoría A11/#15.)
            Serilog.Log.Error(ex, "Canal {Key}: tipo de entrada NO SOPORTADO; el canal queda en simulado y NO grabará la fuente real. Causa: {Causa}", key, ex.Message);
            return SimulatedBuild(key);
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Canal {Key}: no se pudo construir en modo real (¿fuente sin señal?); queda en simulado.", key);
            return SimulatedBuild(key);
        }
    }

    private async Task<ChannelBuild> BuildChannelAsync(string key, CancellationToken ct)
    {
        var (channelId, def, profile) = await SeedAndResolveAsync(key, ct).ConfigureAwait(false);
        var (engine, preview) = await BuildRuntimeAsync(key, channelId, def, profile, ct).ConfigureAwait(false);
        return new ChannelBuild(key, channelId, engine, preview, def);
    }

    private static ChannelBuild SimulatedBuild(string key)
    {
        var sim = new SimulatedChannelEngine(key);
        return new ChannelBuild(key, sim.ChannelId, sim, Preview: null, Def: null);
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

    /// <summary>Margen máximo para reasignar una entrada en caliente (derribar el runtime viejo + abrir el
    /// nuevo). Si se supera —una fuente/dispositivo que no responde al abrir, o una BD bloqueada bajo carga—,
    /// la operación se ABORTA y el canal se RESTAURA a su entrada anterior, en vez de dejar el «Aplicando…»
    /// colgado para siempre. Holgado para cubrir una NDI (≈8 s de apertura) y discos cargados con varios
    /// canales grabando.</summary>
    private static readonly TimeSpan RebindTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Reasigna la entrada de un canal EN CALIENTE: derriba el runtime viejo (libera el dispositivo, que en
    /// vivo es exclusivo), construye el nuevo con la entrada elegida y —SOLO si tuvo éxito— persiste la
    /// fuente y el vínculo. ROBUSTO: toda la operación está ACOTADA por <see cref="RebindTimeout"/>; si falla
    /// o expira (fuente sin respuesta, dispositivo colgado, BD bloqueada), el canal se RESTAURA a un motor
    /// funcional (su entrada anterior o, en última instancia, simulado) para que la UI nunca quede con
    /// «Aplicando…» colgado ni con un preview muerto, y se lanza una excepción clara para el operador.
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

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(RebindTimeout);
        try
        {
            await RebindCoreAsync(channelId, key, newDef, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Expiró NUESTRO timeout (no una cancelación del llamador): la fuente/dispositivo/BD no respondió.
            Serilog.Log.Error("Canal {Key}: la entrada «{Input}» no respondió en {T:0}s; restaurando la entrada anterior.",
                key, newDef.Name, RebindTimeout.TotalSeconds);
            await RestoreChannelAsync(channelId, key).ConfigureAwait(false);
            throw new TimeoutException(
                $"La entrada «{newDef.Name}» no respondió en {RebindTimeout.TotalSeconds:0} s; se restauró la entrada anterior del Canal {key}.");
        }
        catch (Exception ex)
        {
            // Fallo real (p. ej. el dispositivo no abre): restaura y propaga la causa (ya revertido).
            Serilog.Log.Error(ex, "Canal {Key}: fallo al aplicar la entrada «{Input}»; restaurando la entrada anterior.", key, newDef.Name);
            await RestoreChannelAsync(channelId, key).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Cuerpo de la reasignación (acotado por <paramref name="ct"/>). Derriba el runtime viejo,
    /// construye el nuevo, lo intercambia y —al final, solo en éxito— persiste. Cualquier fallo lo maneja
    /// <see cref="RebindAsync"/> restaurando el canal.</summary>
    private async Task RebindCoreAsync(Guid channelId, string key, InputSource newDef, CancellationToken ct)
    {
        var channels = _sp.GetRequiredService<IChannelRepository>();
        var profiles = _sp.GetRequiredService<IRecordingProfileRepository>();

        // 1) Derriba el runtime viejo ANTES de abrir el nuevo (un dispositivo en vivo no admite dos dueños).
        //    Acotado por ct: un FFmpeg de solo-preview sale casi al instante con «q»; si aun así no cerrara,
        //    no bloqueamos indefinidamente (el cierre sigue en segundo plano y acaba liberando el dispositivo).
        if (_engines.Remove(channelId, out var old))
            await old.DisposeAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
        _previews.Remove(channelId);

        // 2) Construye el runtime nuevo con la entrada asignada y el perfil vigente del canal.
        var channel = await channels.GetAsync(channelId, ct).ConfigureAwait(false);
        var profile = (channel?.ProfileId is { } pid ? await profiles.GetAsync(pid, ct).ConfigureAwait(false) : null)
                      ?? DemoProfile(key);
        var (engine, preview) = await BuildRuntimeAsync(key, channelId, newDef, profile, ct).ConfigureAwait(false);

        // 3) Compromiso: intercambia el runtime y notifica a la UI (re-enlaza el preview). A partir de aquí el
        //    canal ya opera con la entrada nueva.
        _engines[channelId] = engine;
        _previews.Add(channelId, preview);
        _sources[channelId] = newDef; // entrada vigente del canal (para el chequeo de exclusividad al reasignar)
        ChannelRebound?.Invoke(channelId);

        // 4) Persiste la fuente y el vínculo Channel→InputSource (sobrevive a reinicios) SOLO ahora: si el
        //    cambio hubiera fallado antes, el estado persistido sigue apuntando a la entrada ANTERIOR y la
        //    restauración la reconstruye (rollback limpio). Best-effort: un fallo aquí no tumba el canal ya
        //    conmutado (solo se perdería la persistencia → revertiría al reiniciar).
        try
        {
            var sources = _sp.GetRequiredService<IInputSourceRepository>();
            if (await sources.GetAsync(newDef.Id, ct).ConfigureAwait(false) is null)
                await sources.AddAsync(newDef, ct).ConfigureAwait(false);
            else
                await sources.UpdateAsync(newDef, ct).ConfigureAwait(false);
            if (channel is not null)
            {
                channel.InputSourceId = newDef.Id;
                await channels.UpdateAsync(channel, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Canal {Key}: la entrada «{Input}» se aplicó pero NO se pudo persistir (revertirá al reiniciar).", key, newDef.Name);
        }
    }

    /// <summary>
    /// Restaura un canal a un motor FUNCIONAL tras un fallo/timeout de reasignación: lo reconstruye desde el
    /// estado PERSISTIDO (que, al persistir la nueva entrada solo en éxito, apunta a la entrada ANTERIOR) con
    /// la misma construcción guardada del arranque —acotada y con caída a simulado—, así que NUNCA lanza y
    /// SIEMPRE deja el canal registrado + notifica a la UI para que reemplace el preview muerto.
    /// </summary>
    private async Task RestoreChannelAsync(Guid channelId, string key)
    {
        try
        {
            var b = await BuildChannelGuardedAsync(key).ConfigureAwait(false);
            // Si un intento previo dejó otro motor a medio registrar, dispón el saliente antes de sustituir.
            if (_engines.Remove(channelId, out var stale) && !ReferenceEquals(stale, b.Engine))
                try { await stale.DisposeAsync().ConfigureAwait(false); } catch { /* best-effort */ }
            _engines[channelId] = b.Engine;
            _keys[b.ChannelId] = b.Key;
            _previews.Remove(channelId);
            if (b.Preview is not null) _previews.Add(channelId, b.Preview);
            if (b.Def is not null) _sources[channelId] = b.Def;
            ChannelRebound?.Invoke(channelId); // la UI re-enlaza al motor restaurado (nunca a un preview muerto)
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Canal {Key}: no se pudo restaurar tras un fallo de reasignación.", key);
        }
    }

    private async Task<(Guid ChannelId, InputSource Def, RecordingProfile Profile)> SeedAndResolveAsync(string key, CancellationToken ct)
    {
        // Repos singleton STATELESS sobre el factory de DbContext (contexto de corta vida por operación):
        // seguros de usar EN PARALELO desde la construcción de varios canales. Además cada key siembra SU
        // propio clip/perfil/canal (Guid estable por key), así que no hay carrera de seed entre canales.
        var channels = _sp.GetRequiredService<IChannelRepository>();
        var sources = _sp.GetRequiredService<IInputSourceRepository>();
        var profiles = _sp.GetRequiredService<IRecordingProfileRepository>();

        var clip = DemoClip(key);
        var profile = DemoProfile(key);
        var channelId = StableGuid($"channel:{key}");
        var channel = new Channel { Id = channelId, Key = key, Name = $"Canal {key}", InputSourceId = clip.Id, ProfileId = profile.Id };

        await SeedIfAbsentAsync(sources, clip.Id, clip, ct).ConfigureAwait(false);
        await SeedIfAbsentAsync(profiles, profile.Id, profile, ct).ConfigureAwait(false);
        await SeedIfAbsentAsync(channels, channelId, channel, ct).ConfigureAwait(false);

        // Estado vigente persistido: la entrada y el perfil pudieron cambiarse en sesiones previas.
        var persisted = await channels.GetAsync(channelId, ct).ConfigureAwait(false) ?? channel;
        var def = (persisted.InputSourceId is { } sid ? await sources.GetAsync(sid, ct).ConfigureAwait(false) : null) ?? clip;
        var activeProfile = (persisted.ProfileId is { } pid ? await profiles.GetAsync(pid, ct).ConfigureAwait(false) : null) ?? profile;
        return (channelId, def, activeProfile);
    }

    private async Task<(IChannelEngine Engine, IChannelPreviewSource Preview)> BuildRuntimeAsync(
        string key, Guid channelId, InputSource def, RecordingProfile profile, CancellationToken ct = default)
    {
        var locator = _sp.GetRequiredService<IFfmpegLocator>();
        var bus = _sp.GetRequiredService<IEventBus>();
        var loggers = _sp.GetRequiredService<ILoggerFactory>();
        var sessions = _sp.GetRequiredService<IRecordingSessionRepository>();
        var segments = _sp.GetRequiredService<IRepository<Segment>>();
        var profilesRepo = _sp.GetRequiredService<IRecordingProfileRepository>();
        var resolver = _sp.GetRequiredService<CaptureSourceResolver>();

        var source = resolver.Create(def);
        await source.OpenAsync(ct).ConfigureAwait(false);

        // Motor de captura UNIFICADO: un solo proceso abre la fuente y da preview + grabación a la vez.
        var capture = new FfmpegChannelEngine(locator, loggers.CreateLogger<FfmpegChannelEngine>())
        {
            OutputRoot = Path.Combine(_ctx.Root, "recordings"),
            FragmentedMp4 = _ctx.FragmentedMp4, // fMP4 robusto vs MP4 estándar (moov, sin remux) según config
        };
        await capture.StartPreviewAsync(source, profile, key, ct).ConfigureAwait(false); // preview siempre activo

        var monitor = new SignalMonitor(bus, loggers.CreateLogger<SignalMonitor>()) { ChannelId = channelId };
        var diskGuard = new Baioss.Record.Infrastructure.Storage.DiskSpaceGuard(loggers.CreateLogger<ChannelHost>());

        var engine = new StandaloneChannelEngine(
            key, source, profile, capture, channelId,
            sessions, segments, bus, monitor, loggers.CreateLogger<StandaloneChannelEngine>(), profilesRepo, diskGuard, _diskUsage);
        // NOTA: la entrada vigente (_sources[channelId]) la fija el LLAMADOR (Initialize al mezclar, o RebindAsync),
        // no aquí, porque la construcción inicial corre en paralelo y _sources no es un diccionario concurrente.
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

    private static async Task SeedIfAbsentAsync<T>(IRepository<T> repo, Guid id, T entity, CancellationToken ct) where T : class
    {
        if (await repo.GetAsync(id, ct).ConfigureAwait(false) is null)
            await repo.AddAsync(entity, ct).ConfigureAwait(false);
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
