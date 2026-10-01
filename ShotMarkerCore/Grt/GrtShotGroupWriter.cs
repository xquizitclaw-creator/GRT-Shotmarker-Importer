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
/// <para>That separation and the shooting distance are both stored in SI — millimetres and
/// metres — whatever units the GRT reading them displays. Neither carries a unit in the
/// .grtload, so this was worth getting from GRT itself rather than from a reader: a tab GRT
/// wrote on an install configured <c>refdistance=in;range=yard</c> holds
/// <c>refDistance="150.0124"</c> for a 5.906 in reference and <c>shootDistance="914.4"</c>
/// for a 1000 yd string. GRT converts on the way to the screen, not on the way to the
/// file.</para>
///
/// <para>Converting here as well divides twice. An imperial install showed a 1000 yd string
/// as 1093.61 yd and its 1463 mm reference as 2.268 in — the distance 1.0936x out and the
/// scale every group measurement is taken against 25.4x out. Note that
/// <see cref="GrtShotGroups.ShootToM"/> and <see cref="GrtShotGroups.RefToMm"/> in
/// GRT-Reloading-Toolkit convert these fields by the install's units when reading, which is
/// the mirror image of the same mistake; that is a bug in a different project, and matching
/// it here only made both wrong together.</para>
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

    public static void Add(GrtLoadDoc doc, ImportItem item, IList<string> log) =>
        AddAll(doc, new[] { item }, log);

    /// <summary>
    /// Appends a whole import: one shot-group tab per string, then ONE velocity measurement
    /// and ONE note covering all of them.
    ///
    /// <para>The picture is what forces a tab per string — a GRT shot group holds exactly one
    /// — but the velocities and the notes do not have to be split that way, and GRT's tab bar
    /// does not scroll, so a tab past the right-hand edge of the window cannot be reached at
    /// all. Three tabs per string put a five-string session's last tabs out of reach; one per
    /// string plus two keeps them on screen. Collecting the charges is also the shape GRT's
    /// own ladder and OCW analysis wants: one measurement holding every charge, rather than
    /// several it cannot compare.</para>
    /// </summary>
    public static void AddAll(GrtLoadDoc doc, IEnumerable<ImportItem> items, IList<string> log)
    {
        var charges = new List<GrtCharge>();
        var notes = new List<string>();
        var titles = new List<string>();

        foreach (ImportItem item in items)
        {
            SmString s = item.String;
            // Best-effort per string, same promise ImportJob makes for reading and rendering:
            // one string the writer chokes on must not cost the shooter the other four.
            try
            {
                if (s.Shots.Count == 0) { log.Add($"'{s.Name}': no shots — not imported"); continue; }

                string title = $"ShotMarker — {s.Name}";
                if (AddTab(doc, item, title, log) is not { } dropped) continue;

                titles.Add(title);
                if (ChargeFor(item, log) is { } charge) charges.Add(charge);
                notes.Add(NoteText(s, dropped));
                log.Add($"'{s.Name}': {s.Shots.Count} shots at {s.DistanceValue.ToString("0", CultureInfo.InvariantCulture)}{s.DistanceUnit}");
            }
            catch (Exception ex)
            {
                log.Add($"'{s.Name}': not imported ({ex.Message})");
            }
        }

        // One string keeps the title it always had, so a single import is tab-for-tab what it
        // was before this collecting existed.
        if (charges.Count > 0)
            doc.AddMeasurement(titles.Count == 1 ? titles[0] : "ShotMarker — velocities", charges);
        if (notes.Count > 0)
            doc.AddNote(titles.Count == 1 ? titles[0] : "ShotMarker — notes",
                        string.Join("\n\n" + new string('-', 60) + "\n\n", notes));
    }

    /// <summary>Writes the shot-group tab and returns the numbers of the shots it could not
    /// plot, or null when the string could not be written at all.</summary>
    private static IReadOnlyList<int>? AddTab(GrtLoadDoc doc, ImportItem item, string title,
                                              IList<string> log)
    {
        SmString s = item.String;
        TargetProjection proj = item.Render.Projection;

        // AddShotGroup formats coordinates with ToString("R"), which writes the literal "NaN"
        // without complaint — GRT then parses it back as 0 and plants a phantom hit in the
        // corner of the picture. SmShot.IsImported carries that guard and the sighters (which
        // the load has no way to hold; see the predicate), and it is the same predicate
        // RenderOptions.ForGrt draws with, so the points written are exactly the discs drawn.
        var plottable = s.Shots.Where(sh => sh.IsImported).ToList();
        // Reported over the record shots alone. A sighter is already accounted for by the line
        // above it, and one that also happens to have no coordinates is not a second loss.
        var record = s.Shots.Where(sh => !sh.IsSighter).ToList();
        var dropped = record.Select(sh => sh.Number).Except(plottable.Select(sh => sh.Number)).ToList();
        int sighters = s.Shots.Count - record.Count;
        if (sighters > 0)
            log.Add($"'{s.Name}': {sighters} sighter(s) — left out, GRT has nowhere to put them");
        if (dropped.Count > 0)
            log.Add($"'{s.Name}': {dropped.Count} shot(s) with no coordinates — not plotted");
        if (plottable.Count == 0)
        {
            // Two different stories, and "no shot has coordinates" told the wrong one for the
            // first: a string of nothing but sighters is a perfectly good warm-up the import
            // has no record shot to carry, not a string the plugin failed to read.
            log.Add(record.Count == 0
                ? $"'{s.Name}': every shot is a sighter — nothing to import"
                : $"'{s.Name}': no record shot has coordinates — not imported");
            return null;
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

        // SI, unconverted: see the remarks on this class for the tab GRT itself wrote on an
        // imperial install, and for what converting here did to one.
        double refDistance = refMm;
        double shootDistance = s.DistanceMetres;
        if (!(refDistance > 0) || !double.IsFinite(refDistance))
        {
            log.Add($"'{s.Name}': the picture has no usable size — not imported");
            return null;
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
        return dropped;
    }

    /// <summary>
    /// The &lt;group&gt; name, which is also where GRT's reader looks for a ladder step — so a
    /// charge given here comes back as <see cref="TargetGroup.ChargeGrains"/>.
    /// </summary>
    private static string GroupName(ImportItem item) =>
        item.ChargeGrains is { } gr
            ? gr.ToString("0.0#", CultureInfo.InvariantCulture) + " gr"
            : item.String.Name;

    /// <summary>This string's velocities as one GRT charge, or null when it has none. Built
    /// rather than written, because every string in an import shares one measurement.</summary>
    private static GrtCharge? ChargeFor(ImportItem item, IList<string> log)
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
        if (shots.Count == 0) { log.Add($"'{s.Name}': no velocities — measurement omitted"); return null; }

        var charge = new GrtCharge
        {
            Name = GroupName(item),
            ValueKg = (item.ChargeGrains ?? 0) * KgPerGrain,
            Note = $"ShotMarker {s.Name}, {s.Timestamp:yyyy-MM-dd HH:mm}",
        };
        // Every shot here already passed !IsFlyer, so its score is always the real one.
        foreach (SmShot sh in shots)
            charge.Shots.Add(new GrtShot(sh.VelocityMps!.Value, sh.Score));

        return charge;
    }

    /// <summary>What the string holds besides plain record shots, as the parenthesised tail of
    /// the note's "Shots:" line — empty when it holds only those. Sighters are counted as left
    /// out because they are: nothing in the tab corresponds to them.</summary>
    private static string Breakdown(SmString s)
    {
        int sighters = s.Shots.Count(sh => sh.IsSighter);
        // !IsSighter so the two clauses never count one shot twice: a sighter the device also
        // flagged invalid is reported once, as the sighter it is.
        int excluded = s.Shots.Count(sh => !sh.IsSighter && (sh.IsInvalid || sh.IsExcludedOnDevice));
        var parts = new List<string>();
        if (sighters > 0) parts.Add($"{sighters} sighter{(sighters > 1 ? "s" : "")}, left out");
        if (excluded > 0) parts.Add($"{excluded} invalid/excluded");
        return parts.Count == 0 ? "" : " (" + string.Join("; ", parts) + ")";
    }

    /// <summary>This string's block of the import note.</summary>
    private static string NoteText(SmString s, IReadOnlyList<int> dropped)
    {
        var ic = CultureInfo.InvariantCulture;
        var lines = new List<string>
        {
            $"String:     {s.Name}",
            $"Shot:       {s.Timestamp:yyyy-MM-dd HH:mm}",
            $"Distance:   {s.DistanceValue.ToString("0", ic)} {s.DistanceUnit}",
            $"Face:       {s.FaceId}",
            // Counts what the label says. IsFlyer is wider still (it also covers a valid
            // record shot ShotMarker's own group left out), and that shot is reported honestly
            // and separately by the "{inGroup} of {recordShots} record shots" line below —
            // counting it here too would make this line lie about the user's data. But a shot
            // the DEVICE excluded (hidden, off-target) has no such second line to report it:
            // that line only appears when the export carries a group at all, so without one a
            // struck-out cross-fire vanished from both — "Shots: 20 (2 sighter/invalid)"
            // printed beside a GRT group of 17, with nothing naming the missing three.
            //
            // The sighters get their own clause rather than sharing that count: they are the
            // one category the tab does not hold at all, and one number covering both fates
            // would describe a shot GRT has as a flyer and a shot GRT has never seen as the
            // same thing.
            $"Shots:      {s.Shots.Count}{Breakdown(s)}",
        };
        if (s.ScoreText is { Length: > 0 }) lines.Add($"Score:      {s.ScoreText}");

        // Kept off the "sighter/invalid/excluded" line above on purpose: that line counts what
        // the DEVICE excluded, and a shot the shooter struck out in the import window is a
        // different fact about the string. Folding the two together would make an exclusion
        // the shooter made look like one ShotMarker made, which is exactly the distinction
        // SmShot.IsExcludedByUser exists to preserve.
        var struckOut = s.Shots.Where(sh => sh.IsExcludedByUser).Select(sh => sh.Number).ToList();
        if (struckOut.Count > 0)
            lines.Add($"Excluded on import: shot{(struckOut.Count > 1 ? "s" : "")} " +
                      string.Join(", ", struckOut.Select(n => n.ToString(ic))));

        // Why the shooter's five sighters are not in the tab they just imported. Said in the
        // note because nothing else can say it: the tab itself simply holds fifteen hits, and
        // a shooter counting discs against their scorecard is owed the reason rather than left
        // to work it out.
        var sighters = s.Shots.Where(sh => sh.IsSighter).Select(sh => sh.Number).ToList();
        if (sighters.Count > 0)
            lines.Add($"Sighters:   shot{(sighters.Count > 1 ? "s" : "")} " +
                      string.Join(", ", sighters.Select(n => n.ToString(ic))) +
                      " — not imported. A GRT hit is a scoring shot or a flyer, and a sighter " +
                      "is neither.");

        if (dropped.Count > 0)
            lines.Add($"Not plotted: shot{(dropped.Count > 1 ? "s" : "")} " +
                      string.Join(", ", dropped.Select(n => n.ToString(ic))) +
                      " — no coordinates.");

        // Said here because the picture can no longer say it. GRT prints its own number
        // beside every hit it holds, counting them from one, and that is the only numbering
        // on the imported picture (RenderOptions.DrawShotNumbers). While every shot is
        // plotted those labels are ShotMarker's numbers; leave one out and everything after
        // it shifts, and the shot numbers named elsewhere in this note stop matching the
        // plot. Sighters shift it by definition — they are always left out, and they come
        // first — so naming GRT's first hit is the one fact that makes the rest readable.
        if (sighters.Count > 0 || dropped.Count > 0)
        {
            int first = s.Shots.First(sh => sh.IsImported).Number;
            // Sighters alone shift the whole run by a constant — GRT's #1 is shot 6, #2 is shot
            // 7, and a shooter told where the count starts can read the rest off. A shot missing
            // from the middle is the harder case and keeps the stronger warning.
            lines.Add($"GRT numbers the hits it holds from 1: its #1 is shot {first.ToString(ic)} above"
                      + (dropped.Count > 0
                         ? ". Past a shot it does not hold, its labels no longer match the shot "
                           + "numbers above."
                         : ", and the rest follow in order."));
        }

        if (s.Stats is { } st)
        {
            lines.Add("");
            // No longer hedged ("for the group it had selected"): task 9b makes GRT measure
            // the same shots ShotMarker measured (SmShot.InSelectedGroup / IsFlyer), so its
            // own analysis of this tab now agrees with these numbers rather than repeating a
            // different shot set's. What can still differ from "every record shot fired" is
            // stated below as a plain count, so an excluded shot is visible, not mysterious.
            //
            // Striking a shot out changes the group GRT now holds, but it cannot change what
            // the device measured — these numbers came off the ShotMarker and stand as its
            // record. They are kept rather than recomputed (which would have this plugin claim
            // an authority it does not have) or blanked (which would throw away a real
            // measurement), and qualified instead, so the note never reads as if the device
            // had measured the curated group.
            lines.Add(struckOut.Count > 0
                ? "ShotMarker's own figures, for the full string, before the exclusions above:"
                : "ShotMarker's own figures:");
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
        return string.Join("\n", lines);
    }
}
