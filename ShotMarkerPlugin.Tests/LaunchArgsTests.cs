using Xunit;

namespace ShotMarker.Plugin.Tests;

public class LaunchArgsTests
{
    [Fact]
    public void ReadsThePortGrtPasses()
    {
        var a = LaunchArgs.Parse(new[] { "--ipcport", "51234" });
        Assert.Equal(51234, a.Port);
        Assert.True(a.ShouldConnect(isLinux: false));
    }

    [Fact]
    public void NoPortMeansFileOnly() =>
        Assert.False(LaunchArgs.Parse(Array.Empty<string>()).ShouldConnect(isLinux: false));

    [Fact]
    public void AnUnreadablePortMeansFileOnly() =>
        Assert.False(LaunchArgs.Parse(new[] { "--ipcport", "x" }).ShouldConnect(isLinux: false));

    // GRTMultiPlatform on Linux hands over a Unix socket path; this plugin does not speak it yet.
    [Fact]
    public void AnIpcFileMeansFileOnly()
    {
        var a = LaunchArgs.Parse(new[] { "--ipcfile", "/tmp/grt.sock", "--ipcport", "51234" });
        Assert.True(a.HasIpcFile);
        Assert.False(a.ShouldConnect(isLinux: false));
    }

    [Fact]
    public void LinuxRunsFileOnly() =>
        Assert.False(LaunchArgs.Parse(new[] { "--ipcport", "51234" }).ShouldConnect(isLinux: true));
}
