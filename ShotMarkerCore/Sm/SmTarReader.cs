using System.Formats.Tar;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

namespace ShotMarker.Core.Sm;

/// <summary>
/// Reads a ShotMarker `.tar` export: an <c>archive.txt</c> JSON index plus one
/// zlib-compressed <c>string-&lt;id&gt;.z</c> per string. Both formats come from
/// .NET itself — System.Formats.Tar and ZLibStream — so no third-party code is involved.
///
/// The current ShotMarker firmware sets <c>"encoded": true</c> on every string and stores
/// each shot as a compact encoded string in the top-level <c>shots</c> array (ported from
/// the ShotMarker bundle's <c>decode_shot</c>/<c>decode64</c>). Older exports may still carry
/// plain shot objects under <c>groups[].shots[]</c> with <c>encoded</c> absent or false; that
/// path is kept as a fallback but is not exercised by the committed fixture.
/// </summary>
public static class SmTarReader
{
    // Feet per metre — the constant the ShotMarker bundle calls FPS; used to turn the
    // device's internal feet-per-second velocity encoding into m/s.
    private const double FeetPerMetre = 3.28084;

    public static IReadOnlyList<SmString> Read(Stream tar, IList<string> log)
    {
        var entries = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            using var reader = new TarReader(tar, leaveOpen: true);
            while (reader.GetNextEntry() is { } entry)
            {
                if (entry.DataStream is null) continue;
                using var ms = new MemoryStream();
                entry.DataStream.CopyTo(ms);
                entries[Path.GetFileName(entry.Name)] = ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            // A truncated archive still yields every entry read before the cut.
            log.Add($"archive ended early ({ex.GetType().Name}); read {entries.Count} entries");
        }

        Dictionary<string, JsonElement>? index = ReadArchiveIndex(entries, log);

        var result = new List<SmString>();
        foreach ((string name, byte[] data) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            if (!name.StartsWith("string-", StringComparison.Ordinal) ||
                !name.EndsWith(".z", StringComparison.Ordinal))
                continue;

            string id = Path.GetFileNameWithoutExtension(name);
            if (id.StartsWith("string-", StringComparison.Ordinal)) id = id["string-".Length..];

            try
            {
                using var src = new MemoryStream(data);
                using var zs = new ZLibStream(src, CompressionMode.Decompress);
                using var json = new MemoryStream();
                zs.CopyTo(json);
                json.Position = 0;

                string? scoreText = index != null && index.TryGetValue(id, out JsonElement idxEntry)
                    ? Str(idxEntry, "group_text")
                    : null;

                SmString? s = ReadString(id, json, scoreText, log);
                if (s != null) result.Add(s);
            }
            catch (Exception ex)
            {
                log.Add($"{name}: unreadable ({ex.Message}) — skipped");
            }
        }
        return result;
    }

    /// <summary>archive.txt is an id-keyed JSON object (name/ts/count/group_text/face_id/...).
    /// Only <c>group_text</c> — the composite score string, e.g. "191-1X" — is not carried by
    /// the per-string JSON, so that is all we take from it. A missing or unreadable index is
    /// not fatal: every other field comes from the string's own JSON.</summary>
    private static Dictionary<string, JsonElement>? ReadArchiveIndex(
        Dictionary<string, byte[]> entries, IList<string> log)
    {
        if (!entries.TryGetValue("archive.txt", out byte[]? bytes)) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(bytes);
            var index = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty p in doc.RootElement.EnumerateObject())
                index[p.Name] = p.Value.Clone();
            return index;
        }
        catch (Exception ex)
        {
            log.Add($"archive.txt: unreadable ({ex.Message})");
            return null;
        }
    }

    private static SmString? ReadString(string id, Stream json, string? scoreText, IList<string> log)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        bool encoded = root.TryGetProperty("encoded", out JsonElement encEl)
                       && encEl.ValueKind == JsonValueKind.True;

        HashSet<string> invalidIds = InvalidIds(root);
        List<SmShot> shots = encoded
            ? ReadEncodedShots(root, invalidIds, log, id)
            : ReadPlainShots(root, invalidIds);

        if (shots.Count == 0) { log.Add($"{id}: no shots — skipped"); return null; }

        SmGroupStats? stats = ReadStats(root);

        return new SmString(
            id,
            Str(root, "name") ?? id,
            Timestamp(root),
            Str(root, "face_id") ?? "",
            Num(root, "dist") ?? 0,
            Str(root, "dist_unit") ?? "m",
            Num(root, "width") ?? 0,
            Num(root, "height") ?? 0,
            Num(root, "bullet"),
            scoreText,
            shots,
            stats);
    }

    // ---- encoded path (the fixture's format) -----------------------------------------

    private static List<SmShot> ReadEncodedShots(
        JsonElement root, HashSet<string> invalidIds, IList<string> log, string stringId)
    {
        var shots = new List<SmShot>();
        if (!root.TryGetProperty("shots", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            return shots;

        (Dictionary<int, string> sighterScores, Dictionary<int, string> recordScores) =
            ParseScoreString(root);

        int sighterOrdinal = 0;
        int recordOrdinal = 0;
        int rawIndex = -1;
        foreach (JsonElement el in arr.EnumerateArray())
        {
            rawIndex++;
            if (el.ValueKind != JsonValueKind.String) continue;
            string encoded = el.GetString() ?? "";

            try
            {
                DecodedShot d = DecodeShot(encoded);

                string? score;
                if (d.Sighter)
                {
                    sighterOrdinal++;
                    score = d.ScoreOverride ??
                            (sighterScores.TryGetValue(sighterOrdinal, out string? sv) ? sv : null);
                }
                else
                {
                    recordOrdinal++;
                    score = d.ScoreOverride ??
                            (recordScores.TryGetValue(recordOrdinal, out string? rv) ? rv : null);
                }

                bool erroredOrFake = d.Fake || d.ErrorCode is not (null or 0);
                bool listedInvalid = invalidIds.Contains(rawIndex.ToString(CultureInfo.InvariantCulture));
                bool invalid = erroredOrFake || listedInvalid;

                // A shot that errored on the device (d.ErrorCode != 0) or is a "fake" entry
                // never had coordinates decoded — NaN rather than (0,0) so a caller that
                // forgets to check IsInvalid/IsFlyer gets an obviously wrong answer, not a
                // silently-plausible one.
                shots.Add(new SmShot(
                    shots.Count + 1,
                    d.XMm ?? double.NaN, d.YMm ?? double.NaN,
                    d.VelocityMps, score, d.TempC,
                    d.Sighter, invalid));
            }
            catch (Exception ex)
            {
                log.Add($"{stringId}: shot {rawIndex} unreadable ({ex.Message}) — skipped");
            }
        }
        return shots;
    }

    private readonly record struct DecodedShot(
        bool Sighter, bool Fake, double? TempC, double? XMm, double? YMm, double? VelocityMps,
        int? ErrorCode, string? ScoreOverride);

    /// <summary>Ported from the ShotMarker bundle's <c>decode_shot</c>. Reads a cursor forward
    /// through the encoded string; a shot whose error byte (<c>d</c>) is non-zero has no
    /// x/y/v at all — those fields stay null.</summary>
    private static DecodedShot DecodeShot(string s)
    {
        var c = new Cursor(s);

        _ = 16777216 * Decode64(c.Take(3)) + Decode64(c.Take(4)); // ts — not surfaced on SmShot

        long f1 = c.Byte();
        bool sighter = (f1 & 4) != 0;

        long f2 = c.Byte();
        bool fake = (f2 & 8) != 0;
        bool hasScoreOverride = (f2 & 16) != 0;

        string? scoreOverride = null;
        if (hasScoreOverride)
        {
            string raw = c.Take(1);
            long ov = Decode64(raw);
            scoreOverride = ov is >= 0 and <= 10 ? ov.ToString(CultureInfo.InvariantCulture) : raw;
        }

        if (fake)
            return new DecodedShot(sighter, true, null, null, null, null, null, scoreOverride);

        double temp = c.Byte() - 20;
        long a = Decode64(c.Take(2));
        for (int l = 0; l < 8; l++)
        {
            int len = ((a >> (10 - l)) & 1) != 0 ? 3 : 2;
            _ = Decode64(c.Take(len)); // sensor timing — bookkeeping only, not needed further
        }

        long d = c.Byte();
        if (d != 0)
            return new DecodedShot(sighter, false, temp, null, null, null, (int)d, scoreOverride);

        double x = Polar(Decode64(c.Take(2)), 4000, 1.6, 2047);
        double y = Polar(Decode64(c.Take(2)), 4000, 1.6, 2047);
        double v = (Decode64(c.Take(2)) + 1000) / FeetPerMetre;
        return new DecodedShot(sighter, false, temp, x, y, v, 0, scoreOverride);
    }

    private static double Polar(long e, double t, double o, double i) =>
        Math.Pow(Math.Abs(e - i) / i, o) * t * Math.Sign(e - i);

    /// <summary>Little-endian base-64 with a +35 character offset.</summary>
    private static long Decode64(string s)
    {
        long o = 0;
        for (int k = s.Length - 1; k >= 0; k--) o = (o << 6) + (s[k] - 35);
        return o;
    }

    private sealed class Cursor
    {
        private readonly string _s;
        private int _i;
        public Cursor(string s) => _s = s;

        public string Take(int n)
        {
            if (_i + n > _s.Length)
                throw new FormatException($"expected {n} more characters at offset {_i} of {_s.Length}");
            string r = _s.Substring(_i, n);
            _i += n;
            return r;
        }

        public long Byte()
        {
            if (_i >= _s.Length)
                throw new FormatException($"expected 1 more character at offset {_i} of {_s.Length}");
            long v = _s[_i] - 35;
            _i++;
            return v;
        }
    }

    /// <summary><c>score_string</c> looks like "S1:7,S2:8,...,1:9,2:10,...,20:9," — S&lt;n&gt;
    /// entries are sighter n, bare &lt;n&gt; entries are record shot n, both 1-based and in
    /// shot order. Values may be non-numeric (e.g. "X").</summary>
    private static (Dictionary<int, string> sighters, Dictionary<int, string> records) ParseScoreString(
        JsonElement root)
    {
        var sighters = new Dictionary<int, string>();
        var records = new Dictionary<int, string>();
        string? raw = Str(root, "score_string");
        if (string.IsNullOrEmpty(raw)) return (sighters, records);

        foreach (string token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = token.IndexOf(':');
            if (colon <= 0 || colon == token.Length - 1) continue;
            string key = token[..colon];
            string val = token[(colon + 1)..];

            if (key[0] is 'S' or 's')
            {
                if (int.TryParse(key.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int sn))
                    sighters[sn] = val;
            }
            else if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rn))
            {
                records[rn] = val;
            }
        }
        return (sighters, records);
    }

    // ---- unencoded fallback path (kept per the brief, not exercised by the fixture) --

    private static List<SmShot> ReadPlainShots(JsonElement root, HashSet<string> invalidIds)
    {
        var shots = new List<SmShot>();
        foreach (JsonElement g in Groups(root))
        {
            if (!g.TryGetProperty("shots", out JsonElement gs) || gs.ValueKind != JsonValueKind.Array)
                continue;
            foreach (JsonElement sh in gs.EnumerateArray())
            {
                bool hidden = sh.TryGetProperty("display", out JsonElement d)
                              && d.ValueKind == JsonValueKind.False;
                string? tag = Str(sh, "display_text");
                bool sighter = hidden
                               || tag?.Contains("sighter", StringComparison.OrdinalIgnoreCase) == true;
                shots.Add(new SmShot(
                    shots.Count + 1,
                    Num(sh, "x") ?? 0, Num(sh, "y") ?? 0,
                    Num(sh, "v"), Str(sh, "score"), Num(sh, "temp"),
                    sighter, invalidIds.Contains(Str(sh, "id") ?? "")));
            }
        }
        return shots;
    }

    // ---- shared helpers ----------------------------------------------------------------

    /// <summary><c>groups</c> is an id-keyed JSON object in every fixture observed, but is
    /// read defensively as either an object or an array so an older export shaped as an
    /// array still works.</summary>
    private static IEnumerable<JsonElement> Groups(JsonElement root)
    {
        if (!root.TryGetProperty("groups", out JsonElement groups))
            return Enumerable.Empty<JsonElement>();
        return groups.ValueKind switch
        {
            JsonValueKind.Object => groups.EnumerateObject().Select(p => p.Value),
            JsonValueKind.Array => groups.EnumerateArray(),
            _ => Enumerable.Empty<JsonElement>(),
        };
    }

    private static SmGroupStats? ReadStats(JsonElement root) => Groups(root).Select(ReadStatsFrom).FirstOrDefault();

    private static SmGroupStats ReadStatsFrom(JsonElement g) => new(
        Num(g, "mr"), Num(g, "size"), Num(g, "ctc"),
        Num(g, "v_avg"), Num(g, "v_sd"), Num(g, "v_es"));

    private static HashSet<string> InvalidIds(JsonElement root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("shots_invalid", out JsonElement inv) && inv.ValueKind == JsonValueKind.Array)
            foreach (JsonElement e in inv.EnumerateArray())
                set.Add(e.ValueKind == JsonValueKind.String ? e.GetString()! : e.ToString());
        return set;
    }

    private static DateTimeOffset Timestamp(JsonElement root) =>
        Num(root, "ts") is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)ms)
            : DateTimeOffset.MinValue;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out JsonElement v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double d) => d,
            _ => null,
        };
    }
}
