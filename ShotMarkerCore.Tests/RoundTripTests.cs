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

    private static (GrtLoadDoc Doc, SmString String, List<string> Log) Import(GrtConfig? cfg = null)
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

    private static GrtShotGroups.Options ReadBackAs(GrtConfig? cfg)
    {
        var (refUnit, shootUnit) = GrtShotGroups.GrtDefaults(cfg);
        return new GrtShotGroups.Options(refUnit, shootUnit, ExcludeFlyers: true);
    }

    [Fact]
    public void EveryShotComesBackWhereItWent()
    {
        var (doc, s, _) = Import();

        var (groups, log) = GrtShotGroups.FromDoc(doc, ReadBackAs(null));
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
        var (groups, _) = GrtShotGroups.FromDoc(doc, ReadBackAs(null));
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
        var (groups, _) = GrtShotGroups.FromDoc(doc, ReadBackAs(null));
        Assert.Equal(s.DistanceMetres, groups.Single().DistanceM, 1);
    }

    [Fact]
    public void TheChargeSurvivesAsTheLadderStep()
    {
        var (doc, _, _) = Import();
        var (groups, _) = GrtShotGroups.FromDoc(doc, ReadBackAs(null));
        Assert.Equal(41.5, groups.Single().ChargeGrains!.Value, 3);
    }

    [Fact]
    public void SightersAndRejectsComeBackAsFlyers()
    {
        var (doc, s, _) = Import();
        GrtShotGroup tab = doc.ShotGroups().Single();

        // Shots with no coordinates (IsInvalid => NaN) are never plotted, by the renderer or
        // by the writer, so the flyers that survive into the file are the sighters.
        Assert.Equal(s.Shots.Count(sh => !sh.IsInvalid && sh.IsFlyer),
                     tab.Groups.Single().Points.Count(p => p.Flyer && !p.PointOfAim));
        Assert.True(tab.Groups.Single().Points.Count(p => p.Flyer) > 0,
            "the fixture is supposed to contain sighters — this test would otherwise prove nothing");
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

        var (metricDoc, s, _) = Import(null);
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
        var (mGroups, _) = GrtShotGroups.FromDoc(metricDoc, ReadBackAs(null));
        var (iGroups, _) = GrtShotGroups.FromDoc(imperialDoc, ReadBackAs(imperial));

        Assert.Equal(mGroups.Single().DistanceM, iGroups.Single().DistanceM, 6);
        Assert.Equal(s.DistanceMetres, iGroups.Single().DistanceM, 3);
        foreach (var (a, b) in mGroups.Single().Impacts.Zip(iGroups.Single().Impacts))
        {
            Assert.Equal(a.XMoa, b.XMoa, 6);
            Assert.Equal(a.YMoa, b.YMoa, 6);
        }
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
