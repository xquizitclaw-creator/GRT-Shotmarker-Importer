using System.Globalization;

namespace ShotMarker.Core.Import;

/// <summary>
/// A headless entry point: the whole import without WinForms, so the pipeline can be
/// exercised on any machine and in CI, and so a failed import can be reproduced from
/// a shell rather than from a screenshot.
/// </summary>
public static class ImportCli
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: --import <export.tar|export.csv> <load.grtload> [charge-grains]");
            return 2;
        }

        var log = new List<string>();
        double? charge = args.Length >= 3 &&
            double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double g) ? g : null;

        var strings = ImportJob.Plan(args[0], log);
        if (strings.Count == 0)
        {
            foreach (string l in log) Console.WriteLine(l);
            Console.WriteLine("nothing to import");
            return 1;
        }

        string outPath = ImportJob.Run(args[1], strings.Select(s => (s, charge)), log);
        foreach (string l in log) Console.WriteLine(l);
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }
}
