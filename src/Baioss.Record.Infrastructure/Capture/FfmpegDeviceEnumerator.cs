using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
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
            list.Add(new DeviceFormat(m.Groups["code"].Value, m.Groups["desc"].Value.Trim()));
        return list;
    }

    private static InputSource MakeSource(InputType type, string name) => new()
    {
        Id = StableGuid($"input:{type}:{name}"),
        Name = name, Type = type, Uri = name,
    };

    private static IReadOnlyList<InputSource> Dedupe(IEnumerable<InputSource> items)
        => items.GroupBy(i => i.Id).Select(g => g.First()).ToList();

    private async Task<string> RunAsync(string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = locator.FfmpegPath,
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("No se pudo iniciar FFmpeg.");
        var so = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        var se = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return so + se; // los listados de dispositivos salen por stderr
    }

    private static Guid StableGuid(string key) => new(MD5.HashData(Encoding.UTF8.GetBytes(key)));

    // dshow:  "USB2.0 HD UVC WebCam" (video)   /   "Micrófono (Realtek)" (audio)
    [GeneratedRegex("\"(?<name>[^\"]+)\"\\s+\\((?<kind>video|audio)\\)")]
    private static partial Regex DshowDeviceRegex();

    // decklink: líneas con el nombre entre comillas simples, p. ej.  'DeckLink Mini Recorder'
    [GeneratedRegex("'(?<name>[^']+)'")]
    private static partial Regex DecklinkDeviceRegex();

    // decklink -list_formats:  "[... @ ...]    Hp50   1920x1080 at 50/1 fps"  → code=Hp50, desc=1920x1080…
    [GeneratedRegex(@"\]\s+(?<code>\S+)\s+(?<desc>\d+x\d+[^\r\n]*)")]
    private static partial Regex DecklinkFormatRegex();
}
