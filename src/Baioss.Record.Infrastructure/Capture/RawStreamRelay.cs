using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Relé loopback del flujo EN CRUDO (NUT: vídeo sin comprimir + PCM, con marcas de tiempo) de una captura de
/// dispositivo. El receptor permanente (un FFmpeg que tiene abierta la tarjeta DeckLink y no la suelta nunca) EMPUJA el
/// NUT al <see cref="SourcePort"/>; el proceso del canal (preview + medidores + grabación con el preset) lo LEE del
/// <see cref="ConsumerPort"/>. Es lo que permite que iniciar o detener una grabación —que reemplaza el proceso del
/// canal— NO reabra la tarjeta: sin el relé, cada Grabar/Detener cerraba y volvía a abrir el dispositivo (0,3–1 s sin
/// frames, más con autodetección, y el preview congelado mientras tanto). Es el hermano del relé de las entradas de red
/// (<see cref="NetworkStreamRelay"/>), que reparte MPEG-TS comprimido; aquí el flujo son 100–250 MB/s por canal, así
/// que se reparte por unidades enteras y sin copiar:
/// <list type="bullet">
///   <item><b>Origen:</b> se drena SIEMPRE, haya consumidor o no; si dejáramos de leer, FFmpeg se bloquearía al escribir
///   y con él la captura de la tarjeta.</item>
///   <item><b>Unidades:</b> el flujo se trocea con <see cref="NutStreamReader"/> en paquetes y frames ENTEROS que se
///   entregan por referencia a cada consumidor (un frame de 1080p en crudo son 4 MB: ni se copia ni se reserva en el
///   heap grande; vuelve al pool cuando el último consumidor lo escribió).</item>
///   <item><b>Consumidores:</b> uno nuevo recibe primero las CABECERAS del flujo (identificación, cabecera principal,
///   flujos, info: sin ellas un lector NUT no entiende nada) y arranca en el SIGUIENTE punto de sincronía; desde ahí,
///   el flujo en vivo. No hay pre-roll que digerir: con vídeo en crudo cada frame es un fotograma clave y el proceso
///   nuevo va al día casi al instante (ver <see cref="RelayReservation.IsLive"/>).</item>
///   <item><b>Contrapresión:</b> un consumidor que no drena (disco atascado) NUNCA frena al origen: su cola se acota en
///   bytes (<see cref="ConsumerQueueMaxBytes"/>) y se le descartan unidades ENTERAS (el lector NUT del proceso sigue
///   entendiendo el flujo: pierde frames, no la sincronía), con aviso.</item>
///   <item><b>Fin del flujo:</b> cuando el origen se desconecta (el receptor se relanza: la tarjeta se perdió o cambió
///   de modo), se CIERRAN los consumidores —FFmpeg ve EOF, finaliza el archivo si grababa y sale limpiamente— y se
///   olvidan las cabeceras: el flujo siguiente trae las suyas (otra base de tiempos). Un consumidor que conecta sin
///   origen queda a la espera y recibe el flujo nuevo desde sus cabeceras.</item>
/// </list>
/// </summary>
public sealed class RawStreamRelay : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly string _name;
    private readonly TcpListener _sourceListener;
    private readonly TcpListener _consumerListener;
    private readonly CancellationTokenSource _cts = new();

    // Cabeceras del flujo actual, consumidores y reservas bajo UN MISMO candado: la instantánea de cabeceras de un
    // consumidor nuevo y su alta deben ser atómicas respecto a cada unidad entrante (si no, una cabecera podría ir a la
    // instantánea Y a la cola → duplicada).
    private readonly object _sync = new();
    private readonly List<NutUnit> _headers = new();
    private readonly List<Consumer> _consumers = new();
    // Consumidores RESERVADOS (ver ReserveConsumer): ya reciben el flujo en vivo en su cola; el próximo socket que
    // conecte se lleva el más antiguo. Si nadie lo reclama en ReservationTimeout, se descarta.
    private readonly Queue<(Consumer Consumer, long ReservedAt)> _reserved = new();
    private DateTimeOffset _lastOverflowWarnUtc;
    private Task? _sourceLoop;
    private Task? _consumerLoop;

    private sealed class Consumer(Channel<NutUnit> queue, NutUnit[] headers, RawStreamRelay relay)
    {
        public Channel<NutUnit> Queue { get; } = queue;
        /// <summary>Instantánea de las cabeceras del flujo al darse de alta (una referencia por unidad).</summary>
        public NutUnit[] Headers { get; } = headers;
        /// <summary>Bytes en cola o en escritura aún no aceptados por su socket (contrapresión y atraso).</summary>
        public long PendingBytes;
        /// <summary>Su socket ya conectó (se marca bajo el candado del relé al aceptarlo: desde ese instante puede arrancar).</summary>
        public bool Connected;
        /// <summary>Ya se escribieron las cabeceras a su socket.</summary>
        public volatile bool HeadersSent;
        /// <summary>Pasó un punto de sincronía tras conectar: desde él recibe el flujo en vivo.</summary>
        public volatile bool Started;
        /// <summary>Se desconectó, el flujo terminó o la reserva caducó: ya no hay nada que esperar de él.</summary>
        public volatile bool Closed;
        /// <summary>Se le descartó algo y aún no ha pasado el siguiente punto de sincronía (bajo el candado del relé).</summary>
        public bool Dropping;
        /// <summary>
        /// ¿Consume ya el flujo en directo? Conectado, arrancado y con un atraso de como mucho DOS unidades del tamaño
        /// mayor visto (la que se le está escribiendo y una en cola). No sirve «nada pendiente»: una escritura de 4 MB
        /// (un frame de 1080p) dura casi lo que el proceso tarda en consumir el frame, así que casi siempre hay algo en
        /// curso aunque vaya en tiempo real; un proceso atrasado de verdad acumula tres o más. Ver <see cref="RelayReservation.IsLive"/>.
        /// </summary>
        public bool IsLive => Closed || (HeadersSent && Started && Interlocked.Read(ref PendingBytes) <= relay.LiveBacklogBytes);
    }

    public RawStreamRelay(string name, ILogger log)
    {
        _name = name;
        _log = log;
        _sourceListener = new TcpListener(IPAddress.Loopback, 0);
        _consumerListener = new TcpListener(IPAddress.Loopback, 0);
        _sourceListener.Start();
        _consumerListener.Start();
        SourcePort = ((IPEndPoint)_sourceListener.LocalEndpoint).Port;
        ConsumerPort = ((IPEndPoint)_consumerListener.LocalEndpoint).Port;
    }

    /// <summary>Puerto al que ESCRIBE el receptor permanente (<c>-f nut tcp://127.0.0.1:puerto</c>).</summary>
    public int SourcePort { get; }

    /// <summary>Puerto del que LEE el proceso del canal (<c>-f nut -i tcp://127.0.0.1:puerto</c>).</summary>
    public int ConsumerPort { get; }

    /// <summary>Bytes en cola por consumidor antes de descartarle unidades (contrapresión). 128 MB ≈ 1 s de 1080p30 en
    /// crudo de 8 bits: un proceso que va más de eso por detrás está atascado, no ocupado.</summary>
    public long ConsumerQueueMaxBytes { get; init; } = 128L * 1024 * 1024;

    /// <summary>Cuánto se guarda una reserva (<see cref="ReserveConsumer"/>) a la espera de que su proceso conecte.</summary>
    public TimeSpan ReservationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Unidad más grande vista en el flujo actual (un frame de vídeo): fija cuánto atraso cuenta como «al día».</summary>
    private long _largestUnit;

    /// <summary>Atraso (bytes pendientes) hasta el que un consumidor cuenta como al día: dos unidades del tamaño mayor visto.</summary>
    public long LiveBacklogBytes => 2 * Math.Max(Interlocked.Read(ref _largestUnit), 64 * 1024);

    /// <summary>¿Está el receptor permanente conectado y entregando flujo?</summary>
    public bool SourceConnected { get; private set; }

    /// <summary>Bytes recibidos del receptor desde el arranque (diagnóstico).</summary>
    public long SourceBytes { get; private set; }

    /// <summary>Bytes ya REPARTIDOS (a las cabeceras guardadas y a las colas de los consumidores) desde el arranque
    /// (diagnóstico y tests: si crece, el receptor entrega y el relé reparte).</summary>
    public long ForwardedBytes { get; private set; }

    /// <summary>Frames de vídeo repartidos desde el arranque (diagnóstico y tests).</summary>
    public long VideoFramesForwarded { get; private set; }

    /// <summary>Conexiones del receptor aceptadas desde el arranque: cambia con cada proceso de captura nuevo.</summary>
    public int SourceGeneration => Volatile.Read(ref _sourceGeneration);
    private int _sourceGeneration;

    /// <summary>Frames de vídeo recibidos en la conexión ACTUAL del receptor (vigilancia de una captura muda).</summary>
    public long VideoFramesThisSource => Interlocked.Read(ref _videoFramesThisSource);
    private long _videoFramesThisSource;

    /// <summary>En qué está el bucle que drena al receptor (diagnóstico): «esperando al receptor», «leyendo» (a la espera de
    /// bytes del receptor) o «repartiendo».</summary>
    public string PumpStage { get; private set; } = "esperando al receptor";

    /// <summary>Consumidores dados de alta ahora mismo, reservados incluidos (diagnóstico y tests).</summary>
    public int ConsumerCount { get { lock (_sync) return _consumers.Count; } }

    /// <summary>Se eleva si hubo que DESCARTAR unidades a un consumidor que no drenaba (su grabación tendrá un salto), con
    /// cuántas se descartaron desde el aviso anterior. Como mucho uno por segundo.</summary>
    public event EventHandler<long>? ConsumerOverflow;
    private long _droppedSinceReport;
    private DateTimeOffset _lastOverflowReportUtc;
    private int _disposed;

    /// <summary>
    /// Reserva para el PRÓXIMO socket que conecte al <see cref="ConsumerPort"/> un consumidor y devuelve cómo saber si ya
    /// va al día. El motor lo llama al construir el proceso: con <see cref="RelayReservation.IsLive"/> sabe cuándo el
    /// proceso nuevo ya lee en directo y puede tomar el relevo del viejo sin avance rápido. El consumidor no acumula nada
    /// hasta conectar: arranca en el primer punto de sincronía tras conectar (un frame de 1080p en crudo son 4 MB; un
    /// atraso de medio segundo acumulado mientras el proceso arranca serían 50 MB que digerir antes de ir al día). No hay
    /// pre-roll que saltarse en el preview (0 frames).
    /// </summary>
    public RelayReservation ReserveConsumer()
    {
        lock (_sync)
        {
            // Las reservas caducan también aquí (y al conectar un consumidor), no solo con flujo: sin él no caducaban nunca.
            ExpireReservations(Stopwatch.GetTimestamp());
            var consumer = NewConsumer();
            _reserved.Enqueue((consumer, Stopwatch.GetTimestamp()));
            _log.LogDebug("Relé crudo {Name}: consumidor reservado ({Headers} cabeceras guardadas).", _name, _headers.Count);
            return new RelayReservation(0, () => consumer.IsLive);
        }
    }

    /// <summary>Alta + instantánea de las cabeceras, atómicas respecto a cada unidad entrante. Bajo <see cref="_sync"/>.</summary>
    private Consumer NewConsumer()
    {
        var queue = Channel.CreateUnbounded<NutUnit>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
        var headers = _headers.ToArray();
        foreach (var h in headers) h.Retain();
        var consumer = new Consumer(queue, headers, this);
        _consumers.Add(consumer);
        return consumer;
    }

    /// <summary>Descarta las reservas que nadie reclamó a tiempo (su cola dejaría de drenarse). Bajo <see cref="_sync"/>.</summary>
    private void ExpireReservations(long now)
    {
        while (_reserved.Count > 0 && Stopwatch.GetElapsedTime(_reserved.Peek().ReservedAt, now) > ReservationTimeout)
        {
            var (consumer, _) = _reserved.Dequeue();
            _consumers.Remove(consumer);
            Discard(consumer);
            _log.LogDebug("Relé crudo {Name}: una reserva de consumidor caducó sin conexión.", _name);
        }
    }

    /// <summary>Cierra un consumidor que nunca conectó y suelta todo lo que retenía.</summary>
    private static void Discard(Consumer consumer)
    {
        consumer.Closed = true;
        consumer.Queue.Writer.TryComplete();
        while (consumer.Queue.Reader.TryRead(out var unit)) unit.Release();
        foreach (var h in consumer.Headers) h.Release();
    }

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
            TcpClient client;
            try { client = await _sourceListener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                // Sin pausa, un fallo persistente de Accept era un bucle caliente. Nadie conectó: no hay flujo que cerrar.
                _log.LogDebug(ex, "Relé crudo {Name}: fallo aceptando al receptor.", _name);
                try { await Task.Delay(250, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    client.ReceiveBufferSize = 1024 * 1024; // frames de MB: menos despertares por frame
                    Interlocked.Exchange(ref _videoFramesThisSource, 0);
                    Interlocked.Increment(ref _sourceGeneration);
                    SourceConnected = true;
                    _log.LogDebug("Relé crudo {Name}: receptor conectado (puerto {Port}).", _name, SourcePort);
                    await PumpSourceAsync(client.GetStream(), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Relé crudo {Name}: el flujo del receptor se cortó.", _name); }
            finally
            {
                if (SourceConnected)
                {
                    SourceConnected = false;
                    _log.LogDebug("Relé crudo {Name}: el receptor cerró el flujo; se cierran los consumidores.", _name);
                }
                // El flujo terminó: los consumidores ven EOF (finalizan y salen) y las cabeceras se olvidan porque el flujo
                // siguiente trae las suyas (otra base de tiempos).
                CloseConsumers();
                PumpStage = "esperando al receptor";
            }
        }
    }

    /// <summary>Drena el receptor SIEMPRE y reparte cada unidad entera: cabeceras guardadas + cola de cada consumidor.</summary>
    private async Task PumpSourceAsync(NetworkStream stream, CancellationToken ct)
    {
        var reader = new NutStreamReader(stream);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                PumpStage = "leyendo";
                var unit = await reader.ReadUnitAsync(ct).ConfigureAwait(false);
                if (unit is null) return; // el receptor cerró (el proceso se relanza)
                SourceBytes += unit.Length;
                PumpStage = "repartiendo";
                int dropped = 0;
                lock (_sync)
                {
                    ExpireReservations(Stopwatch.GetTimestamp());
                    if (unit.Kind == NutUnitKind.FileId) { ClearHeaders(); Interlocked.Exchange(ref _largestUnit, 0); } // empieza otro flujo
                    if (unit.IsHeader) { unit.Retain(); _headers.Add(unit); }
                    if (unit.Length > Interlocked.Read(ref _largestUnit)) Interlocked.Exchange(ref _largestUnit, unit.Length);
                    foreach (var c in _consumers)
                    {
                        // Un consumidor arranca en el primer punto de sincronía que pasa una vez CONECTADO (su bomba escribe
                        // primero las cabeceras y luego la cola, así que el orden se conserva); las cabeceras (solo llegan al
                        // principio del flujo) se le entregan siempre: si ya tenía una instantánea, era parcial.
                        if (unit.Kind == NutUnitKind.Syncpoint && c.Connected) c.Started = true;
                        if (!unit.IsHeader && !c.Started) continue;
                        if (!Enqueue(c, unit)) dropped++;
                    }
                    ForwardedBytes += unit.Length;
                    if (unit.IsVideoFrame) { VideoFramesForwarded++; Interlocked.Increment(ref _videoFramesThisSource); }
                }
                unit.Release();
                if (dropped > 0) ReportOverflow(dropped);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (EndOfStreamException)
        {
            // Lo normal cuando el receptor se relanza (reapertura, canales de audio, apagado): FFmpeg muere a mitad de un
            // frame de varios MB. No es una corrupción; avisar como tal enseñaba a ignorar el aviso de verdad.
            _log.LogInformation("Relé crudo {Name}: el receptor cerró a mitad de una unidad (se relanza).", _name);
        }
        catch (IOException ex)
        {
            _log.LogInformation("Relé crudo {Name}: la conexión con el receptor se cortó ({Reason}).", _name, ex.Message);
        }
        catch (Exception ex)
        {
            // El flujo ya no es de fiar (NUT inválido, o un valor imposible en una cabecera): se corta al receptor (verá el
            // socket cerrado, saldrá y el supervisor lo relanzará con un flujo nuevo desde sus cabeceras) y los consumidores
            // ven EOF.
            _log.LogWarning(ex, "Relé crudo {Name}: flujo NUT inválido; se corta al receptor para que empiece de nuevo.", _name);
        }
    }

    /// <summary>Cola llena: ese consumidor no drena (disco atascado). Se descarta para NO frenar al receptor; se avisa en el
    /// registro (cada 5 s) y por <see cref="ConsumerOverflow"/> (cada segundo, con el total descartado desde el anterior).</summary>
    private void ReportOverflow(int dropped)
    {
        Interlocked.Add(ref _droppedSinceReport, dropped);
        var now = DateTimeOffset.UtcNow;
        if (now - _lastOverflowWarnUtc > TimeSpan.FromSeconds(5))
        {
            _lastOverflowWarnUtc = now;
            _log.LogWarning("Relé crudo {Name}: un consumidor no drena el flujo; se le descartan frames (su grabación tendrá un salto).", _name);
        }
        if (now - _lastOverflowReportUtc < TimeSpan.FromSeconds(1)) return;
        _lastOverflowReportUtc = now;
        long total = Interlocked.Exchange(ref _droppedSinceReport, 0);
        try { ConsumerOverflow?.Invoke(this, total); }
        catch (Exception ex) { _log.LogDebug(ex, "Relé crudo {Name}: un suscriptor del aviso de descartes falló.", _name); }
    }

    /// <summary>Encola una unidad a un consumidor (una referencia más), salvo que su cola supere el tope en bytes. Bajo <see cref="_sync"/>.</summary>
    private bool Enqueue(Consumer consumer, NutUnit unit)
    {
        if (consumer.Closed) return true; // se está retirando: no es un descarte
        // Tras un descarte se reanuda SOLO en un punto de sincronía: en NUT la marca de tiempo de un frame es relativa a la
        // del frame anterior de su flujo, y solo un punto de sincronía la fija de nuevo; una unidad suelta tras el hueco
        // llegaba con una marca calculada sobre otra base (marca hacia atrás en la grabación).
        if (consumer.Dropping && !unit.IsHeader && unit.Kind != NutUnitKind.Syncpoint) return false;
        if (Interlocked.Read(ref consumer.PendingBytes) + unit.Length > ConsumerQueueMaxBytes) { consumer.Dropping = true; return false; }
        consumer.Dropping = false;
        unit.Retain();
        Interlocked.Add(ref consumer.PendingBytes, unit.Length);
        if (consumer.Queue.Writer.TryWrite(unit)) return true;
        Interlocked.Add(ref consumer.PendingBytes, -unit.Length);
        unit.Release();
        return true; // cola completada (consumidor en cierre)
    }

    private void ClearHeaders()
    {
        foreach (var h in _headers) h.Release();
        _headers.Clear();
    }

    private async Task AcceptConsumerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _consumerListener.AcceptTcpClientAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Relé crudo {Name}: fallo aceptando a un consumidor.", _name);
                try { await Task.Delay(100, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
                continue;
            }
            // Protegido: una excepción aquí (un socket que el par ya cerró al aceptarlo, p. ej. un sondeo matado) terminaba
            // este bucle en silencio, y a partir de ahí ningún proceso del canal volvía a recibir flujo (conectaban a nivel
            // TCP y esperaban para siempre, con «SEÑAL OK» y el preview congelado) hasta reiniciar la aplicación.
            try { client.NoDelay = true; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Relé crudo {Name}: un consumidor se fue al conectar; se descarta esa conexión.", _name);
                try { client.Dispose(); } catch { /* noop */ }
                continue;
            }

            // El socket se lleva la reserva más antigua si la hay (su cola ya trae lo llegado desde entonces); si no,
            // alta + instantánea de las cabeceras de este instante, en una operación atómica (ver _sync).
            Consumer consumer;
            bool reserved;
            lock (_sync)
            {
                ExpireReservations(Stopwatch.GetTimestamp());
                reserved = _reserved.TryDequeue(out var reservation);
                consumer = reserved ? reservation.Consumer : NewConsumer();
                consumer.Connected = true;
            }
            _log.LogDebug("Relé crudo {Name}: consumidor conectado (puerto {Port}{Reserved}); {Headers} cabeceras.", _name, ConsumerPort, reserved ? ", reservado" : "", consumer.Headers.Length);
            _ = Task.Run(() => PumpConsumerAsync(client, consumer, ct), CancellationToken.None);
        }
    }

    /// <summary>Vuelca las cabeceras y luego el flujo en vivo a un consumidor. Termina cuando su cola se cierra (el origen
    /// se fue → FFmpeg ve EOF) o cuando el consumidor se desconecta (el motor reemplazó el proceso).</summary>
    private async Task PumpConsumerAsync(TcpClient client, Consumer consumer, CancellationToken ct)
    {
        try
        {
            using (client)
            {
                var stream = client.GetStream();
                // PendingBytes cuenta lo encolado y lo que se está escribiendo hasta que el socket lo acepta: mientras el
                // consumidor va atrasado su cola crece; «al día» = conectado, arrancado y con como mucho dos unidades pendientes.
                foreach (var h in consumer.Headers) await stream.WriteAsync(h.Bytes, ct).ConfigureAwait(false);
                consumer.HeadersSent = true;
                await foreach (var unit in consumer.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    try { await stream.WriteAsync(unit.Bytes, ct).ConfigureAwait(false); }
                    finally
                    {
                        Interlocked.Add(ref consumer.PendingBytes, -unit.Length);
                        unit.Release();
                    }
                }
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* cierre del relé */ }
        catch (Exception ex) { _log.LogDebug(ex, "Relé crudo {Name}: el consumidor cerró el socket.", _name); }
        finally
        {
            consumer.Closed = true;
            lock (_sync) _consumers.Remove(consumer);
            consumer.Queue.Writer.TryComplete();
            while (consumer.Queue.Reader.TryRead(out var pending)) { Interlocked.Add(ref consumer.PendingBytes, -pending.Length); pending.Release(); }
            foreach (var h in consumer.Headers) h.Release();
        }
    }

    private void CloseConsumers()
    {
        Consumer[] connected;
        Consumer[] unclaimed;
        lock (_sync)
        {
            unclaimed = _reserved.Select(r => r.Consumer).ToArray();
            _reserved.Clear();
            foreach (var u in unclaimed) _consumers.Remove(u);
            connected = _consumers.ToArray();
            ClearHeaders();
        }
        foreach (var c in connected) c.Queue.Writer.TryComplete();
        foreach (var u in unclaimed) Discard(u);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return; // idempotente
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
