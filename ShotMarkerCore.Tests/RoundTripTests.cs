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
    /// Every test below reads back as SI, because SI is what the writer emits: GRT stores the
    /// two unlabelled shot-group numbers in millimetres and metres whatever units it displays
    /// (see <see cref="GrtShotGroupWriter"/>). Neither half consults a GRT install any more,
    /// so neither can agree or disagree by accident of whichever install, if any, sits beside
    /// the machine running the suite (ruling F25).
    /// </summary>
    private static (GrtLoadDoc Doc, SmString String, List<string> Log) Import()
    {
        var log = new List<string>();
        SmString s = FirstString();
        RenderedTarget r = TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!);
        var doc = GrtLoadDoc.CreateMinimal("round trip",
            Path.Combine(Path.GetTempPath(), "roundtrip.grtload"));
        GrtShotGroupWriter.Add(doc, new ImportItem(s, r, 41.5), log);
        return (doc, s, log);
    }

    /// <summary>The shots GRT will keep once flyers are excluded (ruling F1), in written order.</summary>
    private static List<SmShot> Scoring(SmString s) => s.Shots.Where(sh => !sh.IsFlyer).ToList();

    /// <summary>The file as the writer left it: millimetres, metres, flyers excluded.</summary>
    private static GrtShotGroups.Options AsWritten { get; } =
        new(RefUnit.Mm, ShootUnit.Meters, ExcludeFlyers: true);

    [Fact]
    public void EveryShotComesBackWhereItWent()
    {
        var (doc, s, _) = Import();

        var (groups, log) = GrtShotGroups.FromDoc(doc, AsWritten);
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
        var (doc, s, _) = Import();
        var (groups, _) = GrtShotGroups.FromDoc(doc, AsWritten);
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
        var (doc, s, _) = Import();
        var (groups, _) = GrtShotGroups.FromDoc(doc, AsWritten);
        Assert.Equal(s.DistanceMetres, groups.Single().DistanceM, 1);
    }

    [Fact]
    public void TheChargeSurvivesAsTheLadderStep()
    {
        var (doc, _, _) = Import();
        var (groups, _) = GrtShotGroups.FromDoc(doc, AsWritten);
        Assert.Equal(41.5, groups.Single().ChargeGrains!.Value, 3);
    }

    /// <summary>What a GRT flyer is, after the sighters stopped being written as one: a record
    /// shot the device or the shooter left out of the group. A sighter is not in the file at all
    /// to be labelled — "Flyer #1" over the shooter's first Match 1 sighter is the defect this
    /// pair of assertions pins shut from both ends.</summary>
    [Fact]
    public void RejectsComeBackAsFlyersAndSightersDoNotComeBackAtAll()
    {
        var (doc, s, _) = Import();
        GrtShotGroupSet set = doc.ShotGroups().Single().Groups.Single();

        // Shots with no coordinates (IsInvalid => NaN) are never plotted, by the renderer or by
        // the writer, and nor is a sighter — so what is left to come back as a flyer is a record
        // shot ShotMarker's own group left out. The fixture has exactly one: shot 11.
        Assert.Equal(s.Shots.Count(sh => sh.IsImported && sh.IsFlyer),
                     set.Points.Count(p => p.Flyer && !p.PointOfAim));
        Assert.True(set.Points.Count(p => p.Flyer) > 0,
            "the fixture is supposed to contain a shot left out of the group — this test would "
            + "otherwise prove nothing");

        // And the sighters are simply gone: five of them, 25 shots read, 20 hits written.
        Assert.Equal(5, s.Shots.Count(sh => sh.IsSighter));
        Assert.Equal(s.Shots.Count(sh => !sh.IsSighter), set.Points.Count);
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
        var (doc, s, _) = Import();
        Assert.Equal("M1 R2 TT11", s.Name); // the fixture's first string, per SmTarReaderTests
        List<SmShot> scoring = Scoring(s);
        Assert.Equal(19, scoring.Count); // the device's own selected group, not all 20 record shots

        double deviceMm = s.Stats!.GroupSizeMm!.Value;
        Assert.Equal(336.4045, deviceMm, 3);

        var (groups, _) = GrtShotGroups.FromDoc(doc, AsWritten);
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
        var (doc, s, _) = Import();

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
    /// The shooting distance is written in metres however the GRT reading it displays
    /// distances, because that is how GRT itself writes the field: a tab from an install
    /// configured <c>range=yard</c> holds <c>shootDistance="914.4"</c> for a 1000 yd string.
    /// GRT converts on the way to the screen, not on the way to the file.
    ///
    /// <para>This test used to assert the opposite — that an imperial install got yards — and
    /// a real one then showed a 1000 yd string as 1093.61 yd, because the writer divided by
    /// 0.9144 and GRT divided again. The reference distance went the same way at 25.4x, and
    /// that number is the scale every group measurement GRT makes is taken against.</para>
    ///
    /// <para>So there is no install to write with any more, and this asserts against the
    /// definition of a yard rather than against <see cref="GrtUnits.MetresPerYard"/>, which
    /// would only agree with the writer by construction. The companion assertion for the
    /// reference distance is
    /// <see cref="GrtShotGroupWriterTests.TheReferencePointsRecoverThePicturesOwnScale"/>,
    /// which recovers the picture's own mm-per-pixel from it.</para>
    ///
    /// <para>Reading it back needs the same SI, which is why <see cref="AsWritten"/> is fixed.
    /// <see cref="GrtShotGroups.ShootToM"/> in GRT-Reloading-Toolkit instead converts this
    /// field by the install's own units, so that reader takes an imperial install 1.09x adrift
    /// from the file GRT wrote — a bug in a different project, and one this writer used to
    /// match.</para>
    /// </summary>
    [Fact]
    public void TheDistanceIsWrittenInMetresWhateverTheInstallDisplays()
    {
        // The install this would once have been written for, kept to name what no longer
        // reaches the writer: inches and yards on screen, SI in the file all the same.
        GrtConfig imperial = FakeGrt("refdistance=in;range=yard;oal=in;velocity=ft/s");
        var (refUnit, shootUnit) = GrtShotGroups.GrtDefaults(imperial);
        Assert.Equal(RefUnit.Inch, refUnit);
        Assert.Equal(ShootUnit.Yards, shootUnit);

        var (doc, s, _) = Import();
        Assert.Equal(1000, s.DistanceValue);
        Assert.Equal("y", s.DistanceUnit);

        GrtShotGroup tab = doc.ShotGroups().Single();
        Assert.Equal(1000 * 0.9144, tab.ShootDistance, 6);

        var (groups, _) = GrtShotGroups.FromDoc(doc, AsWritten);
        Assert.Equal(s.DistanceMetres, groups.Single().DistanceM, 6);
    }

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
