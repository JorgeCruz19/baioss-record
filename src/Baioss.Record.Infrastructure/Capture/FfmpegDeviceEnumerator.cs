using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Application.Abstractions;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Infrastructure.Capture;

/// <summary>
/// Enumera dispositivos de captura interrogando a FFmpeg: DirectShow (cámaras y capturadoras USB)
/// con <c>-f dshow -list_devices true</c> y Blackmagic DeckLink con <c>-sources decklink</c>
/// (o el clásico <c>-f decklink -list_devices 1</c>). FFmpeg imprime los listados por <em>stderr</em>;
/// se parsean a <see cref="InputSource"/> con Id determinista (mismo dispositivo → misma fila al reasignar).
/// </summary>
public sealed partial class FfmpegDeviceEnumerator(IFfmpegLocator locator) : IDeviceEnumerator
{
    public async Task<IReadOnlyList<InputSource>> DiscoverAsync(InputType type, CancellationToken ct = default)
        => type switch
        {
            InputType.DirectShow  => await DiscoverDshowVideoAsync(ct).ConfigureAwait(false),
            InputType.DecklinkSdi => await DiscoverDecklinkAsync(ct).ConfigureAwait(false),
            InputType.Ndi         => await DiscoverNdiAsync(ct).ConfigureAwait(false),
            _ => Array.Empty<InputSource>(),
        };

    public async Task<IReadOnlyList<string>> DiscoverAudioDevicesAsync(InputType type, CancellationToken ct = default)
    {
        if (type is not InputType.DirectShow) return Array.Empty<string>();
        var output = await RunAsync(new[] { "-hide_banner", "-f", "dshow", "-list_devices", "true", "-i", "dummy" }, ct);
        return ParseDshowAudio(output);
    }

    public async Task<IReadOnlyList<DeviceFormat>> DiscoverFormatsAsync(InputType type, string deviceId, CancellationToken ct = default)
    {
        if (type is not InputType.DecklinkSdi || string.IsNullOrWhiteSpace(deviceId)) return Array.Empty<DeviceFormat>();
        var output = await RunAsync(new[] { "-hide_banner", "-f", "decklink", "-list_formats", "1", "-i", deviceId }, ct);
        return ParseDecklinkFormats(output);
    }

    private async Task<IReadOnlyList<InputSource>> DiscoverDshowVideoAsync(CancellationToken ct)
    {
        var output = await RunAsync(new[] { "-hide_banner", "-f", "dshow", "-list_devices", "true", "-i", "dummy" }, ct);
        return ParseDshowVideo(output);
    }

    private static async Task<IReadOnlyList<InputSource>> DiscoverNdiAsync(CancellationToken ct)
    {
        // Descubrimiento por el SDK NDI (NDILibDotNetCoreBase), NO por FFmpeg: el binario de FFmpeg no trae
        // libndi_newtek. Devuelve vacío si el runtime NDI no está instalado (NdiRuntime degrada solo).
        var names = await Task.Run(() => NdiRuntime.DiscoverSources(), ct).ConfigureAwait(false);
        return Dedupe(names.Select(n => MakeSource(InputType.Ndi, n)));
    }

    private async Task<IReadOnlyList<InputSource>> DiscoverDecklinkAsync(CancellationToken ct)
    {
        // '-sources decklink' es el comando moderno; si el build no lo soporta o no devuelve nombres
        // entre comillas simples, se recurre al clásico '-list_devices' (su salida por stderr es estable).
        var output = await RunAsync(new[] { "-hide_banner", "-sources", "decklink" }, ct);
        if (!output.Contains('\''))
            output = await RunAsync(new[] { "-hide_banner", "-f", "decklink", "-list_devices", "1", "-i", "dummy" }, ct);
        return ParseDecklink(output);
    }

    // --- Parseo de la salida de FFmpeg (puro y testeable, sin lanzar procesos) ---

    /// <summary>Extrae los dispositivos de vídeo DirectShow de la salida de <c>-list_devices</c>.</summary>
    public static IReadOnlyList<InputSource> ParseDshowVideo(string output)
    {
        var list = new List<InputSource>();
        foreach (Match m in DshowDeviceRegex().Matches(output))
            if (m.Groups["kind"].Value.Equals("video", StringComparison.Ordinal))
                list.Add(MakeSource(InputType.DirectShow, m.Groups["name"].Value));
        return Dedupe(list);
    }

    /// <summary>Extrae los dispositivos de audio DirectShow de la salida de <c>-list_devices</c>.</summary>
    public static IReadOnlyList<string> ParseDshowAudio(string output)
    {
        var list = new List<string>();
        foreach (Match m in DshowDeviceRegex().Matches(output))
            if (m.Groups["kind"].Value.Equals("audio", StringComparison.Ordinal))
                list.Add(m.Groups["name"].Value);
        return list.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Extrae los nombres de dispositivos DeckLink (entre comillas simples) de la salida de FFmpeg.</summary>
    public static IReadOnlyList<InputSource> ParseDecklink(string output)
    {
        var list = new List<InputSource>();
        foreach (Match m in DecklinkDeviceRegex().Matches(output))
            list.Add(MakeSource(InputType.DecklinkSdi, m.Groups["name"].Value));
        return Dedupe(list);
    }

    /// <summary>
    /// Extrae los modos/formatos de la salida de <c>-list_formats</c>: cada línea de formato lleva el
    /// código (<c>-format_code</c>) seguido de una descripción que empieza por la resolución (WxH).
    /// </summary>
    public static IReadOnlyList<DeviceFormat> ParseDecklinkFormats(string output)
    {
        var list = new List<DeviceFormat>();
        foreach (Match m in DecklinkFormatRegex().Matches(output))
            list.Add(BuildFormat(m.Groups["code"].Value, m.Groups["desc"].Value.Trim()));
        return list;
    }

    /// <summary>
    /// Convierte la descripción cruda ("1920x1080 at 30000/1001 fps (interlaced, upper field first)") en
    /// un <see cref="DeviceFormat"/> con etiqueta LEGIBLE ("1920×1080 · 59.94i") y resolución/tasa
    /// parseadas (para poblar la señal del canal). La tasa guardada es la CODIFICADA (29.97 para 1080i),
    /// correcta para timecode/encoder; la etiqueta usa la de campos en entrelazado (convención broadcast).
    /// </summary>
    private static DeviceFormat BuildFormat(string code, string raw)
    {
        Resolution? res = null;
        var rm = ResolutionRegex().Match(raw);
        if (rm.Success)
            res = new Resolution(int.Parse(rm.Groups["w"].Value, CultureInfo.InvariantCulture),
                                 int.Parse(rm.Groups["h"].Value, CultureInfo.InvariantCulture));

        FrameRate? fr = null;
        var fm = RateRegex().Match(raw);
        if (fm.Success)
        {
            int num = int.Parse(fm.Groups["num"].Value, CultureInfo.InvariantCulture);
            int den = fm.Groups["den"].Success && fm.Groups["den"].Value.Length > 0
                ? int.Parse(fm.Groups["den"].Value, CultureInfo.InvariantCulture) : 1;
            if (num > 0 && den > 0) fr = new FrameRate(num, den);
        }
        bool interlaced = raw.Contains("interlaced", StringComparison.OrdinalIgnoreCase);

        return new DeviceFormat(code, FriendlyLabel(res, fr, interlaced) ?? raw)
        {
            Resolution = res, FrameRate = fr, Interlaced = interlaced,
        };
    }

    private static string? FriendlyLabel(Resolution? res, FrameRate? fr, bool interlaced)
    {
        if (res is not { } r || fr is not { } f) return null;
        double display = interlaced ? f.Value * 2 : f.Value; // entrelazado → tasa de campos (59.94i, 50i)
        char scan = interlaced ? 'i' : 'p';
        return $"{r.Width}×{r.Height} · {FormatRate(display)}{scan}";
    }

    /// <summary>"59.94", "29.97", "23.98" o entero exacto ("25", "50", "60").</summary>
    private static string FormatRate(double v)
    {
        double rounded = Math.Round(v);
        return Math.Abs(v - rounded) < 0.02
            ? ((int)rounded).ToString(CultureInfo.InvariantCulture)
            : v.ToString("0.00", CultureInfo.InvariantCulture);
    }

    private static InputSource MakeSource(InputType type, string name) => new()
    {
        Id = StableGuid($"input:{type}:{name}"),
        Name = name, Type = type, Uri = name,
    };

    private static IReadOnlyList<InputSource> Dedupe(IEnumerable<InputSource> items)
        => items.GroupBy(i => i.Id).Select(g => g.First()).ToList();

    /// <summary>Tope por consulta: si FFmpeg se cuelga (p. ej. <c>-list_formats</c> abriendo una DeckLink sin
    /// señal o en uso), se mata y se devuelve lo capturado, en vez de colgar la búsqueda para siempre.</summary>
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(12);

    private async Task<string> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = locator.FfmpegPath,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
            // FFmpeg emite los nombres de dispositivo en UTF-8. Sin fijarlo, .NET los lee con la codepage de
            // consola (CP1252/850 en Windows ES) y los acentos se corrompen: «Varios micrófonos» →
            // «Varios micrÃ³fonos». Ese nombre corrupto se persiste y se le devuelve a dshow, que NO lo
            // encuentra → «Could not find audio device» / I/O error (-5) al abrir. Leerlo como UTF-8 lo evita.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar FFmpeg.");

        // Lee AMBOS flujos EN PARALELO. Antes se leía stdout hasta EOF y LUEGO stderr (en serie): FFmpeg emite
        // los listados por stderr y, con `-list_formats` de una DeckLink (decenas de modos SDI), la salida de
        // stderr llenaba el buffer de la tubería → FFmpeg se bloqueaba al escribir, stdout nunca cerraba y la
        // lectura nunca retornaba → DEADLOCK que colgaba la búsqueda sin ningún error. Leer concurrentemente lo
        // evita (igual que FfmpegLocator.RunAsync). Sin token en las lecturas: al matar el proceso las tuberías
        // cierran y las lecturas terminan con lo capturado.
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();

        // Timeout duro: si FFmpeg se cuelga abriendo el dispositivo, se mata para no dejarlo reteniendo la
        // tarjeta ni colgar la detección. La cancelación real del usuario (ct) se propaga.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(QueryTimeout);
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* ya terminó */ }
            if (ct.IsCancellationRequested) throw; // cancelación del usuario, no timeout
        }

        // Tras la salida normal (o el Kill, que cierra las tuberías) las lecturas terminan; un pequeño margen
        // evita un cuelgue residual si el SO tarda en cerrar los handles.
        try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch { /* lo capturado basta (en el peor caso, lista vacía → solo «Automático») */ }
        return SafeResult(stdoutTask) + SafeResult(stderrTask); // los listados de dispositivos salen por stderr
    }

    private static string SafeResult(Task<string> t) => t.IsCompletedSuccessfully ? t.Result : "";

    private static Guid StableGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    // dshow:  "USB2.0 HD UVC WebCam" (video)   /   "Micrófono (Realtek)" (audio)
    [GeneratedRegex("\"(?<name>[^\"]+)\"\\s+\\((?<kind>video|audio)\\)")]
    private static partial Regex DshowDeviceRegex();

    // decklink: líneas con el nombre entre comillas simples, p. ej.  'DeckLink Mini Recorder'
    [GeneratedRegex("'(?<name>[^']+)'")]
    private static partial Regex DecklinkDeviceRegex();

    // decklink -list_formats:  "[... @ ...]    Hp50   1920x1080 at 50/1 fps"  → code=Hp50, desc=1920x1080…
    // Se ancla en la forma de la descripción (WxH … fps), no en el corchete del prefijo de log: así
    // tolera variaciones del build (con/sin prefijo "[decklink @ 0x…]", tasas tipo 60000/1001 o 59.94).
    [GeneratedRegex(@"(?<code>[A-Za-z0-9]+)\s+(?<desc>\d+x\d+\s+(?:at\s+)?[\d/.]+\s*fps[^\r\n]*)")]
    private static partial Regex DecklinkFormatRegex();

    // De la descripción: resolución "1920x1080" y tasa "at 30000/1001 fps" (o "at 25/1 fps").
    [GeneratedRegex(@"(?<w>\d+)x(?<h>\d+)")]
    private static partial Regex ResolutionRegex();

    [GeneratedRegex(@"at\s+(?<num>\d+)(?:/(?<den>\d+))?\s*fps")]
    private static partial Regex RateRegex();
}
