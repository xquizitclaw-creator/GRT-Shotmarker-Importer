using Avalonia;
using GrtPluginKit.Ipc;
using ShotMarker.Core.Import;
using ShotMarker.Plugin.Ui;

namespace ShotMarker.Plugin;

internal static class Program
{
    internal const string PluginId = "com.grt.plugin.shotmarker";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--import")
            return ImportCli.Run(args.Skip(1).ToArray());

        LaunchArgs launch = LaunchArgs.Parse(args);

        GrtClient? grt = null;
        if (launch.ShouldConnect)
        {
            grt = launch.IpcFile is { } file ? new GrtClient(file) : new GrtClient(launch.Port);
            try { grt.Connect(); } catch { /* GRT closed the socket; run file-only */ }
        }

        App.Grt = grt is null ? null : new GrtClientHost(grt);
        int rc = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

        grt?.Dispose();
        return rc;
    }

    /// <summary>Also what the headless tests configure, so the window they build is themed the
    /// same as the one a shooter sees.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
