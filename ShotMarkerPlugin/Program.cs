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

        ApplicationConfiguration.Initialize();

        int port = 0;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--ipcport" && int.TryParse(args[i + 1], out int p)) port = p;

        GrtClient? grt = null;
        if (port > 0)
        {
            grt = new GrtClient(port);
            try { grt.Connect(); } catch { /* GRT closed the socket; run file-only */ }
        }

        using (var form = new ImportForm(grt))
            Application.Run(form);

        grt?.Dispose();
        return 0;
    }
}
