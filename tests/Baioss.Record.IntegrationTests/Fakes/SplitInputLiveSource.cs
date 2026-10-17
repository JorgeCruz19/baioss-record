using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Baioss.Record.Application.Capture;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Infrastructure.Capture;

namespace Baioss.Record.IntegrationTests.Fakes;

/// <summary>
/// Fuente en vivo sintética servida como una NDI, sin el runtime NDI: vídeo crudo UYVY (una barra que cruza, 720p a 29.97) y un
/// tono de 1 kHz en f32le estéreo, por DOS sockets loopback y a tiempo real, con los mismos flujos que el receptor NDI
/// (<see cref="NdiFeed{T}"/>, su cola y su fin de flujo) y los mismos argumentos de entrada que <see cref="NdiCaptureSource"/>.
/// </summary>
internal sealed class SplitInputLiveSource : ICaptureSource
{
    public const int Width = 1280, Height = 720;
    private const int FpsN = 30000, FpsD = 1001, SampleRate = 48000, Channels = 2;

    private readonly NdiFeed<byte[]> _video = new(4, b => b, _ => { });
    private readonly NdiFeed<byte[]> _audio = new(16, b => b, _ => { });
    private readonly TcpListener _videoListener = new(IPAddress.Loopback, 0);
    private readonly TcpListener _audioListener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _sender;

    public SplitInputLiveSource()
    {
        _videoListener.Start();
        _audioListener.Start();
        _ = AcceptAsync(_videoListener, _video);
        _ = AcceptAsync(_audioListener, _audio);
        _sender = new Thread(Send) { IsBackground = true, Name = "fuente sintética en dos entradas" };
        _sender.Start();
    }

    /// <summary>Con false, la fuente no sabe cerrar la entrada: el proceso se retira con la «q», como antes del fin de flujo.</summary>
    public bool CanEndInput { get; init; } = true;

    public InputSource Definition { get; } = new() { Name = "sintética en dos entradas", Type = InputType.Ndi, Uri = "split" };
    public SignalInfo CurrentSignal { get; } = new(SignalState.Locked, new Resolution(Width, Height), new FrameRate(FpsN, FpsD),
        AudioLayout.Stereo, HasAudio: true, Timecode: null, Bitrate: null, Scan: ScanType.Progressive);
    public event EventHandler<SignalInfo>? SignalChanged { add { } remove { } }
    public int AudioInputIndex => 1;
    public bool SelfReportsRecovery => true;

    public bool EndInput()
    {
        if (!CanEndInput) return false;
        bool video = _video.End();
        bool audio = _audio.End();
        return video && audio;
    }

    public IReadOnlyList<string> BuildInputArguments() => new[]
    {
        "-f", "rawvideo", "-pixel_format", "uyvy422", "-video_size", $"{Width}x{Height}", "-framerate", $"{FpsN}/{FpsD}",
        "-analyzeduration", "0", "-probesize", "32", "-fflags", "nobuffer",
        "-i", $"tcp://127.0.0.1:{((IPEndPoint)_videoListener.LocalEndpoint).Port}",
        "-f", "f32le", "-ar", SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", Channels.ToString(CultureInfo.InvariantCulture),
        "-analyzeduration", "0", "-probesize", "32",
        "-i", $"tcp://127.0.0.1:{((IPEndPoint)_audioListener.LocalEndpoint).Port}",
    };

    private async Task AcceptAsync(TcpListener listener, NdiFeed<byte[]> feed)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(_cts.Token);
                client.NoDelay = true;
                feed.Attach(client);
            }
            catch (OperationCanceledException) { return; }
            catch { /* reintenta */ }
        }
    }

    /// <summary>Cada periodo de cuadro, el cuadro y su audio (1601 o 1602 muestras), como entrega una fuente NDI.</summary>
    private void Send()
    {
        const int stride = Width * 2, bar = 24;
        var frames = new byte[15][];
        for (int i = 0; i < frames.Length; i++)
        {
            var f = new byte[stride * Height];
            int x0 = (int)(Width / 2.0 + Width * 0.45 * Math.Sin(2 * Math.PI * i / frames.Length)) & ~1;
            for (int x = 0; x < Width; x += 2)
            {
                byte y = (byte)(x >= x0 && x < x0 + bar ? 235 : 48);
                f[x * 2] = 128; f[x * 2 + 1] = y; f[x * 2 + 2] = 128; f[x * 2 + 3] = y; // U Y V Y
            }
            for (int line = 1; line < Height; line++) Buffer.BlockCopy(f, 0, f, line * stride, stride);
            frames[i] = f;
        }
        double fps = (double)FpsN / FpsD;
        long n = 0, samples = 0;
        var clock = Stopwatch.StartNew();
        while (!_cts.IsCancellationRequested)
        {
            if (clock.Elapsed.TotalSeconds < n / fps) { Thread.Sleep(1); continue; }
            _video.Enqueue(frames[n % frames.Length]);
            long target = (long)Math.Round((n + 1) * SampleRate / fps);
            int count = (int)(target - samples);
            var block = new byte[count * Channels * sizeof(float)];
            var pcm = MemoryMarshal.Cast<byte, float>(block.AsSpan());
            for (int i = 0; i < count; i++)
            {
                float s = (float)(0.1 * Math.Sin(2 * Math.PI * 1000 * (samples + i) / SampleRate)); // −20 dBFS
                pcm[2 * i] = s; pcm[2 * i + 1] = s;
            }
            _audio.Enqueue(block);
            samples = target;
            n++;
        }
    }

    public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _sender.Join(TimeSpan.FromSeconds(2));
        await _video.DisposeAsync();
        await _audio.DisposeAsync();
        _videoListener.Stop();
        _audioListener.Stop();
        _cts.Dispose();
    }
}
