using System.Globalization;
using System.Text;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Capture;

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

        // 5) Encoders.
        args.AddRange(VideoEncoderArgs(profile));
        args.AddRange(AudioEncoderArgs(profile));

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
            args.Add("-f"); args.Add("tee");
            args.Add(BuildTeeTarget(profile, recMux, recExt, hasSegmentation));
        }
        else if (hasSegmentation)
        {
            args.AddRange(BuildSegmentOutput(profile, recMux, recExt));
        }
        else
        {
            // Salida directa a un único archivo.
            OutputFilePath = Path.Combine(_outputDirectory, SingleFileName(recExt));
            // Medición de loudness/true-peak para los medidores VU (se emite por stderr).
            args.Add("-af"); args.Add("ebur128=peak=true");
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

        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-stats_period", "1" };
        args.AddRange(FfmpegCodecMap.HwAccelInput(profile.HwAccel));
        args.AddRange(source.BuildInputArguments());

        // El preview lleva, opcionalmente, los detectores de negro/congelado (pasan los frames sin
        // alterarlos: solo registran marcas por stderr que se convierten en alarmas).
        string analyze = _analyze ? $"{BlackDetect},{FreezeDetect}," : "";
        string previewChain = string.Create(CultureInfo.InvariantCulture, $"scale={previewWidth}:{previewHeight},{analyze}format=bgra");
        var filter = new StringBuilder();
        string recLabel = "0:v:0";

        if (recording && !profile.AudioOnly)
        {
            // Bifurca el vídeo: una rama a grabación (con su escala/escaneo/burn-in), otra a preview.
            var recChain = new List<string>();
            if (profile.TargetResolution is { } r)
                recChain.Add(string.Create(CultureInfo.InvariantCulture, $"scale={r.Width}:{r.Height}"));
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
            recChain.Add(string.Create(CultureInfo.InvariantCulture, $"format={RecordingFilterPixelFormat(profile)}"));

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

        args.Add("-filter_complex"); args.Add(filter.ToString());

        // Salida A — preview BGRA por TCP (lo lee la app y lo sube a la textura/bitmap).
        args.Add("-map"); args.Add("[pv]");
        args.Add("-f"); args.Add("rawvideo");
        args.Add(_previewSink);

        // Salida B — medidores VU: ebur128 sobre el audio de entrada, descartado a null. Solo si la
        // fuente declara audio: sin pista, este output quedaría sin streams y FFmpeg abortaría todo
        // (preview incluido). Un dispositivo solo-vídeo simplemente no alimenta medidores.
        if (hasAudio)
        {
            args.Add("-map"); args.Add(audioMap);
            args.Add("-af"); args.Add(_analyze ? $"{SilenceDetect},ebur128=peak=true" : "ebur128=peak=true");
            args.Add("-f"); args.Add("null"); args.Add("-");
        }

        // Salida C — grabación (solo cuando se graba).
        if (recording)
        {
            var (recMux, recExt) = FfmpegCodecMap.Container(profile.Container);

            if (profile.AudioOnly)
            {
                args.Add("-map"); args.Add(audioMap);
                args.AddRange(AudioEncoderArgs(profile));
            }
            else
            {
                args.Add("-map"); args.Add(recLabel);
                if (hasAudio) { args.Add("-map"); args.Add(audioMap); }
                args.AddRange(VideoEncoderArgs(profile));
                if (hasAudio) args.AddRange(AudioEncoderArgs(profile));
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
            args.AddRange(RecordOutputTail(profile, recMux, recExt));
        }

        return args;
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
            var a = new List<string>
            {
                "-f", "segment",
                "-segment_time", seconds.ToString(CultureInfo.InvariantCulture),
                "-segment_format", recMux,
                "-reset_timestamps", "1",
            };
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
        return (Path.Combine(_outputDirectory, $"{baseName}_{counter}.{ext}"), glob);
    }

    /// <summary>Base del nombre: la elegida (manual/programada) o, en su defecto, {canal}_{fecha_hora}.</summary>
    private string ResolvedBase() => _baseName ?? $"{_channelKey}_{DateTime.Now:yyyyMMdd_HHmmss}";

    /// <summary>
    /// Argv de la CARTA DE AJUSTE (slate): al perder la señal en vivo durante la grabación, sustituye la
    /// entrada del dispositivo por barras SMPTE + silencio generados con <c>lavfi</c>, conservando las
    /// MISMAS salidas (preview + medidores + grabación) y la misma resolución/tasa de destino. Así la
    /// grabación continúa (un nuevo segmento) sin romper la base de tiempo; las barras llevan el rótulo
    /// «SIN SEÑAL» como evidencia. Pensado para fuentes de vídeo; se reanuda la fuente al volver la señal.
    /// </summary>
    public IReadOnlyList<string> BuildSlate(bool recording, int previewWidth, int previewHeight)
    {
        var profile = _profile ?? throw new InvalidOperationException("Falta el perfil.");
        if (string.IsNullOrEmpty(_previewSink))
            throw new InvalidOperationException("Falta el destino de preview (WithPreviewSink).");

        IsSegmentedOutput = false;

        // Resolución/tasa del slate = las de la grabación, para que sus segmentos concatenen con los de
        // la fuente real sin saltos de formato.
        var res = profile.TargetResolution ?? _source?.CurrentSignal.Resolution ?? Resolution.Hd1080;
        var rate = profile.OutputFrameRate ?? _source?.CurrentSignal.FrameRate ?? FrameRate.P25;
        string size = string.Create(CultureInfo.InvariantCulture, $"{res.Width}x{res.Height}");
        string r = string.Create(CultureInfo.InvariantCulture, $"{rate.Numerator}/{rate.Denominator}");

        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-stats_period", "1" };
        // Entradas generadas: barras de ajuste (0:v) + silencio (1:a), con base de tiempo continua.
        args.Add("-f"); args.Add("lavfi");
        args.Add("-i"); args.Add(string.Create(CultureInfo.InvariantCulture, $"smptebars=size={size}:rate={r}"));
        args.Add("-f"); args.Add("lavfi");
        args.Add("-i"); args.Add(string.Create(CultureInfo.InvariantCulture, $"anullsrc=channel_layout=stereo:sample_rate={profile.AudioSampleRate}"));

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
        }
        else
        {
            filter.Append(CultureInfo.InvariantCulture, $"[0:v]{label},{previewChain}[pv]");
            recLabel = "[0:v]";
        }
        args.Add("-filter_complex"); args.Add(filter.ToString());

        // Salida A — preview de las barras.
        args.Add("-map"); args.Add("[pv]");
        args.Add("-f"); args.Add("rawvideo");
        args.Add(_previewSink);

        // Salida B — medidores sobre el silencio (marcará -inf; sin silencedetect para no duplicar alarma).
        args.Add("-map"); args.Add("1:a:0");
        args.Add("-af"); args.Add("ebur128=peak=true");
        args.Add("-f"); args.Add("null"); args.Add("-");

        // Salida C — grabación del slate (mismo encoder/segmentación que la grabación normal).
        if (recording)
        {
            var (recMux, recExt) = FfmpegCodecMap.Container(profile.Container);
            args.Add("-map"); args.Add(recLabel);
            args.Add("-map"); args.Add("1:a:0");
            args.AddRange(VideoEncoderArgs(profile));
            args.AddRange(AudioEncoderArgs(profile));
            if (profile.OutputFrameRate is { } fr)
            {
                args.Add("-r");
                args.Add(string.Create(CultureInfo.InvariantCulture, $"{fr.Numerator}/{fr.Denominator}"));
            }
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

    private IEnumerable<string> VideoEncoderArgs(RecordingProfile p)
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
        else if (p.VideoCodec is VideoCodec.H264Qsv or VideoCodec.H264Amf)
        {
            // GPU INTEGRADA (Intel QuickSync / AMD AMF): H.264 por bitrate. Args mínimos y portables entre
            // ambos encoders (evita -preset/-crf/-rc, que difieren); el ritmo va por -b:v/-maxrate/-bufsize.
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
        else if (p.VideoCodec is VideoCodec.DnxHd or VideoCodec.DnxHr)
        {
            string dnxProfile = p.EncoderProfile switch
            {
                EncoderProfile.DnxHrLb  => "dnxhr_lb",
                EncoderProfile.DnxHrSq  => "dnxhr_sq",
                EncoderProfile.DnxHrHq  => "dnxhr_hq",
                EncoderProfile.DnxHrHqx => "dnxhr_hqx",
                EncoderProfile.DnxHr444 => "dnxhr_444",
                // Auto: heurística por profundidad (hqx si 10-bit, si no hq) — comportamiento previo.
                _ => p.PixelFormat is PixelFormat.Yuv422p10le or PixelFormat.Yuv420p10le or PixelFormat.Yuv444p10le
                        ? "dnxhr_hqx" : "dnxhr_hq",
            };
            list.AddRange(new[] { "-profile:v", dnxProfile });
        }
        else if (p.VideoCodec is VideoCodec.ProRes)
        {
            string proresProfile = p.EncoderProfile switch
            {
                EncoderProfile.ProResProxy    => "0",
                EncoderProfile.ProResLt       => "1",
                EncoderProfile.ProResStandard => "2",
                EncoderProfile.ProResHq       => "3",
                EncoderProfile.ProRes4444     => "4",
                EncoderProfile.ProRes4444Xq   => "5",
                _ => "3", // Auto = ProRes 422 HQ (comportamiento previo)
            };
            list.AddRange(new[] { "-profile:v", proresProfile });
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

        // Formato de píxel: override explícito del perfil → píxel natural del perfil intra
        // (p. ej. 4444/444 exigen 4:4:4) → predeterminado del códec.
        var pix = FfmpegCodecMap.PixelFormatArg(p.PixelFormat)
                  ?? ProfilePixelFormat(p.EncoderProfile)
                  ?? DefaultPixelFormat(p.VideoCodec);
        if (pix is not null) { list.Add("-pix_fmt"); list.Add(pix); }
        return list;
    }

    /// <summary>Comando de grabación solo-audio: sin video, codec de audio al contenedor elegido.</summary>
    private IReadOnlyList<string> BuildAudioOnly(ICaptureSource source, RecordingProfile profile, string recMux, string recExt)
    {
        OutputFilePath = Path.Combine(_outputDirectory, SingleFileName(recExt));
        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-stats_period", "1" };
        args.AddRange(source.BuildInputArguments());
        args.Add("-vn");
        args.Add("-map"); args.Add("0:a?");
        args.AddRange(AudioEncoderArgs(profile));
        args.Add("-af"); args.Add("ebur128=peak=true"); // niveles para los medidores VU
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
        VideoCodec.H264Qsv or VideoCodec.H264Amf => "nv12", // formato nativo de QSV/AMF
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
    private static string RecordingFilterPixelFormat(RecordingProfile p)
        => FfmpegCodecMap.PixelFormatArg(p.PixelFormat)
           ?? ProfilePixelFormat(p.EncoderProfile)
           ?? DefaultPixelFormat(p.VideoCodec)
           ?? "nv12";

    private IEnumerable<string> AudioEncoderArgs(RecordingProfile p)
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
        int channels = p.AudioLayout switch
        {
            AudioLayout.Mono => 1, AudioLayout.Stereo => 2,
            AudioLayout.Surround51 => 6, AudioLayout.Surround71 => 8, _ => 2
        };
        list.Add("-ac"); list.Add(channels.ToString(CultureInfo.InvariantCulture));
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
