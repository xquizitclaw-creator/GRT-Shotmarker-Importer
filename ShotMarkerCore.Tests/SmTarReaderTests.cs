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

    // ---- device error / warning bytes and the "does not count" flags ------------------
    //
    // The fixture's 69 shots all decode with a zero status byte and no flags beyond
    // `sighter`, so none of the behaviour below can be reached from it directly. These
    // tests take a REAL encoded shot and flip exactly the one byte under test, so the
    // decoder under exercise is the same one the fixture runs through.

    /// <summary>The vendor's <c>decode_shot</c> splits the status byte: codes 1..31 are
    /// errors and suppress the measurement, codes 32+ are warnings on a shot that still
    /// carries full x/y/v and that ShotMarker plots, scores and counts. A warning must not
    /// cost the shot its coordinates — if it did, and the warned shot was the widest hit,
    /// the imported group would read smaller than the one actually fired.</summary>
    [Theory]
    [InlineData(36)] // "quality"
    [InlineData(38)] // "velocity"
    [InlineData(32)] // "measured off target left"
    [InlineData(99)] // unrecognised warning: the vendor's `default: d < 32 ? error : warning`
    public void AWarningOnTheStatusByteKeepsTheMeasurementAndKeepsTheShotCounting(int code)
    {
        string clean = EncodedShotFromFixture(5);
        SmShot unmutated = Assert.Single(ReadOneSyntheticShot(clean, new List<string>()));

        var log = new List<string>();
        SmShot warned = Assert.Single(ReadOneSyntheticShot(WithStatusByte(clean, code), log));

        Assert.False(warned.IsInvalid);
        Assert.False(warned.IsFlyer);
        Assert.Equal(unmutated.XMm, warned.XMm, 9);
        Assert.Equal(unmutated.YMm, warned.YMm, 9);
        Assert.Equal(unmutated.VelocityMps, warned.VelocityMps);
        Assert.Contains(log, l => l.Contains("warning", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>An error, by contrast, means the device never solved a position: the encoded
    /// string carries no coordinates at all, so the shot is invalid and its position is NaN
    /// rather than a silently-plausible (0,0) at dead centre.</summary>
    [Theory]
    [InlineData(1)]  // "insufficient sensor timings"
    [InlineData(3)]  // "no valid solution"
    [InlineData(31)] // unrecognised error, still below the warning threshold
    public void AnErrorOnTheStatusByteMarksTheShotInvalidWithNoPosition(int code)
    {
        var log = new List<string>();
        SmShot shot = Assert.Single(
            ReadOneSyntheticShot(TruncateAfterStatusByte(WithStatusByte(EncodedShotFromFixture(5), code)), log));

        Assert.True(shot.IsInvalid);
        Assert.True(shot.IsFlyer);
        Assert.True(double.IsNaN(shot.XMm));
        Assert.True(double.IsNaN(shot.YMm));
    }

    /// <summary>The flag byte carries four flags — 1 simulated, 2 hide, 8 off, alongside
    /// 4 sighter — and every one of ShotMarker's own statistics functions excludes hide, off
    /// and fake alike. Such a shot keeps its real coordinates (the device greys it rather than
    /// removing it) but must not count. This is asserted with NO group present, because that
    /// is the case where group membership cannot launder the flag: <c>InSelectedGroup</c> is
    /// null for every shot, so <c>IsFlyer</c> here can only come from the flag itself.</summary>
    [Theory]
    [InlineData(1)] // simulated — a shot that was never fired
    [InlineData(2)] // hide — the shooter struck it out, e.g. a cross-fire
    [InlineData(8)] // off — off target
    public void AShotTheDeviceMarksAsNotCountingIsStillPlottedButNeverCounted(int bit)
    {
        SmShot shot = Assert.Single(
            ReadOneSyntheticShot(WithFlagBit(EncodedShotFromFixture(5), bit), new List<string>()));

        Assert.Null(shot.InSelectedGroup);
        Assert.True(shot.IsExcludedOnDevice);
        Assert.True(shot.IsFlyer);
        Assert.False(shot.IsInvalid);
        Assert.False(double.IsNaN(shot.XMm)); // still drawable, as ShotMarker draws it
    }

    /// <summary>A group whose <c>shots</c> array is present but empty says no more than a
    /// missing one. Returning an empty member set instead would make every shot fail the
    /// membership test at once — an all-flyer tab with an empty group box and a banner
    /// reading "0 of N record shots".</summary>
    [Fact]
    public void AGroupWhoseMemberListIsEmptyLeavesMembershipNullRatherThanFlyeringEveryShot()
    {
        var log = new List<string>();
        SmShot shot = Assert.Single(ReadOneSyntheticShot(
            EncodedShotFromFixture(5), log,
            new JsonObject { ["1"] = new JsonObject { ["id"] = 1, ["shots"] = new JsonArray() } }));

        Assert.Null(shot.InSelectedGroup);
        Assert.False(shot.IsFlyer);
        Assert.Contains(log, l => l.Contains("membership", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads a synthetic one-string archive containing exactly the given encoded
    /// shot, so a test can assert on one mutated shot without the fixture's other 68.</summary>
    private static IReadOnlyList<SmShot> ReadOneSyntheticShot(
        string encodedShot, List<string> log, JsonObject? groups = null)
    {
        var body = new JsonObject
        {
            ["name"] = "synthetic-one-shot",
            ["ts"] = 0,
            ["face_id"] = "X",
            ["dist"] = 100,
            ["dist_unit"] = "m",
            ["width"] = 10,
            ["height"] = 10,
            ["bullet"] = null,
            ["score_string"] = "",
            ["encoded"] = true,
            ["shots"] = new JsonArray(encodedShot),
            ["shots_invalid"] = new JsonArray(),
            ["groups"] = groups ?? new JsonObject(),
        };
        using MemoryStream tar = BuildSyntheticTar(body, "string-7777777777777.z");
        return Assert.Single(SmTarReader.Read(tar, log)).Shots;
    }

    /// <summary>Character values in this encoding are <c>charCode - 35</c>. Sets one bit of
    /// the flag byte, which sits immediately after the seven-character timestamp.
    ///
    /// The bit is set on the decoded VALUE and the offset re-applied, never on the character
    /// itself: 35 is 0b100011, so OR-ing a bit straight into the character silently does
    /// nothing for bits 1 and 2 — which is exactly how this helper first failed.</summary>
    private static string WithFlagBit(string encoded, int bit)
    {
        char[] c = encoded.ToCharArray();
        c[7] = (char)(((c[7] - 35) | bit) + 35);
        return new string(c);
    }

    /// <summary>Replaces the status byte with <paramref name="code"/>. Its offset is not
    /// fixed: it follows the timestamp, two flag bytes, an optional score override, the
    /// temperature and the eight sensor timings, whose individual lengths are themselves
    /// encoded in the two-character word before them. This walks that structure the same way
    /// the decoder does — deliberately, so a test cannot silently mutate the wrong byte.</summary>
    private static int StatusByteOffset(string s)
    {
        int i = 7;                       // ts: 3 + 4
        int f2 = s[i + 1] - 35;
        i += 2;                          // both flag bytes
        if ((f2 & 16) != 0) i += 1;      // score override
        Assert.True((f2 & 8) == 0, "fixture shot is a 'fake' entry and carries no status byte");
        i += 1;                          // temperature
        long a = (s[i] - 35) + ((long)(s[i + 1] - 35) << 6);
        i += 2;
        for (int l = 0; l < 8; l++) i += ((a >> (10 - l)) & 1) != 0 ? 3 : 2;
        return i;
    }

    private static string WithStatusByte(string encoded, int code)
    {
        char[] c = encoded.ToCharArray();
        c[StatusByteOffset(encoded)] = (char)(code + 35);
        return new string(c);
    }

    /// <summary>An errored shot's encoded string genuinely ends at the status byte — the
    /// device wrote no position after it. Truncating mirrors that rather than leaving
    /// coordinates the decoder is being asserted not to read.</summary>
    private static string TruncateAfterStatusByte(string encoded) =>
        encoded[..(StatusByteOffset(encoded) + 1)];
}
