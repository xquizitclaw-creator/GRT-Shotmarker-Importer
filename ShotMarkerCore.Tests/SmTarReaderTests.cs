using ShotMarker.Core.Sm;
using Xunit;

namespace ShotMarker.Core.Tests;

public class SmTarReaderTests
{
    private static IReadOnlyList<SmString> Read(out List<string> log)
    {
        log = new List<string>();
        using FileStream fs = File.OpenRead(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"));
        return SmTarReader.Read(fs, log);
    }

    [Fact]
    public void ReadsEveryStringInTheArchive()
    {
        var strings = Read(out _);
        Assert.Equal(3, strings.Count);
        Assert.All(strings, s => Assert.NotEmpty(s.Shots));
        // The fixture holds three strings named "M1 R2 TT11", "M2 R2 TT11" and "M3 R2 TT11"
        // (verified by decoding the archive directly) — not "M6 R1 TT11".
        Assert.Equal(
            new[] { "M1 R2 TT11", "M2 R2 TT11", "M3 R2 TT11" },
            strings.Select(s => s.Name));
    }

    [Fact]
    public void ReadsTheFaceDistanceAndFrameOfAString()
    {
        var s = Read(out _).First();
        Assert.Equal("NRA_LRFC", s.FaceId);
        Assert.Equal(1000, s.DistanceValue, 0);
        Assert.Equal("y", s.DistanceUnit);
        Assert.Equal(914.4, s.DistanceMetres, 1);
        Assert.True(s.FrameWidthMm > 0 && s.FrameHeightMm > 0);
    }

    [Fact]
    public void ShotCoordinatesAreMillimetresFromCentreAndVelocitiesAreMetresPerSecond()
    {
        var s = Read(out _).First();
        // A 1000 yd F-Class group lives within a 72 inch board: 914 mm from centre at most.
        Assert.All(s.Shots, sh => Assert.InRange(Math.Abs(sh.XMm), 0, 914));
        Assert.All(s.Shots, sh => Assert.InRange(Math.Abs(sh.YMm), 0, 914));
        // The archive stores m/s; a 180 gr .284 leaves at roughly 800 m/s.
        var v = s.Shots.Where(sh => sh.VelocityMps is > 0).Select(sh => sh.VelocityMps!.Value).ToList();
        Assert.NotEmpty(v);
        Assert.All(v, x => Assert.InRange(x, 400, 1200));
    }

    [Fact]
    public void ShotsAreNumberedFromOne()
    {
        var s = Read(out _).First();
        Assert.Equal(Enumerable.Range(1, s.Shots.Count), s.Shots.Select(sh => sh.Number));
    }

    [Fact]
    public void CarriesShotMarkersOwnGroupStatistics()
    {
        var s = Read(out _).First();
        Assert.NotNull(s.Stats);
        Assert.True(s.Stats!.GroupSizeMm > 0);
    }

    [Fact]
    public void ATruncatedEntryIsLoggedAndSkippedRatherThanThrowing()
    {
        // Truncating the archive mid-entry is what a half-copied export looks like.
        byte[] whole = File.ReadAllBytes(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"));
        using var cut = new MemoryStream(whole, 0, whole.Length / 2);
        var log = new List<string>();
        var strings = SmTarReader.Read(cut, log);
        Assert.NotEmpty(log);
        Assert.All(strings, s => Assert.NotEmpty(s.Shots));
    }
}
