using ShotMarker.Core.Sm;
using Xunit;

namespace ShotMarker.Core.Tests;

public class SmExportReaderTests
{
    [Fact]
    public void DispatchesOnFileType()
    {
        var log = new List<string>();
        Assert.NotEmpty(SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log));
        Assert.NotEmpty(SmExportReader.Read(Fixtures.Path("shotmarker/SM_shotslog_Sep_21.csv"), log));
    }

    [Fact]
    public void AnUnsupportedFileIsReportedNotThrown()
    {
        var log = new List<string>();
        string path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".txt");
        File.WriteAllText(path, "not an export");
        try
        {
            Assert.Empty(SmExportReader.Read(path, log));
            Assert.NotEmpty(log);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// The two fixtures do NOT contain the same set of strings: the CSV's "R1 TT11" blocks
    /// each split into two SmStrings (interleaved rifles, see SmCsvReader's remarks), so it
    /// yields 9 strings against the tar's 3. The only strings present in both are
    /// "M1 R2 TT11", "M2 R2 TT11" and "M3 R2 TT11" — verified directly against both fixtures
    /// (the brief's "M6 R1 TT11" is a .tar/.csv naming mismatch the brief did not anticipate;
    /// see task-6-reference.md, which names "M1 R2 TT11" in its ground-truth table).
    ///
    /// Comparison is by string Name intersected across formats, and by shot Number within
    /// that string — never by array index or by asserting equal string/shot counts overall,
    /// since that is not a property either format guarantees (see task-6-reference.md).
    /// IsInvalid shots are filtered out before touching X/Y: the tar marks errored/fake
    /// shots with NaN coordinates and IsInvalid = true, while the CSV has no per-shot invalid
    /// marker and always reports IsInvalid = false, so an unfiltered NaN-vs-real comparison
    /// would silently pass.
    /// </summary>
    [Fact]
    public void TheTwoFormatsAgreeOnTheStringTheyShare()
    {
        var log = new List<string>();
        var tar = SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log);
        var csv = SmExportReader.Read(Fixtures.Path("shotmarker/SM_shotslog_Sep_21.csv"), log);

        SmString? t = tar.FirstOrDefault(s => s.Name == "M1 R2 TT11");
        SmString? c = csv.FirstOrDefault(s => s.Name == "M1 R2 TT11");
        Assert.NotNull(t);
        Assert.NotNull(c);

        List<SmShot> tShots = t!.Shots.Where(s => !s.IsInvalid).OrderBy(s => s.Number).ToList();
        List<SmShot> cShots = c!.Shots.Where(s => !s.IsInvalid).OrderBy(s => s.Number).ToList();

        Dictionary<int, SmShot> byNumber = cShots.ToDictionary(s => s.Number);

        Assert.NotEmpty(tShots);
        foreach (SmShot a in tShots)
        {
            Assert.True(byNumber.TryGetValue(a.Number, out SmShot? b), $"shot {a.Number}: no matching CSV shot");

            // Coordinate tolerance 0.5 mm: the CSV is pre-rounded to whole millimetres,
            // the tar carries one decimal.
            Assert.Equal(a.XMm, b!.XMm, 0.5);
            Assert.Equal(a.YMm, b.YMm, 0.5);

            if (a.VelocityMps is { } av && b.VelocityMps is { } bv)
                Assert.Equal(av, bv, 0.1);
        }
    }
}
