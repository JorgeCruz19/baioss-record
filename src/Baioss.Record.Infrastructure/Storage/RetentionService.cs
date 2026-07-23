using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// Aplica la RETENCIÓN automática como servicio de fondo: borra/archiva las grabaciones más antiguas que la
/// política. Lee los ajustes VIGENTES de <see cref="IStorageSettingsStore"/> en CADA pasada (frecuencia, días,
/// espacio, acción), así que un cambio desde la UI/API surte efecto sin reiniciar (Fase 4c). Aplica tanto las
/// políticas por canal PERSISTIDAS en el repositorio (reservadas para una futura UI/API) como, si la retención
/// está habilitada, una política GLOBAL a cada canal. Sin nada habilitado/persistido, no toca nada (seguro).
/// </summary>
public sealed class RetentionService : BackgroundService
{
    private readonly IStorageManager _storage;
    private readonly IChannelRepository _channels;
    private readonly IRetentionPolicyRepository _policies;
    private readonly IStorageSettingsStore _settings;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(
        IStorageManager storage, IChannelRepository channels, IRetentionPolicyRepository policies,
        IStorageSettingsStore settings, ILogger<RetentionService> log)
    {
        _storage = storage;
        _channels = channels;
        _policies = policies;
        _settings = settings;
        _log = log;
    }

    /// <summary>Carpeta raíz de grabaciones por DEFECTO (…/recordings): el disco sobre el que se aplica la
    /// retención por ESPACIO si no hay destinos de canal conocidos. La fija el cableado de DI. (Fase 2.)</summary>
    public string RecordingsRoot { get; init; } = "";

    /// <summary>Provee las carpetas de destino VIGENTES de los canales (MULTI-DISCO): la retención por espacio se
    /// aplica en CADA disco de destino, no solo en el por defecto. null o vacío ⇒ solo <see cref="RecordingsRoot"/>.</summary>
    public Func<IReadOnlyCollection<string>>? RecordingRootsProvider { get; init; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Arranque diferido: deja que la BD y los canales se compongan antes de la primera pasada.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        _log.LogInformation("Servicio de retención activo (lee los ajustes en vivo; opt-in por «RetentionEnabled»).");
        while (!ct.IsCancellationRequested)
        {
            try { await SweepAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "Retención: fallo en la pasada."); }

            // Frecuencia leída EN VIVO cada iteración (un cambio se aplica en la siguiente pasada).
            var s = _settings.Current;
            var interval = s.IntervalMinutes > 0
                ? TimeSpan.FromMinutes(Math.Max(5, s.IntervalMinutes))
                : TimeSpan.FromHours(6);
            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        // 1) Políticas por canal persistidas (si las hay). Independientes del opt-in global.
        foreach (var policy in await _policies.ListAsync(ct).ConfigureAwait(false))
            await _storage.ApplyRetentionAsync(policy, ct).ConfigureAwait(false);

        var s = _settings.Current; // ajustes VIGENTES (Fase 4c)
        if (!s.RetentionEnabled) return; // opt-in: sin habilitar, no se borra nada global

        // 2) Política GLOBAL por DÍAS (opt-in), aplicada a cada canal.
        if (s.RetentionDays > 0)
            foreach (var ch in await _channels.ListAsync(ct).ConfigureAwait(false))
                await _storage.ApplyRetentionAsync(new RetentionPolicy
                {
                    ChannelId = ch.Id,
                    RetentionDays = s.RetentionDays,
                    Action = s.Action,
                    ArchivePath = s.ArchivePath,
                }, ct).ConfigureAwait(false);

        // 3) Retención por ESPACIO (Fase 2 + MULTI-DISCO): mantiene un mínimo de espacio libre en CADA disco de
        //    destino borrando las grabaciones NO protegidas más antiguas DE ESE DISCO (EnforceFreeSpaceAsync acota
        //    el borrado al volumen). Off si no hay objetivo. Un canal por disco o todos al mismo: se deduplica por
        //    volumen para no medir/limpiar el mismo disco dos veces.
        if (s.MinFreeGB > 0 || s.MinFreePercent > 0)
            foreach (var folder in DistinctVolumeFolders(WatchRoots()))
                await _storage.EnforceFreeSpaceAsync(folder, s.MinFreeGB, s.MinFreePercent,
                    s.Action, s.ArchivePath, ct).ConfigureAwait(false);
    }

    /// <summary>Carpetas de destino a limpiar: las de los canales (cada una en su disco) o, si no hay, la de por defecto.</summary>
    private IEnumerable<string> WatchRoots()
    {
        var fromChannels = RecordingRootsProvider?.Invoke();
        IEnumerable<string> roots = fromChannels is { Count: > 0 } ? fromChannels : new[] { RecordingsRoot };
        return roots.Where(r => !string.IsNullOrWhiteSpace(r));
    }

    /// <summary>Una carpeta por VOLUMEN distinto (para no aplicar la retención por espacio dos veces al mismo disco
    /// cuando varios canales comparten disco). La limpieza de EnforceFreeSpaceAsync ya abarca todo el volumen.</summary>
    private static IEnumerable<string> DistinctVolumeFolders(IEnumerable<string> roots)
        => roots.GroupBy(VolumeKey, StringComparer.OrdinalIgnoreCase).Select(g => g.First());

    private static string VolumeKey(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? path; }
        catch { return path; }
    }
}
