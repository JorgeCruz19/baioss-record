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

    /// <summary>Ruta absoluta del archivo principal cuando NO hay segmentación.</summary>
    public string OutputFilePath { get; private set; } = "";

    public FfmpegArgumentBuilder From(ICaptureSource source) { _source = source; return this; }
    public FfmpegArgumentBuilder Using(RecordingProfile profile) { _profile = profile; return this; }
    public FfmpegArgumentBuilder ToDirectory(string dir) { _outputDirectory = dir; return this; }
    public FfmpegArgumentBuilder ProxyToDirectory(string dir) { _proxyDirectory = dir; return this; }
    public FfmpegArgumentBuilder ForChannel(string key) { _channelKey = key; return this; }

    public IReadOnlyList<string> Build()
    {
        var source = _source ?? throw new InvalidOperationException("Falta la fuente de captura.");
        var profile = _profile ?? throw new InvalidOperationException("Falta el perfil.");
        var (recMux, recExt) = FfmpegCodecMap.Container(profile.Container);

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

        // 3) Filtros: solo si hay proxy o burn-in de timecode.
        bool gpu = FfmpegCodecMap.IsGpuEncoder(profile.VideoCodec);
        bool hasProxy = profile.Proxy is not null;
        bool needFilter = hasProxy || profile.BurnTimecode;
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

    private (string Filter, string MainLabel) BuildFilterGraph(RecordingProfile p, bool gpu, bool hasProxy)
    {
        string scale = gpu ? "scale_cuda" : "scale";
        var sb = new StringBuilder();

        if (hasProxy)
        {
            var px = p.Proxy!;
            sb.Append("[0:v]split=2[main][prx];");
            sb.Append(CultureInfo.InvariantCulture, $"[prx]{scale}={px.Resolution.Width}:{px.Resolution.Height}[proxout]");
        }
        else
        {
            sb.Append("[0:v]null[main]");
        }

        if (p.BurnTimecode)
        {
            // drawtext requiere frames en CPU; sobre GPU se baja con hwdownload.
            string tc = "drawtext=timecode='00\\:00\\:00\\:00':rate=25:fontcolor=white:" +
                        "box=1:boxcolor=black@0.5:x=20:y=20";
            sb.Append(gpu
                ? $";[main]hwdownload,format=nv12,{tc}[mainb]"
                : $";[main]{tc}[mainb]");
            return (sb.ToString(), "[mainb]");
        }
        return (sb.ToString(), "[main]");
    }

    private IEnumerable<string> VideoEncoderArgs(RecordingProfile p)
    {
        var list = new List<string> { "-c:v", FfmpegCodecMap.VideoEncoder(p.VideoCodec) };

        if (FfmpegCodecMap.IsGpuEncoder(p.VideoCodec))
        {
            list.AddRange(new[]
            {
                "-preset", "p5", "-tune", "hq", "-rc", "cbr",
                "-b:v", p.VideoBitrate.BitsPerSecond.ToString(CultureInfo.InvariantCulture),
                "-g", p.GopSize.ToString(CultureInfo.InvariantCulture), "-bf", "0",
            });
            if (p.ClosedGop) list.Add("-no-scenecut");
        }
        else if (p.VideoCodec is VideoCodec.DnxHd or VideoCodec.DnxHr)
        {
            list.AddRange(new[] { "-profile:v", "dnxhr_hq", "-pix_fmt", "yuv422p" });
        }
        else if (p.VideoCodec is VideoCodec.ProRes)
        {
            list.AddRange(new[] { "-profile:v", "3", "-pix_fmt", "yuv422p10le" });
        }
        else // libx264 / libx265
        {
            list.AddRange(new[]
            {
                "-preset", "veryfast", "-pix_fmt", "yuv420p",
                "-b:v", p.VideoBitrate.BitsPerSecond.ToString(CultureInfo.InvariantCulture),
                "-g", p.GopSize.ToString(CultureInfo.InvariantCulture),
            });
        }
        return list;
    }

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
