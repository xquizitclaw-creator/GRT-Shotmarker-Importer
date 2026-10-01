using GrtPluginKit.Grt;
using GrtReloadingToolkit.Ocw;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Grt;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;
using Xunit;

namespace ShotMarker.Core.Tests;

public class GrtShotGroupWriterTests
{
    private static SmString FirstString()
    {
        var log = new List<string>();
        return SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
    }

    private static ImportItem Item(double? charge = 41.5) => Item(FirstString(), charge);

    private static ImportItem Item(SmString s, double? charge = 41.5) =>
        new(s, TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!), charge);

    private static GrtLoadDoc NewDoc() =>
        GrtLoadDoc.CreateMinimal("t", Path.Combine(Path.GetTempPath(), "t.grtload"));

    [Fact]
    public void WritesATabAMeasurementAndANote()
    {
        var doc = NewDoc();
        var log = new List<string>();
        GrtShotGroupWriter.Add(doc, Item(), log);

        Assert.Single(doc.ShotGroups());
        GrtMeasurement m = Assert.Single(doc.Measurements());
        Assert.Single(m.Charges);
        Assert.NotEmpty(m.Charges[0].Shots);
    }

    [Fact]
    public void TheChargeIsNamedAndWeighedFromTheImport()
    {
        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, Item(41.5), new List<string>());
        GrtCharge c = doc.Measurements().Single().Charges[0];
        Assert.Equal("41.5 gr", c.Name);
        Assert.Equal(41.5, c.ChargeGrains!.Value, 3);
    }

    [Fact]
    public void TheChargeWeightAlsoSurvivesWithoutItsName()
    {
        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, Item(41.5), new List<string>());
        GrtCharge c = doc.Measurements().Single().Charges[0];
        // ChargeGrains prefers the name; force the value path GRT itself uses.
        var valueOnly = new GrtCharge { Name = "", ValueKg = c.ValueKg };
        Assert.Equal(41.5, valueOnly.ChargeGrains!.Value, 3);
    }

    [Fact]
    public void AStringWithoutVelocitiesStillImportsItsPositions()
    {
        SmString full = FirstString();
        var stripped = full with
        {
            Shots = full.Shots.Select(sh => sh with { VelocityMps = null }).ToList(),
        };

        var doc = NewDoc();
        var log = new List<string>();
        GrtShotGroupWriter.Add(doc, Item(stripped), log);

        Assert.Single(doc.ShotGroups());
        Assert.Empty(doc.Measurements());
        Assert.Contains(log, l => l.Contains("no velocities"));
    }

    [Fact]
    public void AnEmptyStringIsNotImportedAtAll()
    {
        SmString empty = FirstString() with { Shots = new List<SmShot>() };
        var doc = NewDoc();
        var log = new List<string>();
        GrtShotGroupWriter.Add(doc, Item(empty), log);

        Assert.Empty(doc.ShotGroups());
        Assert.Empty(doc.Measurements());
        Assert.Contains(log, l => l.Contains("no shots"));
    }

    [Fact]
    public void ASavedLoadIsWellFormedXml()
    {
        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, Item(), new List<string>());
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            var xml = new System.Xml.XmlDocument();
            xml.Load(path);           // throws if malformed
            Assert.Single(GrtLoadDoc.Load(path).ShotGroups());
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Generated loads use the plugin's own sibling family. The toolkit prunes the "toolkit"
    /// family to its three newest, so sharing it would let the toolkit delete this plugin's
    /// output (and the other way round).
    /// </summary>
    [Fact]
    public void ASiblingIsSavedIntoTheShotmarkerFamilyNotTheToolkitOne()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sm-sib-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var doc = GrtLoadDoc.CreateMinimal("t", Path.Combine(dir, "MyLoad.grtload"));
            GrtShotGroupWriter.Add(doc, Item(), new List<string>());
            string outPath = GrtShotGroupWriter.Save(doc);

            Assert.Equal("shotmarker", GrtShotGroupWriter.SiblingFamily);
            Assert.Contains("_shotmarker_", Path.GetFileName(outPath));
            Assert.DoesNotContain("_toolkit_", Path.GetFileName(outPath));
            Assert.True(GrtLoadDoc.LooksLikeGeneratedSibling(outPath));
            Assert.Single(GrtLoadDoc.Load(outPath).ShotGroups());
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ---- the NaN invariant -------------------------------------------------------------

    /// <summary>
    /// Errored and "fake" shots carry <c>double.NaN</c> coordinates. GrtLoadDoc formats every
    /// number with <c>ToString("R")</c>, which writes the literal <c>NaN</c> without complaint,
    /// and nothing downstream catches it: GRT's own reader parses it back as 0 and silently
    /// plants a hit in the top-left corner of the picture. So the guarantee has to be proved on
    /// the serialised bytes, not on the object graph.
    /// </summary>
    [Fact]
    public void NoNaNCanReachAWrittenLoad()
    {
        SmString real = FirstString();
        var shots = real.Shots.ToList();
        shots.Add(new SmShot(shots.Count + 1, double.NaN, double.NaN, double.NaN,
            null, null, IsSighter: false, IsInvalid: true, InSelectedGroup: null));
        shots.Add(new SmShot(shots.Count + 1, double.NaN, double.NaN, 812.5,
            "X", null, IsSighter: true, IsInvalid: true, InSelectedGroup: null));
        SmString poisoned = real with { Shots = shots };

        var doc = NewDoc();
        var log = new List<string>();
        GrtShotGroupWriter.Add(doc, Item(poisoned), log);

        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            foreach (var (owner, name, value) in Attributes(path))
            {
                // The picture is base64, in whose alphabet "NaN" turns up by pure chance —
                // this assertion found that on its first run. Numbers are what matter, and
                // "NaN"/"Infinity" is exactly how ToString("R") and ToString("0.0") spell a
                // non-finite double in the invariant culture.
                Assert.DoesNotContain("NaN", value, StringComparison.Ordinal);
                Assert.DoesNotContain("Infinity", value, StringComparison.Ordinal);
                if (owner is "point" or "ShotGroup" or "shot" && double.TryParse(value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double n))
                    Assert.True(double.IsFinite(n), $"<{owner} {name}=\"{value}\">");
            }

            GrtShotGroup tab = GrtLoadDoc.Load(path).ShotGroups().Single();
            Assert.True(double.IsFinite(tab.RefDistance) && tab.RefDistance > 0);
            Assert.True(double.IsFinite(tab.ShootDistance) && tab.ShootDistance > 0);
            foreach (double v in new[] { tab.RefP1X, tab.RefP1Y, tab.RefP2X, tab.RefP2Y })
                Assert.True(double.IsFinite(v));
            foreach (GrtShotPoint p in tab.Groups.Single().Points)
            {
                Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y));
                Assert.InRange(p.X, 0, 1);
                Assert.InRange(p.Y, 0, 1);
            }

            // The two coordinate-less shots are absent from the picture's hits, and said so.
            Assert.Equal(real.Shots.Count(sh => !sh.IsInvalid),
                         tab.Groups.Single().Points.Count);
            Assert.Contains(log, l => l.Contains("no coordinates"));
        }
        finally { File.Delete(path); }
    }

    /// <summary>Every attribute in a saved load, except the base64 picture payloads.</summary>
    private static IEnumerable<(string Owner, string Name, string Value)> Attributes(string path)
    {
        var xml = new System.Xml.XmlDocument();
        xml.Load(path);
        foreach (System.Xml.XmlNode node in xml.SelectNodes("//*")!)
            foreach (System.Xml.XmlAttribute a in node.Attributes!)
                if (!(node.Name == "picture" && a.Name == "data"))
                    yield return (node.Name, a.Name, a.Value);
    }

    /// <summary>
    /// A NaN velocity must not reach the measurement either: GrtLoadDoc writes velocities with
    /// <c>ToString("0.0")</c>, which is just as happy to emit <c>NaN</c>.
    /// </summary>
    [Fact]
    public void ANaNVelocityNeverReachesTheMeasurement()
    {
        SmString real = FirstString();
        var shots = real.Shots
            .Select(sh => sh with { VelocityMps = double.NaN })
            .Append(new SmShot(999, 10, 10, 800, "10", null, false, false, true))
            .ToList();

        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, Item(real with { Shots = shots }), new List<string>());

        List<GrtShot> written = doc.Measurements().Single().Charges[0].Shots;
        Assert.Single(written);
        Assert.Equal(800, written[0].VelocityMps, 3);
    }

    /// <summary>
    /// F28: the note's "Shots: N (K sighter/invalid/excluded)" label must count exactly what
    /// it names. Task 9b widened <see cref="SmShot.IsFlyer"/> to also cover a valid record shot
    /// ShotMarker's own group left out, so counting <c>IsFlyer</c> here (as the line used to)
    /// would claim M1 R2 TT11 has six such shots when it has five. That sixth shot is shot 11 —
    /// genuine and scoring — already reported honestly, and separately, by the
    /// "{inGroup} of {recordShots} record shots" line pinned below. Device-excluded shots ARE
    /// counted here, because that second line only exists when the export carries a group.
    /// </summary>
    [Fact]
    public void TheShotCountLabelCountsExactlyWhatItNames()
    {
        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, Item(), new List<string>());
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            string text = NoteText(path);
            Assert.Contains("(5 sighter/invalid/excluded)", text);
            Assert.Contains("19 of 20 record shots", text);
        }
        finally { File.Delete(path); }
    }

    /// <summary>The decoded text of the single &lt;note&gt; in a saved load.</summary>
    private static string NoteText(string path)
    {
        var xml = new System.Xml.XmlDocument();
        xml.Load(path);
        var note = (System.Xml.XmlElement)xml.SelectSingleNode("//note")!;
        return Uri.UnescapeDataString(note.GetAttribute("text"));
    }

    // ---- the unit conversions are exact inverses of the reader's -----------------------

    [Theory]
    [InlineData(RefUnit.Mm)]
    [InlineData(RefUnit.Cm)]
    [InlineData(RefUnit.Inch)]
    public void RefConversionIsTheInverseOfTheReaders(RefUnit u)
    {
        const double mm = 1234.5678;
        Assert.Equal(mm, GrtShotGroups.RefToMm(GrtShotGroupWriter.MmToRef(mm, u), u), 9);
    }

    [Theory]
    [InlineData(ShootUnit.Meters)]
    [InlineData(ShootUnit.Yards)]
    public void ShootConversionIsTheInverseOfTheReaders(ShootUnit u)
    {
        const double m = 914.4;
        Assert.Equal(m, GrtShotGroups.ShootToM(GrtShotGroupWriter.MToShoot(m, u), u), 9);
    }

    /// <summary>
    /// The reference points bracket the picture's own horizontal midline, so the millimetres
    /// between them are a number the projection already knows exactly — and GRT recovers the
    /// picture's true mm-per-pixel from them whatever unit it reads the distance in.
    ///
    /// <para>Writes with an explicit metric config rather than the default (<c>cfg: null</c>,
    /// which falls back to <see cref="GrtConfig.Current"/> — whatever real GRT install, if any,
    /// happens to sit beside the machine running the suite) so the <see cref="RefUnit.Mm"/>
    /// below is pinned rather than a hope (ruling F25).</para>
    /// </summary>
    [Fact]
    public void TheReferencePointsRecoverThePicturesOwnScale()
    {
        ImportItem item = Item();
        var doc = NewDoc();
        GrtShotGroupWriter.Add(doc, item, new List<string>(), MetricGrt());
        GrtShotGroup tab = doc.ShotGroups().Single();

        Assert.Equal(tab.RefP1Y, tab.RefP2Y, 12);     // level: no vertical component at all
        double refPx = (tab.RefP2X - tab.RefP1X) * tab.ImageWidth;
        double mmPerPx = GrtShotGroups.RefToMm(tab.RefDistance, RefUnit.Mm) / refPx;

        TargetProjection p = item.Render.Projection;
        Assert.Equal(p.WidthMm / p.PixelWidth, mmPerPx, 9);
        Assert.Equal(tab.ImageWidth, p.PixelWidth);
    }

    /// <summary>
    /// Every string needs its own picture — a GRT shot group holds one — but GRT's tab bar
    /// does not scroll, so a tab past the right-hand edge of the window cannot be reached by
    /// any means. Three tabs per string put the back half of a session's work out of sight;
    /// one per string plus a shared measurement and a shared note keeps it on screen.
    /// </summary>
    [Fact]
    public void AMultiStringImportAddsOneTabPerStringPlusOneMeasurementAndOneNote()
    {
        var log = new List<string>();
        List<SmString> strings = SmExportReader.Read(
            Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).ToList();
        Assert.True(strings.Count > 1, "fixture has only one string — this proves nothing");

        var doc = NewDoc();
        GrtShotGroupWriter.AddAll(doc, strings.Select(s => Item(s)), log);

        Assert.Equal(strings.Count, doc.ShotGroups().Count());
        GrtMeasurement m = Assert.Single(doc.Measurements());
        Assert.Equal(strings.Count, m.Charges.Count);
        Assert.Equal(1, NoteCount(doc));
    }

    /// <summary>One string keeps the three tabs and the titles it has always had, so the
    /// collecting above costs the common case nothing.</summary>
    [Fact]
    public void ASingleStringImportKeepsItsOwnName()
    {
        SmString s = FirstString();
        var doc = NewDoc();
        GrtShotGroupWriter.AddAll(doc, new[] { Item(s) }, new List<string>());

        // The measurement's title comes back with a "-1" suffix: the kit's UniqueTitle sees
        // the shot-group tab has already taken the bare name. Long-standing, and what the
        // shooter has always seen — the point here is that it is still the STRING's name and
        // not the generic one a multi-string import falls back to.
        string title = $"ShotMarker — {s.Name}";
        Assert.Equal(title, doc.ShotGroups().Single().Title);
        Assert.StartsWith(title, doc.Measurements().Single().Title);
    }

    /// <summary>A string the writer chokes on must not cost the shooter the others — the same
    /// best-effort promise ImportJob makes for reading and rendering.</summary>
    [Fact]
    public void AStringWithNoPlottableShotsDoesNotStopTheRest()
    {
        var log = new List<string>();
        SmString good = FirstString();
        SmString bad = good with
        {
            Name = "no coordinates",
            Shots = good.Shots.Select(sh => sh with { XMm = double.NaN, YMm = double.NaN }).ToList(),
        };

        var doc = NewDoc();
        GrtShotGroupWriter.AddAll(doc, new[] { Item(bad), Item(good) }, log);

        Assert.Single(doc.ShotGroups());
        Assert.Equal($"ShotMarker — {good.Name}", doc.ShotGroups().Single().Title);
        Assert.Contains(log, l => l.Contains("no shot has coordinates"));
    }

    /// <summary>The note has to say which shots were left off the picture. GRT prints its own
    /// number beside every hit it holds, counting from one, and that is the only numbering on
    /// the imported picture — so a dropped shot silently renumbers everything after it.</summary>
    [Fact]
    public void TheNoteNamesTheShotsThatCouldNotBePlotted()
    {
        SmString s = FirstString();
        int lost = s.Shots[2].Number;
        SmString holed = s with
        {
            Shots = s.Shots
                .Select(sh => sh.Number == lost ? sh with { XMm = double.NaN } : sh)
                .ToList(),
        };

        var doc = NewDoc();
        GrtShotGroupWriter.AddAll(doc, new[] { Item(holed) }, new List<string>());
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            var xml = new System.Xml.XmlDocument();
            xml.Load(path);
            string text = Uri.UnescapeDataString(
                ((System.Xml.XmlElement)xml.SelectSingleNode("//note")!).GetAttribute("text"));
            Assert.Contains($"Not plotted: shot {lost}", text);
            Assert.Contains("no longer match the shot numbers above", text);
        }
        finally { File.Delete(path); }
    }

    private static int NoteCount(GrtLoadDoc doc)
    {
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".grtload");
        try
        {
            doc.Save(path);
            var xml = new System.Xml.XmlDocument();
            xml.Load(path);
            return xml.SelectNodes("//note")!.Count;
        }
        finally { File.Delete(path); }
    }

    /// <summary>An explicit metric GRT install, so a test that asserts against a hardcoded
    /// <see cref="RefUnit.Mm"/> or <see cref="ShootUnit.Meters"/> is not merely hoping that the
    /// machine running the suite has no real GRT install of its own (ruling F25).</summary>
    private static GrtConfig MetricGrt()
    {
        string dir = Path.Combine(Path.GetTempPath(), "grt-fake-" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "GordonsReloadingTool.cfg"),
                "ValueUnits=refdistance=mm;range=m;oal=mm;velocity=m/s\n");
            return GrtConfig.Load(dir)!;
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }
}
