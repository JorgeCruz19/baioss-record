namespace Baioss.Record.Engine.FFmpeg;

/// <summary>Qué debe hacer el watchdog en este tick.</summary>
public enum StallVerdict
{
    /// <summary>Todo en orden: nada que hacer.</summary>
    Healthy,
    /// <summary>Hay estancamiento y el DISCO de destino acaba de dejar de responder: avisar y esperar (no matar).</summary>
    VolumeStalled,
    /// <summary>El disco sigue sin responder: seguir esperando en silencio.</summary>
    StillStalled,
    /// <summary>El disco volvió a responder: retirar el aviso y dar a FFmpeg una ventana nueva antes de juzgarlo.</summary>
    VolumeResumed,
    /// <summary>Estancamiento con el disco operativo (o sin sonda): el colgado es FFmpeg → matar y recuperar.</summary>
    KillProcess,
}

/// <summary>
/// Árbitro del watchdog: separa «FFmpeg no escribe» de «el disco no acepta escrituras», que desde fuera se ven
/// igual (el archivo no crece, el progreso se para) pero exigen reacciones OPUESTAS. Matar a un FFmpeg cuyo
/// disco está colgado no arregla el disco y, con MP4 estándar, cuesta el archivo entero (queda sin índice);
/// el 6/9/2026 costó 22 minutos. Es una máquina de estados pura (sin proceso, sin reloj) para poder probarla.
///
/// Regla: mientras el disco esté marcado como colgado, en cada tick se le vuelve a preguntar AL DISCO —no al
/// estancamiento— si ya responde. Juzgar la vuelta por «ya no hay estancamiento» sería un error: el que espera
/// resetea los relojes de estancamiento, así que el tick siguiente parecería sano aunque el disco siguiera muerto.
/// </summary>
public sealed class StallArbiter
{
    private readonly Func<Task<bool>>? _volumeResponsive;

    /// <param name="volumeResponsive">Sonda del disco de destino (<c>true</c> = acepta escrituras). <c>null</c> = sin
    /// sonda (p. ej. solo preview): todo estancamiento se atribuye a FFmpeg, como antes.</param>
    public StallArbiter(Func<Task<bool>>? volumeResponsive) => _volumeResponsive = volumeResponsive;

    /// <summary>Verdadero mientras el disco de destino esté marcado como colgado.</summary>
    public bool VolumeStalled { get; private set; }

    public async Task<StallVerdict> DecideAsync(bool progressStalled, bool fileStalled)
    {
        if (VolumeStalled)
        {
            if (!await ProbeAsync().ConfigureAwait(false)) return StallVerdict.StillStalled;
            VolumeStalled = false;
            return StallVerdict.VolumeResumed;
        }

        if (!progressStalled && !fileStalled) return StallVerdict.Healthy;

        if (_volumeResponsive is not null && !await ProbeAsync().ConfigureAwait(false))
        {
            VolumeStalled = true;
            return StallVerdict.VolumeStalled;
        }
        return StallVerdict.KillProcess;
    }

    /// <summary>Una sonda que falla o lanza cuenta como «no responde»: ante la duda, no se mata.</summary>
    private async Task<bool> ProbeAsync()
    {
        if (_volumeResponsive is null) return true;
        try { return await _volumeResponsive().ConfigureAwait(false); }
        catch { return false; }
    }
}
