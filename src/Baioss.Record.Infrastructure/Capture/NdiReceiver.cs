using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
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
/// <para>Vídeo y audio se sirven en HILOS SEPARADOS con colas acotadas: FFmpeg abre sus entradas en serie y,
/// mientras abre el audio, deja de leer el vídeo; con un solo hilo el <c>Write</c> de vídeo se bloquearía y
/// el audio nunca se serviría (deadlock). Las colas descartan los frames más viejos si FFmpeg se atrasa.</para>
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

    private readonly Channel<Frame> _videoQueue =
        Channel.CreateBounded<Frame>(new BoundedChannelOptions(4) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Channel<Frame> _audioQueue =
        Channel.CreateBounded<Frame>(new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    private CancellationTokenSource? _cts;
    private Task? _recvLoop, _videoWriter, _audioWriter;
    private IntPtr _recv;
    private volatile NetworkStream? _videoStream;
    private volatile NetworkStream? _audioStream;
    private int _bytesPerPixel = 4; // se fija con el primer frame según el FourCC real (UYVY=2, BGRA=4)
    private volatile bool _present;           // ¿llega vídeo NDI ahora mismo? (para detectar pérdida en caliente)
    private DateTimeOffset _lastVideoUtc;     // marca del último frame de vídeo recibido
    private DateTimeOffset _lastPresenceLoss; // cuándo se perdió la presencia (para re-detectar formato si la caída fue larga)

    /// <summary>Se dispara al PERDER (false) o RECUPERAR (true) la presencia de vídeo NDI, para que la fuente
    /// actualice su <c>CurrentSignal</c> y el canal entre/salga de carta de ajuste sin esperar al watchdog. (C3.)</summary>
    public event Action<bool>? PresenceChanged;

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
            allow_video_fields = false,
        };
        _recv = NDIlib.recv_create_v3(ref settings);
        if (_recv == IntPtr.Zero) { _log.LogWarning("NDI: no se pudo crear el receptor para «{Source}».", _sourceName); return false; }

        _videoListener.Start();
        _audioListener.Start();
        VideoPort = ((IPEndPoint)_videoListener.LocalEndpoint).Port;
        AudioPort = ((IPEndPoint)_audioListener.LocalEndpoint).Port;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        _ = AcceptAsync(_videoListener, s => _videoStream = s, token);
        _ = AcceptAsync(_audioListener, s => _audioStream = s, token);
        _recvLoop = Task.Run(() => ReceiveLoop(token), token);
        _videoWriter = Task.Run(() => WriteLoop(_videoQueue.Reader, () => _videoStream, token), token);
        _audioWriter = Task.Run(() => WriteLoop(_audioQueue.Reader, () => _audioStream, token), token);

        var done = await Task.WhenAny(_firstVideo.Task, Task.Delay(firstFrameTimeout, ct)).ConfigureAwait(false);
        if (done != _firstVideo.Task) { _log.LogWarning("NDI: «{Source}» no entregó vídeo en {T}.", _sourceName, firstFrameTimeout); return false; }
        _log.LogInformation("NDI: «{Source}» conectada — {W}x{H} @ {N}/{D} {Pix}, audio {SR}Hz {Ch}ch.",
            _sourceName, Width, Height, FrameRateN, FrameRateD, VideoPixelFormat, SampleRate, Channels);
        return true;
    }

    // FFmpeg (cliente) se conecta a cada puerto al arrancar; ante reconexión (reinicio al alternar grabación)
    // se vuelve a aceptar el nuevo cliente.
    private async Task AcceptAsync(TcpListener listener, Action<NetworkStream?> assign, CancellationToken ct)
    {
        // Mantiene el TcpClient ACTIVO para disponerlo en la próxima reconexión (y al cancelar). Antes, cada
        // reconexión de FFmpeg (toggle de grabación / slate / respawn) dejaba el socket+stream anterior sin
        // cerrar: en 24/7 acumulaba handles y podía agotar puertos/handles del SO. (Auditoría 24/7, A8/#29/#37.)
        TcpClient? current = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var next = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                    next.NoDelay = true;
                    assign(next.GetStream());              // los writes nuevos van YA al socket nuevo
                    var old = current; current = next;
                    try { old?.Dispose(); } catch { /* ya cerrado */ } // cierra el cliente anterior (no fugar el socket)
                    _log.LogDebug("NDI: FFmpeg conectó a un socket de «{Source}».", _sourceName);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { _log.LogDebug(ex, "NDI: re-aceptando conexión de FFmpeg."); }
            }
        }
        finally { try { current?.Dispose(); } catch { /* noop */ } } // al cancelar, cierra el cliente vivo
    }

    // Saca buffers de la cola y los escribe al socket de FFmpeg. Un loop por flujo (vídeo / audio): así el
    // bloqueo de uno (FFmpeg dejó de leer) no afecta al otro. Cada buffer se DEVUELVE al pool tras usarlo
    // (escrito o no): es el único punto de retorno, así que no hay doble-free. Los frames que la cola descarta
    // por saturación (DropOldest) no pasan por aquí y los recoge el GC —pérdida mínima y sin corrupción.
    private static async Task WriteLoop(ChannelReader<Frame> reader, Func<NetworkStream?> getStream, CancellationToken ct)
    {
        try
        {
            await foreach (var frame in reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    var s = getStream();
                    if (s is not null) // si FFmpeg aún no conectó (o se desconectó) se descarta el frame
                        await s.WriteAsync(frame.Buffer.AsMemory(0, frame.Length), ct).ConfigureAwait(false);
                }
                catch { /* FFmpeg cerró el socket: el siguiente getStream() reflejará la reconexión */ }
                finally { ArrayPool<byte>.Shared.Return(frame.Buffer); }
            }
        }
        catch (OperationCanceledException) { /* cierre */ }
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
            Width = v.xres; Height = v.yres;
            if (v.frame_rate_N > 0 && v.frame_rate_D > 0) { FrameRateN = v.frame_rate_N; FrameRateD = v.frame_rate_D; }
            // El FourCC real decide el -pixel_format y los bytes por píxel. Con UYVY_BGRA casi siempre es UYVY
            // (16 bpp); BGRA solo si la fuente lleva alfa. Cualquier otro caso → bgra como red de seguridad.
            switch (v.FourCC)
            {
                case NDIlib.FourCC_type_e.FourCC_type_UYVY:
                    VideoPixelFormat = "uyvy422"; _bytesPerPixel = 2; break;
                case NDIlib.FourCC_type_e.FourCC_type_BGRA:
                case NDIlib.FourCC_type_e.FourCC_type_BGRX:
                    VideoPixelFormat = "bgra"; _bytesPerPixel = 4; break;
                default:
                    VideoPixelFormat = "bgra"; _bytesPerPixel = 4;
                    _log.LogWarning("NDI: FourCC inesperado {FourCC} en «{Source}»; se asume bgra.", v.FourCC, _sourceName);
                    break;
            }
            _firstVideo.TrySetResult(true);
        }

        // Notifica la recuperación AHORA, con el formato ya re-fijado (ver nota arriba): así CurrentSignal
        // refleja la resolución/tasa reales del nuevo frame y no un 0×0 transitorio. (C3 + #32.)
        if (recovered) { try { PresenceChanged?.Invoke(true); } catch { /* no romper la recepción */ } }

        int rowBytes = v.xres * _bytesPerPixel;
        int size = v.yres * rowBytes;
        var buf = ArrayPool<byte>.Shared.Rent(size);
        if (v.line_stride_in_bytes == rowBytes)
            Marshal.Copy(v.p_data, buf, 0, size);                 // sin relleno: copia contigua
        else
            for (int y = 0; y < v.yres; y++)                      // con relleno: quita el padding por fila
                Marshal.Copy(v.p_data + y * v.line_stride_in_bytes, buf, y * rowBytes, rowBytes);

        if (!_videoQueue.Writer.TryWrite(new Frame(buf, size)))
            ArrayPool<byte>.Shared.Return(buf);                   // cola completada (cierre): no se encola
    }

    private void OnAudio(ref NDIlib.audio_frame_v2_t a)
    {
        if (a.sample_rate > 0) SampleRate = a.sample_rate;
        if (a.no_channels > 0) Channels = a.no_channels;
        if (a.p_data == IntPtr.Zero || a.no_samples <= 0 || a.no_channels <= 0) return;

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
            if (!_audioQueue.Writer.TryWrite(new Frame(buf, bytes)))
                ArrayPool<byte>.Shared.Return(buf);
        }
        finally { Marshal.FreeHGlobal(tmp); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is not null) await _cts.CancelAsync().ConfigureAwait(false);
        _videoQueue.Writer.TryComplete();
        _audioQueue.Writer.TryComplete();
        foreach (var t in new[] { _recvLoop, _videoWriter, _audioWriter })
            if (t is not null) { try { await t.ConfigureAwait(false); } catch { /* cancelación */ } }
        if (_recv != IntPtr.Zero) { NDIlib.recv_destroy(_recv); _recv = IntPtr.Zero; }
        try { _videoListener.Stop(); } catch { /* noop */ }
        try { _audioListener.Stop(); } catch { /* noop */ }
        _cts?.Dispose();
    }
}
