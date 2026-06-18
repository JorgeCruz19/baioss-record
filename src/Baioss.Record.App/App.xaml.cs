using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Channels;
using Baioss.Record.App.Demo;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Infrastructure.Capture;
using Baioss.Record.Infrastructure.Channels;

namespace Baioss.Record.App;

/// <summary>
/// Composition root. Construye los canales con el motor FFmpeg real (grabación a MP4)
/// usando un clip como fuente en vivo; si no se encuentra FFmpeg o el clip, cae a
/// canales simulados para que la app siga abriendo.
/// </summary>
public partial class App : System.Windows.Application
{
    // Ruta del FFmpeg provisto y clip de demo usado como "fuente en vivo".
    private const string FfmpegDir = @"C:\Users\jcruz\Documents\Projects\baioss-record\src\Baioss.Record.Engine.FFmpeg\ffmpeg";
    private static readonly string ClipPath = Path.GetFullPath("tools/test/clip.mp4");

    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var channels = await BuildChannelsAsync();

        _host = Host.CreateDefaultBuilder()
            .UseSerilog((ctx, cfg) => cfg
                .WriteTo.File("logs/baioss-.log", rollingInterval: RollingInterval.Day)
                .Enrich.FromLogContext())
            .ConfigureServices(s =>
            {
                foreach (var c in channels) s.AddSingleton<IChannelEngine>(c);
                s.AddSingleton<IChannelManager, ChannelManager>();
                s.AddSingleton<ShellViewModel>();
                s.AddSingleton<MainWindow>();
            })
            .Build();

        await _host.StartAsync();
        _host.Services.GetRequiredService<MainWindow>().Show();
    }

    private static async Task<IReadOnlyList<IChannelEngine>> BuildChannelsAsync()
    {
        try
        {
            if (File.Exists(Path.Combine(FfmpegDir, "ffmpeg.exe")) && File.Exists(ClipPath))
            {
                var locator = new FfmpegLocator(FfmpegDir);
                bool nvenc = await locator.IsVideoEncoderUsableAsync("h264_nvenc");
                var codec = nvenc ? VideoCodec.H264Nvenc : VideoCodec.H264x264;
                return new[] { "A", "B" }.Select(k => BuildRealChannel(k, locator, codec)).ToArray();
            }
        }
        catch { /* cualquier fallo de inicialización → modo simulado */ }

        return new IChannelEngine[] { new SimulatedChannelEngine("A"), new SimulatedChannelEngine("B") };
    }

    private static IChannelEngine BuildRealChannel(string key, FfmpegLocator locator, VideoCodec codec)
    {
        var def = new InputSource
        {
            Name = $"Clip {key}", Type = InputType.File, Uri = ClipPath,
            Parameters = { ["loop"] = "1", ["realtime"] = "1" }, // bucle a velocidad real = fuente en vivo
            ExpectedResolution = Resolution.Hd720, ExpectedFrameRate = FrameRate.P25,
        };
        var source = new FileCaptureSource(def);
        source.OpenAsync().GetAwaiter().GetResult();

        var profile = new RecordingProfile
        {
            Name = "MP4 (demo)", VideoCodec = codec, HwAccel = HwAccel.None,
            VideoBitrate = Bitrate.FromMbps(8), GopSize = 50,
            AudioCodec = AudioCodec.Aac, AudioLayout = AudioLayout.Stereo, Container = ContainerFormat.Mp4,
        };

        var recorder = new FfmpegRecorderEngine(locator, NullLogger<FfmpegRecorderEngine>.Instance)
        {
            OutputRoot = "recordings", ProxyRoot = "proxies",
        };
        return new StandaloneChannelEngine(key, source, profile, recorder);
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
        base.OnExit(e);
    }
}
