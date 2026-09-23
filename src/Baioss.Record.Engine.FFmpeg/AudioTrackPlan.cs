using System.Collections.Generic;
using System.Linq;
using Baioss.Record.Application.Capture;
using Baioss.Record.Application.Localization;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;

namespace Baioss.Record.Engine.FFmpeg;

/// <summary>
/// Lo que el archivo va a llevar de audio con una fuente multicanal: qué PARES de la entrada acaban en él, cuántas pistas
/// y cómo contarlo en palabras («8 pistas», «1 pista 5.1», «1 pista · 16 canales»). Sale de las MISMAS rutas que arman la
/// grabación (<see cref="FfmpegArgumentBuilder.AudioRoutes"/>), así que la aplicación y el panel web cuentan lo que FFmpeg
/// hará de verdad y no lo que dice la entrada: la entrada puede elegir «Todos los pares» y el preset quedarse con menos
/// (un 5.1 con los seis primeros canales, un mono, un MP3 con el primer par). Antes los medidores marcaban como grabados
/// todos los pares elegidos aunque al archivo fuera solo uno, y el operador lo descubría al abrirlo.
/// </summary>
/// <param name="Pairs">Pares (1-based) que acaban en el archivo, en orden.</param>
/// <param name="Tracks">Número de flujos de audio del archivo.</param>
/// <param name="Label">Las pistas en palabras, en el idioma de la aplicación.</param>
public sealed record AudioTrackPlan(IReadOnlyList<int> Pairs, int Tracks, string Label)
{
    /// <summary>El plan para esa fuente y ese perfil, o null con una fuente estéreo (no hay nada que elegir ni que contar).</summary>
    public static AudioTrackPlan? For(ICaptureSource source, RecordingProfile profile)
    {
        var routes = FfmpegArgumentBuilder.AudioRoutes(source, profile);
        if (routes is null || routes.Count == 0) return null;

        var pairs = routes.SelectMany(r => r.Channels).Select(c => c / 2 + 1).Distinct().OrderBy(p => p).ToArray();
        var only = routes[0];
        string label = routes.Count > 1 ? Localizer.F("Audio_Tracks_Many", routes.Count)
            : only.Layout switch
            {
                AudioLayout.Surround51 => Localizer.F("Audio_Tracks_Surround", "5.1"),
                AudioLayout.Surround71 => Localizer.F("Audio_Tracks_Surround", "7.1"),
                AudioLayout.Mono => Localizer.T("Audio_Tracks_Mono"),
                AudioLayout.Stereo => Localizer.T("Audio_Tracks_OneStereo"),
                _ => Localizer.F("Audio_Tracks_Multichannel", only.Channels.Count), // pista PCM sin nombre («16c»)
            };
        return new AudioTrackPlan(pairs, routes.Count, label);
    }
}
