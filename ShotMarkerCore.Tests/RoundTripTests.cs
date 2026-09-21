using GrtPluginKit.Grt;
using GrtReloadingToolkit.Ocw;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Grt;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;
using Xunit;

namespace ShotMarker.Core.Tests;

/// <summary>
/// The load-bearing test of the whole plugin. <see cref="GrtShotGroups.FromDoc"/> is an
/// independently written reader already in service inside GRT's own toolkit; if the writer's
/// fraction arithmetic or its reference-distance scale is wrong, the two cannot agree by
/// accident. Every expectation here is stated in ShotMarker's own millimetres, straight off
/// the parsed string — never in the writer's terms.
/// </summary>
public class RoundTripTests
{
    private static SmString FirstString()
    {
        var log = new List<string>();
        return SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
    }

    /// <summary>
    /// Every test below writes with this same config and reads back with it, so both halves
    /// agree by construction and never by accident of whatever GRT install (if any) happens to
    /// sit beside the machine running the suite (ruling F25). <see cref="Import"/> and
    /// <see cref="ReadBackAs"/> both take the config as a required argument for exactly that
    /// reason: neither can silently fall back to <see cref="GrtConfig.Current"/>.
    /// </summary>
    private static (GrtLoadDoc Doc, SmString String, List<string> Log) Import(GrtConfig cfg)
    {
        var log = new List<string>();
        SmString s = FirstString();
        RenderedTarget r = TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!);
        var doc = GrtLoadDoc.CreateMinimal("round trip",
            Path.Combine(Path.GetTempPath(), "roundtrip.grtload"));
        GrtShotGroupWriter.Add(doc, new ImportItem(s, r, 41.5), log, cfg);
        return (doc, s, log);
    }

    /// <summary>The shots GRT will keep once flyers are excluded (ruling F1), in written order.</summary>
    private static List<SmShot> Scoring(SmString s) => s.Shots.Where(sh => !sh.IsFlyer).ToList();

    private static GrtShotGroups.Options ReadBackAs(GrtConfig cfg)
    {
        var (refUnit, shootUnit) = GrtShotGroups.GrtDefaults(cfg);
        return new GrtShotGroups.Options(refUnit, shootUnit, ExcludeFlyers: true);
    }

    [Fact]
    public void EveryShotComesBackWhereItWent()
    {
        GrtConfig metric = Metric();
        var (doc, s, _) = Import(metric);

        var (groups, log) = GrtShotGroups.FromDoc(doc, ReadBackAs(metric));
        TargetGroup g = Assert.Single(groups);

        // FromDoc reports MOA offsets from the centroid, so compare like with like.
        // MoaMm owns the 29.0888 constant (ruling F3) — it is not restated here.
        double mmPerMoa = GrtShotGroups.MoaMm(s.DistanceMetres);
        List<SmShot> scoring = Scoring(s);
        double cx = scoring.Average(sh => sh.XMm), cy = scoring.Average(sh => sh.YMm);

        Assert.Equal(scoring.Count, g.Impacts.Count);
        foreach (var (shot, impact) in scoring.Zip(g.Impacts))
        {
            Assert.Equal((shot.XMm - cx) / mmPerMoa, impact.XMoa, 3);
            Assert.Equal((shot.YMm - cy) / mmPerMoa, impact.YMoa, 3);
        }
        Assert.DoesNotContain(log, l => l.Contains("skipped"));
    }

    [Fact]
    public void TheGroupKeepsItsRealSize()
    {
        GrtConfig metric = Metric();
        var (doc, s, _) = Import(metric);
        var (groups, _) = GrtShotGroups.FromDoc(doc, ReadBackAs(metric));
        TargetGroup g = groups.Single();

        double mmPerMoa = GrtShotGroups.MoaMm(s.DistanceMetres);
        List<SmShot> scoring = Scoring(s);

        double readWidthMm = (g.Impacts.Max(i => i.XMoa) - g.Impacts.Min(i => i.XMoa)) * mmPerMoa;
        double realWidthMm = scoring.Max(sh => sh.XMm) - scoring.Min(sh => sh.XMm);
        double readHeightMm = (g.Impacts.Max(i => i.YMoa) - g.Impacts.Min(i => i.YMoa)) * mmPerMoa;
        double realHeightMm = scoring.Max(sh => sh.YMm) - scoring.Min(sh => sh.YMm);

        // Tighter than "looks about right": a whole-number millimetre. A scale that were out
        // by an inch/mm confusion would be 25.4x adrift, and by a yard/metre one 1.09x.
        Assert.Equal(realWidthMm, readWidthMm, 1);
        Assert.Equal(realHeightMm, readHeightMm, 1);
    }

    [Fact]
    public void TheShootingDistanceSurvives()
    {
        GrtConfig metric = Metric();
        var (doc, s, _) = Import(metric);
        var (groups, _) = GrtShotGroups.FromDoc(doc, ReadBackAs(metric));
        Assert.Equal(s.DistanceMetres, groups.Single().DistanceM, 1);
    }

    [Fact]
    public void TheChargeSurvivesAsTheLadderStep()
    {
        GrtConfig metric = Metric();
        var (doc, _, _) = Import(metric);
        var (groups, _) = GrtShotGroups.FromDoc(doc, ReadBackAs(metric));
        Assert.Equal(41.5, groups.Single().ChargeGrains!.Value, 3);
    }

    [Fact]
    public void SightersAndRejectsComeBackAsFlyers()
    {
        var (doc, s, _) = Import(Metric());
        GrtShotGroup tab = doc.ShotGroups().Single();

        // Shots with no coordinates (IsInvalid => NaN) are never plotted, by the renderer or
        // by the writer, so the flyers that survive into the file are the sighters.
        Assert.Equal(s.Shots.Count(sh => !sh.IsInvalid && sh.IsFlyer),
                     tab.Groups.Single().Points.Count(p => p.Flyer && !p.PointOfAim));
        Assert.True(tab.Groups.Single().Points.Count(p => p.Flyer) > 0,
            "the fixture is supposed to contain sighters — this test would otherwise prove nothing");
    }

    // ---- task 9b: honour ShotMarker's group selection ----------------------------------

    /// <summary>Extreme spread — the maximum distance between any two of a group's points.
    /// ShotMarker's own <see cref="SmGroupStats.GroupSizeMm"/> is exactly this figure computed
    /// over the 19 selected shots' ground-truth millimetres (verified empirically while writing
    /// this test: the two agreed to 0 difference, not merely close), so it is also how "the
    /// same 336.4045mm" is computed on both sides of the round trip below.</summary>
    private static double ExtremeSpreadMm(IEnumerable<(double X, double Y)> points)
    {
        var pts = points.ToList();
        double max = 0;
        for (int i = 0; i < pts.Count; i++)
            for (int j = i + 1; j < pts.Count; j++)
            {
                double dx = pts[i].X - pts[j].X, dy = pts[i].Y - pts[j].Y;
                max = Math.Max(max, Math.Sqrt(dx * dx + dy * dy));
            }
        return max;
    }

    /// <summary>
    /// Task 9b test 6, the load-bearing proof this task exists for: with the group selection
    /// now honoured (<see cref="SmShot.InSelectedGroup"/> / <see cref="SmShot.IsFlyer"/>),
    /// GRT's own independent reader recomputes the same 336.4045mm extreme spread the device
    /// reported over its 19 selected shots — not the 406.5mm a naive "all 20 record shots"
    /// group would give.
    ///
    /// <para>Tolerance: 0.001mm (one micron), derived rather than tuned. <see
    /// cref="GrtShotGroupWriter"/> writes hits as continuous <c>double</c> fractions of the
    /// picture (<see cref="TargetProjection.ToFraction"/> never rounds), and
    /// <c>GrtShotGroups.FromDoc</c> recovers millimetres as <c>(fraction - origin) * w *
    /// (refMm / refPx)</c> where <c>refPx = (refP2X - refP1X) * w</c> — the same integer pixel
    /// width <c>w</c> both the writer and the reader use algebraically cancels out of the X
    /// axis entirely, leaving only IEEE double round-off (round-trip <c>"R"</c>-format XML
    /// serialisation is lossless). The Y axis is not immune in principle — it depends on the
    /// image's pixel <em>height</em> too, and width and height round to integer pixels
    /// independently — but empirically, printing the ground-truth and read-back figures side
    /// by side while designing this test showed them differing by 1.1e-13mm, i.e. double
    /// rounding noise, nothing structural. 0.001mm keeps three further orders of magnitude of
    /// headroom above that observed noise while remaining 70,000x tighter than the 70mm this
    /// task's fix is meant to prove (336.4mm vs. the old, wrong 406.5mm).</para>
    /// </summary>
    [Fact]
    public void TheExtremeSpreadAgreesWithTheDevicesSelectedGroupNotTheWholeString()
    {
        GrtConfig metric = Metric();
        var (doc, s, _) = Import(metric);
        Assert.Equal("M1 R2 TT11", s.Name); // the fixture's first string, per SmTarReaderTests
        List<SmShot> scoring = Scoring(s);
        Assert.Equal(19, scoring.Count); // the device's own selected group, not all 20 record shots

        double deviceMm = s.Stats!.GroupSizeMm!.Value;
        Assert.Equal(336.4045, deviceMm, 3);

        var (groups, _) = GrtShotGroups.FromDoc(doc, ReadBackAs(metric));
        TargetGroup g = groups.Single();
        double mmPerMoa = GrtShotGroups.MoaMm(s.DistanceMetres);
        double readBackMm = ExtremeSpreadMm(g.Impacts.Select(i => (i.XMoa * mmPerMoa, i.YMoa * mmPerMoa)));

        Assert.True(Math.Abs(readBackMm - deviceMm) < 0.001,
            $"expected {deviceMm}mm (device, 19 selected shots), GRT's own reader computed {readBackMm}mm");

        // The wrong number this task replaces: all 20 non-sighter shots, including the one
        // ShotMarker itself excluded. If this ever regressed back to "every record shot",
        // the read-back figure would land near here instead, not near deviceMm above.
        double allRecordShotsMm = ExtremeSpreadMm(
            s.Shots.Where(sh => !sh.IsSighter && !sh.IsInvalid).Select(sh => (sh.XMm, sh.YMm)));
        Assert.True(Math.Abs(allRecordShotsMm - 406.5) < 0.5);
        Assert.True(Math.Abs(readBackMm - allRecordShotsMm) > 1,
            "the read-back figure must not accidentally match the over-inclusive one");
    }

    /// <summary>
    /// Task 9b test 7: the measurement tab's velocity series — written by <see
    /// cref="GrtShotGroupWriter"/>'s F27 fix, which filters by the same <c>!IsFlyer</c> as the
    /// shot group — reproduces the device's own v_avg/v_sd/v_es for M1 R2 TT11's 19 selected
    /// shots. Sample standard deviation (divide by n-1, matching what the device's own
    /// v_sd — verified below by cross-check, not just against the brief's literal — implies)
    /// and max-minus-min extreme spread.
    ///
    /// <para>Tolerance: <c>GrtLoadDoc.AddMeasurement</c> (the toolkit's own code, not this
    /// plugin's, and not something task 9b may touch) writes each shot's velocity as
    /// <c>ToString("0.0")</c> — one decimal place. That is a real, fixed 0.1 m/s quantisation
    /// every velocity survives the round trip through, not a defect of this test. Over 19
    /// shots independently rounded by up to ±0.05, the average and sample sd it produces land
    /// within a few thousandths of the unrounded figures (observed: ~0.004 for both, while
    /// designing this test); the extreme spread is two independent ±0.05 roundings apart from
    /// the unrounded max−min, so up to ±0.1 (observed: ~0.02). 0.05 m/s for avg/sd and 0.15 m/s
    /// for es keep clear headroom above what was actually observed while staying far tighter
    /// than a wrong-shot-set mismatch would produce (the F27 regression this guards against —
    /// the whole string's v_avg/v_sd/v_es differ from the 19-shot group's by whole m/s, not
    /// hundredths).</para>
    /// </summary>
    [Fact]
    public void TheVelocitySeriesReproducesTheDevicesFigures()
    {
        GrtConfig metric = Metric();
        var (doc, s, _) = Import(metric);

        List<GrtShot> written = doc.Measurements().Single().Charges[0].Shots;
        var v = written.Select(sh => sh.VelocityMps).ToList();
        Assert.Equal(19, v.Count); // the device's own selected group, not all 20 record shots

        double avg = v.Average();
        double sd = Math.Sqrt(v.Sum(x => (x - avg) * (x - avg)) / (v.Count - 1));
        double es = v.Max() - v.Min();

        const double avgSdTolMps = 0.05, esTolMps = 0.15;

        Assert.True(Math.Abs(avg - 570.2487) < avgSdTolMps, $"avg={avg}");
        Assert.True(Math.Abs(sd - 6.6553) < avgSdTolMps, $"sd={sd}");
        Assert.True(Math.Abs(es - 24.0792) < esTolMps, $"es={es}");

        // Cross-check against the device's own stated figures carried on SmString.Stats, not
        // just literals in this test.
        Assert.True(Math.Abs(avg - s.Stats!.VelocityAvgMps!.Value) < avgSdTolMps);
        Assert.True(Math.Abs(sd - s.Stats!.VelocitySdMps!.Value) < avgSdTolMps);
        Assert.True(Math.Abs(es - s.Stats!.VelocityEsMps!.Value) < esTolMps);
    }

    // ---- the units are GRT's, not ours -------------------------------------------------

    /// <summary>
    /// A GRT install that displays reference distances in inches and ranges in yards reads the
    /// two unlabelled shot-group numbers in those units. Nothing in the .grtload says which unit
    /// they are in, so a writer that always emits millimetres and metres is read 25.4x and 1.09x
    /// adrift — a plausible-looking group with wrong measurements. Same shots, same picture,
    /// same answer.
    /// </summary>
    [Fact]
    public void AnImperialGrtReadsTheSameGroupBackAtTheSameScale()
    {
        GrtConfig imperial = FakeGrt("refdistance=in;range=yard;oal=in;velocity=ft/s");
        var (refUnit, shootUnit) = GrtShotGroups.GrtDefaults(imperial);
        Assert.Equal(RefUnit.Inch, refUnit);
        Assert.Equal(ShootUnit.Yards, shootUnit);

        GrtConfig metric = Metric();
        var (metricDoc, s, _) = Import(metric);
        var (imperialDoc, _, _) = Import(imperial);

        // The two files carry different numbers ... The 25.4 and 0.9144 are spelled out here
        // on purpose: reusing GrtUnits' constants would make this assertion agree with the
        // writer by construction instead of checking it against the definition of an inch
        // and a yard.
        GrtShotGroup mTab = metricDoc.ShotGroups().Single();
        GrtShotGroup iTab = imperialDoc.ShotGroups().Single();
        Assert.Equal(mTab.RefDistance / 25.4, iTab.RefDistance, 6);
        Assert.Equal(mTab.ShootDistance / 0.9144, iTab.ShootDistance, 6);

        // ... and each is read back by its own GRT to the same real-world group.
        var (mGroups, _) = GrtShotGroups.FromDoc(metricDoc, ReadBackAs(metric));
        var (iGroups, _) = GrtShotGroups.FromDoc(imperialDoc, ReadBackAs(imperial));

        Assert.Equal(mGroups.Single().DistanceM, iGroups.Single().DistanceM, 6);
        Assert.Equal(s.DistanceMetres, iGroups.Single().DistanceM, 3);
        foreach (var (a, b) in mGroups.Single().Impacts.Zip(iGroups.Single().Impacts))
        {
            Assert.Equal(a.XMoa, b.XMoa, 6);
            Assert.Equal(a.YMoa, b.YMoa, 6);
        }
    }

    /// <summary>
    /// An explicit metric GRT install — mm reference distances, metre shooting distances —
    /// constructed the same way <see cref="AnImperialGrtReadsTheSameGroupBackAtTheSameScale"/>
    /// builds its own imperial one. Every other test in this file uses this instead of the
    /// convenience of a null config, precisely so it does not silently become whatever GRT
    /// install (if any) happens to be sitting beside the machine running the suite (ruling F25).
    /// </summary>
    private static GrtConfig Metric() => FakeGrt("refdistance=mm;range=m;oal=mm;velocity=m/s");

    /// <summary>A GRT install whose only interesting setting is the one we read.</summary>
    private static GrtConfig FakeGrt(string valueUnits)
    {
        string dir = Path.Combine(Path.GetTempPath(), "grt-fake-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "GordonsReloadingTool.cfg"),
                "SomethingElse=1\nValueUnits=" + valueUnits + "\n");
            return GrtConfig.Load(dir)!;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
