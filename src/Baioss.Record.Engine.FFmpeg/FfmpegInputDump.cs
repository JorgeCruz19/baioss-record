using System;
using Baioss.Record.Application.Capture;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>Lo que FFmpeg contó de UNA entrada al abrirla: el valor del <c>-i</c> («Input #0, mpegts, from '…'»), el
/// vídeo si lo describió con tamaño y tasa, y si trae alguna pista de audio.</summary>
public sealed record FfmpegInputDescription(string Url, DetectedVideoMode? Video, bool HasAudio);

/// <summary>
/// Lee, línea a línea, el volcado que FFmpeg escribe en stderr al abrir una entrada:
/// <code>
/// Input #0, mpegts, from 'srt://0.0.0.0:9000':
///   Stream #0:0[0x100]: Video: h264 (Constrained Baseline), yuv420p(progressive), 640x360 [SAR 1:1 DAR 16:9], 25 fps, …
///   Stream #0:1[0x101]: Audio: aac (LC), 48000 Hz, stereo, fltp, 96 kb/s
/// Stream mapping:
/// </code>
/// y devuelve la descripción cuando el volcado TERMINA («Stream mapping:» u «Output #», que es cuando ya se han listado
/// todas las pistas); solo la primera vez por apertura: al relanzar el proceso hay que llamar a <see cref="Reset"/>.
/// Con esto una fuente de red sabe que el emisor ya está conectado, el formato real del vídeo y si hay audio (el
/// pipeline necesita saberlo ANTES de construirse: un mapeo de audio sin pista abortaría FFmpeg).
/// </summary>
public sealed class FfmpegInputDump
{
    private string? _url;
    private DetectedVideoMode? _video;
    private bool _hasAudio;
    private bool _done;

    /// <summary>Procesa una línea de stderr; devuelve la descripción de la entrada cuando su volcado se completa.</summary>
    public FfmpegInputDescription? Feed(string line)
    {
        if (_done) return null;
        if (line.StartsWith("Input #", StringComparison.Ordinal))
        {
            var url = FfmpegLogLines.InputUrl(line);
            if (url is not null) { _url = url; _video = null; _hasAudio = false; }
            return null;
        }
        if (_url is null) return null;
        if (line.StartsWith("Stream mapping:", StringComparison.Ordinal) || line.StartsWith("Output #", StringComparison.Ordinal))
        {
            _done = true;
            return new FfmpegInputDescription(_url, _video, _hasAudio);
        }
        if (line.Contains(": Video: ", StringComparison.Ordinal)) { _video ??= FfmpegLogLines.TryParseVideoStream(line); return null; }
        if (line.Contains(": Audio: ", StringComparison.Ordinal)) { _hasAudio = true; return null; }
        return null;
    }

    /// <summary>Olvida el volcado anterior (el proceso se relanzó y abrirá la entrada otra vez).</summary>
    public void Reset()
    {
        _url = null; _video = null; _hasAudio = false; _done = false;
    }
}
