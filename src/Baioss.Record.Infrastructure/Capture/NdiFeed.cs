using System.Net.Sockets;
using System.Threading.Channels;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Un flujo crudo de una fuente NDI (el vídeo o el audio) servido al proceso de FFmpeg conectado a su socket: una cola acotada
/// que descarta lo más viejo si FFmpeg se atrasa y un escritor que la vuelca al socket. Cada flujo tiene los suyos, así que el
/// bloqueo de uno (FFmpeg dejó de leerlo) no frena al otro.
///
/// <para>FIN DE FLUJO (<see cref="End"/>): lo encolado hasta ese instante se le entrega al proceso conectado y después se le
/// cierra el socket (EOF); lo que llega después ya es del proceso siguiente. Así FFmpeg termina por sí mismo con todo lo que
/// llegó, en vez de cortarlo con la «q» y perder lo que aún no había leído.</para>
///
/// <para>ENMARCADO (opcional): un flujo en un contenedor (el vídeo en NUT, con marcas) le da a cada conexión su
/// <see cref="INdiFeedFraming{T}"/>, que pone delante de cada elemento lo que ese proceso necesita (sus cabeceras la primera vez)
/// y puede decidir no escribirle uno. Sin él, los datos tal cual (el audio en crudo).</para>
/// </summary>
/// <typeparam name="T">Lo que se encola (un búfer y su longitud útil).</typeparam>
internal sealed class NdiFeed<T> : IAsyncDisposable
{
    /// <summary>El proceso de FFmpeg conectado: su socket y cómo se le enmarca el flujo.</summary>
    private sealed class Connection(TcpClient client, NetworkStream stream, INdiFeedFraming<T>? framing)
    {
        public TcpClient Client { get; } = client;
        public NetworkStream Stream { get; } = stream;
        public INdiFeedFraming<T>? Framing { get; } = framing;
    }

    /// <summary>La cola de un proceso de FFmpeg (el conectado, o el siguiente si aún no conectó), su conexión y su escritor.</summary>
    private sealed class Pipe(Channel<T> queue)
    {
        public Channel<T> Queue { get; } = queue;
        public volatile Connection? Connection;
        public Task Writer = Task.CompletedTask;
    }

    private readonly int _capacity;
    private readonly Func<T, ReadOnlyMemory<byte>> _data;
    private readonly Action<T> _release;
    private readonly Func<INdiFeedFraming<T>>? _framing;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    // Terminadas: su socket se cierra del todo al conectar el proceso siguiente (o al disponer), como el de un proceso relanzado.
    private readonly List<Pipe> _ended = new();
    private volatile Pipe _current;
    private bool _disposed;

    /// <param name="capacity">Elementos que caben en la cola; al llenarse se descarta el más viejo.</param>
    /// <param name="data">Los bytes que se escriben de cada elemento.</param>
    /// <param name="release">Suelta un elemento ya escrito, descartado o rechazado (devuelve su búfer al pool).</param>
    /// <param name="framing">Crea el enmarcado de cada proceso que conecta (null: los datos tal cual).</param>
    public NdiFeed(int capacity, Func<T, ReadOnlyMemory<byte>> data, Action<T> release, Func<INdiFeedFraming<T>>? framing = null)
    {
        _capacity = capacity;
        _data = data;
        _release = release;
        _framing = framing;
        _current = Open();
    }

    private Pipe Open()
    {
        var pipe = new Pipe(Channel.CreateBounded<T>(
            new BoundedChannelOptions(_capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            item => { Interlocked.Increment(ref _dropped); _release(item); }));
        pipe.Writer = Task.Run(() => WriteLoopAsync(pipe));
        return pipe;
    }

    /// <summary>El enmarcado del proceso conectado ahora (null si no hay ninguno o el flujo no lleva).</summary>
    public INdiFeedFraming<T>? CurrentFraming => _current.Connection?.Framing;

    /// <summary>Elementos descartados por la cola llena (FFmpeg no leía) desde que se creó el flujo.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);
    private long _dropped;

    /// <summary>Encola para el proceso conectado (o el siguiente, si aún no conectó). Si no se puede (cerrado), lo suelta.</summary>
    public void Enqueue(T item)
    {
        if (!_current.Queue.Writer.TryWrite(item)) _release(item);
    }

    /// <summary>
    /// Conectó un proceso de FFmpeg: recibe lo que se encole desde ahora (lo de antes se descartó: no había a quién dárselo), con
    /// su propio enmarcado si el flujo lo lleva. El cliente anterior —un proceso relanzado o ya terminado— se cierra: antes cada
    /// reconexión (Grabar/Detener, carta de ajuste, relanzamiento) dejaba su socket abierto y en 24/7 acumulaba handles y puertos
    /// del SO. (Auditoría 24/7, A8/#29/#37.)
    /// </summary>
    public void Attach(TcpClient client)
    {
        NetworkStream stream;
        try { stream = client.GetStream(); }
        catch { client.Dispose(); throw; } // ya desconectado: no hay a quién servir
        var stale = new List<TcpClient?>();
        lock (_gate)
        {
            if (_disposed) stale.Add(client); // llegó durante el cierre: nadie lo va a servir
            else
            {
                stale.Add(_current.Connection?.Client);
                _current.Connection = new Connection(client, stream, _framing?.Invoke());
                stale.AddRange(_ended.Select(p => p.Connection?.Client));
                _ended.Clear();
            }
        }
        foreach (var c in stale) { try { c?.Dispose(); } catch { /* ya cerrado */ } }
    }

    /// <summary>
    /// Fin de flujo para el proceso conectado: se le entrega lo que ya estaba en la cola y se le cierra el socket, sin esperar
    /// (lo hace su escritor). Lo que se encole desde ahora espera al proceso siguiente. Devuelve false si no había ningún proceso
    /// conectado (no hay a quién cerrarle nada).
    /// </summary>
    public bool End()
    {
        Pipe ending;
        bool connected;
        lock (_gate)
        {
            if (_disposed) return false;
            ending = _current;
            connected = ending.Connection is not null;
            _current = Open();
            if (connected) _ended.Add(ending);
        }
        ending.Queue.Writer.TryComplete();
        return connected;
    }

    private async Task WriteLoopAsync(Pipe pipe)
    {
        try
        {
            await foreach (var item in pipe.Queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    if (pipe.Connection is { } c) // sin proceso conectado (aún, o ya no), se descarta
                    {
                        if (c.Framing is null)
                            await c.Stream.WriteAsync(_data(item), _cts.Token).ConfigureAwait(false);
                        else if (c.Framing.TryPrefix(item, out var prefix))
                        {
                            await c.Stream.WriteAsync(prefix, _cts.Token).ConfigureAwait(false);
                            await c.Stream.WriteAsync(_data(item), _cts.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { /* FFmpeg cerró el socket: el siguiente proceso conectará otro */ }
                finally { _release(item); }
            }
            // Cola cerrada por End: todo lo que se encoló antes ya está en el socket. Fin de flujo para ese proceso: lee lo que le
            // quede y ve EOF.
            try { pipe.Connection?.Client.Client.Shutdown(SocketShutdown.Send); } catch { /* ya desconectado */ }
        }
        catch (OperationCanceledException) { /* cierre */ }
    }

    public async ValueTask DisposeAsync()
    {
        Pipe[] pipes;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pipes = _ended.Append(_current).ToArray();
            _ended.Clear();
        }
        await _cts.CancelAsync().ConfigureAwait(false);
        foreach (var p in pipes)
        {
            p.Queue.Writer.TryComplete();
            try { await p.Writer.ConfigureAwait(false); } catch { /* cancelación */ }
            try { p.Connection?.Client.Dispose(); } catch { /* noop */ }
        }
        _cts.Dispose();
    }
}
