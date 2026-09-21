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
