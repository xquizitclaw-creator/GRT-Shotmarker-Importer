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
