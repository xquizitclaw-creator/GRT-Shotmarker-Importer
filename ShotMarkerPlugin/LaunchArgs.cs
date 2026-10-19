namespace ShotMarker.Plugin;

/// <summary>How GRT started the plugin. GRT and GRTMultiPlatform pass <c>--ipcport &lt;port&gt;</c>
/// (TCP on 127.0.0.1) on Windows and macOS. GRTMultiPlatform on Linux passes
/// <c>--ipcfile &lt;path&gt;</c> (a Unix socket) instead, which this plugin does not speak yet —
/// Linux waits for the shared kit — so there, and on Linux at all, the window runs file-only:
/// no GRT connection, the import asks where to write.</summary>
internal sealed record LaunchArgs(int Port, bool HasIpcFile)
{
    public static LaunchArgs Parse(IReadOnlyList<string> args)
    {
        int port = 0;
        bool ipcFile = false;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "--ipcfile") ipcFile = true;
            if (i + 1 < args.Count && args[i] == "--ipcport" && int.TryParse(args[i + 1], out int p)) port = p;
        }
        return new LaunchArgs(port, ipcFile);
    }

    public bool ShouldConnect(bool isLinux) => Port > 0 && !HasIpcFile && !isLinux;
}
