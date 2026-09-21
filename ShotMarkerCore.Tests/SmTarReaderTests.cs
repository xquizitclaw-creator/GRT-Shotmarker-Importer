using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    // ---- task 9b: honour ShotMarker's group selection ----------------------------------

    /// <summary>Task 9b test 1. Verified against the fixture (task-9b-brief.md): M1's group
    /// holds 19 of its 25 shots — the five sighters plus one genuine record shot, Number 11.</summary>
    [Fact]
    public void M1sGroupHoldsExactlyNineteenShotsAndExcludesRecordShotEleven()
    {
        var s = Read(out _).First(x => x.Name == "M1 R2 TT11");

        Assert.Equal(19, s.Shots.Count(sh => sh.InSelectedGroup == true));

        SmShot shot11 = s.Shots.Single(sh => sh.Number == 11);
        Assert.False(shot11.IsSighter);
        Assert.False(shot11.InSelectedGroup);
        Assert.True(shot11.IsFlyer);
    }

    /// <summary>Task 9b test 2 — the regression guard: M2 and M3's groups only exclude their
    /// sighters, so this task must not over-exclude a genuine record shot from either.</summary>
    [Theory]
    [InlineData("M2 R2 TT11")]
    [InlineData("M3 R2 TT11")]
    public void M2AndM3sGroupsHoldTwentyMembersAndExcludeNoRecordShot(string name)
    {
        var s = Read(out _).First(x => x.Name == name);

        Assert.Equal(20, s.Shots.Count(sh => sh.InSelectedGroup == true));
        Assert.DoesNotContain(s.Shots, sh => !sh.IsSighter && sh.InSelectedGroup == false);
    }

    /// <summary>Task 9b test 3: every group member's <c>ts</c> resolves to exactly one shot.
    /// The member count read straight off the raw JSON (independent of
    /// <see cref="SmTarReader"/>'s own join) must equal the number of shots the reader marked
    /// <c>InSelectedGroup == true</c> — if a member's <c>ts</c> failed to match a root shot,
    /// this count would fall short; ShotMarker's own <c>ts</c> uniqueness (verified in the
    /// brief) rules out the other way a mismatch could hide.</summary>
    [Theory]
    [InlineData("M1 R2 TT11", 19)]
    [InlineData("M2 R2 TT11", 20)]
    [InlineData("M3 R2 TT11", 20)]
    public void EveryGroupMemberResolvesToExactlyOneShot(string name, int expectedMembers)
    {
        var s = Read(out _).First(x => x.Name == name);
        int rawMemberCount = RawGroupMemberCount(s.Id);

        Assert.Equal(expectedMembers, rawMemberCount);
        Assert.Equal(rawMemberCount, s.Shots.Count(sh => sh.InSelectedGroup == true));
    }

    /// <summary>Task 9b test 4: a group with no usable <c>shots</c> array (an older export
    /// shape, per <c>SmTarReader.ReadPlainShots</c>'s own remarks) must never be guessed at —
    /// every shot's <see cref="SmShot.InSelectedGroup"/> stays null, one line is logged, and
    /// nothing is excluded that would not already be excluded (a null does not make a record
    /// shot a flyer).</summary>
    [Fact]
    public void AGroupWithNoShotsArrayLeavesMembershipNullLogsOnceAndExcludesNothing()
    {
        // A real encoded record shot (M1's Number 6 — the first shot after the five
        // sighters) so this is exercising the same decoder the fixture does, not a
        // hand-rolled encoding.
        string encodedRecordShot = EncodedShotFromFixture(5);

        var body = new JsonObject
        {
            ["name"] = "synthetic-no-group-shots",
            ["ts"] = 0,
            ["face_id"] = "X",
            ["dist"] = 100,
            ["dist_unit"] = "m",
            ["width"] = 10,
            ["height"] = 10,
            ["bullet"] = null,
            ["score_string"] = "",
            ["encoded"] = true,
            ["shots"] = new JsonArray(encodedRecordShot),
            ["shots_invalid"] = new JsonArray(),
            ["groups"] = new JsonObject
            {
                ["1"] = new JsonObject { ["id"] = 1, ["size"] = 42.0 }, // no "shots" key
            },
        };

        using MemoryStream tar = BuildSyntheticTar(body, "string-8888888888888.z");
        var log = new List<string>();
        var strings = SmTarReader.Read(tar, log);

        SmString s = Assert.Single(strings);
        SmShot shot = Assert.Single(s.Shots);
        Assert.False(shot.IsSighter);
        Assert.Null(shot.InSelectedGroup);
        Assert.False(shot.IsFlyer);

        string line = Assert.Single(log);
        Assert.Contains("membership", line, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The <c>groups[].shots[].length</c> for the string that decoded to <paramref
    /// name="stringId"/>, read directly off the raw JSON — deliberately not going through
    /// <see cref="SmTarReader"/> at all, so it is an independent count to compare its join
    /// against.</summary>
    private static int RawGroupMemberCount(string stringId)
    {
        using FileStream fs = File.OpenRead(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"));
        using var reader = new TarReader(fs);
        while (reader.GetNextEntry() is { } entry)
        {
            string name = System.IO.Path.GetFileNameWithoutExtension(entry.Name);
            if (!name.EndsWith(stringId, StringComparison.Ordinal) || entry.DataStream is null) continue;
            using var raw = new MemoryStream();
            entry.DataStream.CopyTo(raw);
            raw.Position = 0;
            using var zs = new ZLibStream(raw, CompressionMode.Decompress);
            using var json = new MemoryStream();
            zs.CopyTo(json);
            json.Position = 0;
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement group = doc.RootElement.GetProperty("groups").EnumerateObject().First().Value;
            return group.GetProperty("shots").GetArrayLength();
        }
        throw new InvalidOperationException($"string-{stringId}.z not found in fixture");
    }

    /// <summary>The raw encoded shot string at <paramref name="index"/> of the first string's
    /// root <c>shots</c> array — the same fixture data <see cref="FirstEncodedShotFromFixture"/>
    /// reads, generalised to any index so a synthetic archive can be built from a real record
    /// (non-sighter) shot rather than a hand-rolled encoding.</summary>
    private static string EncodedShotFromFixture(int index)
    {
        using FileStream fs = File.OpenRead(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"));
        using var reader = new TarReader(fs);
        while (reader.GetNextEntry() is { } entry)
        {
            if (!entry.Name.StartsWith("string-", StringComparison.Ordinal) || entry.DataStream is null)
                continue;
            using var raw = new MemoryStream();
            entry.DataStream.CopyTo(raw);
            raw.Position = 0;
            using var zs = new ZLibStream(raw, CompressionMode.Decompress);
            using var json = new MemoryStream();
            zs.CopyTo(json);
            json.Position = 0;
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("shots")[index].GetString()!;
        }
        throw new InvalidOperationException("no string-*.z entry found in fixture");
    }

    /// <summary>Builds a one-entry, one-string synthetic <c>.tar</c> in memory: zlib-compress
    /// <paramref name="body"/> and write it as <paramref name="entryName"/>. Factored out of
    /// <see cref="DecodesShotsInvalidEntriesAsRejectedShotsRatherThanTreatingThemAsIndices"/>'s
    /// own inline version so <see cref="AGroupWithNoShotsArrayLeavesMembershipNullLogsOnceAndExcludesNothing"/>
    /// does not have to duplicate it; that existing test is left exactly as it was.</summary>
    private static MemoryStream BuildSyntheticTar(JsonObject body, string entryName)
    {
        var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            using var compressed = new MemoryStream();
            using (var zs = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(body.ToJsonString());
                zs.Write(utf8, 0, utf8.Length);
            }
            var entry = new PaxTarEntry(TarEntryType.RegularFile, entryName)
            {
                DataStream = new MemoryStream(compressed.ToArray()),
            };
            writer.WriteEntry(entry);
        }
        tar.Position = 0;
        return tar;
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

    [Fact]
    public void DecodesShotsInvalidEntriesAsRejectedShotsRatherThanTreatingThemAsIndices()
    {
        // shots_invalid is a parallel array of rejected/deleted shots, each encoded exactly
        // like `shots` — not a list of indices into `shots`. The committed fixture's
        // shots_invalid is always empty, so this builds a small synthetic archive: a real
        // encoded shot string taken from the fixture (shot 0 of the first string — a
        // sighter with known ground-truth coordinates, per task-4-decoder-reference.md's
        // expected-output table) placed in shots_invalid with an otherwise-empty `shots`.
        string encodedShot = FirstEncodedShotFromFixture();

        var body = new JsonObject
        {
            ["name"] = "synthetic",
            ["ts"] = 0,
            ["face_id"] = "X",
            ["dist"] = 100,
            ["dist_unit"] = "m",
            ["width"] = 10,
            ["height"] = 10,
            ["bullet"] = null,
            ["score_string"] = "",
            ["encoded"] = true,
            ["shots"] = new JsonArray(),
            ["shots_invalid"] = new JsonArray(encodedShot),
            ["groups"] = new JsonObject(),
        };

        using var tar = new MemoryStream();
        using (var writer = new TarWriter(tar, TarEntryFormat.Pax, leaveOpen: true))
        {
            using var compressed = new MemoryStream();
            using (var zs = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                byte[] utf8 = Encoding.UTF8.GetBytes(body.ToJsonString());
                zs.Write(utf8, 0, utf8.Length);
            }
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "string-9999999999999.z")
            {
                DataStream = new MemoryStream(compressed.ToArray()),
            };
            writer.WriteEntry(entry);
        }
        tar.Position = 0;

        var log = new List<string>();
        var strings = SmTarReader.Read(tar, log);

        SmString s = Assert.Single(strings);
        SmShot shot = Assert.Single(s.Shots);
        Assert.True(shot.IsInvalid);
        Assert.Equal(1, shot.Number);
        Assert.Equal(478.6, shot.XMm, 1);
        Assert.Equal(-227.3, shot.YMm, 1);
        Assert.NotNull(shot.VelocityMps);
        Assert.Equal(570.6, shot.VelocityMps!.Value, 1);
    }

    private static string FirstEncodedShotFromFixture()
    {
        using FileStream fs = File.OpenRead(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"));
        using var reader = new TarReader(fs);
        while (reader.GetNextEntry() is { } entry)
        {
            if (!entry.Name.StartsWith("string-", StringComparison.Ordinal) || entry.DataStream is null)
                continue;
            using var raw = new MemoryStream();
            entry.DataStream.CopyTo(raw);
            raw.Position = 0;
            using var zs = new ZLibStream(raw, CompressionMode.Decompress);
            using var json = new MemoryStream();
            zs.CopyTo(json);
            json.Position = 0;
            using JsonDocument doc = JsonDocument.Parse(json);
            return doc.RootElement.GetProperty("shots")[0].GetString()!;
        }
        throw new InvalidOperationException("no string-*.z entry found in fixture");
    }
}
