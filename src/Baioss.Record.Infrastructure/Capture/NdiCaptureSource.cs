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

    /// <summary>Formato de píxel negociado con la fuente (uyvy422 normalmente; bgra si lleva alfa); null si no abierta.</summary>
    public string? VideoPixelFormat => _receiver?.VideoPixelFormat;

    public async Task OpenAsync(CancellationToken ct = default)
    {
        var name = Definition.Uri ?? Definition.Name
            ?? throw new InvalidOperationException("Falta el nombre de la fuente NDI.");

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
        // SINCRONÍA A/V: NO se usa -use_wallclock_as_timestamps en estas entradas. Se probó (anclar el PTS al
        // reloj de llegada) y ROMPE el audio: con f32le crudo servido por socket, FFmpeg agrupa los bloques de
        // muestras y deja de leerlos tras ~1 s → la pista de audio queda truncada/malformada (verificado con un
        // banco de pruebas determinista: con wallclock el audio caía a ~0,02 s; sin él, vídeo 5,97 s / audio
        // 6,03 s, perfectamente cuadrados). Sin timestamps, FFmpeg deriva el PTS por contador (vídeo nframe/fps,
        // audio nmuestra/sr), lo que mantiene la sincronía MIENTRAS no se descarten frames de vídeo. La causa del
        // desfase es, por tanto, el descarte de vídeo bajo CPU saturada (cola DropOldest del receptor); se ataca
        // bajando la CPU (formato nativo UYVY + ArrayPool en el receptor), no con wallclock.
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
