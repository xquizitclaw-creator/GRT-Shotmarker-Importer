using GrtPluginKit.Grt;
using ShotMarker.Core.Import;
using ShotMarker.Core.Sm;
using Xunit;

namespace ShotMarker.Core.Tests;

public class ImportJobTests
{
    private static string TempLoad(string dir)
    {
        string path = Path.Combine(dir, "Krieger-2 284 Shehane.grtload");
        GrtLoadDoc.CreateMinimal("284 Shehane", path).Save(path);
        return path;
    }

    [Fact]
    public void PlanListsEveryStringInTheExport()
    {
        var log = new List<string>();
        var strings = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log);
        Assert.Equal(3, strings.Count);
    }

    [Fact]
    public void RunWritesASiblingAndLeavesTheOriginalAlone()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string load = TempLoad(dir);
            byte[] before = File.ReadAllBytes(load);
            var log = new List<string>();

            var strings = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log);
            string outPath = ImportJob.Run(load, strings.Select(s => (s, (double?)41.5)), log);

            Assert.NotEqual(load, outPath);
            Assert.True(File.Exists(outPath));
            Assert.Contains("_shotmarker_", Path.GetFileName(outPath));
            Assert.Equal(before, File.ReadAllBytes(load));
            Assert.Equal(3, GrtLoadDoc.Load(outPath).ShotGroups().Count());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void OneUnreadableStringDoesNotFailTheBatch()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string load = TempLoad(dir);
            var log = new List<string>();
            var good = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
            var empty = good with { Shots = Array.Empty<SmShot>() };

            string outPath = ImportJob.Run(load,
                new[] { (empty, (double?)41.0), (good, (double?)41.5) }, log);

            Assert.Single(GrtLoadDoc.Load(outPath).ShotGroups());
            Assert.Contains(log, l => l.Contains("no shots"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AnUnknownFaceFallsBackToAPlainPlotWithAWarning()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string load = TempLoad(dir);
            var log = new List<string>();
            var s = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First()
                    with { FaceId = "NO_SUCH_FACE" };

            string outPath = ImportJob.Run(load, new[] { (s, (double?)null) }, log);

            Assert.Single(GrtLoadDoc.Load(outPath).ShotGroups());
            Assert.Contains(log, l => l.Contains("NO_SUCH_FACE"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    /// <summary>
    /// The WinForms shell (task 11) pairs a ticked grid row with its charge by carrying the
    /// <c>SmString</c> on the row rather than by list position, because the grid can be
    /// re-sorted by the user and a positional pairing would then attach the wrong charge to
    /// the wrong string. That contract is really "the (SmString, charge) tuple ImportJob.Run
    /// receives determines the pairing, not Plan()'s original order" — provable here even
    /// though the form itself is not unit-testable.
    /// </summary>
    [Fact]
    public void ChargesLandOnTheRightStringEvenOutOfPlansOriginalOrder()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string load = TempLoad(dir);
            var log = new List<string>();
            var strings = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log);
            Assert.True(strings.Count >= 2, "fixture needs at least two strings for this test to mean anything");

            var chargeByName = strings
                .Select((s, i) => (s.Name, Charge: 40.0 + i))
                .ToDictionary(x => x.Name, x => x.Charge);

            // Selected in the REVERSE of Plan()'s order — a positional pairing (row index i ->
            // strings[i]) would silently swap the charges here.
            var selected = strings
                .Reverse()
                .Select(s => (s, (double?)chargeByName[s.Name]))
                .ToList();

            string outPath = ImportJob.Run(load, selected, log);
            var doc = GrtLoadDoc.Load(outPath);

            foreach (SmString s in strings)
            {
                GrtShotGroup group = doc.ShotGroups().Single(g => g.Title == $"ShotMarker — {s.Name}");
                string expected = chargeByName[s.Name].ToString("0.0#", System.Globalization.CultureInfo.InvariantCulture) + " gr";
                Assert.Equal(expected, group.Groups.Single().Name);
            }
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void WithNoLoadToWriteIntoOneIsCreated()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var log = new List<string>();
            var s = ImportJob.Plan(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
            string outPath = ImportJob.Run(Path.Combine(dir, "nothing-here.grtload"),
                new[] { (s, (double?)41.5) }, log);
            Assert.True(File.Exists(outPath));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
