using System.Globalization;
using Baioss.Record.Application.Capture;
using Baioss.Record.Domain;
using Baioss.Record.Domain.Entities;
using Baioss.Record.Domain.ValueObjects;
using Baioss.Record.Engine.FFmpeg;
using Xunit;
using Xunit.Abstractions;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// El desentrelazado de la rama de grabación (FfmpegArgumentBuilder.DeinterlaceFilter) con el FFmpeg incluido, cuadro a
/// cuadro (framemd5, sin codificar), sobre un 1080i29.97 TFF sintético en 4:2:2 como el de la DeckLink. Una tele mete material
/// progresivo en el 1080i (telenovelas, series, anuncios: cada cuadro, los dos campos de un instante): la cadena de antes (bwdif a
/// todo) rehacía cada cuadro desde un solo campo y, por campo, cada repetición desde uno distinto, y las texturas finas temblaban de
/// una a otra. Ahora sale intacto (por campo, dos veces); lo entrelazado de verdad, bit a bit como antes.
/// </summary>
public sealed class DeinterlaceCadenceTests
{
    private readonly ITestOutputHelper _out;
    public DeinterlaceCadenceTests(ITestOutputHelper output) => _out = output;

    /// <summary>Material progresivo metido en el 1080i29.97: un cuadro por instante.</summary>
    private static string Progressive(int seconds) =>
        string.Create(CultureInfo.InvariantCulture, $"testsrc2=size=1920x1080:rate=30000/1001:d={seconds},format=yuv422p");

    /// <summary>Entrelazado de verdad: 59.94 tejido de dos en dos, cada campo de un instante (en 4:2:2 desde el principio: tejer
    /// cuadros 4:2:0 mezclaría en cada campo el croma del otro).</summary>
    private static string Interlaced(int seconds) => string.Create(CultureInfo.InvariantCulture,
        $"testsrc2=size=1920x1080:rate=60000/1001:d={seconds},format=yuv422p,tinterlace=mode=interleave_top");

    /// <summary>Una señal 1080i29.97 TFF como la de la DeckLink: lo que el motor mira para decidir cómo desentrelazar.</summary>
    private sealed class InterlacedSignal : ICaptureSource
    {
        public InputSource Definition { get; } = new() { Name = "1080i", Type = InputType.File, Uri = "lavfi" };
        public SignalInfo CurrentSignal { get; } = new(SignalState.Locked, new Resolution(1920, 1080), FrameRate.P2997, AudioLayout.Stereo,
            HasAudio: true, Timecode: null, Bitrate: null, FormatLabel: "1920×1080 · 59.94i", Scan: ScanType.InterlacedTff);
        public event EventHandler<SignalInfo>? SignalChanged { add { } remove { } }
        public bool DeliversInRealTime => true;
        public IReadOnlyList<string> BuildInputArguments() => new[] { "-f", "lavfi", "-i", "testsrc2" };
        public Task OpenAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task CloseAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Lo que la rama de grabación hace antes de escalar, tal como lo escribe el motor con esa señal y un preset progresivo
    /// a 59.94 (un cuadro por campo) o a 29.97 (uno por cuadro).</summary>
    private static string Chain(bool perField)
    {
        var profile = new RecordingProfile { Name = "p", VideoCodec = VideoCodec.H264Nvenc, OutputFrameRate = perField ? FrameRate.P5994 : FrameRate.P2997 };
        var args = new FfmpegArgumentBuilder().From(new InterlacedSignal()).Using(profile).ForChannel("T").ToDirectory(Path.GetTempPath())
            .WithPreviewSink("tcp://127.0.0.1:9").BuildLive(recording: true, 640, 360).ToList();
        string graph = args[args.IndexOf("-filter_complex") + 1];
        int start = graph.IndexOf(";[vrec]", StringComparison.Ordinal) + ";[vrec]".Length;
        return graph[start..graph.IndexOf(",scale", start, StringComparison.Ordinal)];
    }

    /// <summary>La de antes del 2026-10-22: bwdif a todos los cuadros.</summary>
    private static string OldChain(bool perField) => $"bwdif=mode={(perField ? "send_field" : "send_frame")}:parity=tff:deint=all";

    /// <summary>La huella de cada cuadro que sale del grafo.</summary>
    private static async Task<List<string>> HashesAsync(string graph)
    {
        var (output, log) = await RecordedFileCheck.RunWithLogAsync(new FfmpegLocator(TestAssets.FfmpegDir!).FfmpegPath,
            "-hide_banner", "-nostats", "-v", "error", "-filter_complex", graph + "[o]", "-map", "[o]", "-f", "framemd5", "-");
        var hashes = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.StartsWith('#')).Select(l => l[(l.LastIndexOf(',') + 1)..].Trim()).ToList();
        Assert.True(hashes.Count > 0, $"FFmpeg no sacó ningún cuadro: {log}");
        return hashes;
    }

    private static int Repeats(IReadOnlyList<string> hashes) => Enumerable.Range(1, hashes.Count - 1).Count(i => hashes[i] == hashes[i - 1]);

    [SkippableTheory]
    [InlineData(true)]  // 1080p59.94
    [InlineData(false)] // 1080p29.97
    public async Task ProgressiveMaterialInTheInterlacedSignal_IsRecordedIntact_InsteadOfRebuiltFromOneField(bool perField)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var source = await HashesAsync(Progressive(2));
        var now = await HashesAsync($"{Progressive(2)},{Chain(perField)}");
        var before = await HashesAsync($"{Progressive(2)},{OldChain(perField)}");
        var intact = source.ToHashSet();
        _out.WriteLine($"{source.Count} cuadros de la fuente → ahora {now.Count} ({now.Count(intact.Contains)} intactos, {Repeats(now)} repetidos) · " +
                       $"antes {before.Count} ({before.Count(intact.Contains)} intactos, {Repeats(before)} repetidos)");

        // Cada cuadro de la fuente tal cual; por campo, dos veces seguidas (su movimiento es a 29.97).
        Assert.Equal(source.SelectMany(h => Enumerable.Repeat(h, perField ? 2 : 1)), now);
        // Antes ninguno salía intacto, y por campo ninguno se repetía: cada campo rehecho por su lado.
        Assert.Equal(now.Count, before.Count);
        Assert.DoesNotContain(before, intact.Contains);
        Assert.Equal(0, Repeats(before));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATrulyInterlacedSignal_IsDeinterlacedBitForBitAsBefore(bool perField)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var now = await HashesAsync($"{Interlaced(2)},{Chain(perField)}");
        var before = await HashesAsync($"{Interlaced(2)},{OldChain(perField)}");
        _out.WriteLine($"ahora {now.Count} cuadros ({Repeats(now)} repetidos), antes {before.Count}; iguales {now.Zip(before).Count(p => p.First == p.Second)}");

        Assert.Equal(before, now);
        Assert.Equal(0, Repeats(now));
    }

    [SkippableFact]
    public async Task AtACutBetweenThem_IdetFollowsWithinThreeFrames()
    {
        // idet decide con sus cuatro últimos cuadros: tras un corte de progresivo a entrelazado, los tres primeros cuadros
        // entrelazados salen tejidos (una décima de segundo con peine en lo que se mueve) y, al revés, los tres primeros
        // progresivos se desentrelazan como antes (medido con este corte el 2026-10-22: tres en cada sentido).
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        string cut = $"{Progressive(2)}[a];{Interlaced(2)}[b];{Progressive(2)}[c];[a][b][c]concat=n=3:v=1:a=0";
        var now = await HashesAsync($"{cut},{Chain(perField: true)}");
        var before = await HashesAsync($"{cut},{OldChain(perField: true)}");
        Assert.Equal(360, now.Count); // 60 cuadros progresivos, 60 entrelazados y otros 60 progresivos: dos por cada uno
        Assert.Equal(360, before.Count);

        // Por cuadro de la fuente: P intacto (sus dos cuadros, iguales), D desentrelazado como antes, x otra cosa (medido: el
        // primero que bwdif vuelve a desentrelazar tras los intactos, con su primer campo distinto del de antes).
        string pattern = string.Concat(Enumerable.Range(0, 180).Select(k =>
            now[2 * k] == now[2 * k + 1] ? 'P' : now[2 * k] == before[2 * k] && now[2 * k + 1] == before[2 * k + 1] ? 'D' : 'x'));
        _out.WriteLine(pattern);

        Assert.Equal(new string('P', 60), pattern[..60]);
        string toInterlaced = pattern[60..120], toProgressive = pattern[120..];
        int follows = toInterlaced.IndexOf('D', StringComparison.Ordinal);
        Assert.InRange(follows, 0, 4);
        Assert.InRange(toInterlaced[..follows].Count(c => c == 'P'), 0, 3);
        Assert.Equal(new string('D', 60 - follows), toInterlaced[follows..]);
        int back = toProgressive.IndexOf('P', StringComparison.Ordinal);
        Assert.InRange(back, 0, 3);
        Assert.Equal(new string('P', 60 - back), toProgressive[back..]);
    }
}
