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
/// </summary>
/// <typeparam name="T">Lo que se encola (un búfer y su longitud útil).</typeparam>
internal sealed class NdiFeed<T> : IAsyncDisposable
{
    /// <summary>La cola de un proceso de FFmpeg (el conectado, o el siguiente si aún no conectó), su socket y su escritor.</summary>
    private sealed class Pipe(Channel<T> queue)
    {
        public Channel<T> Queue { get; } = queue;
        public volatile TcpClient? Client;
        public volatile NetworkStream? Stream;
        public Task Writer = Task.CompletedTask;
    }

    private readonly int _capacity;
    private readonly Func<T, ReadOnlyMemory<byte>> _data;
    private readonly Action<T> _release;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    // Terminadas: su socket se cierra del todo al conectar el proceso siguiente (o al disponer), como el de un proceso relanzado.
    private readonly List<Pipe> _ended = new();
    private volatile Pipe _current;
    private bool _disposed;

    /// <param name="capacity">Elementos que caben en la cola; al llenarse se descarta el más viejo.</param>
    /// <param name="data">Los bytes que se escriben de cada elemento.</param>
    /// <param name="release">Suelta un elemento ya escrito, descartado o rechazado (devuelve su búfer al pool).</param>
    public NdiFeed(int capacity, Func<T, ReadOnlyMemory<byte>> data, Action<T> release)
    {
        _capacity = capacity;
        _data = data;
        _release = release;
        _current = Open();
    }

    private Pipe Open()
    {
        var pipe = new Pipe(Channel.CreateBounded<T>(
            new BoundedChannelOptions(_capacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true }, _release));
        pipe.Writer = Task.Run(() => WriteLoopAsync(pipe));
        return pipe;
    }

    /// <summary>Encola para el proceso conectado (o el siguiente, si aún no conectó). Si no se puede (cerrado), lo suelta.</summary>
    public void Enqueue(T item)
    {
        if (!_current.Queue.Writer.TryWrite(item)) _release(item);
    }

    /// <summary>
    /// Conectó un proceso de FFmpeg: recibe lo que se encole desde ahora (lo de antes se descartó: no había a quién dárselo). El
    /// cliente anterior —un proceso relanzado o ya terminado— se cierra: antes cada reconexión (Grabar/Detener, carta de ajuste,
    /// relanzamiento) dejaba su socket abierto y en 24/7 acumulaba handles y puertos del SO. (Auditoría 24/7, A8/#29/#37.)
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
                stale.Add(_current.Client);
                _current.Client = client;
                _current.Stream = stream;
                stale.AddRange(_ended.Select(p => p.Client));
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
            connected = ending.Client is not null;
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
                    if (pipe.Stream is { } s) // sin proceso conectado (aún, o ya no), se descarta
                        await s.WriteAsync(_data(item), _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch { /* FFmpeg cerró el socket: el siguiente proceso conectará otro */ }
                finally { _release(item); }
            }
            // Cola cerrada por End: todo lo que se encoló antes ya está en el socket. Fin de flujo para ese proceso: lee lo que le
            // quede y ve EOF.
            try { pipe.Client?.Client.Shutdown(SocketShutdown.Send); } catch { /* ya desconectado */ }
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
            try { p.Client?.Dispose(); } catch { /* noop */ }
        }
        _cts.Dispose();
    }
}
