using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Persistence;
using Baioss.Record.Application.Storage;

namespace Baioss.Record.Infrastructure.Storage;

/// <summary>
/// Aplica la RETENCIÓN automática como servicio de fondo: cada <see cref="RetentionOptions.IntervalHours"/>
/// horas borra/archiva las grabaciones más antiguas que la política. Aplica tanto las políticas por canal
/// PERSISTIDAS en el repositorio (reservadas para una futura UI/API) como, si <see cref="RetentionOptions"/>
/// está habilitada, una política GLOBAL de la config a cada canal. Sin nada habilitado/persistido, no toca
/// nada (seguro por defecto).
/// </summary>
public sealed class RetentionService : BackgroundService
{
    private readonly IStorageManager _storage;
    private readonly IChannelRepository _channels;
    private readonly IRetentionPolicyRepository _policies;
    private readonly RetentionOptions _options;
    private readonly ILogger<RetentionService> _log;

    public RetentionService(
        IStorageManager storage, IChannelRepository channels, IRetentionPolicyRepository policies,
        RetentionOptions options, ILogger<RetentionService> log)
    {
        _storage = storage;
        _channels = channels;
        _policies = policies;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Arranque diferido: deja que la BD y los canales se compongan antes de la primera pasada.
        try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        if (_options.Enabled)
            _log.LogInformation("Retención automática activa: conservar {Days} días, acción {Action}, cada {Hours} h.",
                _options.Days, _options.Action, Math.Max(1, _options.IntervalHours));

        var interval = TimeSpan.FromHours(Math.Max(1, _options.IntervalHours));
        while (!ct.IsCancellationRequested)
        {
            try { await SweepAsync(ct).ConfigureAwait(false); }
            catch (Exception ex) { _log.LogError(ex, "Retención: fallo en la pasada."); }

            try { await Task.Delay(interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task SweepAsync(CancellationToken ct)
    {
        // 1) Políticas por canal persistidas (si las hay).
        foreach (var policy in await _policies.ListAsync(ct).ConfigureAwait(false))
            await _storage.ApplyRetentionAsync(policy, ct).ConfigureAwait(false);

        // 2) Política GLOBAL de la config (opt-in), aplicada a cada canal.
        if (_options.Enabled && _options.Days > 0)
            foreach (var ch in await _channels.ListAsync(ct).ConfigureAwait(false))
                await _storage.ApplyRetentionAsync(new RetentionPolicy
                {
                    ChannelId = ch.Id,
                    RetentionDays = _options.Days,
                    Action = _options.Action,
                    ArchivePath = _options.ArchivePath,
                }, ct).ConfigureAwait(false);
    }
}
