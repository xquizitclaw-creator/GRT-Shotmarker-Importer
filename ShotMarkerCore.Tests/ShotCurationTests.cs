using GrtPluginKit.Grt;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Grt;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;
using Xunit;

namespace ShotMarker.Core.Tests;

/// <summary>
/// Curation: the shooter striking a shot out in the import window, before anything is
/// written. The flag is deliberately separate from <see cref="SmShot.IsExcludedOnDevice"/> —
/// what the device decided and what the shooter decided are different facts, and the note
/// has to be able to report them apart.
/// </summary>
public class ShotCurationTests
{
    private static SmString FirstString()
    {
        var log = new List<string>();
        return SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
    }

    /// <summary>The string with its first counting, velocity-carrying shot struck out,
    /// and that shot's number.</summary>
    private static (SmString Curated, int Number) WithOneStruckOut(SmString s)
    {
        SmShot target = s.Shots.First(sh => !sh.IsFlyer && sh.VelocityMps is > 0);
        var shots = s.Shots
            .Select(sh => sh.Number == target.Number ? sh with { IsExcludedByUser = true } : sh)
            .ToList();
        return (s with { Shots = shots }, target.Number);
    }

    private static ImportItem Item(SmString s) =>
        new(s, TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!), 41.5);

    private static GrtLoadDoc NewDoc() =>
        GrtLoadDoc.CreateMinimal("t", Path.Combine(Path.GetTempPath(), "t.grtload"));

    [Fact]
    public void AStruckOutShotIsAFlyer()
    {
        var shot = new SmShot(1, 10, 10, 800, "10", null, IsSighter: false, IsInvalid: false,
                              InSelectedGroup: true);
        Assert.False(shot.IsFlyer);
        Assert.True((shot with { IsExcludedByUser = true }).IsFlyer);
    }

    /// <summary>Struck out, not deleted: the hit is still plotted and still written to the
    /// group, flagged as a flyer. The shooter can see what they excluded, and GRT keeps it
    /// out of the group statistics.</summary>
    [Fact]
    public void AStruckOutShotIsStillPlottedButFlaggedAsAFlyer()
    {
        SmString s = FirstString();
        var (curated, _) = WithOneStruckOut(s);

        var before = NewDoc();
        GrtShotGroupWriter.Add(before, Item(s), new List<string>());
        var after = NewDoc();
        GrtShotGroupWriter.Add(after, Item(curated), new List<string>());

        List<GrtShotPoint> b = before.ShotGroups().Single().Groups.Single().Points.ToList();
        List<GrtShotPoint> a = after.ShotGroups().Single().Groups.Single().Points.ToList();

        Assert.Equal(b.Count, a.Count);
        Assert.Equal(b.Count(p => p.Flyer) + 1, a.Count(p => p.Flyer));
    }

    /// <summary>The velocity series uses the same predicate as the group (ruling F27), so
    /// striking a shot out has to drop its velocity too — otherwise GRT's SD disagrees with
    /// the group on screen.</summary>
    [Fact]
    public void AStruckOutShotsVelocityIsNotMeasured()
    {
        SmString s = FirstString();
        var (curated, _) = WithOneStruckOut(s);

        var before = NewDoc();
        GrtShotGroupWriter.Add(before, Item(s), new List<string>());
        var after = NewDoc();
        GrtShotGroupWriter.Add(after, Item(curated), new List<string>());

        int b = before.Measurements().Single().Charges[0].Shots.Count;
        int a = after.Measurements().Single().Charges[0].Shots.Count;
        Assert.Equal(b - 1, a);
    }

    /// <summary>The note must not let ShotMarker's own figures stand unqualified beside a
    /// shot set they were not computed over.</summary>
    [Fact]
    public void TheNoteReportsTheStrikeOutsAndQualifiesTheDeviceFigures()
    {
        SmString s = FirstString();
        var (curated, number) = WithOneStruckOut(s);

        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, Item(curated), new List<string>());
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            string text = NoteText(path);
            Assert.Contains("Excluded on import:", text);
            Assert.Contains($"shot {number}", text);
            Assert.Contains("for the full string, before the exclusions above", text);
        }
        finally { File.Delete(path); }
    }

    /// <summary>With nothing struck out the note is unchanged — no empty section, and the
    /// device's figures carry no qualifier they have not earned.</summary>
    [Fact]
    public void TheNoteSaysNothingWhenNothingWasStruckOut()
    {
        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, Item(FirstString()), new List<string>());
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            string text = NoteText(path);
            Assert.DoesNotContain("Excluded on import:", text);
            Assert.DoesNotContain("before the exclusions above", text);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The count line names device exclusions. A shot the shooter struck out is not
    /// one, and is reported on its own line — otherwise "sighter/invalid/excluded" would
    /// quietly absorb it and the two kinds of exclusion become indistinguishable.</summary>
    [Fact]
    public void TheDeviceExclusionCountIgnoresTheShootersOwn()
    {
        SmString s = FirstString();
        var (curated, _) = WithOneStruckOut(s);
        int devices = s.Shots.Count(sh => sh.IsSighter || sh.IsInvalid || sh.IsExcludedOnDevice);

        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, Item(curated), new List<string>());
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            Assert.Contains($"({devices} sighter/invalid/excluded)", NoteText(path));
        }
        finally { File.Delete(path); }
    }

    /// <summary>The banner's w/h are measured off the shots drawn and follow a strike-out;
    /// everything else on it came off the device and does not. The qualifier is what keeps
    /// those two shot sets from sitting on one line claiming to be one thing.</summary>
    [Fact]
    public void TheBannerQualifiesTheDeviceFiguresOnceAShotIsStruckOut()
    {
        SmString s = FirstString();
        var (curated, _) = WithOneStruckOut(s);

        Assert.DoesNotContain("excluded on import", TargetRenderer.StatsBanner(s));
        string banner = TargetRenderer.StatsBanner(curated);
        Assert.Contains("1 shot excluded on import", banner);
        Assert.Contains("SM figures above are the full string", banner);
    }

    /// <summary>The recomputed half of the banner has to actually move, or the qualifier is
    /// describing a line that did not change.</summary>
    [Fact]
    public void TheBannersMeasuredExtentFollowsAStrikeOut()
    {
        SmString s = FirstString();
        // Strike out every counting shot on the leftmost edge, so the measured width must
        // shrink. Every shot on the edge, not just one: this fixture has two at the same x,
        // and striking one of a tied pair moves nothing.
        double edge = s.Shots.Where(sh => !sh.IsFlyer).Min(sh => sh.XMm);
        var curated = s with
        {
            Shots = s.Shots.Select(sh =>
                !sh.IsFlyer && sh.XMm == edge ? sh with { IsExcludedByUser = true } : sh).ToList(),
        };

        static double Width(string banner) =>
            double.Parse(banner.Split("  ").First(p => p.StartsWith("w ")).Substring(2),
                         System.Globalization.CultureInfo.InvariantCulture);

        Assert.True(Width(TargetRenderer.StatsBanner(curated)) < Width(TargetRenderer.StatsBanner(s)));
    }

    private static string NoteText(string path)
    {
        var xml = new System.Xml.XmlDocument();
        xml.Load(path);
        var note = (System.Xml.XmlElement)xml.SelectSingleNode("//note")!;
        return Uri.UnescapeDataString(note.GetAttribute("text"));
    }
}
