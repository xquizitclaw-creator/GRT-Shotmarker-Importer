using ShotMarker.Core.Sm;
using Xunit;

namespace ShotMarker.Core.Tests;

public class SmCsvReaderTests
{
    private static IReadOnlyList<SmString> Read(out List<string> log)
    {
        log = new List<string>();
        using var r = new StreamReader(Fixtures.Path("shotmarker/SM_shotslog_Sep_21.csv"));
        return SmCsvReader.Read(r, log);
    }

    [Fact]
    public void ReadsTheHeaderMetadataOfEachString()
    {
        var s = Read(out _).First();
        Assert.Equal("M6 R1 TT11", s.Name);
        Assert.Equal(1000, s.DistanceValue, 0);
        Assert.Equal("y", s.DistanceUnit);
        Assert.True(s.FrameWidthMm > 0);
    }

    [Fact]
    public void ConvertsVelocityFromFeetPerSecondToMetresPerSecond()
    {
        var v = Read(out _).SelectMany(s => s.Shots)
                           .Where(sh => sh.VelocityMps is > 0)
                           .Select(sh => sh.VelocityMps!.Value).ToList();
        Assert.NotEmpty(v);
        // The CSV column is fps; leaving it unconverted would put these near 2700.
        Assert.All(v, x => Assert.InRange(x, 400, 1200));
    }

    [Fact]
    public void MarksSighterTaggedShots()
    {
        var all = Read(out _).SelectMany(s => s.Shots).ToList();
        Assert.Contains(all, sh => sh.IsSighter);
    }

    [Fact]
    public void KeepsLetterScoresAsText()
    {
        var all = Read(out _).SelectMany(s => s.Shots).ToList();
        Assert.Contains(all, sh => sh.Score == "X");
    }

    [Fact]
    public void ABlankOrRaggedRowIsLoggedNotFatal()
    {
        var log = new List<string>();
        using var r = new StringReader(
            "Date: 2026-09-21\n" +
            "String: Test\n" +
            "Target: #220 1887 x 1908\n" +
            "Face: NRA Long Range FC at 1000y\n" +
            ",time,tags,id,score,temp C,x mm,y mm,v fps,yaw deg, pitch deg,quality,xy_err\n" +
            ",09:00:00,,1,X,20,10.5,-4.2,2700,0,0,1,0.5\n" +
            ",09:00:30,,2\n" +
            "\n" +
            ",09:01:00,,3,10,20,-30.1,12.0,2698,0,0,1,0.5\n");
        var strings = SmCsvReader.Read(r, log);
        Assert.Single(strings);
        Assert.Equal(2, strings[0].Shots.Count);
        Assert.NotEmpty(log);
    }

    [Fact]
    public void ReadsAllSixStringsAndAssignsSequentialShotNumbers()
    {
        var strings = Read(out var log);
        Assert.Equal(6, strings.Count);
        Assert.Equal(new[] { "M6 R1 TT11", "M5 R1 TT11", "M4 R1 TT11",
                              "M3 R2 TT11", "M2 R2 TT11", "M1 R2 TT11" },
                     strings.Select(s => s.Name));

        // Six strings, one per block — a multi-target block's two targets share one
        // SmString rather than becoming two, so Number is unique per string with no
        // extra bookkeeping. Shot counts verified against the fixture directly (awk).
        Assert.Equal(new[] { 33, 34, 30, 22, 22, 25 }, strings.Select(s => s.Shots.Count));
        foreach (var s in strings)
            Assert.Equal(Enumerable.Range(1, s.Shots.Count), s.Shots.Select(sh => sh.Number));

        // All six strings share the one face on this device; it must resolve to the
        // library's id, not stay empty (which would mean the name match failed).
        Assert.All(strings, s => Assert.Equal("NRA_LRFC", s.FaceId));
    }

    [Fact]
    public void NoShotIsInvalidBecauseTheCsvHasNoPerShotInvalidMarker()
    {
        // "incomplete" is a near-universal tag in this fixture (string-level status, not a
        // per-shot flag) and must not be read as IsInvalid.
        var all = Read(out _).SelectMany(s => s.Shots).ToList();
        Assert.DoesNotContain(all, sh => sh.IsInvalid);
    }
}
