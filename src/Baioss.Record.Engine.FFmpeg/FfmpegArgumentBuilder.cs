using System.Globalization;
using System.Text;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Localization;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Construye el argv de FFmpeg para una sesión de grabación. Devuelve tokens (no una
/// cadena de shell) para evitar problemas de quoting/inyección.
///
/// Estrategia de salida:
///  - solo grabación  → salida directa (más simple y robusta);
///  - con segmentación → muxer <c>segment</c> (prefijo de tiempo + contador + reset_timestamps);
///  - con streaming    → muxer <c>tee</c> (grabación + N destinos comparten un encode);
///  - con proxy        → salida adicional escalada (split en filter_complex).
///
/// El nombre del archivo principal lo resuelve el llamador (<see cref="OutputFilePath"/>)
/// para no depender de strftime fuera del muxer segment.
/// </summary>
public sealed class FfmpegArgumentBuilder
{
    private ICaptureSource? _source;
    private RecordingProfile? _profile;
    private string _outputDirectory = ".";
    private string _proxyDirectory = ".";
    private string _channelKey = "A";
    private string _previewSink = "";
    private bool _analyze = true;
    private string? _baseName;
    private int _segmentStartNumber = 1;
    private bool _fragmentedMp4 = true; // true = fMP4 robusto ante corte; false = MP4 estándar (moov, seekable, sin remux)
    private TimeSpan _writeQueue = TimeSpan.Zero; // cola de escritura de la salida de grabación (0 = la de FFmpeg, 8 paquetes)
    private TimeSpan? _plannedRemaining; // lo que le queda a la grabación hasta su fin previsto, desde que arranca este proceso
    private bool _fieldRatePreview; // preview de una señal entrelazada: un cuadro por campo (true) o el primer campo de cada cuadro

    // Filtros de análisis de señal (siempre activos, alimentan alarmas): negro/congelado en vídeo,
    // silencio en audio. Umbrales broadcast típicos: 2 s sostenidos.
    private const string BlackDetect = "blackdetect=d=2:pix_th=0.10";
    private const string FreezeDetect = "freezedetect=n=-60dB:d=2";
    private const string SilenceDetect = "silencedetect=n=-50dB:d=2";

    /// <summary>Ruta absoluta del archivo principal cuando NO hay segmentación (vacío si se segmenta).</summary>
    public string OutputFilePath { get; private set; } = "";

    /// <summary>True si la última construcción produjo salida segmentada (muxer <c>segment</c>).</summary>
    public bool IsSegmentedOutput { get; private set; }

    /// <summary>Patrón glob de los archivos de segmento del canal (p. ej. <c>A_*.mov</c>) para vigilarlos.</summary>
    public string SegmentFileGlob { get; private set; } = "";

    /// <summary>
    /// Glob de los segmentos de ESTE proceso: su base real (la elegida, o <c>{canal}_{fecha_hora}</c> por defecto) +
    /// <c>_*</c>. Sin nombre, <see cref="SegmentFileGlob"/> abarca TODO el histórico del canal en la carpeta (meses de
    /// archivos en 24/7); el motor usa este para medir y vigilar solo lo de la sesión. Vacío si la salida no es segmentada.
    /// </summary>
    public string SegmentPrefixGlob { get; private set; } = "";

    public FfmpegArgumentBuilder From(ICaptureSource source) { _source = source; return this; }
    public FfmpegArgumentBuilder Using(RecordingProfile profile) { _profile = profile; return this; }
    public FfmpegArgumentBuilder ToDirectory(string dir) { _outputDirectory = dir; return this; }
    public FfmpegArgumentBuilder ProxyToDirectory(string dir) { _proxyDirectory = dir; return this; }
    public FfmpegArgumentBuilder ForChannel(string key) { _channelKey = key; return this; }

    /// <summary>Destino (URL FFmpeg, p. ej. <c>tcp://127.0.0.1:9001</c>) de los frames BGRA de preview.</summary>
    public FfmpegArgumentBuilder WithPreviewSink(string ffmpegUrl) { _previewSink = ffmpegUrl; return this; }

    /// <summary>Inserta (o no) los filtros de análisis de señal (negro/congelado/silencio) en <see cref="BuildLive"/>.</summary>
    public FfmpegArgumentBuilder WithSignalAnalysis(bool on) { _analyze = on; return this; }

    /// <summary>Modo de contenedor MP4/MOV: <c>true</c> (por defecto) fMP4 fragmentado (robusto ante corte
    /// eléctrico/kill, seek por estimación → se remuxea a faststart al detener); <c>false</c> MP4 ESTÁNDAR con
    /// el <c>moov</c> al final (100% seekable en reproducción local, cierre rápido y SIN remux, pero un corte
    /// abrupto ANTES del cierre limpio deja el archivo sin índice → apto para máquinas con SAI/UPS).</summary>
    public FfmpegArgumentBuilder WithFragmentedMp4(bool on) { _fragmentedMp4 = on; return this; }

    /// <summary>
    /// Cola de ESCRITURA de la salida de grabación de <see cref="BuildLive"/>/<see cref="BuildSlate"/>: cuánto material ya
    /// codificado puede esperar a que el disco acepte escrituras sin frenar al resto del proceso. Sin ella (la de FFmpeg es
    /// de 8 paquetes), un disco que tarda unos segundos en responder —uno que despierta de la suspensión, por ejemplo— para
    /// el proceso ENTERO: el preview se congela con la grabación y la entrada deja de leerse (con el relé DeckLink, a ~1 s
    /// se descartan frames y la grabación tiene un hueco; medido en producción). Se emite como <c>-thread_queue_size</c> de
    /// salida, que FFmpeg cuenta en PAQUETES (ver <see cref="WriteQueuePackets"/>); solo existe desde FFmpeg 6 y el llamador
    /// debe comprobar que el FFmpeg instalado la admite. <see cref="TimeSpan.Zero"/> = sin cola (lo de siempre).
    /// </summary>
    public FfmpegArgumentBuilder WithWriteQueue(TimeSpan time) { _writeQueue = time; return this; }

    /// <summary>
    /// Nombre base del archivo (sin extensión) elegido por el operador (manual) o derivado de la
    /// programación (<c>dd-MM-yyyy_Título</c>). <c>null</c> = nombre por defecto <c>{canal}_{fecha_hora}</c>.
    /// Para salida segmentada el contador de segmento se añade como <c>_1, _2…</c> (1-based) en lugar del
    /// prefijo de tiempo + <c>%03d</c>. El llamador es responsable de pasar un nombre ÚNICO (sin colisión).
    /// </summary>
    public FfmpegArgumentBuilder WithBaseName(string? name)
    { _baseName = string.IsNullOrWhiteSpace(name) ? null : name.Trim(); return this; }

    /// <summary>Número del PRIMER segmento (1-based). Permite numeración continua entre reinicios/slate.</summary>
    public FfmpegArgumentBuilder WithSegmentStartNumber(int n)
    { _segmentStartNumber = n < 1 ? 1 : n; return this; }

    /// <summary>
    /// Lo que le queda a la grabación hasta su fin previsto (<see cref="SegmentationPolicy.PlannedEnd"/>) contado desde que
    /// arranca ESTE proceso, o null si no hay fin previsto. Con corte por duración, el corte que caería junto a ese fin no se
    /// hace (ver <see cref="SegmentSplitTimes"/>).
    /// </summary>
    public FfmpegArgumentBuilder WithPlannedRemaining(TimeSpan? remaining) { _plannedRemaining = remaining; return this; }

    /// <summary>
    /// Preview de una señal ENTRELAZADA (ver <see cref="PreviewDeinterlaceFilter"/>): <c>false</c> (por defecto) el primer campo
    /// de cada cuadro (29.97 desde un 1080i59.94: sin fantasma y con el coste de siempre; en SD, el cuadro reconstruido);
    /// <c>true</c> un cuadro por CAMPO (59.94, el movimiento de la tele, al doble de frames que procesar, copiar y subir a la
    /// pantalla). Señal progresiva: sin efecto.
    /// </summary>
    public FfmpegArgumentBuilder WithFieldRatePreview(bool on) { _fieldRatePreview = on; return this; }

    /// <summary>
    /// Frames de preview por cada frame de la fuente en la última construcción: 2 si el preview saca un cuadro por campo (señal
    /// entrelazada), 1 si no. FFmpeg cuenta el progreso (<c>frame</c>, <c>fps</c>) en la primera salida de vídeo, que es el
    /// preview: quien lo lea lo divide por esto para seguir contando frames de la fuente; y el preview llega a la cadencia de la
    /// fuente multiplicada por esto.
    /// </summary>
    public int PreviewFramesPerSourceFrame { get; private set; } = 1;

    public IReadOnlyList<string> Build()
    {
        var source = _source ?? throw new InvalidOperationException("Falta la fuente de captura.");
        var profile = _profile ?? throw new InvalidOperationException("Falta el perfil.");
        var (recMux, recExt) = FfmpegCodecMap.Container(profile.Container);

        if (profile.AudioOnly)
            return BuildAudioOnly(source, profile, recMux, recExt);

        var args = new List<string>
        {
            // Sin -nostdin: el supervisor envía 'q' por stdin para el cierre ordenado.
            "-hide_banner",
            "-progress", "pipe:1", "-stats_period", "1",
        };

        // 1) Decode por hardware cuando aplica.
        args.AddRange(FfmpegCodecMap.HwAccelInput(profile.HwAccel));

        // 2) Entrada (cada ICaptureSource aporta sus flags de protocolo).
        args.AddRange(source.BuildInputArguments());

        // 2b) Audio multicanal: si la fuente entrega más de un estéreo, los canales se ELIGEN con pan (ver AudioRoutingFilter).
        string? audioFilter = AudioRoutingFilter(source, profile);

        // 3) Filtros: si hay proxy, burn-in, un tamaño elegido (escalado) o salida entrelazada.
        bool gpu = FfmpegCodecMap.IsGpuEncoder(profile.VideoCodec);
        bool hasProxy = profile.Proxy is not null;
        bool needFilter = hasProxy || profile.BurnTimecode
                          || profile.TargetResolution is not null
                          || profile.ScanType is not ScanType.Progressive;
        string mainVideo = "0:v:0";

        if (needFilter)
        {
            var (filter, mainLabel) = BuildFilterGraph(profile, gpu, hasProxy);
            args.Add("-filter_complex");
            args.Add(filter);
            mainVideo = mainLabel;
        }

        // 4) Mapeo principal video + audio (audio opcional con '?').
        args.Add("-map"); args.Add(mainVideo);
        args.Add("-map"); args.Add("0:a?");

        // 5) Encoders. Con pan, el nº de canales lo fija el filtro (no -ac).
        args.AddRange(VideoEncoderArgs(profile, profile.TargetResolution ?? source.CurrentSignal.Resolution,
            profile.OutputFrameRate ?? source.CurrentSignal.FrameRate));
        args.AddRange(AudioEncoderArgs(profile, channelCount: audioFilter is null));

        // 5b) Opciones de salida de video: frecuencia de cuadro y orden de campo (escaneo).
        if (profile.OutputFrameRate is { } fr)
        {
            args.Add("-r");
            args.Add(string.Create(CultureInfo.InvariantCulture, $"{fr.Numerator}/{fr.Denominator}"));
        }
        string? fieldOrder = profile.ScanType switch
        {
            ScanType.InterlacedTff => "tt",
            ScanType.InterlacedBff => "bb",
            _ => null // progresivo
        };
        if (fieldOrder is not null) { args.Add("-field_order"); args.Add(fieldOrder); }

        // 6) Salida principal.
        bool hasStreams = profile.StreamTargets.Any(t => t.Enabled);
        bool hasSegmentation = profile.Segmentation is { Trigger: SegmentTrigger.Duration or SegmentTrigger.WallClock };

        if (hasStreams)
        {
            if (audioFilter is not null) { args.Add("-af"); args.Add(audioFilter); }
            args.Add("-f"); args.Add("tee");
            args.Add(BuildTeeTarget(profile, recMux, recExt, hasSegmentation));
        }
        else if (hasSegmentation)
        {
            if (audioFilter is not null) { args.Add("-af"); args.Add(audioFilter); }
            args.AddRange(BuildSegmentOutput(profile, recMux, recExt));
        }
        else
        {
            // Salida directa a un único archivo.
            OutputFilePath = Path.Combine(_outputDirectory, SingleFileName(recExt));
            // Medición de loudness/true-peak para los medidores VU (se emite por stderr), tras elegir los canales.
            args.Add("-af"); args.Add(audioFilter is null ? "ebur128=peak=true" : $"{audioFilter},ebur128=peak=true");
            args.AddRange(RobustMovFlags(profile.Container));
            args.Add("-f"); args.Add(recMux);
            args.Add("-y"); args.Add(OutputFilePath);
        }

        // 7) Proxy como salida adicional.
        if (hasProxy)
        {
            var p = profile.Proxy!;
            var (proxMux, proxExt) = FfmpegCodecMap.Container(p.Container);
            args.Add("-map"); args.Add("[proxout]");
            args.Add("-map"); args.Add("0:a?");
            // Con una fuente multicanal el proxy lleva el primer par elegido: el audio crudo serían 8 canales en un AAC
            // de 128 k, y con 16 el codificador ni siquiera abre.
            if (audioFilter is not null) { args.Add("-af"); args.Add(MeterPan(source)); }
            args.AddRange(new[]
            {
                "-c:v", FfmpegCodecMap.VideoEncoder(p.Codec),
                "-b:v", p.Bitrate.BitsPerSecond.ToString(CultureInfo.InvariantCulture),
                "-c:a", "aac", "-b:a", "128k",
                "-f", proxMux, "-y",
                Path.Combine(_proxyDirectory, $"{_channelKey}_proxy.{proxExt}")
            });
        }

        return args;
    }

    /// <summary>
    /// Construye el argv del proceso ÚNICO de captura de un canal: abre la fuente una sola vez y la
    /// bifurca (<c>split</c>) en preview (BGRA al destino de <see cref="WithPreviewSink"/>) + medidores
    /// (ebur128) y, cuando <paramref name="recording"/> es true, también la salida de grabación. Así
    /// preview y grabación coexisten sobre un dispositivo en vivo, que no admite dos aperturas.
    /// </summary>
    public IReadOnlyList<string> BuildLive(bool recording, int previewWidth, int previewHeight)
    {
        var source = _source ?? throw new InvalidOperationException("Falta la fuente de captura.");
        var profile = _profile ?? throw new InvalidOperationException("Falta el perfil.");
        if (string.IsNullOrEmpty(_previewSink))
            throw new InvalidOperationException("Falta el destino de preview (WithPreviewSink).");

        IsSegmentedOutput = false;

        // Una fuente sin pista de audio (cámara/dispositivo solo-vídeo) no admite el medidor ebur128
        // ni un mapeo de audio: un output solo-audio sin streams haría abortar a FFmpeg.
        bool hasAudio = source.CurrentSignal.HasAudio;
        // De qué entrada FFmpeg sale el audio: 0:a junto al vídeo (dshow/decklink/archivo) o 1:a en una
        // entrada aparte (NDI sirve vídeo y audio por sockets separados). Lo decide la fuente.
        string audioMap = $"{source.AudioInputIndex}:a:0?";
        // Audio multicanal (DeckLink con 8/16 canales, NDI multicanal): los canales a grabar se ELIGEN con pan dentro
        // del grafo (un flujo por pista según el modo del perfil) y se reparten con asplit a medidores y grabación.
        // Los medidores miden TODOS los canales capturados (ebur128 da un true-peak por canal) y el detector de
        // silencio vigila el primer par elegido. Con 2 canales, la tubería de siempre.
        var routes = hasAudio ? AudioRoutes(source, profile) : null;
        string meterChain = routes is null
            ? (_analyze ? $"{SilenceDetect},ebur128=peak=true" : "ebur128=peak=true")
            : MeterChain(source, _analyze);

        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-stats_period", "1" };
        args.AddRange(FfmpegCodecMap.HwAccelInput(profile.HwAccel));
        args.AddRange(source.BuildInputArguments());

        // El preview lleva, opcionalmente, los detectores de negro/congelado (pasan los frames sin
        // alterarlos: solo registran marcas por stderr que se convierten en alarmas). Si la fuente entrega un pre-roll
        // (relé de una entrada de red: la grabación empieza antes del botón), el preview se lo salta —antes de escalar y de
        // los detectores, que no deben ver material repetido—: pintarlo era un rebobinado a ×3–×4 en cada Grabar/Detener.
        // Una señal entrelazada se desentrelaza después del salto (que cuenta frames de la fuente) y antes de reducirla.
        string analyze = _analyze ? $"{BlackDetect},{FreezeDetect}," : "";
        string skip = source.PreviewFramesToSkip > 0
            ? string.Create(CultureInfo.InvariantCulture, $"select=gte(n\\,{source.PreviewFramesToSkip}),") : "";
        string? previewDeinterlace = PreviewDeinterlaceFilter(source.CurrentSignal, previewWidth, previewHeight, _fieldRatePreview);
        PreviewFramesPerSourceFrame = previewDeinterlace is not null && _fieldRatePreview ? 2 : 1;
        string previewChain = string.Create(CultureInfo.InvariantCulture,
            $"{skip}{(previewDeinterlace is null ? "" : previewDeinterlace + ",")}scale={previewWidth}:{previewHeight},{analyze}format=bgra");
        var filter = new StringBuilder();
        string recLabel = "0:v:0";
        // Tamaño y cadencia de lo que se codifica: los del preset o, si los deja en nativo, los de la señal.
        var recFrame = profile.TargetResolution ?? source.CurrentSignal.Resolution;
        var recRate = profile.OutputFrameRate ?? source.CurrentSignal.FrameRate;

        if (recording && !profile.AudioOnly)
        {
            // Bifurca el vídeo: una rama a grabación (con su escala/escaneo/burn-in), otra a preview.
            var recChain = new List<string>();
            // Desentrelazar ANTES de escalar: escalar en vertical cuadros con dos campos entretejidos los mezcla.
            string? deinterlace = DeinterlaceFilter(source.CurrentSignal, profile);
            if (deinterlace is not null) recChain.Add(deinterlace);
            if (profile.TargetResolution is { } r)
                recChain.Add(string.Create(CultureInfo.InvariantCulture, $"scale={r.Width}:{r.Height}"));
            // …y convertir al formato del encoder DESPUÉS de bwdif, también sin tamaño fijo. bwdif sale en el formato en que
            // entra: sin un scale detrás, el format= final se lo impone y FFmpeg convierte la fuente ANTES de desentrelazar. Con
            // la DeckLink (uyvy422) y un destino 4:2:0, el croma de los dos campos se promediaba: una franja de color tan ancha
            // como el movimiento entre campos en cada borde que se mueve (medido: 26 px a 1500 px/s; croma a 32 dB de PSNR, 56
            // con el scale). Un scale sin tamaño deja a bwdif en el submuestreo de la fuente (yuv422p desde uyvy422, yuv420p
            // desde un H.264 4:2:0 de red, sin tocarlo) y hace él la conversión; si no hay nada que convertir, no hace nada.
            // (Un format=yuv422p fijo delante de bwdif no sirve: a una fuente 4:2:0 le mezclaría los campos al subirla a 4:2:2.)
            else if (deinterlace is not null)
                recChain.Add("scale");
            if (profile.ScanType is ScanType.InterlacedTff) recChain.Add("setfield=mode=tff");
            else if (profile.ScanType is ScanType.InterlacedBff) recChain.Add("setfield=mode=bff");
            if (profile.BurnTimecode)
            {
                // El timecode quemado avanza al ritmo REAL de la fuente/salida (no «25» fijo): con 25 fijo
                // derivaba en fuentes 29.97/30/50/60 y el campo FF nunca alcanzaba sus valores. (Auditoría #18.)
                int tcRate = Math.Max(1, (int)Math.Round(
                    (profile.OutputFrameRate ?? source.CurrentSignal.FrameRate ?? FrameRate.P25).Value, MidpointRounding.AwayFromZero));
                recChain.Add(string.Create(CultureInfo.InvariantCulture,
                    $"drawtext=timecode='00\\:00\\:00\\:00':rate={tcRate}:fontcolor=white:box=1:boxcolor=black@0.5:x=20:y=20"));
            }
            // SIEMPRE como último paso de la rama de grabación: convertir al formato del encoder. La fuente puede
            // entregar un pixel format que el encoder por hardware NO acepta (NDI sirve uyvy422 y h264_nvenc lo
            // rechaza con EINVAL/-22); al venir de un filter_complex FFmpeg no auto-inserta la conversión, así que
            // la hacemos explícita. Si el formato ya coincide, es un no-op barato.
            recChain.Add(string.Create(CultureInfo.InvariantCulture, $"format={RecordingFilterPixelFormat(profile, recFrame, recRate)}"));

            filter.Append("[0:v]split=2[vrec][vprev];");
            if (recChain.Count > 0)
            {
                filter.Append(CultureInfo.InvariantCulture, $"[vrec]{string.Join(',', recChain)}[vmain];");
                recLabel = "[vmain]";
            }
            else recLabel = "[vrec]";
            filter.Append(CultureInfo.InvariantCulture, $"[vprev]{previewChain}[pv]");
        }
        else
        {
            filter.Append(CultureInfo.InvariantCulture, $"[0:v]{previewChain}[pv]");
        }

        if (routes is not null)
        {
            // Rama de audio: selección de canales y, de ahí, medidores (y las pistas de grabación si procede). Los
            // medidores van DENTRO del grafo: un stream que sale de filter_complex no admite un -af aparte.
            filter.Append(';');
            filter.Append(AudioGraph(string.Create(CultureInfo.InvariantCulture, $"[{source.AudioInputIndex}:a:0]"),
                recording ? routes : null, meterChain, "arec"));
        }

        args.Add("-filter_complex"); args.Add(filter.ToString());

        // Salida A — preview BGRA por TCP (lo lee la app y lo sube a la textura/bitmap). Cada frame decodificado, una vez
        // (passthrough): el vsync CFR de rawvideo duplicaba o tiraba frames según las marcas de tiempo, y tras saltarse el
        // pre-roll rellenaría el hueco con copias del primer frame en directo.
        args.Add("-map"); args.Add("[pv]");
        args.Add("-fps_mode"); args.Add("passthrough");
        args.Add("-f"); args.Add("rawvideo");
        args.Add(_previewSink);

        // Salida B — medidores VU: ebur128 sobre el audio de entrada, descartado a null. Solo si la
        // fuente declara audio: sin pista, este output quedaría sin streams y FFmpeg abortaría todo
        // (preview incluido). Un dispositivo solo-vídeo simplemente no alimenta medidores.
        if (routes is not null)
        {
            args.Add("-map"); args.Add("[amout]");
            args.Add("-f"); args.Add("null"); args.Add("-");
        }
        else if (hasAudio)
        {
            args.Add("-map"); args.Add(audioMap);
            args.Add("-af"); args.Add(meterChain);
            args.Add("-f"); args.Add("null"); args.Add("-");
        }

        // Salida C — grabación (solo cuando se graba).
        if (recording)
        {
            var (recMux, recExt) = FfmpegCodecMap.Container(profile.Container);

            // Con selección de canales, la grabación toma las ramas [arecN] del grafo (una por pista, ya con los
            // canales elegidos); si no, el audio crudo de la entrada.
            var recAudio = routes is not null ? RouteLabels(routes, "arec") : new[] { audioMap };
            if (profile.AudioOnly)
            {
                foreach (var a in recAudio) { args.Add("-map"); args.Add(a); }
                args.AddRange(AudioEncoderArgs(profile, channelCount: routes is null));
                if (routes is not null) args.AddRange(TrackTitles(routes, profile.Container));
            }
            else
            {
                args.Add("-map"); args.Add(recLabel);
                if (hasAudio) foreach (var a in recAudio) { args.Add("-map"); args.Add(a); }
                args.AddRange(VideoEncoderArgs(profile, recFrame, recRate));
                if (hasAudio) args.AddRange(AudioEncoderArgs(profile, channelCount: routes is null));
                if (hasAudio && routes is not null) args.AddRange(TrackTitles(routes, profile.Container));
                if (profile.OutputFrameRate is { } fr)
                {
                    args.Add("-r");
                    args.Add(string.Create(CultureInfo.InvariantCulture, $"{fr.Numerator}/{fr.Denominator}"));
                }
                string? fieldOrder = profile.ScanType switch
                {
                    ScanType.InterlacedTff => "tt", ScanType.InterlacedBff => "bb", _ => null
                };
                if (fieldOrder is not null) { args.Add("-field_order"); args.Add(fieldOrder); }
            }
            // Solo esta salida escribe a disco: la cola la separa del preview y de la entrada (ver WithWriteQueue).
            double recFps = profile.AudioOnly ? 0 : (profile.OutputFrameRate ?? source.CurrentSignal.FrameRate)?.Value ?? 60;
            args.AddRange(WriteQueueArgs(recFps, profile.AudioOnly || hasAudio ? recAudio.Count : 0));
            args.AddRange(RecordOutputTail(profile, recMux, recExt));
        }

        return args;
    }

    /// <summary>
    /// Desentrelazado de la rama de grabación cuando la señal es ENTRELAZADA y el preset PROGRESIVO (todos los NVENC, los
    /// 1080p…). Sin él, los dos campos de un 1080i se grababan entretejidos en cuadros marcados progresivos: peine en todo lo
    /// que se mueve (medido con una DeckLink en 1080i59.94 y el preset NVENC 1080p29.97: idet, 300 de 301 cuadros TFF). El
    /// relé en crudo no conserva la marca de entrelazado en los cuadros, así que el orden de campos sale de la señal (lo que
    /// la tarjeta o el emisor dijeron al abrir) y se procesan todos los cuadros. Si el preset dobla la tasa de cuadro de la
    /// señal (1080i59.94 → 1080p59.94) sale un cuadro por CAMPO —movimiento real a 59.94— en vez de repetir cada cuadro
    /// entretejido. Señal progresiva o desconocida, o preset entrelazado: null (como siempre).
    /// </summary>
    internal static string? DeinterlaceFilter(SignalInfo signal, RecordingProfile profile)
    {
        if (profile.AudioOnly || profile.ScanType is not ScanType.Progressive) return null;
        string? parity = signal.Scan switch { ScanType.InterlacedTff => "tff", ScanType.InterlacedBff => "bff", _ => null };
        if (parity is null) return null;
        bool perField = profile.OutputFrameRate is { Value: > 0 } output && signal.FrameRate is { Value: > 0 } input
                        && Math.Abs(output.Value - 2 * input.Value) < 0.01 * output.Value;
        return $"bwdif=mode={(perField ? "send_field" : "send_frame")}:parity={parity}:deint=all";
    }

    /// <summary>
    /// Desentrelazado de la rama de PREVIEW cuando la señal es entrelazada (1080i de una DeckLink o de una entrada de red).
    /// Reducir a 640×360 un cuadro con los dos campos entretejidos los mezclaba: en movimiento, doble imagen (fantasma), y el
    /// movimiento avanzaba a 29.97 en vez de a los 59.94 de la tele.
    /// <list type="bullet">
    ///   <item>Con <paramref name="perField"/>, un cuadro por CAMPO (el preview va al doble de la tasa de cuadro de la señal):
    ///   primero se escala solo en HORIZONTAL (con la misma altura el escalado vertical es la identidad y cada línea sigue
    ///   siendo de su campo), luego bwdif reconstruye cada campo a 640×1080 —un tercio de los píxeles: a 1920×1080 costaba
    ///   1,6–2 núcleos— y después se reduce a la altura del preview. bwdif en un solo hilo: repartir cuadros tan pequeños
    ///   entre todos los del equipo costaba ~0,1 núcleos más que hacerlo en uno (medido con un clip DeckLink 1080i29.97).
    ///   Como en la grabación, el orden de campos sale de la señal y se procesan todos los cuadros (el relé en crudo no
    ///   conserva la marca de entrelazado).</item>
    ///   <item>Sin él (por defecto), se queda con el primer campo de cada cuadro (540 líneas, de sobra para un preview de 360):
    ///   sin fantasma, al ritmo de siempre y más barato que reducir el cuadro entero (con la aplicación real y dos DeckLink,
    ///   0,40 núcleos por proceso de canal frente a 0,62). Un campo de SD (243 o 288 líneas) habría que estirarlo: ahí bwdif
    ///   reconstruye el cuadro entero, uno por cuadro (con SD es barato). Un cuadro por campo cuesta bastante más de lo que
    ///   parece en un banco de pruebas: 1,1 núcleos por proceso de canal, y ≈0,25 más en la aplicación por canal (el doble de
    ///   frames que reducir, enviar, copiar y subir a la pantalla).</item>
    /// </list>
    /// Señal progresiva o barrido desconocido: null (como siempre).
    /// </summary>
    internal static string? PreviewDeinterlaceFilter(SignalInfo signal, int previewWidth, int previewHeight, bool perField)
    {
        string? parity = signal.Scan switch { ScanType.InterlacedTff => "tff", ScanType.InterlacedBff => "bff", _ => null };
        if (parity is null) return null;
        if (!perField && signal.Resolution is { Height: > 0 } size && size.Height / 2 >= previewHeight)
            return $"field=type={(parity == "tff" ? "top" : "bottom")}";
        return string.Create(CultureInfo.InvariantCulture,
            $"scale={previewWidth}:ih,bwdif=mode={(perField ? "send_field" : "send_frame")}:parity={parity}:deint=all:threads=1");
    }

    /// <summary>
    /// Paquetes de la cola de escritura para cubrir <paramref name="time"/> en una salida con estos flujos: FFmpeg la mide en
    /// paquetes, no en tiempo, y el ritmo depende de lo que lleve la salida. Vídeo: un paquete por frame. Audio: ~50 por
    /// segundo y pista, por arriba (AAC 47 a 48 kHz, MP2/MP3 42, Opus 50, PCM uno por bloque de la fuente: ≤ 60), para que
    /// la cola cubra al menos el tiempo pedido. Acotada a [64, 32768]; 0 si no se pide cola.
    /// </summary>
    internal static int WriteQueuePackets(TimeSpan time, double videoFps, int audioStreams)
    {
        if (time <= TimeSpan.Zero) return 0;
        double perSecond = Math.Max(0, videoFps) + 50.0 * Math.Max(0, audioStreams);
        return (int)Math.Clamp(Math.Ceiling(time.TotalSeconds * perSecond), 64, 32768);
    }

    private IEnumerable<string> WriteQueueArgs(double videoFps, int audioStreams)
    {
        int packets = WriteQueuePackets(_writeQueue, videoFps, audioStreams);
        return packets > 0
            ? new[] { "-thread_queue_size", packets.ToString(CultureInfo.InvariantCulture) }
            : Array.Empty<string>();
    }

    /// <summary>
    /// Flags de contenedor para una grabación MP4/MOV ROBUSTA: fMP4 fragmentado (índice <c>moov</c> al
    /// INICIO + un fragmento por keyframe O cada ~1 s, lo que ocurra antes, vía <c>-frag_duration</c>).
    /// Clave para la fiabilidad: a diferencia de <c>+faststart</c> —que reescribe TODO el archivo al cerrar
    /// para mover el moov al principio y, si el proceso se corta a mitad (timeout/cuelgue/cierre simultáneo
    /// de varios canales), deja un MP4 SIN índice, ilegible («sin códecs»)— el fragmentado escribe el moov de
    /// entrada y cierra fragmentos cada segundo, así el archivo queda SIEMPRE reproducible hasta el último
    /// fragmento aunque la grabación se interrumpa de golpe (un corte pierde como mucho ~1 s). El
    /// <c>-frag_duration</c> es imprescindible: solo con <c>frag_keyframe</c>, si el GOP es largo el primer
    /// fragmento tarda y un corte temprano dejaría el archivo a 0 bytes. Vacío si no es MP4/MOV.
    /// </summary>
    private IEnumerable<string> RobustMovFlags(ContainerFormat container)
    {
        if (container is not (ContainerFormat.Mp4 or ContainerFormat.Mov)) return Array.Empty<string>();
        // fMP4 fragmentado (robusto ante corte). En modo MP4 ESTÁNDAR (_fragmentedMp4=false) NO se emiten
        // movflags: FFmpeg escribe el moov al FINAL al cerrar → archivo 100% seekable en local, cierre rápido y
        // SIN remux; el precio es que un corte ANTES del cierre limpio lo deja sin índice (apto con SAI/UPS).
        return _fragmentedMp4
            ? new[] { "-movflags", "+frag_keyframe+empty_moov+default_base_moof", "-frag_duration", "1000000" }
            : Array.Empty<string>();
    }

    /// <summary>
    /// Cola de la salida de grabación: un único archivo (MP4/MOV en fMP4 robusto, ver <see cref="RobustMovFlags"/>)
    /// o, si el perfil define segmentación, el muxer <c>segment</c> (cada segmento es un archivo COMPLETO, de
    /// modo que un fallo/cuelgue solo pierde el segmento en curso). Fija
    /// <see cref="OutputFilePath"/> / <see cref="IsSegmentedOutput"/> / <see cref="SegmentFileGlob"/>.
    /// </summary>
    private IEnumerable<string> RecordOutputTail(RecordingProfile p, string recMux, string recExt)
    {
        if (p.Segmentation is { Trigger: SegmentTrigger.Duration or SegmentTrigger.Size or SegmentTrigger.WallClock } seg)
        {
            IsSegmentedOutput = true;
            OutputFilePath = "";
            var (pattern, glob) = SegmentNameParts(recExt);
            SegmentFileGlob = glob;
            long seconds = SegmentSeconds(p, seg);
            // Con nombre dado: «{base}_1, _2…» (1-based, sin relleno) con número inicial CONTINUO entre
            // reinicios/slate (lo fija el motor). Sin nombre (legado): «{canal}_{fecha_hora}_%03d».
            // Nota: combinar `-strftime 1` con %03d rompe la cabecera del muxer (mpegts: "Could not write
            // header"); reset_timestamps deja cada segmento empezando en 0.
            var a = new List<string> { "-f", "segment" };
            // Con fin previsto, sin el corte que caería junto a él (ver SegmentSplitTimes); si no, uno cada N segundos.
            if (seg.Trigger is SegmentTrigger.Duration && SegmentSplitTimes(seconds, _plannedRemaining) is { } splits)
                a.AddRange(new[] { "-segment_times", splits });
            else
                a.AddRange(new[] { "-segment_time", seconds.ToString(CultureInfo.InvariantCulture) });
            a.AddRange(new[] { "-segment_format", recMux, "-reset_timestamps", "1" });
            // Robustez ante corte eléctrico: cada segmento MP4/MOV se escribe como fMP4 (fragmentos + moov al
            // inicio), igual que el archivo único. Sin esto, un corte a mitad de un segmento dejaba ese segmento
            // (hasta N minutos) SIN moov e ilegible; el fMP4 robusto solo cubría el archivo único. (Auditoría A6/#9.)
            if (p.Container is ContainerFormat.Mp4 or ContainerFormat.Mov && _fragmentedMp4)
            {
                a.Add("-segment_format_options");
                a.Add("movflags=+frag_keyframe+empty_moov+default_base_moof:frag_duration=1000000");
            }
            if (_baseName is not null) { a.Add("-segment_start_number"); a.Add(_segmentStartNumber.ToString(CultureInfo.InvariantCulture)); }
            if (seg.Trigger is SegmentTrigger.WallClock) { a.Add("-segment_atclocktime"); a.Add("1"); }
            a.Add("-y"); a.Add(pattern);
            return a;
        }

        OutputFilePath = Path.Combine(_outputDirectory, SingleFileName(recExt));
        var single = new List<string>();
        single.AddRange(RobustMovFlags(p.Container));
        single.Add("-f"); single.Add(recMux);
        single.Add("-y"); single.Add(OutputFilePath);
        return single;
    }

    /// <summary>
    /// Duración de cada segmento en segundos. Para corte por tamaño se deriva del bitrate
    /// (tamaño·8 ÷ bps), ya que el muxer <c>segment</c> corta por tiempo, no por bytes. Mínimo 1 s.
    /// </summary>
    private static long SegmentSeconds(RecordingProfile p, SegmentationPolicy seg)
    {
        if (seg.Trigger is SegmentTrigger.Size && seg.MaxBytes is { } bytes && bytes > 0)
        {
            long bps = (p.MaxBitrate ?? p.VideoBitrate).BitsPerSecond + p.AudioBitrate.BitsPerSecond;
            if (bps > 0) return Math.Max(1, bytes * 8 / bps);
        }
        return Math.Max(1, (long)(seg.Duration ?? TimeSpan.FromMinutes(15)).TotalSeconds);
    }

    /// <summary>Hasta dónde un corte cuenta como «junto al fin previsto»: cubre lo que tarda en llegar el Detener tras el fin (el
    /// tick del programador y el relevo del preview, con tope de 15 s) y no deja un último segmento ridículo. Nunca más de un
    /// cuarto de segmento.</summary>
    internal static readonly TimeSpan EndSplitTolerance = TimeSpan.FromSeconds(30);

    /// <summary>Tras el fin previsto se sigue cortando al mismo ritmo esto (al menos 6 cortes; con segmentos muy cortos, hasta
    /// el tope de la lista), por si el Detener no llegara: el último segmento no crece sin límite.</summary>
    internal static readonly TimeSpan SplitsAfterEnd = TimeSpan.FromHours(1);

    /// <summary>Tope de cortes en <c>-segment_times</c> (unos 6 caracteres cada uno en la línea de comandos).</summary>
    internal const int MaxListedSplits = 400;

    /// <summary>
    /// Cortes del muxer <c>segment</c> (<c>-segment_times</c>, en segundos desde el inicio del proceso) para una grabación con
    /// fin previsto a <paramref name="remaining"/>: los de siempre, uno cada <paramref name="segmentSeconds"/>, MENOS el que
    /// caiga a menos de <see cref="EndSplitTolerance"/> (o de un cuarto de segmento) de ese fin. El Detener llega un instante
    /// DESPUÉS del fin (tick del programador, relevo del preview), así que ese corte dejaba un último segmento de menos de un
    /// segundo: una programada de 30 min con segmentos de 10 salía en 3 de 600 s y uno de 0,6 s. Sin él, el último segmento
    /// absorbe ese instante (600,6 s) y no se pierde nada: el Detener llega cuando siempre.
    /// </summary>
    /// <returns>La lista, o null si ningún corte cae junto al fin (o no hay fin previsto): entonces vale el <c>-segment_time</c>
    /// de siempre. Null también si la lista pasaría de <see cref="MaxListedSplits"/> cortes (días de grabación con segmentos de un
    /// minuto): ahí se prefiere el segmento corto a una línea de comandos desmesurada.</returns>
    internal static string? SegmentSplitTimes(long segmentSeconds, TimeSpan? remaining)
    {
        if (remaining is not { } left || left <= TimeSpan.Zero || segmentSeconds <= 0) return null;
        double end = left.TotalSeconds;
        double tolerance = Math.Min(segmentSeconds / 4.0, EndSplitTolerance.TotalSeconds);
        long atEnd = (long)Math.Round(end / segmentSeconds, MidpointRounding.AwayFromZero);
        if (atEnd < 1 || Math.Abs(atEnd * segmentSeconds - end) > tolerance) return null;
        const long MinSplitsAfterEnd = 6;
        long after = Math.Min(Math.Max(MinSplitsAfterEnd, (long)Math.Ceiling(SplitsAfterEnd.TotalSeconds / segmentSeconds)),
                              MaxListedSplits - (atEnd - 1));
        if (after < MinSplitsAfterEnd) return null;
        long last = atEnd + after;
        var splits = new StringBuilder();
        for (long k = 1; k <= last; k++)
        {
            if (k == atEnd) continue;
            if (splits.Length > 0) splits.Append(',');
            splits.Append((k * segmentSeconds).ToString(CultureInfo.InvariantCulture));
        }
        return splits.ToString();
    }

    /// <summary>Nombre del archivo único (con extensión): el base elegido o, por defecto, {canal}_{fecha_hora}.</summary>
    private string SingleFileName(string ext) => $"{ResolvedBase()}.{ext}";

    /// <summary>
    /// Patrón del muxer <c>segment</c> y su glob asociado, coherentes entre sí. Con nombre dado el contador
    /// es <c>_%d</c> (1-based, lo completa el muxer); sin nombre, el legado <c>_%03d</c>.
    /// </summary>
    private (string Pattern, string Glob) SegmentNameParts(string ext)
    {
        string baseName = ResolvedBase();
        string counter = _baseName is not null ? "%d" : "%03d";
        // Glob de vigilancia: con nombre dado, específico ({base}_*); sin nombre (legado), amplio por canal
        // ({canal}_*), porque el prefijo lleva fecha/hora y se siembran los anteriores como ya emitidos.
        string glob = _baseName is not null ? $"{_baseName}_*.{ext}" : $"{_channelKey}_*.{ext}";
        SegmentPrefixGlob = $"{baseName}_*.{ext}";
        return (Path.Combine(_outputDirectory, $"{baseName}_{counter}.{ext}"), glob);
    }

    /// <summary>Base del nombre: la elegida (manual/programada) o, en su defecto, {canal}_{fecha_hora}.</summary>
    private string ResolvedBase() => _baseName ?? $"{_channelKey}_{DateTime.Now:yyyyMMdd_HHmmss}";

    /// <summary>
    /// Argv de la CARTA DE AJUSTE (slate): al perder la señal en vivo durante la grabación, sustituye la
    /// entrada del dispositivo por barras SMPTE + silencio generados con <c>lavfi</c> a tiempo real (X s sin
    /// señal = X s de barras), conservando las MISMAS salidas (preview + medidores + grabación) y la misma
    /// resolución/tasa de destino. Así la grabación continúa (un nuevo segmento) sin romper la base de tiempo;
    /// las barras llevan el rótulo «SIN SEÑAL» como evidencia. Pensado para fuentes de vídeo; se reanuda la
    /// fuente al volver la señal.
    /// </summary>
    public IReadOnlyList<string> BuildSlate(bool recording, int previewWidth, int previewHeight)
    {
        var profile = _profile ?? throw new InvalidOperationException("Falta el perfil.");
        if (string.IsNullOrEmpty(_previewSink))
            throw new InvalidOperationException("Falta el destino de preview (WithPreviewSink).");

        IsSegmentedOutput = false;
        PreviewFramesPerSourceFrame = 1; // las barras son progresivas: un frame de preview por frame

        // Resolución/tasa del slate = las de la grabación, para que sus segmentos concatenen con los de
        // la fuente real sin saltos de formato.
        var res = profile.TargetResolution ?? _source?.CurrentSignal.Resolution ?? Resolution.Hd1080;
        var rate = profile.OutputFrameRate ?? _source?.CurrentSignal.FrameRate ?? FrameRate.P25;
        string size = string.Create(CultureInfo.InvariantCulture, $"{res.Width}x{res.Height}");
        string r = string.Create(CultureInfo.InvariantCulture, $"{rate.Numerator}/{rate.Denominator}");

        // Con una fuente multicanal, el silencio tiene TANTOS canales como la fuente y pasa por los MISMOS pans que la
        // grabación normal: así las piezas de la carta de ajuste llevan exactamente las mismas pistas que las reales.
        var routes = _source is not null ? AudioRoutes(_source, profile) : null;
        string silence = routes is null
            ? "stereo"
            : string.Create(CultureInfo.InvariantCulture, $"{_source!.AudioChannelCount}c");

        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-stats_period", "1" };
        // UNA entrada generada con las barras de ajuste (0:v) y el silencio (0:a), con base de tiempo continua y AL RITMO DEL
        // RELOJ (-re), como la fuente a la que sustituye. Sin freno, lavfi genera tan deprisa como codifica la máquina: medido
        // con el FFmpeg empaquetado, barras a ~14× el tiempo real (x264 a 720p25) → una caída de señal de minutos dejaba HORAS
        // de barras en el disco, la pieza no duraba lo que el hueco y el canal en barras acaparaba la CPU de los demás. Una
        // sola entrada y no una por generador: con dos, FFmpeg retiene el silencio para no adelantarse al vídeo (que sale del
        // codificador con retraso) y al cerrar la pieza el audio acababa 0,6 s antes que el vídeo (1,2 s sin -re); así acaba
        // como las piezas de la fuente real. -re y no -readrate: vale con cualquier FFmpeg (el cliente puede traer el suyo y
        // -readrate llegó en la 5.0).
        args.Add("-re"); args.Add("-f"); args.Add("lavfi");
        args.Add("-i"); args.Add(string.Create(CultureInfo.InvariantCulture,
            $"smptebars=size={size}:rate={r}[out0];anullsrc=channel_layout={silence}:sample_rate={profile.AudioSampleRate}[out1]"));

        string label = "drawtext=text='SIN SEÑAL':fontcolor=white:fontsize=48:box=1:boxcolor=black@0.6:" +
                       "x=(w-text_w)/2:y=h*0.12";
        string previewChain = string.Create(CultureInfo.InvariantCulture, $"scale={previewWidth}:{previewHeight},format=bgra");

        var filter = new StringBuilder();
        string recLabel;
        if (recording)
        {
            filter.Append(CultureInfo.InvariantCulture, $"[0:v]{label},split=2[vrec][vprev];");
            filter.Append(CultureInfo.InvariantCulture, $"[vprev]{previewChain}[pv]");
            recLabel = "[vrec]";
            // Preset entrelazado: las barras salen de lavfi como cuadros progresivos, sin marca de campo. Sin ella x264 las
            // codificaba en BFF y el MOV de ProRes declaraba el campo inferior primero («bt»), entre piezas TFF de la señal
            // (medido con el FFmpeg empaquetado). Misma marca que la rama de grabación en vivo.
            string? field = profile.ScanType switch
            {
                ScanType.InterlacedTff => "setfield=mode=tff", ScanType.InterlacedBff => "setfield=mode=bff", _ => null
            };
            if (field is not null && !profile.AudioOnly)
            {
                filter.Append(CultureInfo.InvariantCulture, $";[vrec]{field}[vslate]");
                recLabel = "[vslate]";
            }
        }
        else
        {
            filter.Append(CultureInfo.InvariantCulture, $"[0:v]{label},{previewChain}[pv]");
            recLabel = "[0:v]";
        }
        if (routes is not null)
        {
            filter.Append(';');
            filter.Append(AudioGraph("[0:a]", recording ? routes : null, "ebur128=peak=true", "aslate"));
        }
        args.Add("-filter_complex"); args.Add(filter.ToString());

        // Salida A — preview de las barras (cada frame una vez, como en el pipeline en vivo).
        args.Add("-map"); args.Add("[pv]");
        args.Add("-fps_mode"); args.Add("passthrough");
        args.Add("-f"); args.Add("rawvideo");
        args.Add(_previewSink);

        // Salida B — medidores sobre el silencio (marcará -inf; sin silencedetect para no duplicar alarma).
        args.Add("-map"); args.Add(routes is null ? "0:a:0" : "[amout]");
        if (routes is null) { args.Add("-af"); args.Add("ebur128=peak=true"); }
        args.Add("-f"); args.Add("null"); args.Add("-");

        // Salida C — grabación del slate (mismo encoder/segmentación que la grabación normal).
        if (recording)
        {
            var (recMux, recExt) = FfmpegCodecMap.Container(profile.Container);
            args.Add("-map"); args.Add(recLabel);
            foreach (var a in routes is null ? new[] { "0:a:0" } : RouteLabels(routes, "aslate")) { args.Add("-map"); args.Add(a); }
            args.AddRange(VideoEncoderArgs(profile, res, rate));
            args.AddRange(AudioEncoderArgs(profile, channelCount: routes is null));
            if (routes is not null) args.AddRange(TrackTitles(routes, profile.Container));
            if (profile.OutputFrameRate is { } fr)
            {
                args.Add("-r");
                args.Add(string.Create(CultureInfo.InvariantCulture, $"{fr.Numerator}/{fr.Denominator}"));
            }
            args.AddRange(WriteQueueArgs(rate.Value, routes is null ? 1 : routes.Count));
            args.AddRange(RecordOutputTail(profile, recMux, recExt));
        }
        return args;
    }

    private (string Filter, string MainLabel) BuildFilterGraph(RecordingProfile p, bool gpu, bool hasProxy)
    {
        var sb = new StringBuilder();
        string mainIn;

        if (hasProxy)
        {
            var px = p.Proxy!;
            string proxyScale = gpu ? "scale_cuda" : "scale";
            sb.Append("[0:v]split=2[main][prx];");
            sb.Append(CultureInfo.InvariantCulture, $"[prx]{proxyScale}={px.Resolution.Width}:{px.Resolution.Height}[proxout]");
            mainIn = "[main]";
        }
        else
        {
            mainIn = "[0:v]";
        }

        // Cadena de filtros del video principal: escalado al tamaño elegido y/o burn-in de timecode.
        // El escalado va por software (scale): así el encoder (incl. NVENC) funciona con frames de CPU
        // sin exigir una cadena _cuda completa, manteniéndolo robusto en cualquier GPU.
        var chain = new List<string>();
        if (p.TargetResolution is { } r)
            chain.Add(string.Create(CultureInfo.InvariantCulture, $"scale={r.Width}:{r.Height}"));
        // Marca los cuadros como entrelazados (campo TFF/BFF) para que el encoder los codifique así.
        if (p.ScanType is ScanType.InterlacedTff)
            chain.Add("setfield=mode=tff");
        else if (p.ScanType is ScanType.InterlacedBff)
            chain.Add("setfield=mode=bff");
        if (p.BurnTimecode)
        {
            string tc = "drawtext=timecode='00\\:00\\:00\\:00':rate=25:fontcolor=white:" +
                        "box=1:boxcolor=black@0.5:x=20:y=20";
            chain.Add(gpu ? $"hwdownload,format=nv12,{tc}" : tc);
        }

        if (chain.Count == 0)
            return (sb.ToString(), mainIn); // solo proxy: el principal va directo

        if (hasProxy) sb.Append(';');
        sb.Append(CultureInfo.InvariantCulture, $"{mainIn}{string.Join(',', chain)}[mainout]");
        return (sb.ToString(), "[mainout]");
    }

    /// <param name="p">Perfil de grabación.</param>
    /// <param name="frame">Tamaño de lo que se codifica (el del preset o el de la señal), si se conoce.</param>
    /// <param name="rate">Cadencia de lo que se codifica (la del preset o la de la señal), si se conoce.</param>
    private IEnumerable<string> VideoEncoderArgs(RecordingProfile p, Resolution? frame, FrameRate? rate)
    {
        var list = new List<string> { "-c:v", FfmpegCodecMap.VideoEncoder(p.VideoCodec) };
        string bitrate = p.VideoBitrate.BitsPerSecond.ToString(CultureInfo.InvariantCulture);
        string quality = p.Quality.ToString(CultureInfo.InvariantCulture);
        string gop = p.GopSize.ToString(CultureInfo.InvariantCulture);

        long maxBps = (p.MaxBitrate ?? p.VideoBitrate).BitsPerSecond;
        string maxrate = maxBps.ToString(CultureInfo.InvariantCulture);
        string bufsize = (maxBps * 2).ToString(CultureInfo.InvariantCulture);
        bool interlaced = p.ScanType is ScanType.InterlacedTff or ScanType.InterlacedBff;

        if (FfmpegCodecMap.IsGpuEncoder(p.VideoCodec))
        {
            list.AddRange(new[] { "-preset", "p5", "-tune", "hq" });
            switch (p.RateControl)
            {
                case RateControlMode.ConstantQuality:
                    list.AddRange(new[] { "-rc", "constqp", "-qp", quality }); break;
                case RateControlMode.VariableBitrate:
                    list.AddRange(new[] { "-rc", "vbr", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize }); break;
                default: // CBR
                    list.AddRange(new[] { "-rc", "cbr", "-b:v", bitrate }); break;
            }
            list.AddRange(new[] { "-g", gop, "-bf", "0" });
            // -no-scenecut (NVENC) es booleano y EXIGE valor explícito: la forma «bare» hace que FFmpeg tome el
            // siguiente token (-c:a) como su valor y deje «aac» suelto como nombre de archivo de salida → aborta
            // con EINVAL (-22) «Unable to choose an output format for 'aac'». Debe ir como «-no-scenecut 1».
            if (p.ClosedGop) list.AddRange(new[] { "-no-scenecut", "1" });
        }
        else if (p.VideoCodec is VideoCodec.H264Qsv or VideoCodec.H264Amf or VideoCodec.HevcQsv)
        {
            // GPU INTEGRADA (Intel QuickSync H.264/HEVC / AMD AMF): por bitrate. Args mínimos y portables entre
            // los encoders (evita -preset/-crf/-rc, que difieren); el ritmo va por -b:v/-maxrate/-bufsize.
            switch (p.RateControl)
            {
                case RateControlMode.VariableBitrate:
                    list.AddRange(new[] { "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize }); break;
                default: // CBR (y calidad constante: estos HW encoders no comparten un CRF uniforme → bitrate)
                    list.AddRange(new[] { "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize }); break;
            }
            list.Add("-g"); list.Add(gop);
        }
        else if (p.VideoCodec is VideoCodec.Mpeg2Video)
        {
            // MPEG-2 broadcast (XDCAM/IMX/PS/TS): CBR con VBV acotado.
            list.AddRange(new[] { "-b:v", bitrate, "-minrate", bitrate, "-maxrate", maxrate, "-bufsize", bufsize, "-g", gop });
            if (interlaced) { list.Add("-flags"); list.Add("+ilme+ildct"); }
        }
        else if (DnxhdBitrateMbps(p, frame, rate) is { } dnxhdMbps)
        {
            // DNxHD CLÁSICO (DnxHd, o un DNxHR entrelazado): el bitrate ELIGE el CID (1080i: 145/220 Mbps a 29.97, 120/185 a
            // 25) y, a diferencia de DNxHR, admite DCT entrelazada: un «1080i» graba de verdad los dos campos.
            list.AddRange(new[] { "-profile:v", "dnxhd", "-b:v", (dnxhdMbps * 1_000_000L).ToString(CultureInfo.InvariantCulture) });
            if (interlaced) { list.Add("-flags"); list.Add("+ildct"); }
        }
        else if (p.VideoCodec is VideoCodec.DnxHd or VideoCodec.DnxHr)
        {
            // DNxHR (por perfil, cualquier resolución): solo PROGRESIVO, FFmpeg rechaza +ildct en sus perfiles («Interlaced
            // encoding is not supported for DNxHR profiles»). También un DNxHD de un formato sin CID clásico (4K, SD, 4:4:4)
            // y un DNxHR entrelazado sin equivalente DNxHD (LB, 444), que guardan los campos en cuadros progresivos.
            string dnxProfile = p.EncoderProfile switch
            {
                EncoderProfile.DnxHrLb  => "dnxhr_lb",
                EncoderProfile.DnxHrSq  => "dnxhr_sq",
                EncoderProfile.DnxHrHq  => "dnxhr_hq",
                EncoderProfile.DnxHrHqx => "dnxhr_hqx",
                EncoderProfile.DnxHr444 => "dnxhr_444",
                // Auto: por el formato de píxel (444 si 4:4:4, hqx si 10 bits, si no hq): 4:4:4 solo lo admite dnxhr_444.
                _ => p.PixelFormat switch
                {
                    PixelFormat.Yuv444p10le => "dnxhr_444",
                    PixelFormat.Yuv422p10le or PixelFormat.Yuv420p10le => "dnxhr_hqx",
                    _ => "dnxhr_hq",
                },
            };
            list.AddRange(new[] { "-profile:v", dnxProfile });
        }
        else if (p.VideoCodec is VideoCodec.ProRes)
        {
            list.AddRange(new[] { "-profile:v", ProResFormat(p).Profile });
            // Sin DCT entrelazada el codificador escribe cuadros PROGRESIVOS: un preset «1080i» grababa los campos entretejidos
            // en cuadros marcados progresivos (medido con una DeckLink en 1080i59.94). prores_aw codifica en BFF los cuadros
            // marcados BFF y en TFF el resto; la marca la pone el setfield de la rama de grabación.
            if (interlaced) { list.Add("-flags"); list.Add("+ildct"); }
        }
        else // libx264 / libx265
        {
            list.AddRange(new[] { "-preset", "veryfast" });
            switch (p.RateControl)
            {
                case RateControlMode.ConstantQuality:
                    list.AddRange(new[] { "-crf", quality }); break;
                case RateControlMode.VariableBitrate:
                    list.AddRange(new[] { "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize }); break;
                default: // CBR aproximado: tasa objetivo = máxima con buffer acotado
                    list.AddRange(new[] { "-b:v", bitrate, "-maxrate", bitrate, "-bufsize", bufsize }); break;
            }
            list.Add("-g"); list.Add(gop);
            if (interlaced) { list.Add("-flags"); list.Add("+ilme+ildct"); }
        }

        var pix = EncoderPixelFormat(p, frame, rate);
        if (pix is not null) { list.Add("-pix_fmt"); list.Add(pix); }
        return list;
    }

    /// <summary>
    /// Formato de píxel del codificador: ProRes, el de su perfil (<see cref="ProResFormat"/>); DNxHD clásico, el de su CID
    /// (<see cref="DnxhdPixelFormat"/>); el resto, el explícito del perfil → el natural del perfil intra (DNxHR 444/HQX)
    /// → el predeterminado del códec. Null = que elija el codificador (NVENC).
    /// </summary>
    private static string? EncoderPixelFormat(RecordingProfile p, Resolution? frame, FrameRate? rate)
    {
        if (p.VideoCodec is VideoCodec.ProRes) return ProResFormat(p).PixelFormat;
        if (DnxhdBitrateMbps(p, frame, rate) is not null) return DnxhdPixelFormat(p);
        return FfmpegCodecMap.PixelFormatArg(p.PixelFormat)
               ?? ProfilePixelFormat(p.EncoderProfile)
               ?? DefaultPixelFormat(p.VideoCodec);
    }

    /// <summary>
    /// Perfil (<c>-profile:v</c> 0–5) y formato de píxel de ProRes. El croma lo fija el PERFIL —4444/XQ son 4:4:4, los demás
    /// 4:2:2— y no un formato suelto del preset: prores_aw no abre un 4444 con 4:2:2 (EINVAL «need YUV444P10 input») y con
    /// 4:4:4 convierte un 422 en 4444 por su cuenta. Auto = 422 HQ, o 4444 si el preset pide 4:4:4.
    /// </summary>
    internal static (string Profile, string PixelFormat) ProResFormat(RecordingProfile p)
    {
        string profile = p.EncoderProfile switch
        {
            EncoderProfile.ProResProxy    => "0",
            EncoderProfile.ProResLt       => "1",
            EncoderProfile.ProResStandard => "2",
            EncoderProfile.ProResHq       => "3",
            EncoderProfile.ProRes4444     => "4",
            EncoderProfile.ProRes4444Xq   => "5",
            _ => p.PixelFormat is PixelFormat.Yuv444p10le ? "4" : "3",
        };
        return (profile, profile is "4" or "5" ? "yuv444p10le" : "yuv422p10le");
    }

    /// <summary>Un CID del DNxHD clásico que codifica el <c>dnxhd</c> de FFmpeg: formato, tamaño FIJO de cuadro y las claves
    /// de bitrate (Mbps) que lo eligen. Los Mbps reales son ese tamaño por la cadencia: el CID 1243 da 220 a 29.97 y 183,5 a 25,
    /// de ahí los nombres de Avid «DNxHD 220» y «DNxHD 185» para el mismo formato.</summary>
    private sealed record DnxhdCid(int Cid, int Width, int Height, bool Interlaced, string PixelFormat, int FrameBytes, int[] Mbps);

    /// <summary>
    /// Los CID que el FFmpeg empaquetado acepta sin <c>-strict experimental</c>, con las claves que lista al rechazar unos
    /// parámetros («Valid DNxHD profiles») y el tamaño de cuadro medido codificando cada uno (2026-10-15). FFmpeg busca la
    /// clave en la lista del CID SIN mirar la cadencia: 145 a 25 cuadros da el CID 1242 (121 Mbps reales), como 120. No hay
    /// DNxHD 444 (el codificador solo admite 4:4:4 con dnxhr_444), ni 1440×1080i por debajo de 120 (exige experimental).
    /// </summary>
    private static readonly DnxhdCid[] DnxhdCids =
    {
        new(1243, 1920, 1080, true,  "yuv422p",     917504, new[] { 185, 220 }),
        new(1242, 1920, 1080, true,  "yuv422p",     606208, new[] { 120, 145 }),
        new(1241, 1920, 1080, true,  "yuv422p10le", 917504, new[] { 185, 220 }),
        new(1244, 1440, 1080, true,  "yuv422p",     606208, new[] { 120, 145 }),
        new(1238, 1920, 1080, false, "yuv422p",     917504, new[] { 175, 185, 220, 365, 440 }),
        new(1237, 1920, 1080, false, "yuv422p",     606208, new[] { 115, 120, 145, 240, 290 }),
        new(1253, 1920, 1080, false, "yuv422p",     188416, new[] { 36, 45, 75, 90 }),
        new(1235, 1920, 1080, false, "yuv422p10le", 917504, new[] { 175, 185, 365, 440 }), // FFmpeg no acepta 220 aquí
        new(1259, 1440, 1080, false, "yuv422p",     417792, new[] { 63, 84, 100, 110 }),
        new(1251, 1280,  720, false, "yuv422p",     458752, new[] { 90, 110, 180, 220 }),
        new(1252, 1280,  720, false, "yuv422p",     303104, new[] { 60, 75, 120, 145 }),
        new(1250, 1280,  720, false, "yuv422p10le", 458752, new[] { 90, 180, 220 }),
        new(1258,  960,  720, false, "yuv422p",     212992, new[] { 42, 60, 75, 115 }),
    };

    /// <summary>
    /// Bitrate (Mbps) con el que se le pide a FFmpeg el DNxHD CLÁSICO de un perfil, o null si se graba otra cosa.
    /// <list type="bullet">
    ///   <item><see cref="VideoCodec.DnxHd"/>: un bitrate que es clave de un CID del formato se respeta tal cual (lo que haría
    ///   FFmpeg); cualquier otro —con FFmpeg sería «video parameters incompatible with DNxHD» y una grabación que no
    ///   arranca— se ajusta al CID cuyos Mbps reales a esa cadencia quedan más cerca, y sin bitrate (un preset DNxHD que
    ///   confiaba en el perfil) al CID de más calidad, el equivalente a DNxHR HQ/HQX. Si el formato no existe en DNxHD
    ///   clásico (4K, SD, 4:4:4, tamaño desconocido), null: DNxHR por perfil, lo que hacía DnxHd antes de esta rama.</item>
    ///   <item><see cref="VideoCodec.DnxHr"/> ENTRELAZADO: DNxHR no puede entrelazar (FFmpeg rechaza +ildct en sus perfiles) y
    ///   grababa los campos en cuadros progresivos. Se graba el DNxHD clásico equivalente (<see cref="DnxhrInterlacedMbps"/>):
    ///   así se arreglan sin tocar nada los canales que aplicaron los antiguos presets «DNxHR HQ · 1080i…» (guardan su copia
    ///   del perfil) y los presets propios. Progresivo, o sin equivalente (LB, 444): null, DNxHR.</item>
    /// </list>
    /// </summary>
    internal static int? DnxhdBitrateMbps(RecordingProfile p, Resolution? frame, FrameRate? rate)
    {
        if (p.AudioOnly || frame is not { } f) return null;
        bool interlaced = p.ScanType is not ScanType.Progressive;
        long requested;
        if (p.VideoCodec is VideoCodec.DnxHd) requested = p.VideoBitrate.BitsPerSecond / 1_000_000; // como FFmpeg: Mbps enteros
        else if (p.VideoCodec is VideoCodec.DnxHr && interlaced && DnxhrInterlacedMbps(p, rate) is { } equivalent) requested = equivalent;
        else return null;

        string pix = DnxhdPixelFormat(p);
        var cids = DnxhdCids.Where(c => c.Width == f.Width && c.Height == f.Height && c.Interlaced == interlaced && c.PixelFormat == pix)
                            .ToList();
        if (cids.Count == 0) return null;
        if (cids.Any(c => c.Mbps.Contains((int)requested))) return (int)requested;
        if (rate is not { Value: > 0 } r)
        {
            // Sin cadencia no hay Mbps reales que comparar: la clave más próxima (las claves son los nombres de Avid). La
            // mayor siempre es del CID de más calidad.
            var keys = cids.SelectMany(c => c.Mbps).ToList();
            return requested <= 0 ? keys.Max() : keys.MinBy(m => Math.Abs(m - requested));
        }
        double Real(DnxhdCid c) => c.FrameBytes * 8.0 * r.Value / 1_000_000;
        var cid = requested <= 0 ? cids.MaxBy(c => c.FrameBytes)! : cids.MinBy(c => Math.Abs(Real(c) - requested))!;
        // La clave de ese CID más próxima a sus Mbps reales: 185 a 25 cuadros y 220 a 29.97, como los nombra Avid.
        return cid.Mbps.MinBy(m => Math.Abs(m - Real(cid)));
    }

    /// <summary>
    /// Mbps del DNxHD clásico equivalente a un perfil DNxHR entrelazado, a la cadencia de la grabación: SQ, la clase 145 (120
    /// a 25); HQ y HQX, la 220 (185 a 25), en 8 o 10 bits según el perfil (<see cref="DnxhdPixelFormat"/>). LB y 444 no tienen
    /// equivalente entrelazado (no hay DNxHD 1080i por debajo de 145, ni DNxHD 444 en FFmpeg): null, siguen en DNxHR.
    /// </summary>
    private static long? DnxhrInterlacedMbps(RecordingProfile p, FrameRate? rate)
    {
        double? at2997 = p.EncoderProfile switch
        {
            EncoderProfile.DnxHrSq => 145,
            EncoderProfile.DnxHrHq or EncoderProfile.DnxHrHqx => 220,
            EncoderProfile.DnxHrLb or EncoderProfile.DnxHr444 => null,
            _ => p.PixelFormat is PixelFormat.Yuv444p10le ? null : 220, // Auto: hq/hqx; 4:4:4 sería dnxhr_444
        };
        if (at2997 is not { } mbps) return null;
        return rate is { Value: > 0 } r ? (long)Math.Round(mbps * r.Value / 29.97) : (long)mbps;
    }

    /// <summary>Formato de píxel del DNxHD clásico: 8 bits salvo que el preset pida 10 bits —en el formato de píxel o con el
    /// perfil HQX— (los CID «x»: 220x…) o 4:4:4, que no tiene CID clásico en FFmpeg y deja el preset en DNxHR 444.</summary>
    private static string DnxhdPixelFormat(RecordingProfile p)
        => (FfmpegCodecMap.PixelFormatArg(p.PixelFormat) ?? ProfilePixelFormat(p.EncoderProfile)) switch
        {
            "yuv422p10le" or "yuv420p10le" => "yuv422p10le",
            "yuv444p10le" => "yuv444p10le",
            _ => "yuv422p",
        };

    /// <summary>Comando de grabación solo-audio: sin video, codec de audio al contenedor elegido.</summary>
    private IReadOnlyList<string> BuildAudioOnly(ICaptureSource source, RecordingProfile profile, string recMux, string recExt)
    {
        OutputFilePath = Path.Combine(_outputDirectory, SingleFileName(recExt));
        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-stats_period", "1" };
        args.AddRange(source.BuildInputArguments());
        args.Add("-vn");
        args.Add("-map"); args.Add("0:a?");
        string? audioFilter = AudioRoutingFilter(source, profile);
        args.AddRange(AudioEncoderArgs(profile, channelCount: audioFilter is null));
        // Selección de canales (si la fuente trae más de 2) + niveles para los medidores VU.
        args.Add("-af"); args.Add(audioFilter is null ? "ebur128=peak=true" : $"{audioFilter},ebur128=peak=true");
        args.Add("-f"); args.Add(recMux);
        args.Add("-y"); args.Add(OutputFilePath);
        return args;
    }

    /// <summary>Píxel natural de un perfil intra cuando el preset no fija PixelFormat explícito.</summary>
    private static string? ProfilePixelFormat(EncoderProfile profile) => profile switch
    {
        EncoderProfile.ProRes4444 or EncoderProfile.ProRes4444Xq or EncoderProfile.DnxHr444 => "yuv444p10le",
        EncoderProfile.DnxHrHqx => "yuv422p10le",
        _ => null
    };

    private static string? DefaultPixelFormat(VideoCodec codec) => codec switch
    {
        VideoCodec.ProRes => "yuv422p10le",
        VideoCodec.DnxHd or VideoCodec.DnxHr => "yuv422p",
        VideoCodec.Mpeg2Video or VideoCodec.H264x264 or VideoCodec.H265x265 => "yuv420p",
        VideoCodec.H264Qsv or VideoCodec.H264Amf or VideoCodec.HevcQsv => "nv12", // formato nativo de QSV/AMF
        _ => null // NVENC: el encoder elige (nv12/p010)
    };

    /// <summary>
    /// Formato de píxel destino para el filtro de la rama de GRABACIÓN (un <c>format=</c> al final de
    /// <c>[vrec]</c>). Imprescindible cuando la fuente entrega un formato que el encoder por hardware no acepta
    /// (p. ej. NDI sirve <c>uyvy422</c> y <c>h264_nvenc</c> NO lo admite): al venir de un <c>filter_complex</c>,
    /// FFmpeg NO auto-inserta la conversión y el encoder aborta con EINVAL (-22). Reusa el mismo formato que el
    /// <c>-pix_fmt</c> de salida; para NVENC (que devuelve null porque «elige solo») cae a <c>nv12</c>, su
    /// formato nativo 8-bit. La conversión es necesaria de todos modos y desde uyvy422 es barata (4:2:2→4:2:0).
    /// </summary>
    private static string RecordingFilterPixelFormat(RecordingProfile p, Resolution? frame, FrameRate? rate)
        => EncoderPixelFormat(p, frame, rate) ?? "nv12";

    /// <summary>
    /// Un flujo de audio de la grabación: el <c>pan</c> que lo produce, si lleva título de pista cuál, qué canales de la
    /// fuente (0-based) van dentro y con qué distribución (null = pista PCM sin nombre). Con los canales y la distribución
    /// <see cref="AudioTrackPlan"/> le cuenta al operador qué pares acaban de verdad en el archivo y cuántas pistas.
    /// </summary>
    internal sealed record AudioRoute(string Pan, string? Title, IReadOnlyList<int> Channels, AudioLayout? Layout = null);

    /// <summary>
    /// Flujos de audio de la GRABACIÓN según el modo de pistas del perfil, o <c>null</c> si la fuente entrega 2 canales
    /// (tubería de siempre). Se usa SOLO con más de 2 canales: ahí <c>-ac</c> no sirve, porque FFmpeg MEZCLARÍA todos los
    /// canales de la tarjeta en la pista (el par 5-6 acabaría sonando dentro del 1-2); <c>pan</c> selecciona sin mezclar.
    /// <list type="bullet">
    ///   <item>Single: un pan con la distribución del perfil (estéreo del par elegido, 5.1 con los seis primeros…).</item>
    ///   <item>PairsAsTracks: un pan estéreo por par elegido, cada uno con título «Canales 3-4».</item>
    ///   <item>Multichannel: un pan con todos los canales elegidos en una pista PCM de distribución sin nombre («8c»).</item>
    /// </list>
    /// El modo pedido se AJUSTA a lo que el códec y el contenedor pueden llevar sin estropear el audio (medido con el
    /// FFmpeg empaquetado, auditoría 2026-09-18):
    /// <list type="bullet">
    ///   <item>Single con estéreo (lo que trae todo preset por defecto) y VARIOS pares elegidos en la entrada («Todos los
    ///   pares») → una pista estéreo por par, como PairsAsTracks: una pista estéreo no puede llevar más de un par, y lo
    ///   que el operador eligió en la entrada manda. Antes se guardaba SOLO el primer par, en silencio, mientras los
    ///   medidores marcaban todos como grabados (lo vio el operador al abrir el archivo, 2026-09-22). Mono, 5.1 y 7.1
    ///   sí son una decisión del preset y se respetan (los primeros canales elegidos forman esa pista).</item>
    ///   <item>Multichannel con un códec con pérdida → una pista estéreo por par. Una pista 5.1/7.1 en AAC o FDK-AAC
    ///   codifica el canal 4 como LFE (banda limitada): un tono de 1,2 kHz ahí salía a −89 dB; FDK con «quad» lo
    ///   rematriza a 5.0; y MP2/MP3, que solo admiten estéreo, MEZCLAN los ocho canales sin dar error.</item>
    ///   <item>Varias pistas en un contenedor de un solo flujo de audio (WAV, MP3) → una sola: todos los canales en una
    ///   pista si es PCM (WAV), o el primer par si tiene pérdida. Si no, el muxer aborta y no se graba nada.</item>
    ///   <item>Single con una distribución que el códec no puede llevar (5.1 con MP2) → estéreo del primer par.</item>
    /// </list>
    /// </summary>
    internal static IReadOnlyList<AudioRoute>? AudioRoutes(ICaptureSource source, RecordingProfile profile)
    {
        int available = source.AudioChannelCount;
        if (!AudioSelection.RequiresRouting(available)) return null;
        var channels = AudioSelection.FromParameters(source.Definition.Parameters).ChannelIndexes(available);

        bool pcm = FfmpegCodecMap.IsPcmAudio(profile.AudioCodec, profile.Container);
        var mode = profile.AudioTracks;
        if (mode is AudioTrackMode.Single && profile.AudioLayout == AudioLayout.Stereo && channels.Count > 2)
            mode = AudioTrackMode.PairsAsTracks;
        if (mode is AudioTrackMode.Multichannel && !pcm) mode = AudioTrackMode.PairsAsTracks;
        if (mode is AudioTrackMode.PairsAsTracks && FfmpegCodecMap.SingleAudioStream(profile.Container))
            mode = pcm ? AudioTrackMode.Multichannel : AudioTrackMode.Single;

        switch (mode)
        {
            case AudioTrackMode.PairsAsTracks:
            {
                var routes = new List<AudioRoute>();
                for (int i = 0; i < channels.Count; i += 2)
                {
                    int a = channels[i];
                    bool paired = i + 1 < channels.Count;
                    int b = paired ? channels[i + 1] : a;
                    // El título va en el idioma de la aplicación: es lo que verá el montador en su editor. Un canal
                    // suelto (fuente con recuento impar) se duplica en L/R y se titula en singular.
                    string title = paired
                        ? Localizer.F("Audio_TrackTitle", string.Create(CultureInfo.InvariantCulture, $"{a + 1}-{b + 1}"))
                        : Localizer.F("Audio_TrackTitleOne", a + 1);
                    routes.Add(new AudioRoute(string.Create(CultureInfo.InvariantCulture, $"pan=stereo|c0=c{a}|c1=c{b}"), title,
                        paired ? new[] { a, b } : new[] { a }, AudioLayout.Stereo));
                }
                return routes;
            }
            case AudioTrackMode.Multichannel:
                return new[] { new AudioRoute(MultichannelPan(channels), null, channels) };
            default:
            {
                var layout = profile.AudioLayout;
                if (LayoutChannels(layout) > FfmpegCodecMap.MaxAudioChannels(profile.AudioCodec, profile.Container))
                    layout = AudioLayout.Stereo;
                // Los canales que ese pan usa de verdad: mono y estéreo, los dos primeros; 5.1 y 7.1, los seis u ocho primeros.
                var used = channels.Take(Math.Max(2, LayoutChannels(layout))).ToArray();
                return new[] { new AudioRoute(PanFilter(layout, channels), null, used, layout) };
            }
        }
    }

    private static int LayoutChannels(AudioLayout layout) => layout switch
    {
        AudioLayout.Mono => 1, AudioLayout.Surround51 => 6, AudioLayout.Surround71 => 8, _ => 2
    };

    /// <summary>Primer flujo de audio (para las rutas de un solo stream: <see cref="Build"/> y solo-audio), o null.</summary>
    internal static string? AudioRoutingFilter(ICaptureSource source, RecordingProfile profile)
        => AudioRoutes(source, profile)?[0].Pan;

    /// <summary>El primer par elegido de una fuente multicanal, en estéreo: lo que vigila el detector de silencio.</summary>
    private static string MeterPan(ICaptureSource source)
        => PanFilter(AudioLayout.Stereo, AudioSelection.FromParameters(source.Definition.Parameters).ChannelIndexes(source.AudioChannelCount));

    /// <summary>
    /// Cadena de medida de una fuente multicanal: <c>ebur128</c> ANTES de elegir nada, sobre todos los canales capturados
    /// (un true-peak por canal → un medidor por par en la UI/API) y, si se analiza la señal, el detector de silencio
    /// DESPUÉS del pan al primer par elegido: la alarma habla de lo que se graba, no del resto del SDI.
    /// </summary>
    private static string MeterChain(ICaptureSource source, bool analyze)
        => analyze ? $"ebur128=peak=true,{MeterPan(source)},{SilenceDetect}" : "ebur128=peak=true";

    /// <summary>
    /// Pan de una sola pista PCM con todos los canales elegidos, en una distribución SIN NOMBRE («8c», «16c»): así ningún
    /// canal se trata como LFE ni se rematriza; MXF, MKV, AVI y WAV la escriben con cualquier recuento (probado con 16).
    /// Solo para PCM: con un códec con pérdida <see cref="AudioRoutes"/> reparte por pares.
    /// </summary>
    internal static string MultichannelPan(IReadOnlyList<int> channels)
    {
        int count = channels.Count;
        string layout = count switch
        {
            1 => "mono",
            2 => "stereo",
            _ => string.Create(CultureInfo.InvariantCulture, $"{count}c"),
        };
        var sb = new StringBuilder("pan=").Append(layout);
        for (int i = 0; i < count; i++) sb.Append(CultureInfo.InvariantCulture, $"|c{i}=c{channels[i]}");
        return sb.ToString();
    }

    /// <summary>
    /// Grafo de audio a partir de <paramref name="input"/> («[0:a:0]» o «[1:a]»): reparte con asplit a los medidores
    /// (<paramref name="meterChain"/>, sobre todos los canales capturados → [amout]) y a una rama por pista
    /// (<paramref name="label"/>1…N). Sin pistas (solo preview) va todo a los medidores.
    /// </summary>
    private static string AudioGraph(string input, IReadOnlyList<AudioRoute>? routes, string meterChain, string label)
    {
        if (routes is null || routes.Count == 0)
            return string.Create(CultureInfo.InvariantCulture, $"{input}{meterChain}[amout]");
        var sb = new StringBuilder();
        sb.Append(input).Append(CultureInfo.InvariantCulture, $"asplit={routes.Count + 1}[m0]");
        for (int i = 1; i <= routes.Count; i++) sb.Append(CultureInfo.InvariantCulture, $"[r{i}]");
        sb.Append(CultureInfo.InvariantCulture, $";[m0]{meterChain}[amout]");
        for (int i = 1; i <= routes.Count; i++) sb.Append(CultureInfo.InvariantCulture, $";[r{i}]{routes[i - 1].Pan}[{label}{i}]");
        return sb.ToString();
    }

    private static IReadOnlyList<string> RouteLabels(IReadOnlyList<AudioRoute> routes, string label)
        => Enumerable.Range(1, routes.Count).Select(i => string.Create(CultureInfo.InvariantCulture, $"[{label}{i}]")).ToList();

    /// <summary>
    /// Títulos de pista (metadatos por stream de audio) para que el editor vea «Canales 3-4» y no «Audio 2». En MP4/MOV van
    /// además como <c>handler_name</c> (el nombre del <c>hdlr</c> de la pista; si no, «SoundHandler»): ahí el <c>title</c>
    /// acaba en un átomo <c>udta/name</c> que el demuxer de FFmpeg no lee —ffprobe no lo ve— y que el remux a faststart del
    /// Detener, por eso mismo, ya no escribe; el <c>handler_name</c> sí lo leen ffprobe y el remux, que lo conserva (medido
    /// con FFmpeg 8.0, también en segmentos). En los demás basta <c>title</c>: MKV y AVI lo guardan (MKV añadiría el
    /// <c>handler_name</c> como una etiqueta HANDLER_NAME repetida) y MXF y TS no guardan ninguno de los dos.
    /// </summary>
    private static IEnumerable<string> TrackTitles(IReadOnlyList<AudioRoute> routes, ContainerFormat container)
    {
        bool handlerName = container is ContainerFormat.Mp4 or ContainerFormat.Mov;
        for (int i = 0; i < routes.Count; i++)
        {
            if (routes[i].Title is not { } title) continue;
            string stream = string.Create(CultureInfo.InvariantCulture, $"-metadata:s:a:{i}");
            yield return stream;
            yield return $"title={title}";
            if (!handlerName) continue;
            yield return stream;
            yield return $"handler_name={title}";
        }
    }

    /// <summary>
    /// Construye el <c>pan</c> para una distribución de salida a partir de los canales de entrada elegidos (0-based,
    /// en orden). Mono mezcla el primer par; estéreo toma los dos primeros; 5.1/7.1 colocan los canales en orden
    /// (L R C LFE Ls Rs [Lb Rb], el orden habitual del audio embebido). Las salidas sin canal quedan en silencio.
    /// </summary>
    internal static string PanFilter(AudioLayout layout, IReadOnlyList<int> channels)
    {
        if (channels.Count == 0) throw new ArgumentException("Hace falta al menos un canal de entrada.", nameof(channels));
        int first = channels[0];
        int second = channels.Count > 1 ? channels[1] : channels[0];
        switch (layout)
        {
            case AudioLayout.Mono:
                return channels.Count > 1
                    ? string.Create(CultureInfo.InvariantCulture, $"pan=mono|c0=0.5*c{first}+0.5*c{second}")
                    : string.Create(CultureInfo.InvariantCulture, $"pan=mono|c0=c{first}");
            case AudioLayout.Surround51: return Multichannel("5.1", 6);
            case AudioLayout.Surround71: return Multichannel("7.1", 8);
            default:
                return string.Create(CultureInfo.InvariantCulture, $"pan=stereo|c0=c{first}|c1=c{second}");
        }

        string Multichannel(string name, int outputs)
        {
            var sb = new StringBuilder("pan=").Append(name);
            for (int i = 0; i < outputs && i < channels.Count; i++)
                sb.Append(CultureInfo.InvariantCulture, $"|c{i}=c{channels[i]}");
            return sb.ToString();
        }
    }

    /// <param name="p">Perfil de grabación.</param>
    /// <param name="channelCount">Emitir <c>-ac</c> según la distribución del perfil. Falso cuando un <c>pan</c> ya
    /// fijó los canales (repetirlo sería inocuo en estéreo, pero volvería a mezclar en mono/5.1 sobre lo elegido).</param>
    private IEnumerable<string> AudioEncoderArgs(RecordingProfile p, bool channelCount = true)
    {
        var codec = FfmpegCodecMap.EffectiveAudioEncoder(p.AudioCodec, p.Container);
        var list = new List<string>
        {
            "-c:a", codec, "-ar", p.AudioSampleRate.ToString(CultureInfo.InvariantCulture)
        };
        if (!codec.StartsWith("pcm", StringComparison.Ordinal))
        {
            list.Add("-b:a");
            list.Add(p.AudioBitrate.BitsPerSecond.ToString(CultureInfo.InvariantCulture));
        }
        if (channelCount)
        {
            int channels = p.AudioLayout switch
            {
                AudioLayout.Mono => 1, AudioLayout.Stereo => 2,
                AudioLayout.Surround51 => 6, AudioLayout.Surround71 => 8, _ => 2
            };
            list.Add("-ac"); list.Add(channels.ToString(CultureInfo.InvariantCulture));
        }
        return list;
    }

    private IEnumerable<string> BuildSegmentOutput(RecordingProfile p, string recMux, string recExt)
    {
        var seg = p.Segmentation!;
        long seconds = (long)(seg.Duration ?? TimeSpan.FromMinutes(15)).TotalSeconds;
        var (pattern, glob) = SegmentNameParts(recExt); // sin `-strftime 1` (rompía la cabecera)
        IsSegmentedOutput = true;
        SegmentFileGlob = glob;
        var a = new List<string>
        {
            "-f", "segment",
            "-segment_time", seconds.ToString(CultureInfo.InvariantCulture),
            "-segment_format", recMux,
            "-reset_timestamps", "1",
        };
        if (_baseName is not null) { a.Add("-segment_start_number"); a.Add(_segmentStartNumber.ToString(CultureInfo.InvariantCulture)); }
        a.Add("-y"); a.Add(pattern);
        return a;
    }

    // Esquemas de URL admitidos para un destino de streaming (whitelist). Cualquier otro se rechaza.
    private static readonly string[] AllowedStreamSchemes =
        { "rtmp://", "rtmps://", "srt://", "udp://", "rtp://", "tcp://", "http://", "https://" };

    /// <summary>
    /// Valida la URL de un destino de streaming antes de incrustarla en el target del muxer <c>tee</c>. La
    /// sintaxis del tee es <c>[opciones]url|[opciones]url…</c>: el separador <c>|</c> y los corchetes <c>[ ]</c>
    /// son metacaracteres, así que una URL que los contenga podría inyectar ramas o alterar opciones del muxer
    /// (p. ej. desviar el stream a un destino ajeno). Se exige además un esquema conocido. Fail-closed: una URL
    /// inválida aborta la construcción del comando en vez de emitir argumentos potencialmente inyectados. (Auditoría 24/7, #56.)
    /// </summary>
    private static void ValidateStreamUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("La URL de streaming no puede estar vacía.");
        if (url.Contains('|') || url.Contains('[') || url.Contains(']'))
            throw new ArgumentException($"La URL de streaming contiene caracteres no permitidos (| [ ]): «{url}».");
        if (!AllowedStreamSchemes.Any(s => url.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"La URL de streaming usa un esquema no permitido (rtmp/rtmps/srt/udp/rtp/tcp/http/https): «{url}».");
    }

    /// <summary>Rama de grabación (archivo o segmentos) + ramas de streaming para el muxer tee.</summary>
    private string BuildTeeTarget(RecordingProfile p, string recMux, string recExt, bool segmented)
    {
        var branches = new List<string>();

        if (segmented)
        {
            var seg = p.Segmentation!;
            long seconds = (long)(seg.Duration ?? TimeSpan.FromMinutes(15)).TotalSeconds;
            var (pattern, glob) = SegmentNameParts(recExt);
            IsSegmentedOutput = true;
            SegmentFileGlob = glob;
            string startNum = _baseName is not null ? $":segment_start_number={_segmentStartNumber}" : "";
            branches.Add($"[f=segment:segment_time={seconds}:segment_format={recMux}:reset_timestamps=1{startNum}]{pattern}");
        }
        else
        {
            OutputFilePath = Path.Combine(_outputDirectory, SingleFileName(recExt));
            branches.Add($"[f={recMux}:onfail=ignore]{OutputFilePath}");
        }

        foreach (var t in p.StreamTargets.Where(t => t.Enabled))
        {
            ValidateStreamUrl(t.Url); // rechaza inyección de ramas/opciones del tee y esquemas no permitidos (#56)
            string mux = t.Protocol switch
            {
                StreamProtocol.Srt or StreamProtocol.Udp => "mpegts",
                StreamProtocol.Rtmp => "flv",
                StreamProtocol.Ndi => "libndi_newtek",
                _ => "mpegts"
            };
            branches.Add($"[f={mux}:onfail=ignore]{t.Url}");
        }

        return string.Join('|', branches);
    }
}
