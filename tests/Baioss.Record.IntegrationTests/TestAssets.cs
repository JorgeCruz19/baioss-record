namespace Baioss.Record.IntegrationTests;

/// <summary>Localiza el FFmpeg y el clip de prueba incluidos en <c>tools/</c> (subiendo desde el binario).</summary>
internal static class TestAssets
{
    public static string? FfmpegDir { get; }
    public static string? Clip { get; }

    static TestAssets()
    {
        var exe = FindUpwards(Path.Combine("tools", "ffmpeg", "ffmpeg.exe"));
        FfmpegDir = exe is null ? null : Path.GetDirectoryName(exe);
        Clip = FindUpwards(Path.Combine("tools", "test", "clip.mp4"));
    }

    /// <summary>True si tanto FFmpeg como el clip están disponibles para los tests que los requieren.</summary>
    public static bool Available => FfmpegDir is not null && Clip is not null;

    private static string? FindUpwards(string relative)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                var candidate = Path.Combine(dir.FullName, relative);
                if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
        }
        return null;
    }
}
