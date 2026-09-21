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
    public void ReadsAllNineStringsAndAssignsSequentialShotNumbers()
    {
        var strings = Read(out var log);

        // Six blocks in the file, but three of them (M6/M5/M4 R1 TT11) interleave two
        // rifles' shots — one SmString per target, so 3 single-target + 3*2 = 9 strings.
        // The unprefixed target keeps the block's own name and is emitted first; the
        // "R"-prefixed target is emitted second, named with a "[R]" suffix. Shot counts
        // and names verified against the fixture directly (python, grouping by id prefix).
        Assert.Equal(9, strings.Count);
        Assert.Equal(new[]
        {
            "M6 R1 TT11", "M6 R1 TT11 [R]",
            "M5 R1 TT11", "M5 R1 TT11 [R]",
            "M4 R1 TT11", "M4 R1 TT11 [R]",
            "M3 R2 TT11", "M2 R2 TT11", "M1 R2 TT11",
        }, strings.Select(s => s.Name));

        Assert.Equal(new[] { 16, 17, 17, 17, 15, 15, 22, 22, 25 }, strings.Select(s => s.Shots.Count));

        // Number restarts at 1 within each emitted string (sighters included), same
        // convention as the .tar reader — trivially true here since each target has its
        // own shot list, but confirmed rather than assumed.
        foreach (var s in strings)
            Assert.Equal(Enumerable.Range(1, s.Shots.Count), s.Shots.Select(sh => sh.Number));

        // Ids stay unique across the whole file, not just within a block.
        Assert.Equal(strings.Select(s => s.Id).Distinct().Count(), strings.Count);

        // All nine strings share the one face on this device; it must resolve to the
        // library's id, not stay empty (which would mean the name match failed).
        Assert.All(strings, s => Assert.Equal("NRA_LRFC", s.FaceId));
    }

    [Fact]
    public void MultiTargetScoreColumnsAreAssignedToTheCorrectTarget()
    {
        // Guard against the R/unprefixed score columns being swapped: for each of the
        // three multi-target blocks, sum the emitted shots' own scores (non-sighter shots
        // only, X = 10 and counted, matching how ShotMarker's own declared composite score
        // is computed) and check it equals the ScoreText this reader assigned to that
        // target. If the reader mapped a group to the wrong header column, the group's own
        // shots would sum to the OTHER declared value, so this fails exactly when the
        // columns are swapped. Expected values below are the coordinator-verified table.
        var expected = new Dictionary<string, string>
        {
            ["M6 R1 TT11"] = "142-4X",
            ["M6 R1 TT11 [R]"] = "134-3X",
            ["M5 R1 TT11"] = "137-2X",
            ["M5 R1 TT11 [R]"] = "129-2X",
            ["M4 R1 TT11"] = "137-1X",
            ["M4 R1 TT11 [R]"] = "133-1X",
        };

        var strings = Read(out _).Where(s => expected.ContainsKey(s.Name)).ToList();
        Assert.Equal(expected.Count, strings.Count);

        foreach (var s in strings)
        {
            string computed = SumNonSighterScore(s.Shots);
            Assert.Equal(expected[s.Name], computed);
            Assert.Equal(expected[s.Name], s.ScoreText);
        }
    }

    private static string SumNonSighterScore(IReadOnlyList<SmShot> shots)
    {
        int total = 0, xCount = 0;
        foreach (var sh in shots)
        {
            if (sh.IsSighter || sh.Score == null) continue;
            if (sh.Score.Equals("X", StringComparison.OrdinalIgnoreCase)) { total += 10; xCount++; }
            else if (int.TryParse(sh.Score, out int v)) total += v;
        }
        return xCount > 0 ? $"{total}-{xCount}X" : total.ToString();
    }

    [Fact]
    public void SingleTargetBlocksKeepTheirExistingNameAndScore()
    {
        // M1/M2/M3 R2 TT11 have only one target each and must be unaffected by the
        // multi-target split: same name, single (non-joined) ScoreText.
        var byName = Read(out _).ToDictionary(s => s.Name);
        Assert.Equal("194-3X", byName["M3 R2 TT11"].ScoreText);
        Assert.Equal("192-1X", byName["M2 R2 TT11"].ScoreText);
        Assert.Equal("191-1X", byName["M1 R2 TT11"].ScoreText);
    }

    [Fact]
    public void NoShotIsInvalidBecauseTheCsvHasNoPerShotInvalidMarker()
    {
        // "incomplete" is a near-universal tag in this fixture (string-level status, not a
        // per-shot flag) and must not be read as IsInvalid.
        var all = Read(out _).SelectMany(s => s.Shots).ToList();
        Assert.DoesNotContain(all, sh => sh.IsInvalid);
    }

    /// <summary>Task 9b test 5: the CSV carries no group-membership information at all, so
    /// every shot's <see cref="SmShot.InSelectedGroup"/> must be null throughout — never a
    /// guessed true or false — and <see cref="SmShot.IsFlyer"/> must come out exactly as it
    /// did before this task (sighter or nothing else), because a null must not turn a shot
    /// into a flyer.</summary>
    [Fact]
    public void CsvReadStringsHaveNoGroupMembershipAndTheSameFlyerResultsAsBeforeThisTask()
    {
        var all = Read(out _).SelectMany(s => s.Shots).ToList();
        Assert.NotEmpty(all);
        Assert.All(all, sh => Assert.Null(sh.InSelectedGroup));
        Assert.All(all, sh => Assert.Equal(sh.IsSighter || sh.IsInvalid, sh.IsFlyer));
        // The fixture is supposed to contain both sighters and record shots — otherwise the
        // assertion above would hold trivially for every shot being the same kind.
        Assert.Contains(all, sh => sh.IsSighter);
        Assert.Contains(all, sh => !sh.IsSighter);
    }
}
