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
///   <item><b>Consumidores:</b> cada conexión recibe primero la ventana de PRE-ROLL y luego el flujo en vivo, sin hueco
///   ni solape. La ventana EMPIEZA EN UN FOTOGRAMA CLAVE de vídeo (el bit random_access_indicator que el muxer mpegts
///   de FFmpeg pone en el paquete que abre el PES de cada keyframe) y cubre al menos <see cref="PrerollWindow"/> de
///   FLUJO (por DTS de vídeo, no por reloj de llegada: un servidor que entrega a ráfagas no la acorta). Así el proceso
///   nuevo decodifica desde el primer byte —la grabación empieza un poco ANTES de pulsar Grabar y nunca pierde
///   material— y su análisis de entrada (2 s, ver <c>NetworkStreamCaptureSource.ConsumerArgumentsFor</c>) se completa con
///   lo que ya tiene: con MPEG-TS FFmpeg agota siempre el tiempo de análisis, y sin pre-roll suficiente esperaba ese
///   tiempo en vivo (3 s de preview congelado en cada Grabar/Detener, medido).</item>
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
    private readonly List<PrerollEntry> _preroll = new();
    private long _prerollLength;
    // Consumidores RESERVADOS (ver ReserveConsumer): ya reciben el flujo en vivo en su cola; el próximo socket que
    // conecte se lleva el más antiguo. Si nadie lo reclama en ReservationTimeout, se descarta.
    private readonly Queue<(Consumer Consumer, long ReservedAt)> _reserved = new();

    /// <summary>Un trozo del flujo reenviado (paquetes TS enteros): si empieza con un fotograma clave de vídeo lleva su DTS;
    /// <paramref name="VideoFrames"/> = PES de vídeo que arrancan en él.</summary>
    private readonly record struct PrerollEntry(byte[] Data, long Timestamp, bool Keyframe, long Dts, int VideoFrames);
    private readonly List<Consumer> _consumers = new();
    private DateTimeOffset _lastOverflowWarnUtc;
    private Task? _sourceLoop;
    private Task? _consumerLoop;
    // Emisores con relojes DISTINTOS para audio y vídeo (visto en un servidor RTMP real: el audio 3,4 h «por delante»
    // según sus marcas): el audio se realinea al reloj del vídeo antes de repartir el flujo (ver TsAudioClockAligner).
    private readonly TsAudioClockAligner _aligner = new();

    private sealed class Consumer(Channel<byte[]> queue, byte[][] preroll)
    {
        public Channel<byte[]> Queue { get; } = queue;
        public byte[][] Preroll { get; } = preroll;
        /// <summary>Ya se volcó el pre-roll entero a su socket.</summary>
        public volatile bool PrerollSent;
        /// <summary>Hay una escritura a su socket en curso que aún no ha aceptado (va atrasado).</summary>
        public volatile bool Writing;
        /// <summary>Se desconectó, el flujo terminó o la reserva caducó: ya no hay nada que esperar de él.</summary>
        public volatile bool Closed;
        /// <summary>¿Consume ya el flujo en directo? Ver <see cref="RelayReservation.IsLive"/>.</summary>
        public bool IsLive => Closed || (PrerollSent && !Writing && Queue.Reader.Count == 0);
    }

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

    /// <summary>Cuánto flujo, como mínimo, se entrega a un consumidor nuevo: desde el último fotograma clave que ya tiene esta
    /// duración de vídeo por detrás (con un GOP más largo, desde el primer keyframe que haya). Debe superar el análisis de
    /// entrada del proceso del canal (2 s) para que arranque sin esperar flujo en vivo. Sin fotogramas clave (flujo sin
    /// vídeo) se mide por reloj de llegada.</summary>
    public TimeSpan PrerollWindow { get; init; } = TimeSpan.FromSeconds(2.5);

    /// <summary>Tope en bytes de la ventana de pre-roll (con bitrates altos o GOP largos, la ventana por flujo no dispara la
    /// memoria). Superado, se descarta lo más viejo y la ventana vuelve a empezar en el siguiente fotograma clave.</summary>
    public int PrerollMaxBytes { get; init; } = 32 * 1024 * 1024;

    /// <summary>Fragmentos pendientes por consumidor antes de descartar (contrapresión). A 64 KiB por fragmento, 64 MiB.</summary>
    public int ConsumerQueueCapacity { get; init; } = 1024;

    /// <summary>Cuánto se guarda una reserva (<see cref="ReserveConsumer"/>) a la espera de que su proceso conecte.</summary>
    public TimeSpan ReservationTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>¿Está el receptor permanente conectado y entregando flujo?</summary>
    public bool SourceConnected { get; private set; }

    /// <summary>Bytes recibidos del receptor desde el arranque (diagnóstico).</summary>
    public long SourceBytes { get; private set; }

    /// <summary>Bytes ya REPARTIDOS (ventana de pre-roll y colas de los consumidores) desde el arranque: lo recibido menos lo que
    /// el alineador aún retiene o el resto de un paquete partido (diagnóstico y tests: tras esperar a que alcance lo escrito,
    /// una reserva o un consumidor nuevo ven todo lo enviado).</summary>
    public long ForwardedBytes { get; private set; }

    /// <summary>En qué está el bucle que drena al receptor (diagnóstico): «esperando al receptor», «leyendo» (a la espera de
    /// bytes del receptor), «alineando» o «repartiendo».</summary>
    public string PumpStage { get; private set; } = "esperando al receptor";

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

    /// <summary>
    /// Reserva para el PRÓXIMO socket que conecte al <see cref="ConsumerPort"/> la instantánea del pre-roll de este
    /// instante (desde ya recibe también el flujo en vivo, así no pierde ni duplica nada) y devuelve cuántos frames de
    /// vídeo contiene y si ese consumidor ya va al día. El motor lo llama al construir el proceso: con el número exacto,
    /// el preview de ese proceso se salta el pre-roll (que la grabación sí necesita) en vez de reproducirlo a ×3–×4
    /// hasta alcanzar el directo; y con <see cref="RelayReservation.IsLive"/> sabe cuándo el proceso nuevo ya lee en
    /// directo y puede tomar el relevo del viejo sin avance rápido. 0 frames sin flujo (el consumidor reservado
    /// recibirá lo que llegue).
    /// </summary>
    public RelayReservation ReserveConsumer()
    {
        lock (_sync)
        {
            var consumer = NewConsumer();
            _reserved.Enqueue((consumer, Stopwatch.GetTimestamp()));
            int frames = _preroll.Sum(p => p.VideoFrames);
            _log.LogDebug("Relé {Name}: consumidor reservado; pre-roll de {Frames} frames de vídeo ({Bytes} bytes).", _name, frames, _prerollLength);
            return new RelayReservation(frames, () => consumer.IsLive);
        }
    }

    /// <summary>Alta + instantánea del pre-roll, atómicas respecto a cada fragmento entrante. Bajo <see cref="_sync"/>.</summary>
    private Consumer NewConsumer()
    {
        var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(ConsumerQueueCapacity)
        {
            SingleReader = true, SingleWriter = true,
            // Wait y NO DropWrite: con los modos Drop*, TryWrite devuelve true también cuando descarta y el
            // desbordamiento sería invisible. Con Wait, TryWrite devuelve false con la cola llena.
            FullMode = BoundedChannelFullMode.Wait,
        });
        var consumer = new Consumer(queue, _preroll.Select(p => p.Data).ToArray());
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
            consumer.Closed = true;
            consumer.Queue.Writer.TryComplete();
            _log.LogDebug("Relé {Name}: una reserva de consumidor caducó sin conexión.", _name);
        }
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
                PumpStage = "leyendo";
                int n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) return; // el receptor cerró (el emisor se fue o el proceso se relanza)
                SourceBytes += n;
                PumpStage = "alineando";
                // Paquetes TS completos, con el audio ya realineado si el emisor trae relojes distintos; vacío mientras
                // se espera el resto de un paquete partido o se retiene el audio hasta ver el primer PTS de vídeo.
                var chunk = _aligner.Process(buffer.AsSpan(0, n), Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
                if (chunk.Length == 0) continue;

                bool dropped = false;
                PumpStage = "repartiendo";
                lock (_sync)
                {
                    long now = Stopwatch.GetTimestamp();
                    AddToPreroll(chunk, now);
                    TrimPreroll(now);
                    ExpireReservations(now);

                    foreach (var c in _consumers)
                        if (!c.Queue.Writer.TryWrite(chunk)) dropped = true;
                    ForwardedBytes += chunk.Length;
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

    /// <summary>Añade el fragmento a la ventana, partido donde empieza cada fotograma clave de vídeo, para que la ventana
    /// pueda arrancar justo ahí. Bajo <see cref="_sync"/>.</summary>
    private void AddToPreroll(byte[] chunk, long now)
    {
        int start = 0, frames = 0;
        bool startsWithKeyframe = false;
        long startDts = 0;
        for (int offset = 0; offset + TsAudioClockAligner.PacketSize <= chunk.Length; offset += TsAudioClockAligner.PacketSize)
        {
            var packet = chunk.AsSpan(offset, TsAudioClockAligner.PacketSize);
            if (!IsVideoPesStart(packet)) continue;
            if (IsRandomAccess(packet) && TsAudioClockAligner.TryReadPesTimestamps(packet, out _, out long dts))
            {
                if (offset > start) Append(chunk[start..offset], startsWithKeyframe, startDts, frames);
                start = offset; startsWithKeyframe = true; startDts = dts; frames = 0;
            }
            frames++;
        }
        if (start < chunk.Length) Append(chunk[start..], startsWithKeyframe, startDts, frames);

        void Append(byte[] data, bool keyframe, long dts, int videoFrames)
        {
            _preroll.Add(new PrerollEntry(data, now, keyframe, dts, videoFrames));
            _prerollLength += data.Length;
        }
    }

    /// <summary>Recorta la ventana: respeta el tope de bytes y la hace empezar en el último fotograma clave que ya tiene
    /// <see cref="PrerollWindow"/> de vídeo por detrás (o en el primero que haya, si el GOP es más largo). Sin fotogramas
    /// clave, descarta por reloj de llegada. Bajo <see cref="_sync"/>.</summary>
    private void TrimPreroll(long now)
    {
        int head = 0;
        long length = _prerollLength;
        while (length > PrerollMaxBytes && head < _preroll.Count - 1) length -= _preroll[head++].Data.Length;

        long windowTicks = (long)(PrerollWindow.TotalSeconds * 90_000);
        long lastDts = _aligner.LastVideoDts;
        int keyframe = -1;
        for (int i = _preroll.Count - 1; i >= head; i--)
            if (_preroll[i].Keyframe && Signed33(lastDts - _preroll[i].Dts) >= windowTicks) { keyframe = i; break; }
        if (keyframe < 0)
            for (int i = head; i < _preroll.Count; i++)
                if (_preroll[i].Keyframe) { keyframe = i; break; }

        if (keyframe >= 0) head = keyframe;
        else while (head < _preroll.Count - 1 && Stopwatch.GetElapsedTime(_preroll[head].Timestamp, now) > PrerollWindow) head++;

        if (head == 0) return;
        for (int i = 0; i < head; i++) _prerollLength -= _preroll[i].Data.Length;
        _preroll.RemoveRange(0, head);
    }

    /// <summary>¿Abre este paquete un PES de vídeo (payload_unit_start en el PID de vídeo)? Uno por frame.</summary>
    private static bool IsVideoPesStart(ReadOnlySpan<byte> packet)
    {
        if (packet[0] != 0x47 || (packet[1] & 0x40) == 0) return false;
        return (((packet[1] & 0x1F) << 8) | packet[2]) == TsAudioClockAligner.VideoPid;
    }

    /// <summary>¿Lleva random_access_indicator (campo de adaptación)? El muxer mpegts de FFmpeg lo pone en el paquete que
    /// abre el PES de cada fotograma clave del vídeo (y en todos los de audio, que aquí no cuentan). Medido con el
    /// FFmpeg empaquetado.</summary>
    private static bool IsRandomAccess(ReadOnlySpan<byte> packet)
    {
        int adaptation = (packet[3] >> 4) & 0x3;
        return (adaptation & 0x2) != 0 && packet[4] > 0 && (packet[5] & 0x40) != 0;
    }

    /// <summary>Diferencia con signo entre dos marcas de 33 bits (dan la vuelta cada 26,5 h).</summary>
    private static long Signed33(long difference)
    {
        const long modulo = 1L << 33;
        long d = difference & (modulo - 1);
        return d >= modulo / 2 ? d - modulo : d;
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

            // El socket se lleva la reserva más antigua si la hay (su instantánea es la del momento de reservar, y su cola
            // ya trae lo llegado desde entonces); si no, alta + instantánea del pre-roll de este instante, en una
            // operación atómica (ver _sync): todo lo anterior viaja en la instantánea y todo lo posterior en la cola.
            Consumer consumer;
            bool reserved;
            lock (_sync)
            {
                reserved = _reserved.TryDequeue(out var reservation);
                consumer = reserved ? reservation.Consumer : NewConsumer();
            }
            _log.LogDebug("Relé {Name}: consumidor conectado (puerto {Port}{Reserved}); pre-roll de {Bytes} bytes.", _name, ConsumerPort, reserved ? ", reservado" : "", consumer.Preroll.Sum(c => c.Length));
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
                // Writing marca cada escritura hasta que el socket la acepta: mientras el consumidor va atrasado (digiere el
                // pre-roll más deprisa que el directo pero aún no lo ha agotado) la escritura queda pendiente y su cola crece;
                // «al día» = pre-roll entregado, nada en curso y cola vacía (ver RelayReservation.IsLive).
                consumer.Writing = true;
                foreach (var chunk in consumer.Preroll)
                    await stream.WriteAsync(chunk, ct).ConfigureAwait(false);
                consumer.PrerollSent = true;
                consumer.Writing = false;
                await foreach (var chunk in consumer.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    consumer.Writing = true;
                    await stream.WriteAsync(chunk, ct).ConfigureAwait(false);
                    consumer.Writing = false;
                }
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* cierre del relé */ }
        catch (Exception ex) { _log.LogDebug(ex, "Relé {Name}: el consumidor cerró el socket.", _name); }
        finally
        {
            consumer.Closed = true;
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
            _reserved.Clear(); // sus consumidores están en la lista: se cierran con los demás
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

/// <summary>
/// Reserva de un consumidor del relé (ver <see cref="NetworkStreamRelay.ReserveConsumer"/>): cuántos frames de vídeo trae
/// la instantánea del pre-roll que recibirá (los que su preview debe saltarse) y si ese consumidor YA VA AL DÍA.
/// </summary>
public sealed class RelayReservation
{
    private readonly Func<bool> _isLive;

    internal RelayReservation(int videoFrames, Func<bool> isLive)
    {
        VideoFrames = videoFrames;
        _isLive = isLive;
    }

    /// <summary>PES de vídeo (frames) de la instantánea del pre-roll reservada.</summary>
    public int VideoFrames { get; }

    /// <summary>
    /// ¿El consumidor consume ya el flujo EN DIRECTO? True cuando conectó, recibió el pre-roll entero y no tiene nada
    /// pendiente (ni una escritura en curso ni fragmentos en su cola): todo lo que el relé ha recibido está ya en manos
    /// del proceso. Mientras digiere el pre-roll —y el atraso que acumula entre tanto— más deprisa que el directo, hay
    /// fragmentos esperando y esto es false. También true si el consumidor ya se cerró o la reserva caducó (no hay nada
    /// que esperar de él). Un instante «al día» entre dos fragmentos no basta: el motor lo exige sostenido.
    /// </summary>
    public bool IsLive => _isLive();
}
