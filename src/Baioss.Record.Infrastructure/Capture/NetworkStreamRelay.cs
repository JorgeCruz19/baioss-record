using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Relé loopback del flujo COMPRIMIDO (MPEG-TS) de una entrada de red. El receptor permanente (un FFmpeg que mantiene
/// la conexión SRT/RTMP con el emisor y copia el flujo sin recodificar) EMPUJA el TS al <see cref="SourcePort"/>; el
/// proceso del canal (preview + medidores + grabación con el preset) lo LEE del <see cref="ConsumerPort"/>. Es lo que
/// permite que iniciar o detener una grabación —que reemplaza el proceso del canal— NO toque la conexión con el
/// emisor: sin el relé, cada Grabar/Detener cerraba el socket SRT/RTMP y el emisor (OBS, vMix, un codificador) tenía
/// que reconectar, perdiéndose los primeros segundos de cada grabación (y con emisores sin reconexión automática, la
/// grabación entera).
///
/// <list type="bullet">
///   <item><b>Origen:</b> se drena SIEMPRE, haya consumidor o no; si dejáramos de leer, FFmpeg se bloquearía al
///   escribir y con él la conexión con el emisor.</item>
///   <item><b>Consumidores:</b> cada conexión recibe primero la ventana de PRE-ROLL (los últimos
///   <see cref="PrerollWindow"/>) y luego el flujo en vivo, sin hueco ni solape: así el proceso nuevo encuentra un
///   fotograma clave en la ventana y arranca al instante —la grabación empieza un poco ANTES de pulsar Grabar y nunca
///   pierde material—, en vez de esperar al siguiente keyframe del emisor.</item>
///   <item><b>Contrapresión:</b> un consumidor que no drena (disco atascado) NUNCA frena al origen: su cola se
///   desborda y se descarta (aviso en el log), la conexión con el emisor sigue intacta.</item>
///   <item><b>Fin del flujo:</b> cuando el origen se desconecta (el emisor cerró: el receptor termina y se relanza),
///   se CIERRAN los consumidores —FFmpeg ve EOF, finaliza el archivo si grababa y sale limpiamente— y se vacía el
///   pre-roll: el flujo siguiente (otra base de tiempos) no empalma con el anterior. Un consumidor que conecta sin
///   origen queda a la espera y recibe el flujo cuando llegue.</item>
/// </list>
/// </summary>
public sealed class NetworkStreamRelay : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly string _name;
    private readonly TcpListener _sourceListener;
    private readonly TcpListener _consumerListener;
    private readonly CancellationTokenSource _cts = new();

    // Ventana de pre-roll y consumidores bajo UN MISMO candado: la instantánea del pre-roll de un consumidor nuevo y su
    // alta en la lista deben ser atómicas respecto a cada fragmento entrante (si no, un fragmento podría ir a la
    // ventana Y a la cola → bytes duplicados → discontinuidad al inicio de la pieza).
    private readonly object _sync = new();
    private readonly Queue<(byte[] Data, long Timestamp)> _preroll = new();
    private long _prerollLength;
    private readonly List<Consumer> _consumers = new();
    private DateTimeOffset _lastOverflowWarnUtc;
    private Task? _sourceLoop;
    private Task? _consumerLoop;
    // Emisores con relojes DISTINTOS para audio y vídeo (visto en un servidor RTMP real: el audio 3,4 h «por delante»
    // según sus marcas): el audio se realinea al reloj del vídeo antes de repartir el flujo (ver TsAudioClockAligner).
    private readonly TsAudioClockAligner _aligner = new();

    private sealed record Consumer(Channel<byte[]> Queue, byte[][] Preroll);

    public NetworkStreamRelay(string name, ILogger log)
    {
        _name = name;
        _log = log;
        _aligner.SkewCorrected += seconds => _log.LogWarning(
            "Relé {Name}: el emisor trae el audio con un reloj distinto al del vídeo (desfase de {Skew:0.000} s según sus marcas de tiempo, medido en régimen; el primer par daba {First:0.000} s); se realinea el audio al vídeo por orden de llegada.",
            _name, seconds, _aligner.FirstPairSkewSeconds ?? seconds);
        _sourceListener = new TcpListener(IPAddress.Loopback, 0);
        _consumerListener = new TcpListener(IPAddress.Loopback, 0);
        _sourceListener.Start();
        _consumerListener.Start();
        SourcePort = ((IPEndPoint)_sourceListener.LocalEndpoint).Port;
        ConsumerPort = ((IPEndPoint)_consumerListener.LocalEndpoint).Port;
    }

    /// <summary>Puerto al que ESCRIBE el receptor permanente (<c>-f mpegts tcp://127.0.0.1:puerto</c>).</summary>
    public int SourcePort { get; }

    /// <summary>Puerto del que LEE el proceso del canal (<c>-i tcp://127.0.0.1:puerto</c>).</summary>
    public int ConsumerPort { get; }

    /// <summary>Cuánto flujo reciente se entrega a un consumidor nuevo. Debe cubrir MÁS de un GOP del emisor (OBS/vMix
    /// usan 1–2 s) para que el proceso nuevo encuentre un fotograma clave sin esperar.</summary>
    public TimeSpan PrerollWindow { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Tope en bytes de la ventana de pre-roll (con bitrates altos, la ventana por tiempo no dispara la memoria).</summary>
    public int PrerollMaxBytes { get; init; } = 16 * 1024 * 1024;

    /// <summary>Fragmentos pendientes por consumidor antes de descartar (contrapresión). A 64 KiB por fragmento, 64 MiB.</summary>
    public int ConsumerQueueCapacity { get; init; } = 1024;

    /// <summary>¿Está el receptor permanente conectado y entregando flujo?</summary>
    public bool SourceConnected { get; private set; }

    /// <summary>Bytes recibidos del receptor desde el arranque (diagnóstico).</summary>
    public long SourceBytes { get; private set; }

    /// <summary>Consumidores conectados ahora mismo (diagnóstico y tests).</summary>
    public int ConsumerCount { get { lock (_sync) return _consumers.Count; } }

    /// <summary>Desfase (s) restado al audio del flujo actual por traer un reloj distinto al del vídeo; 0 si eran coherentes;
    /// null mientras no se ha visto el primer PES de audio y de vídeo (diagnóstico y tests).</summary>
    public double? AudioClockCorrectionSeconds => _aligner.CorrectionSeconds;

    /// <summary>Retardo de audio manual de la fuente (ms, positivo = el audio suena más tarde); se aplica a cada PES de audio.</summary>
    public int AudioDelayMs
    {
        get => (int)(_aligner.AudioDelayTicks / 90);
        set => _aligner.AudioDelayTicks = value * 90L;
    }

    /// <summary>Se eleva si hubo que DESCARTAR datos para un consumidor que no drenaba (su grabación tendrá un salto).</summary>
    public event EventHandler? ConsumerOverflow;

    /// <summary>Arranca los bucles de aceptación. No bloquea.</summary>
    public void Start()
    {
        _sourceLoop ??= Task.Run(() => AcceptSourceLoopAsync(_cts.Token));
        _consumerLoop ??= Task.Run(() => AcceptConsumerLoopAsync(_cts.Token));
    }

    private async Task AcceptSourceLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = await _sourceListener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                SourceConnected = true;
                _log.LogDebug("Relé {Name}: receptor conectado (puerto {Port}).", _name, SourcePort);
                await PumpSourceAsync(client.GetStream(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Relé {Name}: fallo aceptando al receptor.", _name); }
            finally
            {
                if (SourceConnected)
                {
                    SourceConnected = false;
                    _log.LogDebug("Relé {Name}: el receptor cerró el flujo; se cierran los consumidores.", _name);
                }
                // El flujo terminó: los consumidores ven EOF (finalizan y salen), y la ventana se vacía porque el flujo
                // siguiente no empalma con este (otra base de tiempos). El desfase de relojes se mide de nuevo.
                CloseConsumers();
                _aligner.Reset();
            }
        }
    }

    /// <summary>Drena el receptor SIEMPRE y reparte cada fragmento: ventana de pre-roll + cola de cada consumidor.</summary>
    private async Task PumpSourceAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) return; // el receptor cerró (el emisor se fue o el proceso se relanza)
                SourceBytes += n;
                // Paquetes TS completos, con el audio ya realineado si el emisor trae relojes distintos; vacío mientras
                // se espera el resto de un paquete partido o se retiene el audio hasta ver el primer PTS de vídeo.
                var chunk = _aligner.Process(buffer.AsSpan(0, n), Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
                if (chunk.Length == 0) continue;

                bool dropped = false;
                lock (_sync)
                {
                    long now = Stopwatch.GetTimestamp();
                    _preroll.Enqueue((chunk, now));
                    _prerollLength += chunk.Length;
                    while (_preroll.Count > 0 && (_prerollLength > PrerollMaxBytes ||
                           Stopwatch.GetElapsedTime(_preroll.Peek().Timestamp, now) > PrerollWindow))
                        _prerollLength -= _preroll.Dequeue().Data.Length;

                    foreach (var c in _consumers)
                        if (!c.Queue.Writer.TryWrite(chunk)) dropped = true;
                }
                if (dropped)
                {
                    // Cola llena: ese consumidor no drena (disco atascado). Se descarta para NO frenar al receptor.
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastOverflowWarnUtc > TimeSpan.FromSeconds(5))
                    {
                        _lastOverflowWarnUtc = now;
                        _log.LogWarning("Relé {Name}: un consumidor no drena el flujo; se descartan datos (su grabación tendrá un salto).", _name);
                    }
                    ConsumerOverflow?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task AcceptConsumerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _consumerListener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Relé {Name}: fallo aceptando a un consumidor.", _name); continue; }
            client.NoDelay = true;

            // Alta + instantánea del pre-roll en una operación atómica (ver _sync): todo lo anterior viaja en la
            // instantánea y todo lo posterior en la cola, así el consumidor recibe un flujo contiguo.
            Consumer consumer;
            lock (_sync)
            {
                var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ConsumerQueueCapacity)
                {
                    SingleReader = true, SingleWriter = true,
                    // Wait y NO DropWrite: con los modos Drop*, TryWrite devuelve true también cuando descarta y el
                    // desbordamiento sería invisible. Con Wait, TryWrite devuelve false con la cola llena.
                    FullMode = BoundedChannelFullMode.Wait,
                });
                consumer = new Consumer(queue, _preroll.Select(p => p.Data).ToArray());
                _consumers.Add(consumer);
            }
            _log.LogDebug("Relé {Name}: consumidor conectado (puerto {Port}); pre-roll de {Bytes} bytes.", _name, ConsumerPort, consumer.Preroll.Sum(c => c.Length));
            _ = Task.Run(() => PumpConsumerAsync(client, consumer, ct), CancellationToken.None);
        }
    }

    /// <summary>Vuelca el pre-roll y luego el flujo en vivo a un consumidor. Termina cuando su cola se cierra (el
    /// origen se fue → FFmpeg ve EOF) o cuando el consumidor se desconecta (el motor reemplazó el proceso).</summary>
    private async Task PumpConsumerAsync(TcpClient client, Consumer consumer, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                foreach (var chunk in consumer.Preroll)
                    await stream.WriteAsync(chunk, ct).ConfigureAwait(false);
                await foreach (var chunk in consumer.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                    await stream.WriteAsync(chunk, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* cierre del relé */ }
        catch (Exception ex) { _log.LogDebug(ex, "Relé {Name}: el consumidor cerró el socket.", _name); }
        finally
        {
            lock (_sync) _consumers.Remove(consumer);
        }
    }

    private void CloseConsumers()
    {
        Consumer[] consumers;
        lock (_sync)
        {
            consumers = _consumers.ToArray();
            _preroll.Clear();
            _prerollLength = 0;
        }
        foreach (var c in consumers) c.Queue.Writer.TryComplete();
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _sourceListener.Stop(); } catch { /* noop */ }
        try { _consumerListener.Stop(); } catch { /* noop */ }
        CloseConsumers();
        foreach (var t in new[] { _sourceLoop, _consumerLoop })
        {
            if (t is null) continue;
            try { await t.ConfigureAwait(false); } catch { /* los bucles no propagan */ }
        }
        _cts.Dispose();
    }
}
