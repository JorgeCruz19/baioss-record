using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Baioss.Record.Application.Capture;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// Relee entero un archivo grabado y comprueba que es lo que pide su perfil: el códec, el perfil (ProRes, DNxHD/HR), el formato de
/// píxel, el tamaño, la cadencia, el barrido (la marca de los cuadros y lo que ve idet en la imagen y en el croma) y el audio, que se
/// decodifica sin errores con los cuadros que tocan y que dura lo que se grabó. Lo usan el barrido de los presets de fábrica y el de
/// las combinaciones de un preset propio.
/// </summary>
internal static class RecordedFileCheck
{
    /// <summary>Lo que debe llevar el archivo: el perfil con el que se grabó, el tamaño y la cadencia de lo que se codificó (los del
    /// perfil o, en nativo, los de la fuente), los canales de audio y cuánto duró la grabación, de Grabar a Detener.</summary>
    internal sealed record Expected(RecordingProfile Profile, Resolution Size, FrameRate Rate, int AudioChannels, double Recorded)
    {
        /// <summary>El perfil que debe nombrar ffprobe en el vídeo; null = el de ProRes si lo es (<see cref="ProfileName"/>).</summary>
        public string? VideoProfile { get; init; }

        /// <summary>Barrido de la fuente. Con una progresiva y un preset progresivo no hay campos de por medio y idet no se mira: con
        /// testsrc2 a 50 o 60 cuadros ve campos en la propia fuente, sin codificar nada (medido el 2026-10-19).</summary>
        public ScanType? SourceScan { get; init; }
    }

    /// <summary>
    /// Espera a que el motor acabe con el archivo cerrado: lo verifica en segundo plano y, en MP4/MOV (fMP4, como la aplicación), lo
    /// reescribe a faststart; hasta entonces puede estar a medio reescribir (un MOV de DNxHD de 270 MB tardaba más que una espera
    /// fija y ffmpeg decía «Permission denied»). Lo dice su registro; como mucho, 60 s.
    /// </summary>
    internal static async Task WaitForTheEngineAsync(List<string> logs, string? file, ContainerFormat container)
    {
        // Sin archivo (el intento vacío de una grabación que no arrancó, que el motor borra) no hay nada que esperar.
        if (file is null || !File.Exists(file)) return;
        string name = Path.GetFileName(file);
        string[] done = container is ContainerFormat.Mp4 or ContainerFormat.Mov
            ? new[] { "optimizado para búsqueda", "no se pudo optimizar", "supera el límite", "NO pasó la verificación" }
            : new[] { "verificado", "NO pasó la verificación" };
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(60))
        {
            lock (logs)
                if (logs.Any(l => l.Contains(name, StringComparison.Ordinal) && done.Any(d => l.Contains(d, StringComparison.Ordinal))))
                    return;
            await Task.Delay(200);
        }
    }

    /// <summary>Lo que lleva el archivo, en una línea, y lo que no cuadra con <paramref name="e"/> (vacío: todo bien).</summary>
    internal static async Task<(string Summary, List<string> Problems)> CheckAsync(FfmpegLocator locator, string file, Expected e)
    {
        var problems = new List<string>();
        void Expect(bool ok, string what) { if (!ok) problems.Add(what); }
        var profile = e.Profile;
        // El motor puede tener aún el archivo abierto (el remux a faststart de un MOV de 150 MB, la verificación): hasta poder leerlo
        // sin que nadie escriba, se espera (si no, ffmpeg dice «Permission denied»).
        for (int i = 0; i < 100; i++)
        {
            try { using var _ = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read); break; }
            catch (IOException) { await Task.Delay(200); }
        }
        using var probe = JsonDocument.Parse(await RunAsync(locator.FfprobePath, "-v", "error", "-show_entries",
            "stream=codec_type,codec_name,profile,pix_fmt,width,height,field_order,sample_rate,channels,duration:format=duration",
            "-of", "json", file));
        var streams = probe.RootElement.TryGetProperty("streams", out var s) ? s.EnumerateArray().ToList() : new List<JsonElement>();
        double formatDuration = probe.RootElement.TryGetProperty("format", out var format) ? Number(format, "duration") : double.NaN;
        JsonElement? Find(string type) => streams.Where(x => Text(x, "codec_type") == type).Cast<JsonElement?>().FirstOrDefault();
        var video = Find("video");
        var audio = Find("audio");
        var parts = new List<string>();
        // Una decodificación entera: los errores y los cuadros de vídeo que salen. El analizador de Opus de FFmpeg se queja de un
        // paquete de todo flujo de libopus («Error parsing Opus packet header»), también de uno hecho con FFmpeg a pelo y hasta en
        // ffprobe -show_packets; el audio sale entero (medido el 2026-10-18).
        // Con la base de tiempo del archivo: la de la salida nula redondeaba las marcas de un DNxHR 4K (correlativas en el archivo)
        // y se quejaba de «non monotonically increasing dts» sin que el archivo tuviera nada.
        var (progress, log) = await RunWithLogAsync(locator.FfmpegPath, "-v", "error", "-nostats", "-progress", "pipe:1", "-i", file,
            "-map", "0", "-fps_mode", "passthrough", "-enc_time_base", "demux", "-f", "null", "-");
        var errors = log.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.Contains("Error parsing Opus packet header", StringComparison.Ordinal)).ToList();
        Expect(errors.Count == 0, "errores al decodificar: " + string.Join(" | ", errors.Take(3)));
        long frames = Regex.Matches(progress, @"^frame=(\d+)", RegexOptions.Multiline).LastOrDefault() is { } counted
            ? long.Parse(counted.Groups[1].Value, CultureInfo.InvariantCulture) : 0;

        double vDur = double.NaN;
        if (profile.AudioOnly) Expect(video is null, "un perfil de solo audio grabó vídeo");
        else if (video is not { } v) Expect(false, "el archivo no tiene vídeo");
        else
        {
            vDur = Number(v, "duration") is var d && !double.IsNaN(d) ? d : formatDuration;
            // «interlaced_frame,top_field_first» de los primeros cuadros (ffprobe deja una coma detrás).
            var marks = (await RunAsync(locator.FfprobePath, "-v", "error", "-select_streams", "v:0", "-read_intervals", "%+#6",
                    "-show_entries", "frame=interlaced_frame,top_field_first", "-of", "csv=p=0", file))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(f => f.TrimEnd(',')).ToList();
            string? parity = profile.ScanType switch { ScanType.InterlacedTff => "tff", ScanType.InterlacedBff => "bff", _ => null };
            string codec = Text(v, "codec_name"), pix = Text(v, "pix_fmt"), prof = Text(v, "profile");
            int width = (int)Number(v, "width"), height = (int)Number(v, "height");
            double fps = e.Rate.Value, measured = frames / vDur;
            // Lo que ve idet en la imagen (Y) y en el croma (U): un entrelazado con los dos campos intactos peina en los dos, con su
            // orden; uno escalado en vertical sin separar los campos, o con el croma de los dos campos promediado, no. Un progresivo no
            // peina.
            var (luma, chroma) = await IdetAsync(locator.FfmpegPath, file);
            parts.Add($"v:{codec} «{prof}» {pix} {width}x{height} {Text(v, "field_order")} {measured:0.###} fps ({frames} en {vDur:0.00} s) " +
                      $"idet Y {luma} U {chroma}");

            Expect(codec == CodecName(profile.VideoCodec), $"vídeo «{codec}», se esperaba «{CodecName(profile.VideoCodec)}»");
            string wantedPix = PixelFormatName(profile);
            Expect(SamePixelFormat(pix, wantedPix, profile), $"píxel «{pix}», se esperaba «{wantedPix}»");
            if ((e.VideoProfile ?? ProfileName(profile)) is { } wantedProfile) Expect(prof == wantedProfile, $"perfil «{prof}», se esperaba «{wantedProfile}»");
            Expect(width == e.Size.Width && height == e.Size.Height, $"{width}x{height}, se esperaba {e.Size.Width}x{e.Size.Height}");
            // Con muy pocos cuadros (1 fps en 3 s) cuadros ÷ duración no mide la cadencia: la duración del último pesa demasiado.
            Expect(frames < 10 || Math.Abs(measured - fps) / fps < 0.02, $"cadencia {measured:0.###} fps, se esperaba {fps:0.###}");
            Expect(vDur >= e.Recorded - 1.5, $"el vídeo dura {vDur:0.00} s de {e.Recorded:0.00} s grabados");
            string wantedMarks = parity switch { "tff" => "1,1", "bff" => "1,0", _ => "0," };
            Expect(marks.Count > 0 && marks.All(f => f.StartsWith(wantedMarks, StringComparison.Ordinal)),
                $"cuadros marcados {string.Join(" ", marks)}, se esperaba «{wantedMarks}» ({parity ?? "progresivo"})");
            // En una imagen diminuta idet no decide (160×90: todo «indeterminado»), y sin campos de por medio no hay nada que mirar.
            if (height >= 240 && (parity is not null || e.SourceScan is not ScanType.Progressive))
            {
                Expect(luma.Is(parity), $"idet en la imagen: {luma} (se esperaba {parity ?? "progresivo"})");
                Expect(chroma.Is(parity), $"idet en el croma: {chroma} (se esperaba {parity ?? "progresivo"})");
            }
        }

        if (audio is not { } a) Expect(false, "el archivo no tiene audio");
        else
        {
            double decoded = await DecodedAudioSecondsAsync(locator.FfmpegPath, file);
            string codec = Text(a, "codec_name");
            int rate = (int)Number(a, "sample_rate"), channels = (int)Number(a, "channels");
            parts.Add($"a:{codec} {rate} Hz {channels} c {decoded:0.00} s");
            string wanted = AudioCodecName(profile);
            int wantedRate = profile.AudioCodec is AudioCodec.Opus ? 48_000 : profile.AudioSampleRate; // Opus decodifica siempre a 48 kHz
            Expect(codec == wanted, $"audio «{codec}», se esperaba «{wanted}»");
            Expect(rate == wantedRate, $"audio a {rate} Hz, se esperaban {wantedRate}");
            Expect(channels == e.AudioChannels, $"{channels} canales de audio, se esperaban {e.AudioChannels}");
            double reference = double.IsNaN(vDur) ? e.Recorded : vDur;
            // A cadencias muy bajas el vídeo acaba con la duración de su último cuadro (a 1 fps, hasta 2 s más que el audio).
            double slack = double.IsNaN(vDur) ? 0.5 : Math.Max(0.5, 2.0 / e.Rate.Value);
            Expect(double.IsNaN(vDur) ? decoded >= e.Recorded - 1.5 : Math.Abs(decoded - reference) < slack,
                $"{decoded:0.00} s de audio decodificado para {reference:0.00} s");
        }
        parts.Add($"{new FileInfo(file).Length / 1e6:0.0} MB {Path.GetExtension(file)}");
        return (string.Join(" · ", parts), problems);
    }

    /// <summary>Lo que ffprobe dice del vídeo de cada códec del producto.</summary>
    internal static string CodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.H265x265 or VideoCodec.HevcNvenc or VideoCodec.HevcQsv => "hevc",
        VideoCodec.Av1Nvenc => "av1",
        VideoCodec.ProRes => "prores",
        VideoCodec.DnxHd or VideoCodec.DnxHr => "dnxhd",
        VideoCodec.Mpeg2Video => "mpeg2video",
        _ => "h264",
    };

    /// <summary>El formato de píxel decodificado que corresponde al perfil: el de su perfil en ProRes y DNx, el explícito en el resto
    /// (NVENC y QuickSync, con Auto, codifican nv12: 4:2:0 de 8 bits).</summary>
    internal static string PixelFormatName(RecordingProfile p)
    {
        string? explicitFormat = FfmpegCodecMap.PixelFormatArg(p.PixelFormat);
        if (p.VideoCodec is VideoCodec.ProRes)
            return p.EncoderProfile is EncoderProfile.ProRes4444 or EncoderProfile.ProRes4444Xq
                   || (p.EncoderProfile is not (EncoderProfile.ProResProxy or EncoderProfile.ProResLt or EncoderProfile.ProResStandard or EncoderProfile.ProResHq)
                       && p.PixelFormat is PixelFormat.Yuv444p10le) ? "yuv444p10le" : "yuv422p10le";
        // DNx: el explícito manda (en 4:2:2, que es lo que hay); si no, el del perfil (como FfmpegArgumentBuilder.DnxhdPixelFormat).
        if (p.VideoCodec is VideoCodec.DnxHd or VideoCodec.DnxHr)
            return explicitFormat switch
            {
                "yuv444p10le" => "yuv444p10le",
                "yuv422p10le" or "yuv420p10le" => "yuv422p10le",
                "yuv422p" or "yuv420p" => "yuv422p",
                _ => p.EncoderProfile switch { EncoderProfile.DnxHr444 => "yuv444p10le", EncoderProfile.DnxHrHqx => "yuv422p10le", _ => "yuv422p" },
            };
        return explicitFormat ?? "yuv420p";
    }

    /// <summary>El decodificador de ProRes 4444 saca 12 bits: es el mismo 4:4:4 que se codificó en 10.</summary>
    private static bool SamePixelFormat(string decoded, string wanted, RecordingProfile p) =>
        decoded == wanted || (p.VideoCodec is VideoCodec.ProRes && wanted == "yuv444p10le" && decoded == "yuv444p12le");

    /// <summary>El perfil que ffprobe nombra en los códecs intra, o null si no se comprueba.</summary>
    internal static string? ProfileName(RecordingProfile p) => p.VideoCodec switch
    {
        VideoCodec.ProRes => p.EncoderProfile switch
        {
            EncoderProfile.ProResProxy => "Proxy", EncoderProfile.ProResLt => "LT", EncoderProfile.ProResStandard => "Standard",
            EncoderProfile.ProRes4444 => "4444", EncoderProfile.ProRes4444Xq => "XQ", EncoderProfile.ProResHq => "HQ",
            _ => p.PixelFormat is PixelFormat.Yuv444p10le ? "4444" : "HQ",
        },
        _ => null,
    };

    internal static string AudioCodecName(RecordingProfile p) => FfmpegCodecMap.EffectiveAudioEncoder(p.AudioCodec, p.Container) switch
    {
        "libfdk_aac" => "aac",
        "libopus" => "opus",
        "libmp3lame" => "mp3",
        var e => e,
    };

    /// <summary>Cuadros que idet, cuadro a cuadro, ve TFF, BFF, progresivos o sin decidir.</summary>
    internal readonly record struct Idet(int Tff, int Bff, int Progressive, int Undetermined)
    {
        private int Decided => Tff + Bff + Progressive;

        /// <summary>¿Peina con ese orden de campos («tff», «bff») o, con null, no peina?</summary>
        public bool Is(string? parity) => Decided > 0 && parity switch
        {
            "tff" => Tff > 0.9 * Decided,
            "bff" => Bff > 0.9 * Decided,
            _ => Tff + Bff < 0.1 * Decided,
        };

        public override string ToString() => $"T{Tff}/B{Bff}/P{Progressive}/?{Undetermined}";
    }

    /// <summary>La detección cuadro a cuadro de idet en la imagen (Y) y en el croma (U), en una sola decodificación: sin la
    /// multicuadro, que se fía de la historia y con cuadros saltados (29.97 → 25) ve campos donde no los hay. Antes, mpdecimate quita
    /// los cuadros repetidos: idet compara cada cuadro con el anterior y el siguiente, y con uno igual al lado (29.97 → 50 repite)
    /// también los ve.</summary>
    internal static async Task<(Idet Luma, Idet Chroma)> IdetAsync(string ffmpeg, string file)
    {
        var (_, log) = await RunWithLogAsync(ffmpeg, "-hide_banner", "-nostats", "-i", file, "-filter_complex",
            "[0:v:0]extractplanes=y+u[y][u];[y]mpdecimate,idet[yo];[u]mpdecimate,idet[uo]",
            "-map", "[yo]", "-f", "null", "-", "-map", "[uo]", "-f", "null", "-");
        // Cada idet firma con su nombre (Parsed_idet_2 la imagen, Parsed_idet_4 el croma). FFmpeg 8.0 monta el grafo dos veces y el
        // primero, que no ve ningún cuadro, también escribe su línea (a cero), y no siempre antes: vale la que más cuadros cuenta.
        Idet Of(string name)
        {
            static int G(Match m, int i) => int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
            var m = Regex.Matches(log, $@"\[{name} @ [^\]]+\] Single frame detection: TFF:\s*(\d+) BFF:\s*(\d+) Progressive:\s*(\d+) Undetermined:\s*(\d+)")
                .MaxBy(x => G(x, 1) + G(x, 2) + G(x, 3) + G(x, 4));
            return m is null ? new Idet(0, 0, 0, 0) : new Idet(G(m, 1), G(m, 2), G(m, 3), G(m, 4));
        }
        return (Of("Parsed_idet_2"), Of("Parsed_idet_4"));
    }

    /// <summary>Segundos de audio que saca el decodificador (ver ContainerCodecTests: vale el último «n_samples»).</summary>
    internal static async Task<double> DecodedAudioSecondsAsync(string ffmpeg, string file)
    {
        var (_, log) = await RunWithLogAsync(ffmpeg, "-hide_banner", "-nostats", "-i", file, "-map", "0:a:0",
            "-af", "aformat=sample_rates=48000:channel_layouts=stereo,volumedetect", "-f", "null", "-");
        // Como con idet: el grafo que FFmpeg 8.0 monta primero también escribe su «n_samples: 0»; vale el mayor.
        long samples = Regex.Matches(log, @"n_samples: (\d+)").Select(m => long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .DefaultIfEmpty(0).Max();
        return samples / 2.0 / 48_000;
    }

    /// <summary>Segundos de CPU de los FFmpeg arrancados desde <paramref name="since"/> (el proceso del canal y lo que lance el motor).</summary>
    internal static double FfmpegCpuSeconds(DateTime since)
    {
        double total = 0;
        foreach (var p in Process.GetProcessesByName("ffmpeg"))
        {
            try { if (p.StartTime >= since) total += p.TotalProcessorTime.TotalSeconds; }
            catch { /* ya terminó */ }
            finally { p.Dispose(); }
        }
        return total;
    }

    internal static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "";

    internal static double Number(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;

    internal static string FirstLine(string s) => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    internal static async Task<string> RunAsync(string exe, params string[] args) => (await RunWithLogAsync(exe, args)).Output;

    internal static async Task<(string Output, string Log)> RunWithLogAsync(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (await stdout, await stderr);
    }
}

/// <summary>
/// Fuente en vivo (lavfi con <c>-re</c>) del tamaño, la cadencia y el barrido pedidos, con un tono estéreo: un entrelazado lleva
/// movimiento de verdad entre sus dos campos (el doble de cuadros, entretejidos con el orden pedido), y con un formato de píxel sale
/// en ese formato (uyvy422, el de la DeckLink).
/// </summary>
internal sealed class LiveFeed : ICaptureSource
{
    private readonly string? _pixelFormat;

    public LiveFeed(int width, int height, FrameRate rate, ScanType scan, string? pixelFormat = null)
    {
        Width = width;
        Height = height;
        Rate = rate;
        Scan = scan;
        _pixelFormat = pixelFormat;
    }

    public int Width { get; }
    public int Height { get; }
    public FrameRate Rate { get; }
    public ScanType Scan { get; }
    public Resolution Size => new(Width, Height);

    public string Label => string.Create(CultureInfo.InvariantCulture,
        $"{Width}x{Height}{Scan switch { ScanType.InterlacedTff => "i", ScanType.InterlacedBff => "i(bff)", _ => "p" }}{Rate.Value:0.##}" +
        $"{(_pixelFormat is null ? "" : " " + _pixelFormat)}");

    public InputSource Definition { get; } = new() { Name = "lavfi en tiempo real", Type = InputType.File };
    public SignalInfo CurrentSignal { get; private set; } = SignalInfo.None;
    public event EventHandler<SignalInfo>? SignalChanged;
    public bool DeliversInRealTime => true;

    public Task OpenAsync(CancellationToken ct = default)
    {
        CurrentSignal = new SignalInfo(SignalState.Locked, new Resolution(Width, Height), Rate, AudioLayout.Stereo,
            HasAudio: true, Timecode: null, Bitrate: null, Scan: Scan);
        SignalChanged?.Invoke(this, CurrentSignal);
        return Task.CompletedTask;
    }

    public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;

    public IReadOnlyList<string> BuildInputArguments()
    {
        // El entrelazado se teje en 4:2:2, que tiene una línea de croma por línea de imagen: así el croma de cada campo es solo
        // suyo, como el de una tarjeta (tejido en 4:2:0 y subido después, los campos ya llegaban mezclados en el croma).
        string video = Scan switch
        {
            ScanType.InterlacedTff => $"testsrc2=size={Width}x{Height}:rate={Rate.Numerator * 2}/{Rate.Denominator},format=yuv422p,tinterlace=mode=interleave_top,setfield=tff",
            ScanType.InterlacedBff => $"testsrc2=size={Width}x{Height}:rate={Rate.Numerator * 2}/{Rate.Denominator},format=yuv422p,tinterlace=mode=interleave_bottom,setfield=bff",
            _ => $"testsrc2=size={Width}x{Height}:rate={Rate.Numerator}/{Rate.Denominator}",
        };
        if (_pixelFormat is not null) video += $",format={_pixelFormat}";
        return new[]
        {
            "-re", "-f", "lavfi", "-i",
            string.Create(CultureInfo.InvariantCulture, $"{video}[out0];sine=frequency=1000:sample_rate=48000,aformat=channel_layouts=stereo[out1]"),
        };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
