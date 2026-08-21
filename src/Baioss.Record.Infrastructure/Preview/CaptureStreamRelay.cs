using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Baioss.Record.Infrastructure.Preview;

/// <summary>
/// Relé del flujo YA CODIFICADO (MPEG-TS) que emite el proceso de captura PERMANENTE hacia el proceso de
/// GRABACIÓN. Es la pieza que permite abrir el dispositivo UNA SOLA VEZ: sin ella, iniciar/detener grabación
/// obliga a reemplazar el proceso FFmpeg y por tanto a REABRIR la tarjeta (en una DeckLink con el conector en
/// modo compartido eso invierte la dirección del BNC y el receptor SDI debe re-enganchar ≈1 s, que se graba
/// como negro/barras al principio del archivo).
///
/// <para>Monta DOS servidores TCP en loopback:</para>
/// <list type="bullet">
/// <item><b>Puerto de origen</b>: se conecta el proceso de captura (permanente) y empuja el TS. Se drena
/// SIEMPRE, se grabe o no —si dejáramos de leer, FFmpeg se bloquearía al escribir y ARRASTRARÍA también al
/// preview y a los detectores—.</item>
/// <item><b>Puerto de grabación</b>: se conecta el proceso de grabación mientras dura la grabación. Al detener
/// se cierra el socket: FFmpeg ve EOF y FINALIZA el contenedor correctamente (moov), igual que con la «q».</item>
/// </list>
///
/// <para>PRE-ROLL: se conserva una ventana de los últimos <see cref="PrerollBytes"/> bytes y se vuelca al
/// grabador nada más conectarse. Sin ella, FFmpeg tendría que esperar al siguiente fotograma clave para
/// empezar a copiar y se PERDERÍA hasta un GOP de contenido; con ella la grabación arranca en el keyframe
/// ANTERIOR, así que empieza un poco ANTES de pulsar Grabar y NUNCA se pierde material. No hace falta
/// interpretar el TS: FFmpeg sincroniza solo en el primer keyframe del volcado (verificado: el primer frame
/// del MP4 resultante es I).</para>
///
/// <para>CONTRAPRESIÓN: el grabador escribe a disco y podría atascarse. NUNCA se bloquea al proceso de
/// captura por culpa del grabador (tumbaría la captura de ese canal); si la cola al grabador se llena, se
/// DESCARTA y se avisa con <see cref="RecorderOverflow"/> —la grabación tendrá un salto, pero la captura, el
/// preview y los demás canales siguen intactos—.</para>
/// </summary>
public sealed class CaptureStreamRelay : IAsyncDisposable
{
    private readonly ILogger _log;
    private readonly string _channelKey;
    private readonly TcpListener _sourceListener;
    private readonly TcpListener _recorderListener;
    private readonly CancellationTokenSource _cts = new();

    // Ventana de pre-roll (bytes recientes) y cola hacia el grabador, bajo UN MISMO candado: la instantánea
    // del pre-roll y la creación de la cola deben ser ATÓMICAS respecto a cada fragmento entrante. Con candados
    // separados, los fragmentos llegados entre BeginRecording y la conexión del grabador acababan en la ventana
    // Y TAMBIÉN en la cola → se escribían DOS veces y la grabación arrancaba con un tramo de TS duplicado
    // (discontinuidades/glitch al inicio de cada pieza).
    private readonly object _sync = new();
    private readonly Queue<byte[]> _preroll = new();
    private long _prerollLength;
    private Delivery? _delivery;

    /// <summary>Entrega de UNA grabación: la instantánea del pre-roll tomada al empezar y la cola del flujo en
    /// vivo desde ese instante exacto. Van emparejadas para que no haya ni hueco ni solape entre ambas.</summary>
    private sealed record Delivery(byte[][] Preroll, Channel<byte[]> Queue);

    // Aviso de descartes con freno: durante un atasco de disco se descartan MUCHOS fragmentos seguidos; un log
    // por fragmento sería una tormenta. El evento sí se eleva por fragmento (el motor acumula la cuenta).
    private DateTimeOffset _lastOverflowWarnUtc;

    private Task? _sourceLoop;
    private Task? _recorderLoop;

    public CaptureStreamRelay(string channelKey, ILogger log)
    {
        _channelKey = channelKey;
        _log = log;
        _sourceListener = new TcpListener(IPAddress.Loopback, 0);
        _recorderListener = new TcpListener(IPAddress.Loopback, 0);
        _sourceListener.Start();
        _recorderListener.Start();
        SourcePort = ((IPEndPoint)_sourceListener.LocalEndpoint).Port;
        RecorderPort = ((IPEndPoint)_recorderListener.LocalEndpoint).Port;
    }

    /// <summary>Puerto al que ESCRIBE el proceso de captura permanente (destino <c>-f mpegts tcp://…</c>).</summary>
    public int SourcePort { get; }

    /// <summary>Puerto del que LEE el proceso de grabación (entrada <c>-i tcp://…</c>).</summary>
    public int RecorderPort { get; }

    /// <summary>
    /// Ventana de pre-roll en bytes. Se dimensiona con <see cref="PrerollBytesFor"/> a partir del bitrate del
    /// perfil para que equivalga SIEMPRE a los mismos segundos, independientemente de la calidad configurada
    /// (con un tamaño fijo, un perfil de bitrate bajo daba una ventana de muchos segundos y el archivo empezaba
    /// mucho antes de lo pedido).
    /// </summary>
    public int PrerollBytes { get; set; } = 6 * 1024 * 1024;

    /// <summary>
    /// Bytes de pre-roll equivalentes a <paramref name="seconds"/> del bitrate total dado. Debe cubrir MÁS de un
    /// GOP para que el grabador encuentre un fotograma clave en la ventana (si no, esperaría al siguiente y se
    /// perdería contenido). Acotado a [1 MB, 32 MB] para no disparar la memoria con bitrates altos. Pura, testeable.
    /// </summary>
    internal static int PrerollBytesFor(long totalBitsPerSecond, double seconds = 2.5)
    {
        if (totalBitsPerSecond <= 0 || seconds <= 0) return 1024 * 1024;
        double bytes = totalBitsPerSecond / 8.0 * seconds;
        return (int)Math.Clamp(bytes, 1024 * 1024, 32 * 1024 * 1024);
    }

    /// <summary>Máximo de fragmentos pendientes de entregar al grabador antes de descartar (contrapresión).</summary>
    public int RecorderQueueCapacity { get; init; } = 512;

    /// <summary>Se eleva si hubo que DESCARTAR datos porque el grabador no drenaba (grabación con salto).</summary>
    public event EventHandler? RecorderOverflow;

    /// <summary>¿Está el proceso de captura permanente conectado y entregando flujo?</summary>
    public bool SourceConnected { get; private set; }

    /// <summary>Bytes recibidos del proceso de captura desde el arranque (diagnóstico).</summary>
    public long SourceBytes { get; private set; }

    /// <summary>Arranca los bucles de aceptación. No bloquea.</summary>
    public void Start()
    {
        _sourceLoop ??= Task.Run(() => AcceptSourceLoopAsync(_cts.Token));
        _recorderLoop ??= Task.Run(() => AcceptRecorderLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Habilita la entrega al grabador: a partir de aquí, cuando el proceso de grabación se conecte recibirá el
    /// pre-roll y el flujo en vivo. Se llama ANTES de lanzar el proceso de grabación.
    /// </summary>
    public void BeginRecording()
    {
        lock (_sync)
        {
            var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(RecorderQueueCapacity)
            {
                SingleReader = true,
                SingleWriter = true,
                // Wait y NO DropWrite: con los modos Drop*, TryWrite devuelve true TAMBIÉN cuando descarta, así
                // que el desbordamiento era indetectable y la alarma de overflow no podía dispararse jamás. Con
                // Wait, TryWrite —que nunca bloquea— devuelve false con la cola llena: el fragmento lo
                // descartamos NOSOTROS (la captura jamás se frena) y además nos enteramos para avisar.
                FullMode = BoundedChannelFullMode.Wait,
            });
            // Instantánea del pre-roll EN ESTE instante, emparejada con la cola: todo lo anterior viaja en la
            // instantánea y todo lo posterior en la cola (el bombeo añade y encola bajo este mismo candado),
            // así que el grabador recibe un flujo contiguo sin bytes duplicados ni huecos.
            _delivery = new Delivery(_preroll.ToArray(), queue);
        }
    }

    /// <summary>
    /// Deja de entregar al grabador y CIERRA su cola: el bucle de escritura cerrará el socket, FFmpeg verá EOF
    /// y finalizará el contenedor (moov). Se llama al detener la grabación.
    /// </summary>
    public void EndRecording()
    {
        Delivery? d;
        lock (_sync) { d = _delivery; _delivery = null; }
        d?.Queue.Writer.TryComplete();
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
                _log.LogInformation("Canal {Key}: proceso de captura permanente conectado al relé (puerto {Port}).", _channelKey, SourcePort);
                await PumpSourceAsync(client.GetStream(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Canal {Key}: relé, fallo aceptando la captura.", _channelKey); }
            finally
            {
                SourceConnected = false;
                // La fuente se cerró (proceso de captura reemplazado o caído): su flujo ya NO empalma con el del
                // proceso siguiente (otra base de tiempos y quizá otro perfil). Si la ventana sobreviviera, la
                // próxima grabación arrancaría volcando TS VIEJO delante del flujo nuevo → se vacía.
                lock (_sync) { _preroll.Clear(); _prerollLength = 0; }
            }
        }
    }

    /// <summary>Drena el flujo del proceso de captura SIEMPRE (se grabe o no) y lo reparte: ventana de pre-roll
    /// + cola del grabador si hay grabación en curso.</summary>
    private async Task PumpSourceAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (n <= 0) return; // el proceso de captura cerró (murió o lo reemplazamos): se re-aceptará
                SourceBytes += n;

                var chunk = new byte[n];
                Buffer.BlockCopy(buffer, 0, chunk, 0, n);

                // Ventana de pre-roll y cola en UNA operación atómica (ver _sync): cada fragmento va a la
                // ventana (acotada por bytes, descarta lo más antiguo) y, si hay grabación, a su cola.
                bool dropped;
                lock (_sync)
                {
                    _preroll.Enqueue(chunk);
                    _prerollLength += chunk.Length;
                    while (_prerollLength > PrerollBytes && _preroll.Count > 0)
                        _prerollLength -= _preroll.Dequeue().Length;

                    dropped = _delivery is not null && !_delivery.Queue.Writer.TryWrite(chunk);
                }
                if (dropped)
                {
                    // Cola llena: el grabador no drena (disco atascado). Se descarta para NO frenar la captura.
                    var now = DateTimeOffset.UtcNow;
                    if (now - _lastOverflowWarnUtc > TimeSpan.FromSeconds(5))
                    {
                        _lastOverflowWarnUtc = now;
                        _log.LogWarning("Canal {Key}: el grabador no drena el flujo; se descartan datos (la grabación tendrá un salto).", _channelKey);
                    }
                    RecorderOverflow?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    private async Task AcceptRecorderLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = await _recorderListener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                client.NoDelay = true;
                _log.LogInformation("Canal {Key}: proceso de grabación conectado al relé (puerto {Port}).", _channelKey, RecorderPort);
                await PumpRecorderAsync(client.GetStream(), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { _log.LogDebug(ex, "Canal {Key}: relé, fallo atendiendo al grabador.", _channelKey); }
        }
    }

    /// <summary>
    /// Vuelca el pre-roll y luego el flujo en vivo al grabador. Termina cuando <see cref="EndRecording"/> cierra
    /// la cola (→ se cierra el socket → FFmpeg finaliza el archivo) o cuando el grabador se desconecta.
    /// </summary>
    private async Task PumpRecorderAsync(NetworkStream stream, CancellationToken ct)
    {
        Delivery? d;
        lock (_sync) d = _delivery;
        if (d is null) return; // ya se detuvo antes de que conectara: cerrar sin escribir

        try
        {
            // Pre-roll: la instantánea tomada en BeginRecording — arranca desde el keyframe ANTERIOR para no
            // perder contenido al iniciar, y empalma SIN solape con la cola (se congelaron juntas).
            foreach (var chunk in d.Preroll)
                await stream.WriteAsync(chunk, ct).ConfigureAwait(false);

            await foreach (var chunk in d.Queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                await stream.WriteAsync(chunk, ct).ConfigureAwait(false);

            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* cierre */ }
        catch (IOException ex) { _log.LogDebug(ex, "Canal {Key}: el grabador cerró el socket.", _channelKey); }
    }

    public async ValueTask DisposeAsync()
    {
        EndRecording();
        await _cts.CancelAsync().ConfigureAwait(false);
        try { _sourceListener.Stop(); } catch { /* noop */ }
        try { _recorderListener.Stop(); } catch { /* noop */ }
        foreach (var t in new[] { _sourceLoop, _recorderLoop })
        {
            if (t is null) continue;
            try { await t.ConfigureAwait(false); } catch { /* los bucles no propagan */ }
        }
        _cts.Dispose();
        lock (_sync) { _preroll.Clear(); _prerollLength = 0; }
    }
}
