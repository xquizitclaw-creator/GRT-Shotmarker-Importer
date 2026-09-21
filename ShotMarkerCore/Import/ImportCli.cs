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

        // Ruling F34: a malformed --charge is a scripting mistake, not a "no charge given"
        // that TryParse can quietly turn into null. Silently dropping it and exiting 0 would
        // report success on a ladder import whose charges never made it in.
        double? charge = null;
        if (args.Length >= 3)
        {
            if (!double.TryParse(args[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double g))
            {
                log.Add($"'{args[2]}': not a valid charge (grains)");
                foreach (string l in log) Console.WriteLine(l);
                return 1;
            }
            charge = g;
        }

        var strings = ImportJob.Plan(args[0], log);
        if (strings.Count == 0)
        {
            foreach (string l in log) Console.WriteLine(l);
            Console.WriteLine("nothing to import");
            return 1;
        }

        string outPath;
        try
        {
            outPath = ImportJob.Run(args[1], strings.Select(s => (s, charge)), log);
        }
        catch (Exception ex)
        {
            // Ruling F34: GrtLoadDoc.Load(args[1]) runs inside ImportJob.Run before its own
            // per-string try/catch has a chance to run. Everywhere else in this codebase
            // reports rather than crashes; a corrupt .grtload should be no different.
            log.Add($"'{args[1]}': not imported ({ex.Message})");
            foreach (string l in log) Console.WriteLine(l);
            return 1;
        }

        foreach (string l in log) Console.WriteLine(l);
        Console.WriteLine($"wrote {outPath}");
        return 0;
    }
}
