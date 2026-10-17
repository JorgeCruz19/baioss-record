using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using NewTek;
using NewTek.NDI;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Recibe vídeo + audio de una fuente NDI con el SDK NewTek y los SIRVE por dos sockets TCP loopback como
/// flujos CRUDOS, para que FFmpeg los lea como dos entradas (vídeo <c>-f rawvideo</c>, audio <c>-f f32le</c>).
/// Así una fuente NDI entra en el pipeline FFmpeg (preview + grabación) sin necesitar un FFmpeg compilado con NDI.
///
/// <para>VÍDEO EN FORMATO NATIVO: se pide a NDI <c>UYVY_BGRA</c>, que entrega <b>UYVY</b> (YUV 4:2:2, 16 bpp)
/// salvo que la fuente tenga alfa (entonces BGRA, 32 bpp). UYVY es la MITAD de bytes que BGRA —menos copia,
/// menos tráfico de socket— y, al ser ya YUV, FFmpeg lo convierte a NV12/4:2:0 con un <c>swscale</c> mucho más
/// barato que desde RGB. El <see cref="VideoPixelFormat"/> resultante (uyvy422 / bgra) lo lee la fuente para el
/// <c>-pixel_format</c>. Los buffers de frame se alquilan de <see cref="ArrayPool{T}"/> y se devuelven tras
/// escribirlos, para no generar basura (un frame es de varios MB; a 30-60 fps el GC se dispararía).</para>
///
/// <para>Vídeo y audio se sirven en HILOS SEPARADOS con colas acotadas (<see cref="NdiFeed{T}"/>): FFmpeg abre sus entradas
/// en serie y, mientras abre el audio, deja de leer el vídeo; con un solo hilo el <c>Write</c> de vídeo se bloquearía y
/// el audio nunca se serviría (deadlock). Las colas descartan los frames más viejos si FFmpeg se atrasa.</para>
///
/// <para>Al retirar un proceso que graba, <see cref="EndStream"/> le cierra las dos entradas en el mismo instante para que
/// termine por sí mismo con todo lo que ya llegó: FFmpeg lee el audio por detrás del vídeo y, con la «q», lo que quedaba sin
/// leer se perdía (el archivo acababa con el audio hasta 0,9 s más corto que el vídeo).</para>
///
/// <para>Una fuente SIN audio (o que lo pierde) recibe silencio al ritmo de sus cuadros (<see cref="NdiAudioGapFiller"/>):
/// sin datos en la entrada de audio FFmpeg no arrancaba y el canal no tenía ni preview ni grabación.</para>
/// </summary>
public sealed class NdiReceiver : IAsyncDisposable
{
    /// <summary>Buffer alquilado del <see cref="ArrayPool{T}"/> + su longitud útil (Rent puede dar un array mayor).</summary>
    private readonly record struct Frame(byte[] Buffer, int Length);

    private readonly string _sourceName;
    private readonly ILogger _log;
    private readonly TcpListener _videoListener = new(IPAddress.Loopback, 0);
    private readonly TcpListener _audioListener = new(IPAddress.Loopback, 0);
    private readonly TaskCompletionSource<bool> _firstVideo = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Cada búfer vuelve al pool al escribirlo, al descartarlo la cola por saturación o si ya no se puede encolar (cierre).
    private readonly NdiFeed<Frame> _video = new(4, static f => f.Buffer.AsMemory(0, f.Length), static f => ArrayPool<byte>.Shared.Return(f.Buffer));
    private readonly NdiFeed<Frame> _audio = new(16, static f => f.Buffer.AsMemory(0, f.Length), static f => ArrayPool<byte>.Shared.Return(f.Buffer));

    private CancellationTokenSource? _cts;
    private Task? _recvLoop;
    private IntPtr _recv;
    private int _bytesPerPixel = 4; // se fija con el primer frame según el FourCC real (UYVY=2, BGRA=4)
    private volatile bool _present;           // ¿llega vídeo NDI ahora mismo? (para detectar pérdida en caliente)
    private DateTimeOffset _lastVideoUtc;     // marca del último frame de vídeo recibido
    private DateTimeOffset _lastPresenceLoss; // cuándo se perdió la presencia (para re-detectar formato si la caída fue larga)
    // Silencio cuando la fuente no trae audio (o deja de traerlo): sin datos en la entrada de audio, FFmpeg no arranca.
    private readonly NdiAudioGapFiller _silence = new();
    // Campos sueltos (frame_format_type_field_0/1): el cuadro que se está tejiendo, con el campo superior ya copiado.
    private byte[]? _weave;
    private int _weaveW, _weaveH;
    private NDIlib.FourCC_type_e _weaveFourCC;
    private bool _weaveTop;

    /// <summary>Se dispara al PERDER (false) o RECUPERAR (true) la presencia de vídeo NDI, para que la fuente
    /// actualice su <c>CurrentSignal</c> y el canal entre/salga de carta de ajuste sin esperar al watchdog. (C3.)</summary>
    public event Action<bool>? PresenceChanged;

    /// <summary>Se dispara cuando la fuente cambia de formato (resolución/pixel) EN CALIENTE sin perder presencia
    /// (p. ej. vMix 720→1080 al vuelo): el receptor ya sirve el formato nuevo, pero el FFmpeg en marcha sigue con
    /// el <c>-video_size</c> viejo hasta que el pipeline se RECONSTRUYA. La fuente lo usa para actualizar
    /// <c>CurrentSignal</c> (y avisar). No se fuerza el rebuild aquí (sería una carrera con el slate). (N20/#32.)</summary>
    public event Action? FormatChanged;

    /// <summary>Sin frames de vídeo durante este tiempo ⇒ se considera pérdida de señal NDI.</summary>
    public TimeSpan PresenceTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Si la pérdida de presencia supera este umbral, al recuperar el vídeo se RE-DETECTA el formato
    /// (resolución/tasa/pixel) por si la fuente cambió mientras no emitía. Evita servir frames del nuevo formato
    /// con los parámetros viejos cacheados del primer frame histórico. (Auditoría 24/7, #32.)</summary>
    public TimeSpan FormatResetThreshold { get; init; } = TimeSpan.FromSeconds(5);

    public NdiReceiver(string sourceName, ILogger log)
    {
        _sourceName = sourceName;
        _log = log;
    }

    public int VideoPort { get; private set; }
    public int AudioPort { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    /// <summary><c>-pixel_format</c> de FFmpeg para el vídeo, según el FourCC real del primer frame (uyvy422 / bgra).</summary>
    public string VideoPixelFormat { get; private set; } = "uyvy422";
    public int FrameRateN { get; private set; } = 30000;
    public int FrameRateD { get; private set; } = 1001;
    /// <summary>Los cuadros traen dos campos entretejidos (NDI <c>frame_format_type_interleaved</c>, el superior primero): un
    /// 1080i. La tasa de <see cref="FrameRateN"/>/<see cref="FrameRateD"/> es entonces la de cuadro (30000/1001 en 1080i59.94).</summary>
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
        _ = AcceptAsync(_videoListener, _video, token);
        _ = AcceptAsync(_audioListener, _audio, token);
        _recvLoop = Task.Run(() => ReceiveLoop(token), token);

        var done = await Task.WhenAny(_firstVideo.Task, Task.Delay(firstFrameTimeout, ct)).ConfigureAwait(false);
        if (done != _firstVideo.Task) { _log.LogWarning("NDI: «{Source}» no entregó vídeo en {T}.", _sourceName, firstFrameTimeout); return false; }
        _log.LogInformation("NDI: «{Source}» conectada — {W}x{H} @ {N}/{D} {Scan} {Pix}, audio {SR}Hz {Ch}ch.",
            _sourceName, Width, Height, FrameRateN, FrameRateD, Interlaced ? "entrelazado" : "progresivo", VideoPixelFormat, SampleRate, Channels);
        return true;
    }

    // FFmpeg (cliente) se conecta a cada puerto al arrancar; ante reconexión (reinicio al alternar grabación)
    // se vuelve a aceptar el nuevo cliente: el flujo le sirve desde ya y cierra el anterior (ver NdiFeed.Attach).
    private async Task AcceptAsync(TcpListener listener, NdiFeed<Frame> feed, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var next = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                next.NoDelay = true;
                feed.Attach(next);
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
    /// </summary>
    public bool EndStream()
    {
        bool video = _video.End();
        bool audio = _audio.End();
        return video && audio;
    }

    private void ReceiveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var v = new NDIlib.video_frame_v2_t();
            var a = new NDIlib.audio_frame_v2_t();
            var m = new NDIlib.metadata_frame_t();
            switch (NDIlib.recv_capture_v2(_recv, ref v, ref a, ref m, 100))
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

            // Detección de PÉRDIDA de señal NDI: si no llega vídeo en PresenceTimeout, notifica una sola vez
            // (la RECUPERACIÓN la notifica OnVideo al volver el primer frame). recv_capture_v2 usa timeout de
            // 100 ms, así que este bucle sigue girando aunque la fuente deje de emitir. (Auditoría 24/7, C3.)
            if (_present && DateTimeOffset.UtcNow - _lastVideoUtc > PresenceTimeout)
            {
                _present = false;
                _lastPresenceLoss = DateTimeOffset.UtcNow; // sella la caída para decidir si re-detectar formato al volver (#32)
                try { PresenceChanged?.Invoke(false); } catch { /* no romper el bucle de recepción */ }
            }
        }
    }

    private void OnVideo(ref NDIlib.video_frame_v2_t v)
    {
        if (v.xres <= 0 || v.yres <= 0 || v.p_data == IntPtr.Zero) return;
        // Campos sueltos: se espera a tener los dos y se entregan tejidos, como un cuadro entrelazado.
        byte[]? woven = null;
        int frameHeight = v.yres;
        bool interlaced = IsInterleaved(v.frame_format_type);
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
            ApplyFormat(v.xres, frameHeight, v.FourCC, v.frame_rate_N, v.frame_rate_D, interlaced);
            _firstVideo.TrySetResult(true);
        }
        else if (FormatDiffers(v.xres, frameHeight, PixelFormatFor(v.FourCC), interlaced, Width, Height, VideoPixelFormat, Interlaced))
        {
            // CAMBIO DE FORMATO EN CALIENTE, sin hueco de presencia (N20): la fuente cambió resolución/pixel/barrido al
            // vuelo (p. ej. vMix 720→1080). Re-fija el formato para que la copia de frame y BuildInputArguments
            // sean correctos, AVISA (el FFmpeg en marcha sigue con el -video_size viejo → imagen corrupta hasta
            // reconstruir el pipeline: rebind o salida de slate) y notifica FormatChanged para que la fuente
            // actualice CurrentSignal. No se fuerza el rebuild aquí: sería una carrera con el slate. (#32/N20.)
            int oldW = Width, oldH = Height; string oldPix = VideoPixelFormat; bool oldInterlaced = Interlaced;
            ApplyFormat(v.xres, frameHeight, v.FourCC, v.frame_rate_N, v.frame_rate_D, interlaced);
            _log.LogWarning("NDI: «{Source}» cambió de formato en caliente ({OW}x{OH} {OP}{OI} → {W}x{H} {P}{I}); el pipeline en " +
                "marcha seguirá con el formato anterior hasta reconstruirse (rebind o salida de carta de ajuste).",
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
            if (v.line_stride_in_bytes == rowBytes)
                Marshal.Copy(v.p_data, buf, 0, size);             // sin relleno: copia contigua
            else
                for (int y = 0; y < v.yres; y++)                  // con relleno: quita el padding por fila
                    Marshal.Copy(v.p_data + y * v.line_stride_in_bytes, buf, y * rowBytes, rowBytes);
        }

        _video.Enqueue(new Frame(buf, size));

        // Sin audio de la fuente: el silencio que le toca a este cuadro (y, al detectarlo, a los que ya pasaron sin audio).
        bool wasFilling = _silence.Filling;
        int silent = _silence.OnVideo(DateTimeOffset.UtcNow, SampleRate, FrameRateN, FrameRateD);
        if (silent > 0) EnqueueSilence(silent);
        if (_silence.Filling && !wasFilling)
            _log.LogInformation("NDI: «{Source}» no trae audio; se envía silencio ({SR} Hz, {Ch} canales) para que el canal previsualice y grabe igual.",
                _sourceName, SampleRate, Channels);
    }

    /// <summary>Encola <paramref name="samples"/> muestras de silencio f32le en el formato que sirve el receptor.</summary>
    private void EnqueueSilence(int samples)
    {
        int bytes = samples * Math.Max(1, Channels) * sizeof(float);
        var buf = ArrayPool<byte>.Shared.Rent(bytes);
        Array.Clear(buf, 0, bytes); // los búferes del pool vuelven con datos viejos
        _audio.Enqueue(new Frame(buf, bytes));
    }

    /// <summary>¿El cuadro trae los dos campos entretejidos (el superior primero, según NDI)?</summary>
    internal static bool IsInterleaved(NDIlib.frame_format_type_e type) => type == NDIlib.frame_format_type_e.frame_format_type_interleaved;

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
        int rowBytes = v.xres * (PixelFormatFor(v.FourCC) == "uyvy422" ? 2 : 4);
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

    private static void CopyField(ref NDIlib.video_frame_v2_t v, byte[] frame, int rowBytes, int firstLine)
    {
        for (int y = 0; y < v.yres; y++)
            Marshal.Copy(v.p_data + y * v.line_stride_in_bytes, frame, (2 * y + firstLine) * rowBytes, rowBytes);
    }

    /// <summary>Fija el formato (resolución/tasa/pixel/bytes-por-píxel/barrido) del cuadro que se sirve. Lo comparten el
    /// primer frame, la re-detección tras pérdida prolongada (#32) y el cambio de formato en caliente (N20). El FourCC real
    /// decide el -pixel_format: con UYVY_BGRA casi siempre UYVY (16 bpp); BGRA solo si la fuente lleva alfa.</summary>
    private void ApplyFormat(int width, int height, NDIlib.FourCC_type_e fourCC, int rateN, int rateD, bool interlaced)
    {
        Width = width; Height = height;
        Interlaced = interlaced;
        if (rateN > 0 && rateD > 0) { FrameRateN = rateN; FrameRateD = rateD; }
        switch (fourCC)
        {
            case NDIlib.FourCC_type_e.FourCC_type_UYVY:
                VideoPixelFormat = "uyvy422"; _bytesPerPixel = 2; break;
            case NDIlib.FourCC_type_e.FourCC_type_BGRA:
            case NDIlib.FourCC_type_e.FourCC_type_BGRX:
                VideoPixelFormat = "bgra"; _bytesPerPixel = 4; break;
            default:
                VideoPixelFormat = "bgra"; _bytesPerPixel = 4;
                _log.LogWarning("NDI: FourCC inesperado {FourCC} en «{Source}»; se asume bgra.", fourCC, _sourceName);
                break;
        }
    }

    /// <summary>-pixel_format de FFmpeg para un FourCC NDI (uyvy422 / bgra). Pura.</summary>
    private static string PixelFormatFor(NDIlib.FourCC_type_e fourCC) => fourCC switch
    {
        NDIlib.FourCC_type_e.FourCC_type_UYVY => "uyvy422",
        NDIlib.FourCC_type_e.FourCC_type_BGRA or NDIlib.FourCC_type_e.FourCC_type_BGRX => "bgra",
        _ => "bgra",
    };

    /// <summary>¿El formato entrante difiere del cacheado? (resolución, pixel o barrido). Pura y testeable. (N20.)</summary>
    internal static bool FormatDiffers(int newW, int newH, string newPix, bool newInterlaced, int oldW, int oldH, string oldPix, bool oldInterlaced)
        => newW != oldW || newH != oldH || !string.Equals(newPix, oldPix, StringComparison.Ordinal) || newInterlaced != oldInterlaced;

    private void OnAudio(ref NDIlib.audio_frame_v2_t a)
    {
        if (a.sample_rate > 0) SampleRate = a.sample_rate;
        if (a.no_channels > 0) Channels = a.no_channels;
        if (a.p_data == IntPtr.Zero || a.no_samples <= 0 || a.no_channels <= 0) return;
        if (_silence.Filling) _log.LogInformation("NDI: «{Source}» vuelve a traer audio.", _sourceName);
        _silence.OnAudio(DateTimeOffset.UtcNow);

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
            _audio.Enqueue(new Frame(buf, bytes));
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
        try { _videoListener.Stop(); } catch { /* noop */ }
        try { _audioListener.Stop(); } catch { /* noop */ }
        _cts?.Dispose();
    }
}
