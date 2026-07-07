using Baioss.Record.Application.Storage;
using Xunit;

namespace Baioss.Record.UnitTests;

/// <summary>Fase 4 — formateo legible de tamaños y duraciones (puro, determinista en InvariantCulture).</summary>
public sealed class StorageFormatTests
{
    private const long KB = 1024, MB = 1024 * 1024, GB = 1024L * 1024 * 1024, TB = 1024L * 1024 * 1024 * 1024;

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2 * KB, "2 KB")]
    [InlineData(5 * MB, "5 MB")]
    [InlineData(GB + GB / 2, "1.5 GB")]     // 1.5 GiB → punto decimal (InvariantCulture)
    [InlineData(2 * TB, "2.00 TB")]
    public void Bytes_FormatsByMagnitude(long bytes, string expected) => Assert.Equal(expected, StorageFormat.Bytes(bytes));

    [Fact]
    public void Bytes_NegativeIsZero() => Assert.Equal("0 B", StorageFormat.Bytes(-1));

    [Fact]
    public void Duration_Hours() => Assert.Equal("1 h 02 min", StorageFormat.Duration(new TimeSpan(1, 2, 30)));

    [Fact]
    public void Duration_Minutes() => Assert.Equal("45 min", StorageFormat.Duration(TimeSpan.FromSeconds(45 * 60 + 20)));

    [Fact]
    public void Duration_Seconds() => Assert.Equal("12 s", StorageFormat.Duration(TimeSpan.FromSeconds(12)));

    [Fact]
    public void Duration_NegativeIsZeroSeconds() => Assert.Equal("0 s", StorageFormat.Duration(TimeSpan.FromSeconds(-5)));
}
