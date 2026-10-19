using System.Globalization;
using ShotMarker.Core.Sm;

namespace ShotMarker.Plugin.Ui;

/// <summary>The text the window shows for a string or a shot. Kept apart from the view so it can
/// be tested, and so the list, the caption and the shot table cannot word one fact two ways.</summary>
internal static class RowText
{
    /// <summary>"20" normally; "18 of 20" once some are struck out, so the count in the list
    /// is the count that will be measured.</summary>
    public static string CountedText(SmString s)
    {
        int struck = s.Shots.Count(sh => sh.IsExcludedByUser);
        return struck == 0 ? s.Shots.Count.ToString(CultureInfo.CurrentCulture)
                           : $"{s.Shots.Count - struck} of {s.Shots.Count}";
    }

    /// <summary>Reference D3: uses the same shot set as the writer's velocity measurement
    /// (which is also where the SD/ES cells beside this one come from) — not every shot with
    /// a velocity reading, which disagrees with both. Prefers ShotMarker's own group average
    /// when it is present, for the same reason SD and ES do.</summary>
    public static string MeanVelocityText(SmString s, IFormatProvider ic)
    {
        if (s.Stats?.VelocityAvgMps is { } avg) return avg.ToString("0.0", ic);
        var v = s.Shots
            .Where(sh => !sh.IsFlyer && sh.VelocityMps is > 0 && double.IsFinite(sh.VelocityMps.Value))
            .Select(sh => sh.VelocityMps!.Value)
            .ToList();
        return v.Count > 0 ? v.Average().ToString("0.0", ic) : "";
    }

    /// <summary>The device's own figures are shown here as they are in the note, and carry the
    /// same qualifier once a shot has been struck out: they measured the string ShotMarker
    /// saw, which is no longer the group being imported.
    ///
    /// <para>The sighter count is here for the one place this window differs from the import:
    /// the preview draws the sighters (RenderOptions.ForPreview) and the load cannot hold them,
    /// so the picture above has discs the GRT tab will not. Saying so is what keeps that from
    /// being a surprise.</para></summary>
    public static string CaptionFor(SmString s)
    {
        var ic = CultureInfo.CurrentCulture;
        int sighters = s.Shots.Count(sh => sh.IsSighter);
        string sight = sighters > 0
            ? $"   ·   {sighters} sighter{(sighters > 1 ? "s" : "")} (red, not imported)"
            : "";
        string stats = s.Stats is { } st && st.GroupSizeMm is { } g
            ? $"   ·   ShotMarker: group {g.ToString("0.0", ic)} mm"
              + (st.VelocitySdMps is { } sd ? $", sd {sd.ToString("0.0", ic)} m/s" : "")
              + (s.Shots.Any(sh => sh.IsExcludedByUser) ? " (full string, before strike-outs)" : "")
            : "";
        return $"{s.Name}   ·   {CountedText(s)} shots{sight}   ·   {s.FaceId}{stats}";
    }

    /// <summary>Why the device, or ShotMarker's own group, left this shot out — empty when it
    /// did not, which is also what makes the tick-box the shooter's to set.</summary>
    public static string DeviceExclusion(SmShot sh) =>
        sh.IsSighter ? "sighter"
        : sh.IsInvalid ? "no position"
        : sh.IsExcludedOnDevice ? "excluded on device"
        : sh.InSelectedGroup == false ? "not in group"
        : "";
}
