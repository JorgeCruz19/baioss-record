using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Fuente de captura NDI nativa (SDK NewTek). A diferencia de DeckLink/DirectShow —que FFmpeg ABRE
/// directamente—, NDI se recibe en la app con un <see cref="NdiReceiver"/> que SIRVE vídeo + audio por dos
/// sockets TCP loopback; FFmpeg los lee como dos entradas crudas (rawvideo uyvy422/bgra + f32le). Por eso el
/// audio queda en la entrada 1 (<see cref="AudioInputIndex"/>). La resolución/tasa/pixel reales se conocen al
/// abrir (el receptor espera el primer frame). Requiere el runtime NDI instalado; si no, el canal queda sin señal.
/// </summary>
public sealed class NdiCaptureSource : ICaptureSource
{
    private readonly ILogger _log;
    private NdiReceiver? _receiver;

    public NdiCaptureSource(InputSource definition, ILogger log)
    {
        Definition = definition;
        _log = log;
    }

    public InputSource Definition { get; }
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;

    /// <summary>NDI sirve el audio en una entrada FFmpeg aparte (la 1); el vídeo va en la 0.</summary>
    public int AudioInputIndex => 1;

    /// <summary>NDI reporta pérdida y recuperación de señal por sí mismo (el receptor detecta la presencia de
    /// vídeo y lo publica en <see cref="SignalChanged"/>): el motor NO debe sondear el dispositivo para NDI
    /// (sondearlo abriría un ffmpeg contra los sockets del propio receptor → competencia + falsos positivos). (#39/#59.)</summary>
    public bool SelfReportsRecovery => true;

    /// <summary>Formato de píxel negociado con la fuente (uyvy422 normalmente; bgra si lleva alfa); null si no abierta.</summary>
    public string? VideoPixelFormat => _receiver?.VideoPixelFormat;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        var name = Definition.Uri ?? Definition.Name
            ?? throw new InvalidOperationException("Falta el nombre de la fuente NDI.");

        // Reintento de apertura / reconexión: dispón el receptor ANTERIOR antes de crear otro. Sin esto, cada
        // reintento (p. ej. el bucle de espera de señal que reabre la fuente cada 5 s) fugaría 2 listeners TCP
        // + la instancia NDI del receptor previo → en 24/7 agotaría handles/puertos del SO. (Auditoría N3.)
        if (_receiver is not null)
        {
            _receiver.PresenceChanged -= OnReceiverPresence;
            _receiver.FormatChanged -= OnReceiverFormatChanged;
            await _receiver.DisposeAsync().ConfigureAwait(false);
            _receiver = null;
        }

        _receiver = new NdiReceiver(name, _log);
        bool ok = await _receiver.StartAsync(TimeSpan.FromSeconds(8), ct).ConfigureAwait(false);
        if (!ok)
        {
            // NDI no disponible o sin señal: el canal queda en NoSignal (no habilita Grabar) sin tumbar la app.
            await _receiver.DisposeAsync().ConfigureAwait(false);
            _receiver = null;
            CurrentSignal = SignalInfo.None;
            SignalChanged?.Invoke(this, CurrentSignal);
            return;
        }

        // El receptor avisa de pérdida/recuperación de vídeo en caliente (antes NDI nunca reportaba pérdida y
        // CurrentSignal quedaba «Locked» para siempre → ni SignalLost ni slate). (Auditoría 24/7, C3/#16.)
        _receiver.PresenceChanged += OnReceiverPresence;
        // …y del cambio de formato EN CALIENTE (resolución/pixel al vuelo): actualiza CurrentSignal con el formato
        // nuevo para que la UI y una futura reconstrucción del pipeline lo reflejen. (N20.)
        _receiver.FormatChanged += OnReceiverFormatChanged;

        var res = new Resolution(_receiver.Width, _receiver.Height);
        var rate = new FrameRate(_receiver.FrameRateN, _receiver.FrameRateD);
        CurrentSignal = new SignalInfo(SignalState.Locked, res, rate,
            AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null,
            FormatLabel: $"{res.Width}×{res.Height} · NDI");
        SignalChanged?.Invoke(this, CurrentSignal);
    }

    /// <summary>Traduce la presencia de vídeo NDI a CurrentSignal + SignalChanged: el SignalMonitor publica
    /// entonces SignalLost/Locked y el canal entra/sale de carta de ajuste sin esperar al watchdog. (C3.)</summary>
    private void OnReceiverPresence(bool present)
    {
        if (present && _receiver is not null)
        {
            var res = new Resolution(_receiver.Width, _receiver.Height);
            var rate = new FrameRate(_receiver.FrameRateN, _receiver.FrameRateD);
            CurrentSignal = new SignalInfo(SignalState.Locked, res, rate,
                AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null,
                FormatLabel: $"{res.Width}×{res.Height} · NDI");
        }
        else
        {
            CurrentSignal = SignalInfo.None;
        }
        SignalChanged?.Invoke(this, CurrentSignal);
    }

    /// <summary>La fuente NDI cambió de formato EN CALIENTE (resolución/pixel al vuelo): republica CurrentSignal
    /// con la resolución/tasa nuevas para que la UI lo muestre. El receptor ya sirve el formato nuevo; el pipeline
    /// FFmpeg se corregirá al reconstruirse. (N20.)</summary>
    private void OnReceiverFormatChanged()
    {
        if (_receiver is null) return;
        var res = new Resolution(_receiver.Width, _receiver.Height);
        var rate = new FrameRate(_receiver.FrameRateN, _receiver.FrameRateD);
        CurrentSignal = new SignalInfo(SignalState.Locked, res, rate,
            AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null,
            FormatLabel: $"{res.Width}×{res.Height} · NDI");
        SignalChanged?.Invoke(this, CurrentSignal);
    }

    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<string> BuildInputArguments()
    {
        if (_receiver is null)
            throw new InvalidOperationException("La fuente NDI no está abierta (llama a OpenAsync primero).");

        // Entrada 0: vídeo rawvideo (uyvy422 normalmente; bgra si la fuente lleva alfa). Entrada 1: audio f32le
        // interleaved. Ambas las sirve el NdiReceiver por TCP loopback; FFmpeg se conecta como cliente.
        // Resolución/tasa/pixel/audio = los de la fuente real (el receptor los fijó con el primer frame).
        //
        // El formato de ambas entradas es EXPLÍCITO: se anula el análisis de FFmpeg (analyzeduration=0, probesize
        // mínimo) para que no intente «detectar» leyendo segundos de datos —con un rawvideo de cientos de MB/s eso
        // colgaba la apertura—. nobuffer reduce además la latencia de arranque.
        //
        // SINCRONÍA A/V: NO se usa -use_wallclock_as_timestamps en estas entradas. En el AUDIO rompe (con f32le
        // crudo por socket FFmpeg agrupa los bloques y deja de leerlos tras ~1 s → pista truncada; con wallclock
        // el audio caía a ~0,02 s). Y en el VÍDEO también se PROBÓ (2026-07-05, para atacar la deriva bajo
        // contención) y CONGELA: el rawvideo llega por socket EN RÁFAGAS, el wallclock les da PTS casi iguales +
        // huecos, y el vsync CFR RELLENA los huecos DUPLICANDO frames → congelados intermitentes en preview y
        // grabación (freezedetect: ~6 s de 20 s; peor bajo carga). La duración cuadra —por eso una medición SOLO
        // por duración lo daba por «sincronizado»—, pero la imagen se PARA. REVERTIDO. Sin timestamps, FFmpeg
        // deriva el PTS por contador (vídeo nframe/fps, audio nmuestra/sr), que mantiene la sincronía MIENTRAS no
        // se descarten frames de vídeo. La deriva real (drop de vídeo bajo CPU saturada, cola DropOldest) se ataca
        // bajando la CPU (UYVY nativo + ArrayPool); un arreglo robusto exigiría DUPLICAR el último frame EN EL
        // RECEPTOR (mantener el conteo sin romper el ritmo de llegada) o propagar el timecode NDI — trabajo mayor.
        return new[]
        {
            "-f", "rawvideo", "-pixel_format", _receiver.VideoPixelFormat,
            "-video_size", $"{_receiver.Width}x{_receiver.Height}",
            "-framerate", $"{_receiver.FrameRateN}/{_receiver.FrameRateD}",
            "-analyzeduration", "0", "-probesize", "32", "-fflags", "nobuffer",
            "-i", $"tcp://127.0.0.1:{_receiver.VideoPort}",
            "-f", "f32le",
            "-ar", _receiver.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", _receiver.Channels.ToString(CultureInfo.InvariantCulture),
            "-analyzeduration", "0", "-probesize", "32",
            "-i", $"tcp://127.0.0.1:{_receiver.AudioPort}",
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (_receiver is not null)
        {
            _receiver.PresenceChanged -= OnReceiverPresence;
            _receiver.FormatChanged -= OnReceiverFormatChanged;
            await _receiver.DisposeAsync().ConfigureAwait(false);
        }
        _receiver = null;
    }
}

/// <summary>Fábrica que registra el soporte NDI en el sistema de captura (principio Open/Closed).</summary>
public sealed class NdiCaptureSourceFactory(ILoggerFactory loggers) : ICaptureSourceFactory
{
    public bool CanHandle(InputType type) => type is InputType.Ndi;
    public ICaptureSource Create(InputSource definition)
        => new NdiCaptureSource(definition, loggers.CreateLogger<NdiCaptureSource>());
}
