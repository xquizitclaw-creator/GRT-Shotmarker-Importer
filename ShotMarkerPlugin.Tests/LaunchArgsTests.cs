using Xunit;

namespace ShotMarker.Plugin.Tests;

public class LaunchArgsTests
{
    [Fact]
    public void ReadsThePortGrtPasses()
    {
        var a = LaunchArgs.Parse(new[] { "--ipcport", "51234" });
        Assert.Equal(51234, a.Port);
        Assert.Null(a.IpcFile);
        Assert.True(a.ShouldConnect);
    }

    [Fact]
    public void NoPortMeansFileOnly() =>
        Assert.False(LaunchArgs.Parse(Array.Empty<string>()).ShouldConnect);

    [Fact]
    public void AnUnreadablePortMeansFileOnly() =>
        Assert.False(LaunchArgs.Parse(new[] { "--ipcport", "x" }).ShouldConnect);

    // GRTMultiPlatform on Linux hands over a Unix socket path (GRT's Linux transport).
    [Fact]
    public void ReadsTheSocketFileGrtPasses()
    {
        var a = LaunchArgs.Parse(new[] { "--ipcfile", "/tmp/grt-1a2b.sock" });
        Assert.Equal("/tmp/grt-1a2b.sock", a.IpcFile);
        Assert.True(a.ShouldConnect);
    }

    [Fact]
    public void ASocketFileWinsOverAPort()
    {
        var a = LaunchArgs.Parse(new[] { "--ipcfile", "/tmp/grt.sock", "--ipcport", "51234" });
        Assert.Equal("/tmp/grt.sock", a.IpcFile);
        Assert.True(a.ShouldConnect);
    }

    [Fact]
    public void AnIpcFileWithoutAPathMeansFileOnly() =>
        Assert.False(LaunchArgs.Parse(new[] { "--ipcfile" }).ShouldConnect);
}
