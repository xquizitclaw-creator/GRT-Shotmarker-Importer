using System.Globalization;
using GrtPluginKit.Grt;
using GrtReloadingToolkit.Ocw;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Grt;

/// <summary>
/// Appends one imported string to a GRT load: a shot-group tab with the rendered picture
/// and its hits, a measurement of the velocities, and a note carrying ShotMarker's own
/// statistics.
///
/// <para>Scale is the whole job. GRT stores hits as fractions of the picture and recovers
/// real-world size from two reference points a stated distance apart. This writer places
/// those points itself, on a picture it drew itself, so their separation in millimetres is
/// exact by construction — it is read straight off
/// <see cref="RenderedTarget.Projection"/> (ruling S2) and never recomputed from board size
/// and pixel count.</para>
///
/// <para>The unit that separation is stated in is <em>not</em> stored in the .grtload. It
/// comes from the GRT install that will read the file, and the same is true of the shooting
/// distance. Both are resolved through <see cref="GrtShotGroups.GrtDefaults(GrtConfig?)"/>.
/// Writing millimetres and metres unconditionally puts an inch-and-yard install 25.4x and
/// 1.0936x out — a group that looks perfectly plausible and measures wrong.</para>
/// </summary>
public static class GrtShotGroupWriter
{
    /// <summary>
    /// Kilograms in one grain, matching the literal GRT's own reader divides by in
    /// <see cref="GrtCharge.ChargeGrains"/>, so a charge written here is read back unchanged.
    /// </summary>
    private const double KgPerGrain = 0.00006479891;

    /// <summary>
    /// Mirrors <c>TargetRenderer</c>'s own fallback so the hit markers GRT draws are the size
    /// of the discs in the picture underneath them. Display only — it never affects a
    /// measurement.
    /// </summary>
    private const double DefaultBulletDiameterMm = 7.2;

    /// <summary>
    /// How far in from each edge of the picture the two reference points sit, as a fraction of
    /// its width. Inset only so both markers stay comfortably grabbable in GRT's editor; the
    /// scale GRT recovers is identical for any inset, because the separation is measured on the
    /// same projection that placed them.
    /// </summary>
    private const double RefInset = 0.1;

    /// <summary>
    /// The sibling family generated loads belong to. Each plugin keeps its own, because
    /// GrtLoadDoc prunes a family to its three newest: sharing the toolkit's would let the
    /// toolkit delete this plugin's output, and this plugin the toolkit's.
    /// </summary>
    public const string SiblingFamily = "shotmarker";

    /// <summary>Saves the load beside the user's original as this plugin's own sibling, and
    /// returns the path written. The one supported way to save what this writer produced.</summary>
    public static string Save(GrtLoadDoc doc) => doc.SaveSibling("shotmarker", SiblingFamily);

    /// <summary>
    /// Millimetres expressed in the unit GRT will read a shot-group reference distance in —
    /// the exact inverse of <see cref="GrtShotGroups.RefToMm"/>, which is the function that
    /// will undo it.
    /// </summary>
    public static double MmToRef(double mm, RefUnit u) => u switch
    {
        RefUnit.Cm => mm / 10.0,
        RefUnit.Inch => mm / GrtUnits.MmPerInch,
        _ => mm,
    };

    /// <summary>Metres expressed in the unit GRT will read a shooting distance in — the exact
    /// inverse of <see cref="GrtShotGroups.ShootToM"/>.</summary>
    public static double MToShoot(double m, ShootUnit u) =>
        u == ShootUnit.Yards ? m / GrtUnits.MetresPerYard : m;

    /// <param name="config">The GRT install that will read the file, for the units of the two
    /// unlabelled shot-group numbers. Null falls back to the install this plugin is running
    /// beside, and to metric when there is none.</param>
    public static void Add(GrtLoadDoc doc, ImportItem item, IList<string> log, GrtConfig? config = null)
    {
        SmString s = item.String;
        if (s.Shots.Count == 0) { log.Add($"'{s.Name}': no shots — not imported"); return; }

        string title = $"ShotMarker — {s.Name}";
        if (!AddTab(doc, item, title, config ?? GrtConfig.Current, log)) return;
        AddVelocities(doc, item, title, log);
        AddStatsNote(doc, s, title);
        log.Add($"'{s.Name}': {s.Shots.Count} shots at {s.DistanceValue.ToString("0", CultureInfo.InvariantCulture)}{s.DistanceUnit}");
    }

    private static bool AddTab(GrtLoadDoc doc, ImportItem item, string title, GrtConfig? cfg, IList<string> log)
    {
        SmString s = item.String;
        TargetProjection proj = item.Render.Projection;

        // Errored and "fake" shots carry double.NaN coordinates (SmShot.IsInvalid), and
        // AddShotGroup formats with ToString("R"), which writes the literal "NaN" without
        // complaint — GRT then parses it back as 0 and plants a phantom hit in the corner of
        // the picture. So they are dropped before XMm/YMm is read at all, which is also what
        // TargetRenderer did: the points written are exactly the discs drawn. IsFinite is
        // belt and braces for any future source of a bad coordinate.
        var plottable = s.Shots
            .Where(sh => !sh.IsInvalid && double.IsFinite(sh.XMm) && double.IsFinite(sh.YMm))
            .ToList();
        int dropped = s.Shots.Count - plottable.Count;
        if (dropped > 0)
            log.Add($"'{s.Name}': {dropped} shot(s) with no coordinates — not plotted");
        if (plottable.Count == 0)
        {
            log.Add($"'{s.Name}': no shot has coordinates — not imported");
            return false;
        }

        var set = new GrtShotGroupSet { Name = GroupName(item) };
        foreach (SmShot sh in plottable)
        {
            var (x, y) = proj.ToFraction(sh.XMm, sh.YMm);
            set.Points.Add(new GrtShotPoint(x, y, sh.IsFlyer, PointOfAim: false));
        }

        // The two reference points sit on the picture's own horizontal midline, one inset from
        // each edge. Both the millimetre positions and the fractions come from the projection
        // the picture was drawn with, so the distance between them is exact by construction
        // and needs no knowledge of the board, the face or the pixel count. Level, so the
        // separation has no vertical component to round.
        double midYMm = proj.TopMm - proj.HeightMm / 2;
        double leftMm = proj.LeftMm + proj.WidthMm * RefInset;
        double rightMm = proj.LeftMm + proj.WidthMm * (1 - RefInset);
        double refMm = rightMm - leftMm;
        var (p1x, p1y) = proj.ToFraction(leftMm, midYMm);
        var (p2x, p2y) = proj.ToFraction(rightMm, midYMm);

        var (refUnit, shootUnit) = GrtShotGroups.GrtDefaults(cfg);
        double refDistance = MmToRef(refMm, refUnit);
        double shootDistance = MToShoot(s.DistanceMetres, shootUnit);
        if (!(refDistance > 0) || !double.IsFinite(refDistance))
        {
            log.Add($"'{s.Name}': the picture has no usable size — not imported");
            return false;
        }
        if (!(shootDistance > 0) || !double.IsFinite(shootDistance))
        {
            // GRT falls back to 100 m for a missing distance and says so in its own log;
            // writing a zero is better than writing a wrong number we invented.
            shootDistance = 0;
            log.Add($"'{s.Name}': no shooting distance — GRT will assume one");
        }

        double bulletMm = s.BulletDiameterMm is { } b && b > 0 ? b : DefaultBulletDiameterMm;
        double pointSize = bulletMm / proj.WidthMm;

        doc.AddShotGroup(title, item.Render.Png,
            new ShotGroupGeometry(p1x, p1y, p2x, p2y, refDistance, shootDistance),
            new[] { set }, pointSize);
        return true;
    }

    /// <summary>
    /// The &lt;group&gt; name, which is also where GRT's reader looks for a ladder step — so a
    /// charge given here comes back as <see cref="TargetGroup.ChargeGrains"/>.
    /// </summary>
    private static string GroupName(ImportItem item) =>
        item.ChargeGrains is { } gr
            ? gr.ToString("0.0#", CultureInfo.InvariantCulture) + " gr"
            : item.String.Name;

    private static void AddVelocities(GrtLoadDoc doc, ImportItem item, string title, IList<string> log)
    {
        SmString s = item.String;
        // Ruling F27: the velocity series uses the same predicate as the shot group — exclude
        // IsFlyer (sighters, rejects, and now shots ShotMarker itself left out of the group it
        // had selected), keep the finiteness guard. The device's own v_avg/v_sd/v_es are the
        // group's, not the whole string's, so any other filter reintroduces the exact
        // mismatch task 9b exists to remove. `is > 0` also rejects NaN (every comparison with
        // NaN is false), which matters: the measurement writer formats velocities with
        // ToString("0.0") and would emit "NaN".
        var shots = s.Shots
            .Where(sh => !sh.IsFlyer && sh.VelocityMps is > 0 && double.IsFinite(sh.VelocityMps.Value))
            .ToList();
        if (shots.Count == 0) { log.Add($"'{s.Name}': no velocities — measurement omitted"); return; }

        var charge = new GrtCharge
        {
            Name = GroupName(item),
            ValueKg = (item.ChargeGrains ?? 0) * KgPerGrain,
            Note = $"ShotMarker {s.Name}, {s.Timestamp:yyyy-MM-dd HH:mm}",
        };
        // Every shot here already passed !IsFlyer, so its score is always the real one.
        foreach (SmShot sh in shots)
            charge.Shots.Add(new GrtShot(sh.VelocityMps!.Value, sh.Score));

        doc.AddMeasurement(title, new[] { charge });
    }

    private static void AddStatsNote(GrtLoadDoc doc, SmString s, string title)
    {
        var ic = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"String:     {s.Name}",
            $"Shot:       {s.Timestamp:yyyy-MM-dd HH:mm}",
            $"Distance:   {s.DistanceValue.ToString("0", ic)} {s.DistanceUnit}",
            $"Face:       {s.FaceId}",
            // Counts what the label says. IsFlyer is wider since task 9b (it now also covers a
            // valid record shot ShotMarker's own group left out), and that shot is reported
            // honestly and separately by the "{inGroup} of {recordShots} record shots" line
            // below — counting it here too would make this line lie about the user's data.
            $"Shots:      {s.Shots.Count} ({s.Shots.Count(sh => sh.IsSighter || sh.IsInvalid)} sighter/invalid)",
        };
        if (s.ScoreText is { Length: > 0 }) lines.Add($"Score:      {s.ScoreText}");
        if (s.Stats is { } st)
        {
            lines.Add("");
            // No longer hedged ("for the group it had selected"): task 9b makes GRT measure
            // the same shots ShotMarker measured (SmShot.InSelectedGroup / IsFlyer), so its
            // own analysis of this tab now agrees with these numbers rather than repeating a
            // different shot set's. What can still differ from "every record shot fired" is
            // stated below as a plain count, so an excluded shot is visible, not mysterious.
            lines.Add("ShotMarker's own figures:");
            if (s.Shots.Any(sh => sh.InSelectedGroup.HasValue))
            {
                int inGroup = s.Shots.Count(sh => sh.InSelectedGroup == true);
                int recordShots = s.Shots.Count(sh => !sh.IsSighter);
                lines.Add($"  {inGroup} of {recordShots} record shots");
            }
            if (st.GroupSizeMm is { } g) lines.Add($"  group size    {g.ToString("0.0", ic)} mm");
            if (st.MeanRadiusMm is { } mr) lines.Add($"  mean radius   {mr.ToString("0.0", ic)} mm");
            if (st.CtcMm is { } ctc) lines.Add($"  centre-centre {ctc.ToString("0.0", ic)} mm");
            if (st.VelocityAvgMps is { } v) lines.Add($"  velocity avg  {v.ToString("0.0", ic)} m/s");
            if (st.VelocitySdMps is { } sd) lines.Add($"  velocity sd   {sd.ToString("0.0", ic)} m/s");
            if (st.VelocityEsMps is { } es) lines.Add($"  velocity es   {es.ToString("0.0", ic)} m/s");
        }
        doc.AddNote(title, string.Join("\n", lines));
    }
}
