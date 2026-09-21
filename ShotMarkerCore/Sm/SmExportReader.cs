namespace ShotMarker.Core.Sm;

/// <summary>The one entry point for reading a ShotMarker export, whichever way it was saved.
/// It owns no parsing of its own: it just dispatches on the file extension to
/// <see cref="SmTarReader"/> or <see cref="SmCsvReader"/> and surfaces any error through the
/// caller's log rather than throwing.</summary>
public static class SmExportReader
{
    public static IReadOnlyList<SmString> Read(string path, IList<string> log)
    {
        try
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".tar":
                    using (FileStream fs = File.OpenRead(path)) return SmTarReader.Read(fs, log);
                case ".csv":
                    using (var sr = new StreamReader(path)) return SmCsvReader.Read(sr, log);
                default:
                    log.Add($"{Path.GetFileName(path)}: not a ShotMarker export (.tar or .csv expected)");
                    return Array.Empty<SmString>();
            }
        }
        catch (Exception ex)
        {
            log.Add($"{Path.GetFileName(path)}: {ex.Message}");
            return Array.Empty<SmString>();
        }
    }
}
