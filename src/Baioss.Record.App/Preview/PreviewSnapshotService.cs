using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Baioss.Record.Application.Channels;
using Baioss.Record.Infrastructure.Preview;

namespace Baioss.Record.App.Preview;

/// <summary>
/// Imágenes JPEG de baja resolución del preview de cada canal, para el panel web: la instantánea suelta
/// (<c>GET /api/v1/channels/{id}/preview.jpg</c>) y el flujo por WebSocket (<c>/ws/preview/{id}</c>), que no es más que
/// pedir imágenes a su ritmo. Pensado para NO costar nada cuando nadie mira y muy poco cuando sí:
/// <list type="bullet">
///   <item><b>Bajo demanda.</b> Cada petición pide UNA captura: el siguiente frame del preview se reduce (promedio de
///   área) y se codifica. Sin peticiones no hay suscripción al preview, ni copias, ni codificación.</item>
///   <item><b>Compartido.</b> Varias peticiones a la vez del mismo canal comparten la misma captura, y una imagen más
///   reciente que la ventana que pide el cliente se reutiliza (dos navegadores abiertos no duplican el trabajo).</item>
///   <item><b>Acotado.</b> Un único hilo codifica (prioridad baja), de uno en uno: el panel web nunca compite con la
///   grabación por la CPU, por muchos canales o clientes que haya. Los buffers de captura salen de un pool: a 10
///   imágenes por segundo no se genera basura de ~230 KB por cuadro.</item>
/// </list>
/// El frame llega en un buffer REUTILIZADO por el motor (anillo de 3): se reduce DENTRO del propio evento, en ~1 ms, y no
/// se retiene. El manejador jamás lanza: corre en el hilo lector del preview y una excepción ahí pararía la imagen.
/// </summary>
public sealed class PreviewSnapshotService : IChannelSnapshotProvider, IDisposable
{
    public const int DefaultWidth = 320;
    /// <summary>Anchos admitidos: se cuantiza lo pedido para que clientes distintos compartan la misma imagen.</summary>
    private static readonly int[] Widths = { 160, 240, 320, 480, 640 };

    private const int FrameWaitMs = 1500;     // cuánto se espera al siguiente frame antes de rendirse
    private const int IdleMs = 5000;          // sin peticiones en este tiempo → el canal suelta el preview
    private const int JpegQuality = 60;

    private readonly PreviewCatalog _previews;
    private readonly ConcurrentDictionary<Guid, Tap> _taps = new();
    private readonly BlockingCollection<Action> _jobs = new();
    private readonly Thread _encoder;
    private volatile bool _disposed;

    public PreviewSnapshotService(PreviewCatalog previews)
    {
        _previews = previews;
        // Hilo propio (no el pool): los objetos de imagen de WPF crean un Dispatcher por hilo que los use; con uno
        // dedicado hay exactamente uno, y la codificación queda serializada y en prioridad baja.
        _encoder = new Thread(EncodeLoop) { IsBackground = true, Name = "preview-snapshots", Priority = ThreadPriority.BelowNormal };
        _encoder.Start();
    }

    public Task<byte[]?> GetJpegAsync(Guid channelId, int width, int maxAgeMs = 400, CancellationToken ct = default)
    {
        if (_disposed) return Task.FromResult<byte[]?>(null);
        var source = _previews.For(channelId);
        if (source is null) return Task.FromResult<byte[]?>(null); // canal simulado o entrada reasignándose
        var tap = _taps.GetOrAdd(channelId, _ => new Tap(this));
        // La captura es COMPARTIDA entre peticiones: la cancelación de un cliente (cerró la pestaña) solo debe soltar
        // SU espera, no tumbar la captura de los demás → WaitAsync sobre la tarea común, no un token dentro de ella.
        return tap.GetAsync(source, Quantize(width), Math.Max(0, maxAgeMs)).WaitAsync(ct);
    }

    internal static int Quantize(int width)
    {
        int best = Widths[0];
        foreach (int w in Widths)
            if (Math.Abs(w - width) < Math.Abs(best - width)) best = w;
        return best;
    }

    private Task<byte[]> EncodeAsync(Capture capture)
    {
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            _jobs.Add(() =>
            {
                try { tcs.TrySetResult(EncodeJpeg(capture)); }
                catch (Exception ex) { tcs.TrySetException(ex); }
                finally { capture.Release(); }
            });
        }
        catch (InvalidOperationException) { capture.Release(); tcs.TrySetCanceled(); } // servicio cerrándose
        return tcs.Task;
    }

    private void EncodeLoop()
    {
        try { foreach (var job in _jobs.GetConsumingEnumerable()) job(); }
        catch (ObjectDisposedException) { /* cierre */ }
    }

    private static byte[] EncodeJpeg(Capture c)
    {
        // Bgr32: ignora el cuarto byte (el preview llega como BGRA con alfa sin significado). Create COPIA los píxeles,
        // así que el buffer (del pool, posiblemente más largo de lo necesario) se puede devolver en cuanto termine.
        var bitmap = BitmapSource.Create(c.Width, c.Height, 96, 96, PixelFormats.Bgr32, null, c.Bgra, c.Width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var ms = new MemoryStream(32 * 1024);
        encoder.Save(ms);
        return ms.ToArray();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var tap in _taps.Values) tap.Unbind();
        _taps.Clear();
        _jobs.CompleteAdding();
    }

    /// <summary>Frame ya reducido, en un buffer ALQUILADO al pool (puede ser más largo que Width×Height×4).</summary>
    private sealed class Capture(byte[] bgra, int width, int height)
    {
        private int _released;
        public byte[] Bgra { get; } = bgra;
        public int Width { get; } = width;
        public int Height { get; } = height;
        public void Release() { if (Interlocked.Exchange(ref _released, 1) == 0) ArrayPool<byte>.Shared.Return(Bgra); }
    }

    /// <summary>Toma del preview de UN canal: se suscribe solo mientras hay peticiones y captura solo cuando se le pide.</summary>
    private sealed class Tap(PreviewSnapshotService owner)
    {
        private readonly object _gate = new();
        private IChannelPreviewSource? _source;
        private TaskCompletionSource<Capture>? _waiting; // quien espera el siguiente frame
        private Task<byte[]?>? _inflight;                // captura+codificación en curso, compartida
        private byte[]? _jpeg;                           // última imagen servida
        private long _jpegAt;
        private int _jpegWidth;
        private int _width = DefaultWidth;
        private long _lastRequest;
        private volatile bool _want;

        public Task<byte[]?> GetAsync(IChannelPreviewSource source, int width, int maxAgeMs)
        {
            lock (_gate)
            {
                Volatile.Write(ref _lastRequest, Environment.TickCount64);
                Bind(source);
                if (_jpeg is not null && _jpegWidth == width && Environment.TickCount64 - _jpegAt < maxAgeMs)
                    return Task.FromResult<byte[]?>(_jpeg);
                if (_inflight is { IsCompleted: false } && _width == width) return _inflight;
                _width = width;
                // Arranca DENTRO del candado (Monitor es reentrante): _want queda puesto antes de soltarlo, así un
                // frame que llegue justo después ya ve la petición y UnbindIfIdle no suelta el preview a destiempo.
                return _inflight = CaptureAndEncodeAsync(width);
            }
        }

        private async Task<byte[]?> CaptureAndEncodeAsync(int width)
        {
            var tcs = new TaskCompletionSource<Capture>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) { _waiting = tcs; _want = true; }
            try
            {
                var first = await Task.WhenAny(tcs.Task, Task.Delay(FrameWaitMs)).ConfigureAwait(false);
                if (first != tcs.Task)
                {
                    // No llegó ningún frame a tiempo (el proceso se está reiniciando, la fuente no entrega): se devuelve
                    // la última imagen conocida —mejor una foto de hace un segundo que un hueco— o nada si no hay.
                    lock (_gate) { _want = false; if (ReferenceEquals(_waiting, tcs)) _waiting = null; }
                    // Si el frame llegó justo en la carrera con el plazo, su buffer vuelve al pool.
                    _ = tcs.Task.ContinueWith(t => t.Result.Release(), TaskContinuationOptions.OnlyOnRanToCompletion);
                    lock (_gate) return _jpeg;
                }

                byte[] jpeg = await owner.EncodeAsync(await tcs.Task.ConfigureAwait(false)).ConfigureAwait(false);
                lock (_gate) { _jpeg = jpeg; _jpegAt = Environment.TickCount64; _jpegWidth = width; }
                return jpeg;
            }
            catch
            {
                // Codificador cerrándose o fallo de imagen: la última conocida, o nada. Nunca una excepción al cliente.
                lock (_gate) { _want = false; return _jpeg; }
            }
        }

        /// <summary>(Re)suscribe al preview vigente: una reasignación de entrada cambia la instancia de la fuente.</summary>
        private void Bind(IChannelPreviewSource source)
        {
            if (ReferenceEquals(_source, source)) return;
            if (_source is not null) _source.FrameReady -= OnFrame;
            _source = source;
            _jpeg = null; // la imagen de la entrada anterior ya no vale
            source.FrameReady += OnFrame;
        }

        public void Unbind()
        {
            lock (_gate)
            {
                if (_source is not null) _source.FrameReady -= OnFrame;
                _source = null;
            }
        }

        /// <summary>Suelta el preview si de verdad nadie pide (se re-comprueba BAJO el candado: una petición puede
        /// haber llegado entre la lectura sin candado de OnFrame y este punto).</summary>
        private void UnbindIfIdle()
        {
            lock (_gate)
            {
                if (_want || Environment.TickCount64 - Volatile.Read(ref _lastRequest) <= IdleMs) return;
                if (_source is not null) _source.FrameReady -= OnFrame;
                _source = null;
            }
        }

        // Corre en el hilo lector del preview, a la cadencia de la fuente: la ruta normal (nadie pide) es una lectura
        // volátil y un return. NUNCA debe lanzar.
        private void OnFrame(object? sender, PreviewFrame frame)
        {
            byte[]? rented = null;
            try
            {
                if (!_want)
                {
                    // Sin peticiones recientes: se suelta el preview (coste cero hasta que alguien vuelva a pedir).
                    if (Environment.TickCount64 - Volatile.Read(ref _lastRequest) > IdleMs) UnbindIfIdle();
                    return;
                }

                var (w, h) = PreviewDownscaler.TargetSize(frame.Width, frame.Height, _width);
                int bytes = w * h * 4;
                rented = ArrayPool<byte>.Shared.Rent(bytes);
                PreviewDownscaler.Downscale(frame.Bgra, frame.Width, frame.Height, frame.Stride, rented.AsSpan(0, bytes), w, h);

                TaskCompletionSource<Capture>? waiting;
                lock (_gate) { waiting = _waiting; _waiting = null; _want = false; }
                if (waiting is null) return; // quien esperaba se rindió (plazo): el buffer vuelve al pool en el finally

                var capture = new Capture(rented, w, h);
                rented = null; // ahora es de la captura: lo devuelve quien la codifique (o la carrera con el plazo)
                if (!waiting.TrySetResult(capture)) capture.Release();
            }
            catch { /* una instantánea fallida no puede afectar al preview de la aplicación */ }
            finally { if (rented is not null) ArrayPool<byte>.Shared.Return(rented); }
        }
    }
}
