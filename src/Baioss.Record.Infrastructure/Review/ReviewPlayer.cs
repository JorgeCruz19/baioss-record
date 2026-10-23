using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Baioss.Record.Application.Clips;
using Baioss.Record.Engine.FFmpeg;
using Baioss.Record.Engine.FFmpeg.Clips;

namespace Baioss.Record.Infrastructure.Review;

/// <summary>Un cuadro decodificado del reproductor: BGRA de arriba abajo. El búfer es del reproductor y solo vale durante el
/// evento (quien lo pinte, que lo copie).</summary>
public sealed record ReviewFrame(byte[] Bgra, int Width, int Height, long Frame);

/// <summary>
/// Salida de audio del reproductor (en la aplicación, la tarjeta de sonido de Windows; en las pruebas, nada): PCM de 16 bits,
/// estéreo, 48 kHz. Su posición es el RELOJ de la reproducción: el vídeo se enseña cuando el audio llega a él.
/// </summary>
public interface IReviewAudioOutput : IDisposable
{
    /// <summary>Tira lo que quede por sonar y vuelve a 0 (reanudada).</summary>
    void Reset();
    /// <summary>Encola audio. Espera (sin pasar de <paramref name="ct"/>) mientras haya de sobra por sonar.</summary>
    void Write(byte[] buffer, int count, CancellationToken ct);
    void Pause();
    void Resume();
    /// <summary>Lo que ha sonado desde el último <see cref="Reset"/>.</summary>
    TimeSpan Position { get; }
}

/// <summary>
/// Reproductor de la revisión con el FFmpeg de la aplicación (sin bibliotecas nuevas): un proceso decodifica el vídeo a BGRA
/// —como el preview— desde el FOTOGRAMA CLAVE ANTERIOR al cuadro pedido y tira los de antes, así que lo que se ve es siempre el
/// cuadro exacto y entero (nunca los macrobloques de un reproductor que salta al GOP); otro, el audio en PCM. Avanzar un cuadro
/// lee el siguiente del mismo proceso (instantáneo); retroceder o saltar abre otro. Las órdenes se atienden en orden en un solo
/// hilo y las búsquedas seguidas (arrastrar la barra) se funden: solo se decodifica la última.
/// </summary>
public sealed class ReviewPlayer : IAsyncDisposable
{
    /// <summary>Tamaño máximo de lo que se decodifica para ver (el aspecto se conserva).</summary>
    public const int MaxWidth = 1280, MaxHeight = 720;

    private readonly string _ffmpeg;
    private readonly RecordingTimeline _timeline;
    private readonly IReviewAudioOutput? _audio;
    private readonly IReviewAudioOutput? _audioOwned; // la salida que se recibió (se cierra con el reproductor, aunque no haya audio)
    private readonly ILogger _log;
    private readonly bool _deinterlace;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private readonly CancellationTokenSource _stop = new();
    private readonly TimelineInput? _all; // toda la grabación como una lista concat (si sus piezas casan)
    private readonly string? _allList;
    private readonly byte[][] _buffers;
    private int _nextBuffer;
    private FrameReader? _reader;
    private AudioFeeder? _feeder;
    private long _current = -1;
    private volatile bool _playing;
    private int _pending;

    /// <summary><paramref name="audio"/>: la salida de audio (null = sin sonido); pasa a ser del reproductor, que la cierra al cerrarse.</summary>
    public ReviewPlayer(string ffmpegPath, RecordingTimeline timeline, IReviewAudioOutput? audio, ILogger log)
    {
        _ffmpeg = ffmpegPath;
        _timeline = timeline;
        _audioOwned = audio;
        _audio = timeline.HasAudio ? audio : null;
        _log = log;
        (FrameWidth, FrameHeight) = timeline.HasVideo ? DisplaySize(timeline.Media.Video!) : (0, 0);
        _deinterlace = timeline.Media.Video?.IsInterlaced ?? false;
        _buffers = Enumerable.Range(0, 3).Select(_ => new byte[FrameWidth * FrameHeight * 4]).ToArray();
        if (timeline.Pieces.Count > 1 && timeline.Uniform)
        {
            _all = new TimelineInput(timeline, 0, timeline.Pieces.Count - 1);
            _allList = Path.Combine(Path.GetTempPath(), $"baioss-review-{Guid.NewGuid():N}.ffconcat");
            File.WriteAllText(_allList, _all.ConcatList(), new UTF8Encoding(false));
        }
        _worker = Task.Run(RunAsync);
    }

    public RecordingTimeline Timeline => _timeline;
    public int FrameWidth { get; }
    public int FrameHeight { get; }

    /// <summary>El último cuadro enseñado (−1 antes del primero).</summary>
    public long CurrentFrame => Interlocked.Read(ref _current);
    public bool IsPlaying => _playing;

    /// <summary>Un cuadro nuevo en pantalla (en el hilo del reproductor).</summary>
    public event EventHandler<ReviewFrame>? FrameShown;

    /// <summary>Empezó o paró la reproducción (también al llegar al final).</summary>
    public event EventHandler? PlayingChanged;

    /// <summary>Enseña el cuadro <paramref name="frame"/> (y para la reproducción).</summary>
    public void Seek(long frame) => Post(new Command(CommandKind.Seek, Math.Clamp(frame, 0, Math.Max(0, _timeline.TotalFrames - 1))));

    /// <summary>Avanza (+) o retrocede (−) cuadros desde el actual (y para la reproducción).</summary>
    public void Step(int frames) => Post(new Command(CommandKind.Step, frames));

    public void Play() => Post(new Command(CommandKind.Play, 0));
    public void Pause() => Post(new Command(CommandKind.Pause, 0));
    public void TogglePlay() => Post(new Command(_playing ? CommandKind.Pause : CommandKind.Play, 0));

    /// <summary>Hasta que se atiendan las órdenes que hay en cola (las pruebas esperan así el cuadro de una búsqueda).</summary>
    public async Task WhenIdleAsync(TimeSpan timeout)
    {
        var clock = Stopwatch.StartNew();
        while (Volatile.Read(ref _pending) > 0 && clock.Elapsed < timeout) await Task.Delay(10).ConfigureAwait(false);
    }

    private void Post(Command c)
    {
        Interlocked.Increment(ref _pending);
        if (!_commands.Writer.TryWrite(c)) Interlocked.Decrement(ref _pending);
    }

    private enum CommandKind { Seek, Step, Play, Pause }

    private readonly record struct Command(CommandKind Kind, long Value);

    private async Task RunAsync()
    {
        var ct = _stop.Token;
        try
        {
            while (await _commands.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                if (!_commands.Reader.TryRead(out var command)) continue;
                // Búsquedas seguidas (arrastrar la barra): solo cuenta la última.
                while (command.Kind == CommandKind.Seek && _commands.Reader.TryPeek(out var next) && next.Kind == CommandKind.Seek)
                {
                    _commands.Reader.TryRead(out command);
                    Interlocked.Decrement(ref _pending);
                }
                try { await HandleAsync(command, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _log.LogWarning(ex, "Revisión: fallo del reproductor ({Command}).", command.Kind); }
                finally { Interlocked.Decrement(ref _pending); }
            }
        }
        catch (OperationCanceledException) { /* cierre */ }
    }

    private async Task HandleAsync(Command command, CancellationToken ct)
    {
        switch (command.Kind)
        {
            case CommandKind.Seek:
                SetPlaying(false);
                await ShowAsync(command.Value, ct).ConfigureAwait(false);
                break;
            case CommandKind.Step:
                SetPlaying(false);
                long target = Math.Clamp((_current < 0 ? 0 : _current) + command.Value, 0, Math.Max(0, _timeline.TotalFrames - 1));
                if (target != _current) await ShowAsync(target, ct).ConfigureAwait(false);
                break;
            case CommandKind.Pause:
                SetPlaying(false);
                break;
            case CommandKind.Play:
                await PlayAsync(ct).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>Enseña un cuadro: del proceso que ya está justo antes de él (avanzar), o de uno nuevo que decodifica desde el
    /// fotograma clave anterior.</summary>
    private async Task ShowAsync(long frame, CancellationToken ct)
    {
        StopAudio();
        if (!_timeline.HasVideo) { Publish(null, frame); return; }
        // Hasta medio segundo por delante se lee seguido (más barato que volver a decodificar el GOP desde la clave).
        if (_reader is null || frame < _reader.NextFrame || frame - _reader.NextFrame > Math.Max(2, (long)_timeline.Rate.Value / 2))
            OpenReader(frame);
        while (_reader is { } r)
        {
            var buffer = NextBuffer();
            long index = r.NextFrame;
            if (!await r.ReadAsync(buffer, ct).ConfigureAwait(false))
            {
                // Se acabó esta entrada (la pieza, si las piezas no casan): la siguiente, si queda grabación.
                if (r.EndFrame < _timeline.TotalFrames && r.EndFrame <= frame) { OpenReader(r.EndFrame); continue; }
                return;
            }
            if (index >= frame) { Publish(buffer, index); return; }
        }
    }

    private async Task PlayAsync(CancellationToken ct)
    {
        if (_timeline.TotalFrames == 0) return;
        long start = _current < 0 ? 0 : _current + 1;
        if (start >= _timeline.TotalFrames) start = 0; // al final: desde el principio
        SetPlaying(true);
        if (_timeline.HasVideo && (_reader is null || _reader.NextFrame != start)) OpenReader(start);
        StartAudio(start);
        var clock = Stopwatch.StartNew();
        double frameSeconds = 1 / _timeline.Rate.Value;
        // Con audio, el reloj es lo que ha sonado; hasta que empieza a sonar (lo que tarda en arrancar su proceso, ~0,1 s) se
        // espera un poco para que el vídeo no salga por delante.
        if (_audio is not null)
        {
            var wait = Stopwatch.StartNew();
            while (_audio.Position <= TimeSpan.Zero && wait.ElapsedMilliseconds < 400) await Task.Delay(5, ct).ConfigureAwait(false);
        }
        long frame = start;
        long lastShownAt = 0;
        while (_playing && frame < _timeline.TotalFrames)
        {
            if (_commands.Reader.TryPeek(out _)) return; // otra orden: se atiende (la de pausa para la reproducción)
            if (_timeline.HasVideo)
            {
                var r = _reader!;
                var buffer = NextBuffer();
                long index = r.NextFrame;
                if (!await r.ReadAsync(buffer, ct).ConfigureAwait(false))
                {
                    if (r.EndFrame < _timeline.TotalFrames) { OpenReader(r.EndFrame); continue; }
                    break;
                }
                frame = index;
                double due = (frame - start) * frameSeconds;
                double now = Now(clock);
                // Muy atrasado (la CPU va justa): se salta el cuadro, salvo que haga mucho que no se enseña ninguno.
                if (now - due > 2 * frameSeconds && clock.ElapsedMilliseconds - lastShownAt < 250) { frame++; continue; }
                while (due - now > 0.002 && _playing && !_commands.Reader.TryPeek(out _))
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(due - now, 0.02)), ct).ConfigureAwait(false);
                    now = Now(clock);
                }
                Publish(buffer, frame);
                lastShownAt = clock.ElapsedMilliseconds;
            }
            else
            {
                // Solo audio: la «posición» avanza con lo que suena, a la rejilla de la línea de tiempo.
                await Task.Delay(40, ct).ConfigureAwait(false);
                long at = start + (long)(Now(clock) * _timeline.Rate.Value);
                if (at >= _timeline.TotalFrames) break;
                Publish(null, at);
            }
            frame++;
        }
        if (frame >= _timeline.TotalFrames) { SetPlaying(false); StopAudio(); }
    }

    /// <summary>Segundos de reproducción: lo que ha sonado si hay audio sonando; si no (sin audio, o se acabó), el reloj.</summary>
    private double Now(Stopwatch clock)
    {
        if (_audio is { } a && _feeder is { Finished: false } && a.Position > TimeSpan.Zero) return a.Position.TotalSeconds;
        return clock.Elapsed.TotalSeconds;
    }

    private void SetPlaying(bool playing)
    {
        if (_playing == playing) return;
        _playing = playing;
        if (!playing) _audio?.Pause();
        PlayingChanged?.Invoke(this, EventArgs.Empty);
    }

    private byte[] NextBuffer()
    {
        var b = _buffers[_nextBuffer];
        _nextBuffer = (_nextBuffer + 1) % _buffers.Length;
        return b;
    }

    private void Publish(byte[]? buffer, long frame)
    {
        Interlocked.Exchange(ref _current, frame);
        try { FrameShown?.Invoke(this, new ReviewFrame(buffer ?? Array.Empty<byte>(), FrameWidth, FrameHeight, frame)); }
        catch (Exception ex) { _log.LogDebug(ex, "Revisión: fallo al pintar un cuadro."); }
    }

    /// <summary>La entrada que contiene el cuadro: toda la grabación (si sus piezas casan) o su pieza.</summary>
    private (TimelineInput Input, string Path) InputFor(long frame)
    {
        if (_all is not null) return (_all, _allList!);
        var (piece, _) = _timeline.Locate(frame);
        var input = new TimelineInput(_timeline, piece, piece);
        return (input, input.SingleFile!);
    }

    private void OpenReader(long frame)
    {
        _reader?.Dispose();
        var (input, path) = InputFor(frame);
        var args = ClipCommandBuilder.PlayerVideo(input, path, input.TimeOf(frame), FrameWidth, FrameHeight, _deinterlace, _timeline.Rate);
        _reader = new FrameReader(_ffmpeg, args, frame, input.EndFrame, FrameWidth * FrameHeight * 4, _log);
    }

    private void StartAudio(long frame)
    {
        StopAudio();
        if (_audio is null) return;
        _audio.Reset();
        _feeder = new AudioFeeder(_ffmpeg, _timeline, frame, _audio, InputFor, _log);
    }

    private void StopAudio()
    {
        _feeder?.Dispose();
        _feeder = null;
        _audio?.Pause();
    }

    /// <summary>Tamaño de lo que se decodifica: el aspecto de imagen del vídeo (con su píxel no cuadrado, en SD), dentro de
    /// <see cref="MaxWidth"/>×<see cref="MaxHeight"/>, en pares.</summary>
    internal static (int Width, int Height) DisplaySize(MediaStreamInfo video)
    {
        double sar = 1;
        if (video.SampleAspectRatio?.Split(':') is [var n, var d] && double.TryParse(n, out var sn) && double.TryParse(d, out var sd) && sn > 0 && sd > 0)
            sar = sn / sd;
        double w = Math.Max(2, video.Width * sar), h = Math.Max(2, video.Height);
        double scale = Math.Min(1, Math.Min(MaxWidth / w, MaxHeight / h));
        int width = Math.Max(2, (int)Math.Round(w * scale / 2) * 2), height = Math.Max(2, (int)Math.Round(h * scale / 2) * 2);
        return (width, height);
    }

    public async ValueTask DisposeAsync()
    {
        _commands.Writer.TryComplete();
        _stop.Cancel();
        _playing = false;
        try { await _worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { /* cerrando */ }
        _reader?.Dispose();
        _feeder?.Dispose();
        _audioOwned?.Dispose();
        if (_allList is not null) try { File.Delete(_allList); } catch { /* best-effort */ }
        _stop.Dispose();
    }

    /// <summary>Un proceso que decodifica vídeo desde un cuadro; se lee cuadro a cuadro (la tubería frena a FFmpeg).</summary>
    private sealed class FrameReader : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _stdout;
        private readonly int _frameBytes;

        public FrameReader(string ffmpeg, IReadOnlyList<string> args, long first, long endFrame, int frameBytes, ILogger log)
        {
            _process = ClipProcess.Start(ffmpeg, args, redirectInput: false);
            _stdout = _process.StandardOutput.BaseStream;
            ProcessOutput.ReadLines(_process.StandardError, l => log.LogDebug("revisión ffmpeg: {Line}", l), $"review-{_process.Id}-log");
            NextFrame = first;
            EndFrame = endFrame;
            _frameBytes = frameBytes;
        }

        /// <summary>El cuadro global que saldrá en la próxima lectura.</summary>
        public long NextFrame { get; private set; }
        /// <summary>El cuadro siguiente al último de su entrada.</summary>
        public long EndFrame { get; }

        public async Task<bool> ReadAsync(byte[] buffer, CancellationToken ct)
        {
            int read = 0;
            while (read < _frameBytes)
            {
                int n = await _stdout.ReadAsync(buffer.AsMemory(read, _frameBytes - read), ct).ConfigureAwait(false);
                if (n == 0) return false;
                read += n;
            }
            NextFrame++;
            return true;
        }

        public void Dispose()
        {
            ClipProcess.Kill(_process);
            _process.Dispose();
        }
    }

    /// <summary>Un proceso que decodifica el audio desde un cuadro y lo va encolando en la salida, en un hilo propio (la salida
    /// frena cuando hay de sobra encolado). Al acabar una entrada sigue con la siguiente.</summary>
    private sealed class AudioFeeder : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Thread _thread;
        private Process? _process;

        public AudioFeeder(string ffmpeg, RecordingTimeline timeline, long frame, IReviewAudioOutput output,
            Func<long, (TimelineInput Input, string Path)> inputFor, ILogger log)
        {
            _thread = new Thread(() =>
            {
                var buffer = new byte[48_000 * 4 / 10]; // 100 ms
                long from = frame;
                try
                {
                    while (!_cts.IsCancellationRequested && from < timeline.TotalFrames)
                    {
                        var (input, path) = inputFor(from);
                        var p = ClipProcess.Start(ffmpeg, ClipCommandBuilder.PlayerAudio(input, path, input.TimeOf(from), timeline.Rate), redirectInput: false);
                        _process = p;
                        ProcessOutput.ReadLines(p.StandardError, l => log.LogDebug("revisión ffmpeg (audio): {Line}", l), $"review-{p.Id}-alog");
                        var stdout = p.StandardOutput.BaseStream;
                        int n;
                        while (!_cts.IsCancellationRequested && (n = stdout.Read(buffer, 0, buffer.Length)) > 0)
                            output.Write(buffer, n, _cts.Token);
                        ClipProcess.Kill(p);
                        p.Dispose();
                        from = input.EndFrame;
                    }
                }
                catch (OperationCanceledException) { /* parado */ }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { /* proceso cerrado */ }
                catch (Exception ex) { log.LogDebug(ex, "Revisión: fallo del audio."); }
                finally { Finished = true; }
            }) { IsBackground = true, Name = "review-audio" };
            _thread.Start();
        }

        /// <summary>Ya no queda audio que encolar (se acabó la grabación o se paró).</summary>
        public bool Finished { get; private set; }

        public void Dispose()
        {
            _cts.Cancel();
            if (_process is { } p) ClipProcess.Kill(p);
            _thread.Join(TimeSpan.FromSeconds(2));
            _cts.Dispose();
        }
    }
}
