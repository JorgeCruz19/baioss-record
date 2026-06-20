using System.Globalization;
using System.Text;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Construye el argv de FFmpeg para una sesión de grabación. Devuelve tokens (no una
/// cadena de shell) para evitar problemas de quoting/inyección.
///
/// Estrategia de salida:
///  - solo grabación  → salida directa (más simple y robusta);
///  - con segmentación → muxer <c>segment</c> (strftime + reset_timestamps);
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

    /// <summary>Ruta absoluta del archivo principal cuando NO hay segmentación.</summary>
    public string OutputFilePath { get; private set; } = "";

    public FfmpegArgumentBuilder From(ICaptureSource source) { _source = source; return this; }
    public FfmpegArgumentBuilder Using(RecordingProfile profile) { _profile = profile; return this; }
    public FfmpegArgumentBuilder ToDirectory(string dir) { _outputDirectory = dir; return this; }
    public FfmpegArgumentBuilder ProxyToDirectory(string dir) { _proxyDirectory = dir; return this; }
    public FfmpegArgumentBuilder ForChannel(string key) { _channelKey = key; return this; }

    /// <summary>Destino (URL FFmpeg, p. ej. <c>tcp://127.0.0.1:9001</c>) de los frames BGRA de preview.</summary>
    public FfmpegArgumentBuilder WithPreviewSink(string ffmpegUrl) { _previewSink = ffmpegUrl; return this; }

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
            OutputFilePath = Path.Combine(_outputDirectory,
                $"{_channelKey}_{DateTime.Now:yyyyMMdd_HHmmss}.{recExt}");
            // Medición de loudness/true-peak para los medidores VU (se emite por stderr).
            args.Add("-af"); args.Add("ebur128=peak=true");
            if (profile.Container is ContainerFormat.Mp4 or ContainerFormat.Mov)
            { args.Add("-movflags"); args.Add("+faststart"); }
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

        // Una fuente sin pista de audio (cámara/dispositivo solo-vídeo) no admite el medidor ebur128
        // ni un mapeo de audio: un output solo-audio sin streams haría abortar a FFmpeg.
        bool hasAudio = source.CurrentSignal.HasAudio;

        var args = new List<string> { "-hide_banner", "-progress", "pipe:1", "-stats_period", "1" };
        args.AddRange(FfmpegCodecMap.HwAccelInput(profile.HwAccel));
        args.AddRange(source.BuildInputArguments());

        string previewChain = string.Create(CultureInfo.InvariantCulture, $"scale={previewWidth}:{previewHeight},format=bgra");
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
                recChain.Add("drawtext=timecode='00\\:00\\:00\\:00':rate=25:fontcolor=white:box=1:boxcolor=black@0.5:x=20:y=20");

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
            args.Add("-map"); args.Add("0:a:0?");
            args.Add("-af"); args.Add("ebur128=peak=true");
            args.Add("-f"); args.Add("null"); args.Add("-");
        }

        // Salida C — grabación (solo cuando se graba).
        if (recording)
        {
            var (recMux, recExt) = FfmpegCodecMap.Container(profile.Container);
            OutputFilePath = Path.Combine(_outputDirectory, $"{_channelKey}_{DateTime.Now:yyyyMMdd_HHmmss}.{recExt}");

            if (profile.AudioOnly)
            {
                args.Add("-map"); args.Add("0:a:0?");
                args.AddRange(AudioEncoderArgs(profile));
            }
            else
            {
                args.Add("-map"); args.Add(recLabel);
                if (hasAudio) { args.Add("-map"); args.Add("0:a:0?"); }
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
                if (profile.Container is ContainerFormat.Mp4 or ContainerFormat.Mov)
                { args.Add("-movflags"); args.Add("+faststart"); }
            }
            args.Add("-f"); args.Add(recMux);
            args.Add("-y"); args.Add(OutputFilePath);
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
            if (p.ClosedGop) list.Add("-no-scenecut");
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
        OutputFilePath = Path.Combine(_outputDirectory, $"{_channelKey}_{DateTime.Now:yyyyMMdd_HHmmss}.{recExt}");
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
        _ => null // NVENC: el encoder elige (nv12/p010)
    };

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
        string pattern = Path.Combine(_outputDirectory, $"{_channelKey}_%Y%m%d_%H%M%S_%03d.{recExt}");
        return new[]
        {
            "-f", "segment",
            "-segment_time", seconds.ToString(CultureInfo.InvariantCulture),
            "-segment_format", recMux,
            "-strftime", "1",
            "-reset_timestamps", "1",
            "-y", pattern
        };
    }

    /// <summary>Rama de grabación (archivo o segmentos) + ramas de streaming para el muxer tee.</summary>
    private string BuildTeeTarget(RecordingProfile p, string recMux, string recExt, bool segmented)
    {
        var branches = new List<string>();

        if (segmented)
        {
            var seg = p.Segmentation!;
            long seconds = (long)(seg.Duration ?? TimeSpan.FromMinutes(15)).TotalSeconds;
            string pattern = Path.Combine(_outputDirectory, $"{_channelKey}_%Y%m%d_%H%M%S_%03d.{recExt}");
            branches.Add($"[f=segment:segment_time={seconds}:segment_format={recMux}:strftime=1:reset_timestamps=1]{pattern}");
        }
        else
        {
            OutputFilePath = Path.Combine(_outputDirectory, $"{_channelKey}_{DateTime.Now:yyyyMMdd_HHmmss}.{recExt}");
            branches.Add($"[f={recMux}:onfail=ignore]{OutputFilePath}");
        }

        foreach (var t in p.StreamTargets.Where(t => t.Enabled))
        {
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
