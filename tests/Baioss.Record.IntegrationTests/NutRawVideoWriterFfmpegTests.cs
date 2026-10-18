using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Baioss.Record.Engine.FFmpeg;
using Xunit;

namespace Baioss.Record.IntegrationTests;

/// <summary>
/// El NUT con marcas que el receptor NDI escribe (<see cref="NutRawVideoWriter"/>), leído por el FFmpeg de <c>tools/</c>: cada
/// cuadro con su marca y la duración de un periodo (la tasa declarada viaja en la información del flujo), y la grabación, que
/// rellena los periodos sin cuadro con un filtro <c>fps</c>, pone cada cuadro en su periodo y acaba en el último. Con otro FFmpeg
/// que leyera el flujo de otra manera, esto es lo primero que caería.
/// </summary>
public sealed class NutRawVideoWriterFfmpegTests
{
    private const int W = 16, H = 8;
    private static readonly long[] Periods = { 0, 3, 4, 9, 10, 11, 18 };

    /// <summary>Un archivo NUT con un cuadro en cada periodo de <see cref="Periods"/>: luma distinta en cada uno.</summary>
    private static string WriteSample(string pixelFormat, int rateN, int rateD)
    {
        var writer = new NutRawVideoWriter(W, H, pixelFormat, rateN, rateD);
        string path = Path.Combine(Path.GetTempPath(), $"baioss-nut-{Guid.NewGuid():N}.nut");
        using var file = File.Create(path);
        file.Write(writer.Preamble);
        var header = new byte[NutRawVideoWriter.MaxFrameHeaderBytes];
        int bpp = pixelFormat == "uyvy422" ? 2 : 4;
        for (int i = 0; i < Periods.Length; i++)
        {
            var data = new byte[W * H * bpp];
            byte y = (byte)(30 + 25 * i);
            for (int p = 0; p < data.Length; p += bpp)
            {
                if (bpp == 2) { data[p] = 128; data[p + 1] = y; }           // U/V neutros, Y
                else { data[p] = data[p + 1] = data[p + 2] = y; data[p + 3] = 255; } // gris opaco
            }
            int n = writer.WriteFrameHeader(header, Periods[i], data.Length);
            file.Write(header, 0, n);
            file.Write(data);
        }
        return path;
    }

    [SkippableTheory]
    [InlineData("uyvy422", 30000, 1001)]
    [InlineData("bgra", 60000, 1000)]
    public async Task FfmpegReadsEachFrameWithItsPeriod_AndTheDeclaredRate(string pixelFormat, int rateN, int rateD)
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        string file = WriteSample(pixelFormat, rateN, rateD);
        try
        {
            var json = await RunAsync(locator.FfprobePath, false, "-v", "error", "-show_entries",
                "stream=codec_name,codec_tag_string,pix_fmt,width,height,r_frame_rate:packet=pts,duration", "-of", "json", file);
            using var doc = JsonDocument.Parse(json);
            var stream = doc.RootElement.GetProperty("streams")[0];
            Assert.Equal("rawvideo", stream.GetProperty("codec_name").GetString());
            Assert.Equal(pixelFormat, stream.GetProperty("pix_fmt").GetString());
            Assert.Equal(W, stream.GetProperty("width").GetInt32());
            Assert.Equal(H, stream.GetProperty("height").GetInt32());
            Assert.Equal($"{rateN}/{rateD}", stream.GetProperty("r_frame_rate").GetString());
            var packets = doc.RootElement.GetProperty("packets").EnumerateArray().ToList();
            Assert.Equal(Periods.Select(p => p * NutRawVideoWriter.TicksPerPeriod), packets.Select(p => p.GetProperty("pts").GetInt64()));
            // Un periodo cada uno: la duración que FFmpeg tiene por fiable (con la base de tiempo de un periodo, la de 1 no lo es).
            Assert.All(packets, p => Assert.Equal(NutRawVideoWriter.TicksPerPeriod, p.GetProperty("duration").GetInt64()));
        }
        finally { File.Delete(file); }
    }

    [SkippableFact]
    public async Task TheFpsFilter_PutsEachFrameInItsPeriods_AndEndsWithTheLastOne()
    {
        Skip.IfNot(TestAssets.FfmpegDir is not null, "FFmpeg no disponible en tools/.");
        var locator = new FfmpegLocator(TestAssets.FfmpegDir!);
        string file = WriteSample("uyvy422", 30000, 1001);
        try
        {
            var err = await RunAsync(locator.FfmpegPath, true, "-hide_banner", "-nostats", "-i", file,
                "-vf", "format=nv12,fps=30000/1001,format=gray,showinfo", "-f", "null", "-");
            var frames = err.Split('\n').Where(l => l.Contains("Parsed_showinfo", StringComparison.Ordinal) && l.Contains(" mean:[", StringComparison.Ordinal))
                            .Select(l => (Pts: After(l, "pts:", ' '), Mean: After(l, " mean:[", ']'))).ToList();
            // Un cuadro por periodo, del 0 al 18 (el último dura un periodo: ni uno más), y cada periodo con el último cuadro real
            // que llegó en él o antes: tramos de 3, 1, 5, 1, 1, 7 y 1 periodos.
            Assert.Equal(Enumerable.Range(0, 19).Select(i => (double)i), frames.Select(f => f.Pts));
            var runs = new List<int>();
            for (int i = 0; i < frames.Count; i++)
                if (i == 0 || frames[i].Mean != frames[i - 1].Mean) runs.Add(1); else runs[^1]++;
            Assert.Equal(new[] { 3, 1, 5, 1, 1, 7, 1 }, runs);
        }
        finally { File.Delete(file); }
    }

    private static double After(string line, string key, char end)
    {
        int i = line.IndexOf(key, StringComparison.Ordinal) + key.Length;
        while (i < line.Length && line[i] == ' ') i++; // showinfo alinea los números con espacios («pts:      3»)
        int j = line.IndexOf(end, i);
        var text = (j < 0 ? line[i..] : line[i..j]).Trim();
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    private static async Task<string> RunAsync(string exe, bool stderr, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return stderr ? await e : await o;
    }
}
