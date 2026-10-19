using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NewTek;
using NewTek.NDI;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Recibe vídeo + audio de una fuente NDI con el SDK NewTek y los SIRVE por dos sockets TCP loopback, para que FFmpeg los lea
/// como dos entradas: el vídeo en NUT, con una marca de tiempo por cuadro (<c>-f nut</c>, ver <see cref="NdiNutFraming"/>), y el
/// audio en crudo (<c>-f f32le</c>). Así una fuente NDI entra en el pipeline FFmpeg (preview + grabación) sin necesitar un FFmpeg
/// compilado con NDI.
///
/// <para>VÍDEO EN FORMATO NATIVO: se pide a NDI <c>UYVY_BGRA</c>, que entrega <b>UYVY</b> (YUV 4:2:2, 16 bpp)
/// salvo que la fuente tenga alfa (entonces BGRA, 32 bpp). UYVY es la MITAD de bytes que BGRA —menos copia,
/// menos tráfico de socket— y, al ser ya YUV, FFmpeg lo convierte a NV12/4:2:0 con un <c>swscale</c> mucho más
/// barato que desde RGB. Por eso el BGRA de una fuente con alfa (un grafismo) se pasa a UYVY aquí, una vez por cuadro recibido
/// (<see cref="BgraToUyvy"/>; el alfa no lo graba ninguna salida). El <see cref="VideoPixelFormat"/> resultante (uyvy422; bgra
/// solo con un ancho impar) va en las cabeceras del NUT. Los buffers de frame se alquilan de <see cref="ArrayPool{T}"/> y se
/// devuelven tras escribirlos, para no generar basura (un frame es de varios MB; a 30-60 fps el GC se dispararía).</para>
///
/// <para>Vídeo y audio se sirven en HILOS SEPARADOS con colas acotadas (<see cref="NdiFeed{T}"/>): FFmpeg abre sus entradas
/// en serie y, mientras abre el audio, deja de leer el vídeo; con un solo hilo el <c>Write</c> de vídeo se bloquearía y
/// el audio nunca se serviría (deadlock). Las colas descartan los frames más viejos si FFmpeg se atrasa.</para>
///
/// <para>Al retirar un proceso que graba, <see cref="EndStream"/> le cierra las dos entradas en el mismo instante para que
/// termine por sí mismo con todo lo que ya llegó: FFmpeg lee el audio por detrás del vídeo y, con la «q», lo que quedaba sin
/// leer se perdía (el archivo acababa con el audio hasta 0,9 s más corto que el vídeo).</para>
///
/// <para>Una fuente SIN audio (o que lo pierde) recibe silencio al ritmo de los periodos de su cadencia (<see cref="NdiAudioGapFiller"/>):
/// sin datos en la entrada de audio FFmpeg no arrancaba y el canal no tenía ni preview ni grabación.</para>
///
/// <para>CADENCIA DECLARADA, SIN SERVIRLA ENTERA: una fuente que entrega menos cuadros de los que declara (un grafismo que declara 60
/// y manda 4, un vMix que manda 10 de 29.97) se graba a la cadencia declarada y en tiempo real. <see cref="NdiFrameCadence"/> decide
/// qué periodo ocupa cada cuadro real según el reloj de la fuente y cuándo vence la repetición del último; cada cuadro real va a
/// FFmpeg con la marca de su periodo, y las repeticiones no se envían: son periodos que avanzan (con su silencio, si no hay audio) y
/// que la grabación rellena repitiendo el cuadro anterior (un <c>fps</c> en su rama). Antes el vídeo iba en <c>rawvideo</c>, que
/// FFmpeg marca por contador, y había que servirle cada repetición entera: 60 cuadros de 1080 por segundo de una fuente que manda
/// 4 atravesaban el socket, el preview y la conversión de la grabación (Offside, 2026-10-17: 1,86 núcleos el proceso del canal).
/// El preview pinta los cuadros reales, a su ritmo. Si en <see cref="KeepAlive"/> no ha salido ningún cuadro, sale el último con el
/// periodo en curso: el vídeo de FFmpeg avanza aunque la fuente calle, y el audio (que FFmpeg no lee por delante del vídeo) no se
/// queda esperando; y al cerrar (<see cref="EndStream"/>), el último periodo vencido, para que la grabación llegue hasta él.</para>
/// </summary>
public sealed class NdiReceiver : IAsyncDisposable
{
    private readonly string _sourceName;
    private readonly ILogger _log;
    private readonly TcpListener _videoListener = new(IPAddress.Loopback, 0);
    private readonly TcpListener _audioListener = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource<bool> _firstVideo = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Cada cuadro se suelta (Release) al escribirlo, al descartarlo la cola por saturación o si ya no se puede encolar (cierre),
    // y vuelve al pool cuando lo suelta el último que lo tiene: un cuadro que se vuelve a enviar está en la cola y además guardado
    // como el último. El vídeo va en NUT, cada proceso de FFmpeg con sus cabeceras y sus marcas (NdiNutFraming). El audio, en crudo:
    // su cola cubre más de un segundo, lo que FFmpeg puede tardar en leerlo cuando el vídeo va detrás (un hueco de la fuente, el
    // retraso del codificador).
    private readonly NdiFeed<NdiVideoItem> _video = new(4, static i => i.Frame.Buffer.AsMemory(0, i.Frame.Length),
        static i => i.Frame.Release(), static () => new NdiNutFraming());
    private readonly NdiFeed<NdiFrame> _audio = new(64, static f => f.Buffer.AsMemory(0, f.Length), static f => f.Release());

    private CancellationTokenSource? _cts;
    private Task? _recvLoop;
    private IntPtr _recv;
    private int _bytesPerPixel = 4; // se fija con el primer frame según lo que se sirve (uyvy422=2, bgra=4)
    private volatile bool _present;           // ¿llega vídeo NDI ahora mismo? (para detectar pérdida en caliente)
    private DateTimeOffset _lastVideoUtc;     // marca del último frame de vídeo recibido
    private DateTimeOffset _lastPresenceLoss; // cuándo se perdió la presencia (para re-detectar formato si la caída fue larga)
    // Silencio cuando la fuente no trae audio (o deja de traerlo): sin datos en la entrada de audio, FFmpeg no arranca.
    private readonly NdiAudioGapFiller _silence = new();
    // Cadencia declarada: qué periodo ocupa cada cuadro y cuándo vence la repetición del último (_last, con una referencia propia).
    // Reloj monótono: un ajuste de la hora del sistema no debe soltar ni retener repeticiones. Lo comparten el bucle de recepción y
    // EndStream (otro hilo): bajo _serve, como el periodo en curso, el silencio y lo que se encola.
    private readonly object _serve = new();
    private readonly NdiFrameCadence _cadence = new();
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private NdiFrame? _last;
    private NdiVideoFormat? _format;
    // Periodos de la cadencia declarada desde que arrancó el receptor: el siguiente que ocupa un cuadro (real o repetido). Nunca
    // vuelve atrás: cada proceso de FFmpeg cuenta sus marcas desde su primer cuadro.
    private long _period;
    // El último cuadro real servido con marca: su periodo y su instante en el reloj de la fuente. Con ellos se sabe el instante de la
    // fuente de cualquier periodo (el de una repetición que se envía, el primero de cada proceso), para alinear el audio a él.
    private long _anchorPeriod;
    private TimeSpan? _anchorSource;
    // El audio de cada proceso de FFmpeg empieza en el instante de la fuente de su primer cuadro de vídeo (ver NdiAudioAligner).
    private readonly NdiAudioAligner _aligner = new();
    private long _lastSentPeriod = -1;   // periodo del último cuadro encolado para FFmpeg
    private TimeSpan _lastSentAt;        // y cuándo (nuestro reloj)
    private TimeSpan _lastRealAt;        // llegada del último cuadro real (nuestro reloj)
    // Aviso de que la fuente entrega menos de lo que declara, por ventanas de unos segundos (cuadros reales y repeticiones).
    private TimeSpan _windowStart;
    private long _windowFrames, _windowRepeated;
    private bool _shortWarned;
    // Campos sueltos (frame_format_type_field_0/1): el cuadro que se está tejiendo, con el campo superior ya copiado.
    private byte[]? _weave;
    private int _weaveW, _weaveH;
    private NDIlib.FourCC_type_e _weaveFourCC;
    private bool _weaveTop;
    // Una línea BGRA copiada de la fuente para convertirla a UYVY (ver BgraToUyvy).
    private byte[]? _bgraLine;

    /// <summary>Se dispara al PERDER (false) o RECUPERAR (true) la presencia de vídeo NDI, para que la fuente
    /// actualice su <c>CurrentSignal</c> y el canal entre/salga de carta de ajuste sin esperar al watchdog. (C3.)</summary>
    public event Action<bool>? PresenceChanged;

    /// <summary>Se dispara cuando la fuente cambia de formato (resolución/pixel) EN CALIENTE sin perder presencia
    /// (p. ej. vMix 720→1080 al vuelo): el receptor ya sirve el formato nuevo, pero el FFmpeg en marcha sigue con el de sus
    /// cabeceras NUT —no se le escriben cuadros de otro: se queda con el último bueno— hasta que el pipeline se RECONSTRUYA. La
    /// fuente lo usa para actualizar <c>CurrentSignal</c> (y avisar). No se fuerza el rebuild aquí (sería una carrera con el
    /// slate). (N20/#32.)</summary>
    public event Action? FormatChanged;

    /// <summary>Sin frames de vídeo durante este tiempo ⇒ se considera pérdida de señal NDI.</summary>
    public TimeSpan PresenceTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Si la pérdida de presencia supera este umbral, al recuperar el vídeo se RE-DETECTA el formato
    /// (resolución/tasa/pixel) por si la fuente cambió mientras no emitía. Evita servir frames del nuevo formato
    /// con los parámetros viejos cacheados del primer frame histórico. (Auditoría 24/7, #32.)</summary>
    public TimeSpan FormatResetThreshold { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sin enviar ningún cuadro a FFmpeg durante este tiempo (la fuente calla entre dos cuadros), se envía el último con el periodo
    /// en curso. FFmpeg no deja que una entrada se adelante a la salida más retrasada: con una fuente que calla segundos (un grafismo
    /// que solo manda cuando cambia la imagen), el vídeo del proceso se pararía con ella y el audio esperaría detrás, acumulándose en
    /// el socket y en su cola. Acota esa espera, y lo que la grabación tarda en recibir los periodos que rellena. Por encima de los
    /// huecos de las fuentes medidas (Offside, 200 ms; vMix, hasta 233 ms), para no gastar un cuadro entero en cada uno; los de
    /// 400 ms de Offside, uno de cada cinco, llevan uno.
    /// </summary>
    public TimeSpan KeepAlive { get; init; } = TimeSpan.FromMilliseconds(300);

    public NdiReceiver(string sourceName, ILogger log)
    {
        _sourceName = sourceName;
        _log = log;
    }

    public int VideoPort { get; private set; }
    public int AudioPort { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>
    /// Cuántos formatos distintos (tamaño, píxel, cadencia o barrido) ha servido el receptor: el primer cuadro lo deja en 1, y cada
    /// cambio —en caliente o al volver de una caída larga con otro formato— lo sube. Un proceso de FFmpeg solo recibe cuadros del
    /// formato de sus cabeceras NUT: si este número ya no es el de cuando se conectó, ese proceso no tendrá más vídeo y el canal
    /// tiene que rehacerlo (ver <see cref="Baioss.Record.Application.Capture.ICaptureSource.VideoFormatVersion"/>).
    /// </summary>
    public int FormatVersion => Volatile.Read(ref _formatVersion);
    private int _formatVersion;
    private (NdiVideoFormat? Format, bool Interlaced) _appliedFormat;
    /// <summary><c>-pixel_format</c> de FFmpeg para el vídeo, según el FourCC real del primer frame (uyvy422 / bgra).</summary>
    public string VideoPixelFormat { get; private set; } = "uyvy422";
    public int FrameRateN { get; private set; } = 30000;
    public int FrameRateD { get; private set; } = 1001;
    /// <summary>Los cuadros traen dos campos entretejidos (NDI <c>frame_format_type_interleaved</c>, el superior primero): un
    /// 1080i. La tasa de <see cref="FrameRateN"/>/<see cref="FrameRateD"/> es entonces la de cuadro (30000/1001 en 1080i59.94). Un
    /// «interleaved» a más de 30 cuadros por segundo es un progresivo mal marcado (ver <see cref="IsInterleaved"/>).</summary>
    public bool Interlaced { get; private set; }
    public int SampleRate { get; private set; } = 48000;
    public int Channels { get; private set; } = 2;

    /// <summary>
    /// Arranca la recepción NDI y los servidores TCP, y espera al PRIMER frame de vídeo (para conocer
    /// resolución/tasa). Devuelve false si NDI no está disponible, no se pudo conectar o no llega vídeo a
    /// tiempo (<paramref name="firstFrameTimeout"/>).
    /// </summary>
    public async Task<bool> StartAsync(TimeSpan firstFrameTimeout, CancellationToken ct = default)
    {
        if (!NdiRuntime.IsAvailable) return false;

        var src = new NDIlib.source_t { p_ndi_name = UTF.StringToUtf8(_sourceName) };
        var settings = new NDIlib.recv_create_v3_t
        {
            source_to_connect_to = src,
            // UYVY (YUV 4:2:2, 16 bpp) cuando no hay alfa; BGRA solo si la fuente lo tiene. La mitad de bytes que
            // BGRA y, por ser ya YUV, FFmpeg lo lleva a 4:2:0 con un swscale barato (clave para bajar la CPU).
            color_format = NDIlib.recv_color_format_e.recv_color_format_UYVY_BGRA,
            bandwidth = NDIlib.recv_bandwidth_e.recv_bandwidth_highest,
            // Los cuadros, como los manda la fuente: progresivos, con los dos campos entretejidos (lo normal en un 1080i, p. ej.
            // vMix) o en campos sueltos, que se tejen aquí (Weave). Con false, el SDK fundía los campos y marcaba los cuadros
            // como progresivos: la imagen seguía entretejida pero el entrelazado se perdía —preview con doble imagen en lo que se
            // mueve y presets progresivos grabados sin desentrelazar—.
            allow_video_fields = true,
        };
        _recv = NdiRuntime.CreateReceiver(ref settings); // contado: el runtime solo se cierra al salir sin receptores abiertos
        if (_recv == IntPtr.Zero) { _log.LogWarning("NDI: no se pudo crear el receptor para «{Source}».", _sourceName); return false; }

        _videoListener.Start();
        _audioListener.Start();
        VideoPort = ((IPEndPoint)_videoListener.LocalEndpoint).Port;
        AudioPort = ((IPEndPoint)_audioListener.LocalEndpoint).Port;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _ = AcceptAsync(_videoListener, client => { _video.Attach(client); OnVideoAttached(); }, token);
        _ = AcceptAsync(_audioListener, AttachAudio, token);
        _recvLoop = Task.Run(() => ReceiveLoop(token), token);

        var done = await Task.WhenAny(_firstVideo.Task, Task.Delay(firstFrameTimeout, ct)).ConfigureAwait(false);
        if (done != _firstVideo.Task) { _log.LogWarning("NDI: «{Source}» no entregó vídeo en {T}.", _sourceName, firstFrameTimeout); return false; }
        _log.LogInformation("NDI: «{Source}» conectada — {W}x{H} @ {N}/{D} {Scan} {Pix}, audio {SR}Hz {Ch}ch.",
            _sourceName, Width, Height, FrameRateN, FrameRateD, Interlaced ? "entrelazado" : "progresivo", VideoPixelFormat, SampleRate, Channels);
        return true;
    }

    // FFmpeg (cliente) se conecta a cada puerto al arrancar; ante reconexión (reinicio al alternar grabación)
    // se vuelve a aceptar el nuevo cliente: el flujo le sirve desde ya y cierra el anterior (ver NdiFeed.Attach).
    private async Task AcceptAsync(TcpListener listener, Action<TcpClient> attach, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var next = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                next.NoDelay = true;
                attach(next);
                _log.LogDebug("NDI: FFmpeg conectó a un socket de «{Source}».", _sourceName);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "NDI: re-aceptando conexión de FFmpeg."); }
        }
    }

    /// <summary>
    /// Fin de flujo para el proceso de FFmpeg conectado: recibe lo que ya llegó y después se le cierran las dos entradas en el
    /// mismo instante, así que termina por sí mismo (EOF) con todo dentro; lo que llegue después es para el proceso siguiente. No
    /// espera: el motor espera a que FFmpeg salga. True si había un proceso conectado a las dos entradas.
    /// <para>Por qué: FFmpeg lee la entrada de audio por detrás de la de vídeo —su planificador la frena para no adelantarse a lo
    /// que sale del codificador de vídeo, que retiene cerca de un segundo— y con la «q» deja de leer en el acto: el audio que
    /// esperaba en el socket se perdía y el archivo acababa con el audio 0,1–0,9 s más corto que el vídeo. Con la entrada de
    /// vídeo cerrada, FFmpeg vacía el codificador y lee el audio pendiente hasta el mismo instante.</para>
    /// <para>Antes, el último cuadro con el último periodo vencido, si no salió ya: los periodos que FFmpeg iba a rellenar repitiendo
    /// el anterior solo los rellena hasta un cuadro, y sin él la grabación acababa en el último cuadro real —hasta
    /// <see cref="KeepAlive"/> antes que el audio—.</para>
    /// </summary>
    public bool EndStream()
    {
        lock (_serve)
        {
            if (_last is not null)
            {
                var now = _clock.Elapsed;
                AdvanceRepeats(now);
                if (_period - 1 > _lastSentPeriod) EnqueueVideo(_last.Share(), _period - 1, now);
            }
            bool video = _video.End();
            bool audio = _audio.End();
            _log.LogInformation("NDI: «{Source}» fin de flujo; descartados por cola llena hasta ahora: {Video} cuadros y {Audio} bloques de audio.",
                _sourceName, _video.Dropped, _audio.Dropped);
            return video && audio;
        }
    }

    private void ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var v = new NDIlib.video_frame_v2_t();
            var a = new NDIlib.audio_frame_v2_t();
            var m = new NDIlib.metadata_frame_t();
            switch (NDIlib.recv_capture_v2(_recv, ref v, ref a, ref m, WaitMs()))
            {
                case NDIlib.frame_type_e.frame_type_video:
                    try { OnVideo(ref v); } finally { NDIlib.recv_free_video_v2(_recv, ref v); }
                    break;
                case NDIlib.frame_type_e.frame_type_audio:
                    try { OnAudio(ref a); } finally { NDIlib.recv_free_audio_v2(_recv, ref a); }
                    break;
                case NDIlib.frame_type_e.frame_type_metadata:
                    NDIlib.recv_free_metadata(_recv, ref m);
                    break;
            }

            // El siguiente cuadro no llegó a tiempo: corren los periodos del último (y, si hace mucho que no sale ninguno, se envía).
            RepeatDue();

            // Detección de PÉRDIDA de señal NDI: si no llega vídeo en PresenceTimeout, notifica una sola vez
            // (la RECUPERACIÓN la notifica OnVideo al volver el primer frame). recv_capture_v2 espera como mucho
            // 100 ms, así que este bucle sigue girando aunque la fuente deje de emitir. (Auditoría 24/7, C3.)
            if (_present && DateTimeOffset.UtcNow - _lastVideoUtc > PresenceTimeout)
            {
                _present = false;
                _lastPresenceLoss = DateTimeOffset.UtcNow; // sella la caída para decidir si re-detectar formato al volver (#32)
                ResetCadence(); // sin fuente no se repite nada: el canal pasa a carta de ajuste
                try { PresenceChanged?.Invoke(false); } catch { /* no romper el bucle de recepción */ }
            }
        }
    }

    /// <summary>
    /// Conectó un proceso de FFmpeg al vídeo en un hueco de la fuente: le sale ya el último cuadro, con el periodo en curso. FFmpeg
    /// no termina de abrir la entrada —ni pasa a abrir la del audio— hasta leer un cuadro, y el siguiente de una fuente a saltos
    /// puede tardar hasta <see cref="KeepAlive"/>: el archivo empezaba ese tiempo después del Grabar (medido: 0,2 s menos de vídeo
    /// y 0,1 s menos de audio con un grafismo que manda uno cada 250 ms). Con la fuente a su ritmo no hace falta (el siguiente
    /// cuadro real llega antes de un periodo) y sería peor: ese cuadro es de hace un periodo, el vídeo del proceso empezaría un
    /// periodo antes que su audio (medido: pitido 33 ms más adelantado) y el siguiente real caería en su mismo periodo.
    /// </summary>
    private void OnVideoAttached()
    {
        lock (_serve)
        {
            if (_last is null || FrameRateN <= 0 || FrameRateD <= 0) return;
            var now = _clock.Elapsed;
            if (now - _lastRealAt < TimeSpan.FromSeconds((double)FrameRateD / FrameRateN)) return;
            AdvanceRepeats(now);
            if (_period > _lastSentPeriod) EnqueueVideo(_last.Share(), _period, now);
        }
    }

    /// <summary>
    /// Conectó el audio de un proceso de FFmpeg (siempre después de su vídeo: FFmpeg abre las entradas en serie y no pasa al audio
    /// hasta leer un cuadro). Su audio empieza en el instante de la fuente de su primer cuadro de vídeo, con lo guardado o esperando a
    /// que llegue (<see cref="NdiAudioAligner"/>); con silencio de relleno no hace falta, que ya sigue a los periodos del vídeo. Bajo
    /// el mismo candado que el audio en directo, para que ningún bloque nuevo se le cuele delante.
    /// </summary>
    private void AttachAudio(TcpClient client)
    {
        lock (_serve)
        {
            _audio.Attach(client);
            if (_silence.Filling) return;
            var videoStart = (_video.CurrentFraming as NdiNutFraming)?.FirstSource;
            var (blocks, outcome, amount) = _aligner.StartAt(videoStart);
            foreach (var b in blocks) _audio.Enqueue(b);
            _log.LogDebug("NDI: audio del proceso de «{Source}» alineado con su primer cuadro de vídeo: {Outcome} ({Ms:0} ms).",
                _sourceName, outcome, amount.TotalMilliseconds);
        }
    }

    /// <summary>Espera máxima de <c>recv_capture_v2</c>: hasta que venza la siguiente repetición (y no más de 100 ms).</summary>
    private uint WaitMs()
    {
        TimeSpan? next;
        lock (_serve) next = _cadence.UntilNextRepeat(_clock.Elapsed);
        if (next is not { } until) return 100;
        return (uint)Math.Clamp(Math.Ceiling(until.TotalMilliseconds), 1, 100);
    }

    /// <summary>Corren los periodos de las repeticiones del último cuadro que vencieron (la fuente no envió el siguiente a tiempo):
    /// FFmpeg las rellena solo; si hace <see cref="KeepAlive"/> que no sale ningún cuadro, sale el último con el periodo en curso.</summary>
    private void RepeatDue()
    {
        lock (_serve)
        {
            if (_last is null) return;
            var now = _clock.Elapsed;
            if (AdvanceRepeats(now) > 0 && now - _lastSentAt >= KeepAlive && _period - 1 > _lastSentPeriod)
                EnqueueVideo(_last.Share(), _period - 1, now);
            WatchCadence(now);
        }
    }

    /// <summary>Las repeticiones del último cuadro que vencen ya, como periodos que pasan (con su silencio si no hay audio); devuelve
    /// cuántas. Bajo <see cref="_serve"/>.</summary>
    private int AdvanceRepeats(TimeSpan now)
    {
        int due = _cadence.Due(now);
        if (due <= 0) return 0;
        _period += due;
        Silence(due);
        _windowRepeated += due;
        return due;
    }

    /// <summary>Olvida el último cuadro y la referencia de la cadencia (pérdida de la fuente o cambio de formato: el cuadro
    /// guardado ya no vale para repetirlo). Los periodos siguen donde iban: las marcas de cada proceso de FFmpeg no vuelven atrás.</summary>
    private void ResetCadence()
    {
        lock (_serve)
        {
            _cadence.Reset();
            _last?.Release();
            _last = null;
            _anchorSource = null; // el siguiente cuadro real vuelve a fijar la referencia
            _windowStart = _clock.Elapsed;
            _windowFrames = _windowRepeated = 0;
        }
    }

    /// <summary>Encola un cuadro para FFmpeg en el periodo <paramref name="period"/> (si ya no se puede encolar —cierre—, la cola lo
    /// suelta). Bajo <see cref="_serve"/>.</summary>
    private void EnqueueVideo(NdiFrame frame, long period, TimeSpan now)
    {
        _video.Enqueue(new NdiVideoItem(frame, period, PeriodSource(period)));
        _lastSentPeriod = period;
        _lastSentAt = now;
    }

    /// <summary>El instante de la fuente en que empieza el periodo <paramref name="period"/>, contado desde el último cuadro real con
    /// marca (null sin él). Bajo <see cref="_serve"/>.</summary>
    private TimeSpan? PeriodSource(long period)
        => _anchorSource is { } anchor && FrameRateN > 0 && FrameRateD > 0
            ? anchor + TimeSpan.FromTicks((long)Math.Round((period - _anchorPeriod) * (double)TimeSpan.TicksPerSecond * FrameRateD / FrameRateN))
            : null;

    /// <summary>
    /// Pasaron <paramref name="periods"/> periodos del vídeo: si la fuente no trae audio, el silencio que les toca. El silencio sigue
    /// a los PERIODOS —los que FFmpeg recibe con su cuadro y los que rellena repitiendo el anterior—, que son las marcas del vídeo.
    /// Bajo <see cref="_serve"/>.
    /// </summary>
    private void Silence(long periods)
    {
        if (periods <= 0) return;
        bool wasFilling = _silence.Filling;
        int silent = _silence.OnPeriods(DateTimeOffset.UtcNow, periods, SampleRate, FrameRateN, FrameRateD);
        if (silent > 0) EnqueueSilence(silent);
        if (_silence.Filling && !wasFilling)
        {
            _aligner.Forget(); // el silencio sigue a los periodos: no hay audio que alinear
            _log.LogInformation("NDI: «{Source}» no trae audio; se envía silencio ({SR} Hz, {Ch} canales) para que el canal previsualice y grabe igual.",
                _sourceName, SampleRate, Channels);
        }
    }

    /// <summary>Avisa (una vez por episodio) cuando la fuente entrega claramente menos cuadros de los que declara y se están
    /// repitiendo para mantener la cadencia, y cuando vuelve a su ritmo. Ventanas de 10 s.</summary>
    private void WatchCadence(TimeSpan now)
    {
        var window = now - _windowStart;
        if (window < TimeSpan.FromSeconds(10)) return;
        long served = _windowFrames + _windowRepeated;
        if (served > 0)
        {
            double repeatedShare = (double)_windowRepeated / served;
            double declared = FrameRateD > 0 ? (double)FrameRateN / FrameRateD : 0;
            if (!_shortWarned && repeatedShare > 0.05)
            {
                _shortWarned = true;
                _log.LogInformation("NDI: «{Source}» entrega {Real:0.#} cuadros/s de los {Declared:0.##} que declara; la grabación repite el " +
                    "último hasta el siguiente para ir a la cadencia declarada (y durar lo que dura, en sincronía con el audio), y el " +
                    "preview pinta los que llegan.",
                    _sourceName, _windowFrames / window.TotalSeconds, declared);
            }
            else if (_shortWarned && repeatedShare < 0.01)
            {
                _shortWarned = false;
                _log.LogInformation("NDI: «{Source}» vuelve a entregar su cadencia ({Real:0.#} cuadros/s).", _sourceName, _windowFrames / window.TotalSeconds);
            }
        }
        _windowStart = now;
        _windowFrames = _windowRepeated = 0;
    }

    private void OnVideo(ref NDIlib.video_frame_v2_t v)
    {
        if (v.xres <= 0 || v.yres <= 0 || v.p_data == IntPtr.Zero) return;
        // Campos sueltos: se espera a tener los dos y se entregan tejidos, como un cuadro entrelazado.
        byte[]? woven = null;
        int frameHeight = v.yres;
        bool interlaced = IsInterleaved(v.frame_format_type, v.frame_rate_N, v.frame_rate_D);
        if (IsField(v.frame_format_type))
        {
            woven = Weave(ref v);
            if (woven is null) return; // falta el otro campo
            frameHeight = 2 * v.yres;
            interlaced = true;
        }
        _lastVideoUtc = DateTimeOffset.UtcNow;
        bool recovered = false;
        if (!_present) // recuperación (o primer frame): vuelve a haber vídeo NDI
        {
            _present = true;
            recovered = true;
            // Si la caída fue PROLONGADA, la fuente pudo cambiar de resolución/formato mientras no emitía:
            // se fuerza la re-detección (Width=0) para que ESTE frame re-fije el formato. La presencia se
            // notifica DESPUÉS de re-fijarlo (más abajo), no aquí, para que NdiCaptureSource lea ya los
            // valores NUEVOS al construir CurrentSignal (si no, publicaría 0×0 de forma transitoria). (#32.)
            if (DateTimeOffset.UtcNow - _lastPresenceLoss >= FormatResetThreshold) Width = 0;
        }
        if (Width == 0) // primer frame o re-detección tras pérdida prolongada: fija el formato y desbloquea StartAsync
        {
            ApplyFormat(ref v, frameHeight, interlaced);
            ResetCadence();
            _firstVideo.TrySetResult(true);
        }
        else if (FormatDiffers(v.xres, frameHeight, PixelFormatFor(v.FourCC, v.xres), interlaced, Width, Height, VideoPixelFormat, Interlaced))
        {
            // CAMBIO DE FORMATO EN CALIENTE, sin hueco de presencia (N20): la fuente cambió resolución/pixel/barrido al
            // vuelo (p. ej. vMix 720→1080). Re-fija el formato para que la copia de frame y las cabeceras del proceso siguiente
            // sean correctas, AVISA y notifica FormatChanged para que la fuente actualice CurrentSignal: el FFmpeg en marcha no
            // recibe cuadros del formato nuevo, que no caben en su flujo NUT, y el canal lo rehace al ver que FormatVersion
            // cambió (FfmpegChannelEngine). Aquí no se toca el proceso: sería una carrera con el slate. (#32/N20.)
            int oldW = Width, oldH = Height; string oldPix = VideoPixelFormat; bool oldInterlaced = Interlaced;
            ApplyFormat(ref v, frameHeight, interlaced);
            ResetCadence(); // el último cuadro es del formato anterior: no se repite
            _log.LogWarning("NDI: «{Source}» cambió de formato en caliente ({OW}x{OH} {OP}{OI} → {W}x{H} {P}{I}); el canal rehace " +
                "su proceso con el formato nuevo.",
                _sourceName, oldW, oldH, oldPix, oldInterlaced ? " entrelazado" : "", Width, Height, VideoPixelFormat, Interlaced ? " entrelazado" : "");
            try { FormatChanged?.Invoke(); } catch { /* no romper el bucle de recepción */ }
        }

        // Notifica la recuperación AHORA, con el formato ya re-fijado (ver nota arriba): así CurrentSignal
        // refleja la resolución/tasa reales del nuevo frame y no un 0×0 transitorio. (C3 + #32.)
        if (recovered) { try { PresenceChanged?.Invoke(true); } catch { /* no romper la recepción */ } }

        int rowBytes = v.xres * _bytesPerPixel;
        int size = frameHeight * rowBytes;
        byte[] buf;
        if (woven is not null)
            buf = woven;                                          // ya tejido en un búfer del pool
        else
        {
            buf = ArrayPool<byte>.Shared.Rent(size);
            if (ConvertsToUyvy(v.FourCC, v.xres))                 // BGRA (alfa): a UYVY, línea a línea
                for (int y = 0; y < v.yres; y++)
                    CopyLine(ref v, y, buf, y * rowBytes, frameHeight);
            else if (v.line_stride_in_bytes == rowBytes)
                Marshal.Copy(v.p_data, buf, 0, size);             // sin relleno: copia contigua
            else
                for (int y = 0; y < v.yres; y++)                  // con relleno: quita el padding por fila
                    Marshal.Copy(v.p_data + y * v.line_stride_in_bytes, buf, y * rowBytes, rowBytes);
        }

        // A la cadencia declarada: antes de este cuadro corren los periodos del anterior que la fuente se saltó y aún no corrieron
        // (FFmpeg los rellena repitiéndolo); este va con la marca de su periodo, salvo que lo servido vaya ya demasiado por delante
        // de la fuente. Sea como sea, es el que se repite a partir de ahora. Cada periodo lleva su silencio si la fuente no trae
        // audio (y, al detectarlo, los que ya pasaron sin él).
        var frame = new NdiFrame(buf, size, _format);
        lock (_serve)
        {
            var now = _clock.Elapsed;
            _lastRealAt = now;
            var source = SourceTime(v.timestamp);
            var decision = _cadence.OnFrame(now, source, FrameRateN, FrameRateD);
            _period += decision.RepeatsBefore;
            if (decision.Serve)
            {
                if (source is not null) { _anchorPeriod = _period; _anchorSource = source; }
                EnqueueVideo(frame.Share(), _period++, now);
            }
            Silence(decision.RepeatsBefore + (decision.Serve ? 1 : 0));
            _last?.Release();
            _last = frame;
            _windowFrames++;
            _windowRepeated += decision.RepeatsBefore;
        }
    }

    /// <summary>Marca de un cuadro en el reloj de la fuente (la pone el SDK al enviarlo, en unidades de 100 ns); null si la fuente
    /// no la da.</summary>
    internal static TimeSpan? SourceTime(long timestamp)
        => timestamp > 0 && timestamp != NDIlib.recv_timestamp_undefined ? TimeSpan.FromTicks(timestamp) : null;

    /// <summary>Encola <paramref name="samples"/> muestras de silencio f32le en el formato que sirve el receptor.</summary>
    private void EnqueueSilence(int samples)
    {
        int bytes = samples * Math.Max(1, Channels) * sizeof(float);
        var buf = ArrayPool<byte>.Shared.Rent(bytes);
        Array.Clear(buf, 0, bytes); // los búferes del pool vuelven con datos viejos
        _audio.Enqueue(new NdiFrame(buf, bytes));
    }

    /// <summary>Lo más que va un entrelazado de verdad, en cuadros por segundo: 1080i60 (60 campos), con margen para una tasa
    /// redondeada.</summary>
    internal const double MaxInterlacedFrameRate = 30.5;

    /// <summary>
    /// ¿El cuadro trae los dos campos entretejidos (el superior primero, según NDI)? NDI lo dice con <c>frame_format_type_interleaved</c>,
    /// que es el CERO de la enumeración: un emisor que no rellena el campo (la estructura a cero) marca así sus cuadros progresivos.
    /// Un entrelazado no pasa de 30 cuadros (60 campos) por segundo —1080i50/59.94/60, 480i, 576i—, así que un «interleaved» a más de
    /// <see cref="MaxInterlacedFrameRate"/> es un progresivo mal marcado: «Offside Engine NDI Output» manda un 1080 a 60000/1000 así
    /// (2026-10-17); se enseñaba como «120i» y el canal desentrelazaba cada cuadro. Sin tasa, se cree la marca.
    /// </summary>
    internal static bool IsInterleaved(NDIlib.frame_format_type_e type, int rateN, int rateD)
        => type == NDIlib.frame_format_type_e.frame_format_type_interleaved
           && !(rateN > 0 && rateD > 0 && (double)rateN / rateD > MaxInterlacedFrameRate);

    /// <summary>¿Es un campo suelto (el superior, field_0, o el inferior, field_1)?</summary>
    internal static bool IsField(NDIlib.frame_format_type_e type)
        => type is NDIlib.frame_format_type_e.frame_format_type_field_0 or NDIlib.frame_format_type_e.frame_format_type_field_1;

    /// <summary>
    /// Teje un campo suelto en el cuadro en curso: el superior (field_0, el primero en el tiempo) en las líneas pares, el
    /// inferior (field_1) en las impares. Devuelve el cuadro —un búfer del pool, que pasa al llamador— cuando llega el
    /// inferior de un superior con la misma geometría; si no, null (un inferior sin su superior se descarta).
    /// </summary>
    private byte[]? Weave(ref NDIlib.video_frame_v2_t v)
    {
        int rowBytes = v.xres * (PixelFormatFor(v.FourCC, v.xres) == "uyvy422" ? 2 : 4);
        bool sameGeometry = _weave is not null && _weaveW == v.xres && _weaveH == v.yres && _weaveFourCC == v.FourCC;
        if (v.frame_format_type == NDIlib.frame_format_type_e.frame_format_type_field_0)
        {
            if (_weave is not null && !sameGeometry) { ArrayPool<byte>.Shared.Return(_weave); _weave = null; }
            _weave ??= ArrayPool<byte>.Shared.Rent(2 * v.yres * rowBytes);
            (_weaveW, _weaveH, _weaveFourCC) = (v.xres, v.yres, v.FourCC);
            CopyField(ref v, _weave, rowBytes, firstLine: 0);
            _weaveTop = true;
            return null;
        }
        if (!_weaveTop || !sameGeometry) return null;
        CopyField(ref v, _weave!, rowBytes, firstLine: 1);
        var frame = _weave!;
        _weave = null;
        _weaveTop = false;
        return frame;
    }

    private void CopyField(ref NDIlib.video_frame_v2_t v, byte[] frame, int rowBytes, int firstLine)
    {
        bool convert = ConvertsToUyvy(v.FourCC, v.xres);
        for (int y = 0; y < v.yres; y++)
        {
            int offset = (2 * y + firstLine) * rowBytes;
            if (convert) CopyLine(ref v, y, frame, offset, frameHeight: 2 * v.yres);
            else Marshal.Copy(v.p_data + y * v.line_stride_in_bytes, frame, offset, rowBytes);
        }
    }

    /// <summary>La línea <paramref name="y"/> de un cuadro (o campo) BGRA de la fuente, en UYVY, en <paramref name="frame"/> desde
    /// <paramref name="offset"/>. <paramref name="frameHeight"/> es la altura del cuadro entero (decide la matriz).</summary>
    private void CopyLine(ref NDIlib.video_frame_v2_t v, int y, byte[] frame, int offset, int frameHeight)
    {
        int bgraBytes = v.xres * 4;
        var line = _bgraLine is { } staged && staged.Length >= bgraBytes ? staged : _bgraLine = new byte[bgraBytes];
        Marshal.Copy(v.p_data + y * v.line_stride_in_bytes, line, 0, bgraBytes);
        BgraToUyvy.ConvertLine(line, frame.AsSpan(offset, v.xres * 2), v.xres, frameHeight);
    }

    /// <summary>Fija el formato (resolución/tasa/pixel/bytes-por-píxel/barrido) del cuadro que se sirve. Lo comparten el
    /// primer frame, la re-detección tras pérdida prolongada (#32) y el cambio de formato en caliente (N20). El FourCC real
    /// decide el -pixel_format: con UYVY_BGRA, UYVY (16 bpp), y también el BGRA de una fuente con alfa, que se convierte al
    /// recibirlo (ver <see cref="PixelFormatFor"/>).</summary>
    private void ApplyFormat(ref NDIlib.video_frame_v2_t v, int height, bool interlaced)
    {
        Width = v.xres; Height = height;
        Interlaced = interlaced;
        if (v.frame_rate_N > 0 && v.frame_rate_D > 0) { FrameRateN = v.frame_rate_N; FrameRateD = v.frame_rate_D; }
        VideoPixelFormat = PixelFormatFor(v.FourCC, v.xres);
        _bytesPerPixel = VideoPixelFormat == "uyvy422" ? 2 : 4;
        _format = new NdiVideoFormat(Width, Height, VideoPixelFormat, FrameRateN, FrameRateD); // el de las cabeceras NUT
        if ((_format, Interlaced) != _appliedFormat)
        {
            _appliedFormat = (_format, Interlaced);
            Interlocked.Increment(ref _formatVersion);
        }
        if (v.FourCC is not (NDIlib.FourCC_type_e.FourCC_type_UYVY or NDIlib.FourCC_type_e.FourCC_type_BGRA or NDIlib.FourCC_type_e.FourCC_type_BGRX))
            _log.LogWarning("NDI: FourCC inesperado {FourCC} en «{Source}»; se asume bgra.", v.FourCC, _sourceName);
        else if (ConvertsToUyvy(v.FourCC, v.xres))
            _log.LogInformation("NDI: «{Source}» manda {FourCC} (con alfa): se pasa a UYVY al recibir cada cuadro, sin el alfa, que no graba ninguna salida.",
                _sourceName, v.FourCC);
        if (v.frame_format_type == NDIlib.frame_format_type_e.frame_format_type_interleaved && !interlaced)
            _log.LogInformation("NDI: «{Source}» marca sus cuadros como entrelazados a {Rate:0.##} cuadros/s, más de lo que va un entrelazado " +
                "(30): se tratan como progresivos.", _sourceName, (double)FrameRateN / FrameRateD);
    }

    /// <summary>-pixel_format de FFmpeg con el que se sirve un FourCC NDI: uyvy422, también para el BGRA de una fuente con alfa, que
    /// se convierte al recibirlo (<see cref="ConvertsToUyvy"/>); bgra solo si no se puede (un ancho impar) o el FourCC es otro. Pura.</summary>
    internal static string PixelFormatFor(NDIlib.FourCC_type_e fourCC, int width)
        => fourCC == NDIlib.FourCC_type_e.FourCC_type_UYVY || ConvertsToUyvy(fourCC, width) ? "uyvy422" : "bgra";

    /// <summary>¿Se pasa a UYVY al recibirlo? El BGRA (o BGRX) de una fuente con alfa, si el ancho es par. Pura.</summary>
    internal static bool ConvertsToUyvy(NDIlib.FourCC_type_e fourCC, int width)
        => fourCC is NDIlib.FourCC_type_e.FourCC_type_BGRA or NDIlib.FourCC_type_e.FourCC_type_BGRX && BgraToUyvy.Supports(width);

    /// <summary>¿El formato entrante difiere del cacheado? (resolución, pixel o barrido). Pura y testeable. (N20.)</summary>
    internal static bool FormatDiffers(int newW, int newH, string newPix, bool newInterlaced, int oldW, int oldH, string oldPix, bool oldInterlaced)
        => newW != oldW || newH != oldH || !string.Equals(newPix, oldPix, StringComparison.Ordinal) || newInterlaced != oldInterlaced;

    private void OnAudio(ref NDIlib.audio_frame_v2_t a)
    {
        if (a.p_data == IntPtr.Zero || a.no_samples <= 0 || a.no_channels <= 0)
        {
            lock (_serve)
            {
                if (a.sample_rate > 0) SampleRate = a.sample_rate;
                if (a.no_channels > 0) Channels = a.no_channels;
            }
            return;
        }

        // NDI entrega audio float PLANAR; FFmpeg -f f32le espera INTERLEAVED: se convierte con el helper del SDK.
        int bytes = a.no_samples * a.no_channels * sizeof(float);
        IntPtr tmp = Marshal.AllocHGlobal(bytes);
        try
        {
            var dst = new NDIlib.audio_frame_interleaved_32f_t
            {
                sample_rate = a.sample_rate, no_channels = a.no_channels, no_samples = a.no_samples, p_data = tmp,
            };
            NDIlib.util_audio_to_interleaved_32f_v2(ref a, ref dst);
            var buf = ArrayPool<byte>.Shared.Rent(bytes);
            Marshal.Copy(tmp, buf, 0, bytes);
            lock (_serve) // el silencio y su formato los comparte con EndStream
            {
                if (a.sample_rate > 0) SampleRate = a.sample_rate;
                Channels = a.no_channels;
                if (_silence.Filling) _log.LogInformation("NDI: «{Source}» vuelve a traer audio.", _sourceName);
                _silence.OnAudio(DateTimeOffset.UtcNow);
                // Se guarda (para alinear el audio del próximo proceso con su vídeo) y se encola, salvo lo que el proceso recién
                // conectado aún no deba recibir: el audio de antes del instante de su primer cuadro.
                var block = new NdiFrame(buf, bytes);
                var start = SourceTime(a.timestamp);
                _aligner.Remember(start, block, a.no_samples, a.sample_rate, a.no_channels);
                var admitted = _aligner.Admit(start, block, a.no_samples);
                if (!ReferenceEquals(admitted, block)) block.Release();
                if (admitted is not null) _audio.Enqueue(admitted);
            }
        }
        finally { Marshal.FreeHGlobal(tmp); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        if (_recvLoop is not null) { try { await _recvLoop.ConfigureAwait(false); } catch { /* cancelación */ } }
        await _video.DisposeAsync().ConfigureAwait(false);
        await _audio.DisposeAsync().ConfigureAwait(false);
        if (_recv != IntPtr.Zero) { NdiRuntime.DestroyReceiver(_recv); _recv = IntPtr.Zero; }
        if (_weave is not null) { ArrayPool<byte>.Shared.Return(_weave); _weave = null; }
        _last?.Release();
        _last = null;
        try { _videoListener.Stop(); } catch { /* noop */ }
        try { _audioListener.Stop(); } catch { /* noop */ }
        _cts?.Dispose();
    }
}
