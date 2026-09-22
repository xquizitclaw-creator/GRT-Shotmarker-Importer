using GrtPluginKit.Grt;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Grt;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Import;

/// <summary>
/// Reads an export, renders the selected strings and writes them into a sibling load.
/// Best-effort per string: one unreadable string never costs the shooter the rest of
/// their session.
/// </summary>
public static class ImportJob
{
    public static IReadOnlyList<SmString> Plan(string exportPath, IList<string> log) =>
        SmExportReader.Read(exportPath, log);

    /// <summary>Writes the sibling and returns its path. The open load is never modified —
    /// the GRT plugin interface does not allow editing it in place.</summary>
    public static string Run(string loadPath,
                             IEnumerable<(SmString String, double? ChargeGrains)> selected,
                             IList<string> log)
    {
        GrtLoadDoc doc = File.Exists(loadPath)
            ? GrtLoadDoc.Load(loadPath)
            : GrtLoadDoc.CreateMinimal(Path.GetFileNameWithoutExtension(loadPath), loadPath);

        foreach (var (s, charge) in selected)
        {
            try
            {
                TargetFace face = TargetFaceLibrary.Find(s.FaceId) ?? Fallback(s, log);
                RenderedTarget render = TargetRenderer.Render(s, face);
                GrtShotGroupWriter.Add(doc, new ImportItem(s, render, charge), log);
            }
            catch (Exception ex)
            {
                log.Add($"'{s.Name}': not imported ({ex.Message})");
            }
        }

        return GrtShotGroupWriter.Save(doc);
    }

    private static TargetFace Fallback(SmString s, IList<string> log)
    {
        log.Add($"'{s.Name}': unknown target face '{s.FaceId}' — plotting without scoring rings");
        double w = s.FrameWidthMm > 0 ? s.FrameWidthMm : 1000;
        double h = s.FrameHeightMm > 0 ? s.FrameHeightMm : 1000;
        return TargetFaceLibrary.Generic(w, h);
    }
}
