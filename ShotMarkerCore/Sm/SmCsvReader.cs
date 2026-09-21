using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShotMarker.Core.Faces;

namespace ShotMarker.Core.Sm;

/// <summary>
/// Reads a ShotMarker `.csv` shot-log export: a banner, then one block per string —
/// a positional header row (date, name, device id, frame size, face name + distance,
/// one composite score per target), a column-header row, then one row per shot.
///
/// Differences from the `.tar` format, all resolved here at the boundary so nothing
/// downstream needs to know which format a string came from:
///  - Velocity is reported in feet per second (<c>v fps</c>); converted to m/s.
///  - x/y are already rounded to whole millimetres by the exporter.
///  - The face is a display name ("NRA Long Range FC"), not an id ("NRA_LRFC"); it is
///    matched against <see cref="TargetFaceLibrary"/> by name/short-name.
///  - A multi-target string carries two targets' shots interleaved in one block, with
///    the second target's row ids prefixed "R" (<c>RS1..RS5</c>, <c>R1..R20</c>) and a
///    second composite score column in the header. Both targets are folded into one
///    <see cref="SmString"/> — see the remarks on <see cref="Read"/> for why.
///
/// The last shot-row field is a quoted `sim_t(...)` blob that itself contains commas, so
/// this reader parses CSV properly (quote-aware) rather than splitting on ','.
/// </summary>
public static class SmCsvReader
{
    private const double MetresPerFoot = 0.3048;

    private static readonly Regex KeyValueLine = new(@"^\s*[A-Za-z ]+:\s", RegexOptions.Compiled);
    private static readonly Regex KeyValuePair = new(@"([A-Za-z ]+):\s*([^,]+)", RegexOptions.Compiled);
    private static readonly Regex FrameSize =
        new(@"(\d+(?:\.\d+)?)\s*x\s*(\d+(?:\.\d+)?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Distance =
        new(@"(\d+(?:\.\d+)?)\s*(y|m)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <remarks>
    /// A multi-target string's two targets are NOT split into two <see cref="SmString"/>s.
    /// The file names the block once ("M6 R1 TT11") and shares one physical frame between
    /// both targets, so one <see cref="SmString"/> per block matches the six-strings-in,
    /// six-strings-out shape the CSV reference documents. <see cref="SmShot.Number"/> is
    /// assigned sequentially over the block's whole shot list in file order (both targets
    /// interleaved, sighters included) — the same 1..N-over-everything convention
    /// <c>SmTarReader</c> uses — so numbers never collide within the one list they share.
    /// </remarks>
    public static IReadOnlyList<SmString> Read(TextReader csv, IList<string> log)
    {
        var result = new List<SmString>();
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scores = new List<string>();
        List<SmShot>? shots = null;
        string[]? columns = null;
        int lineNo = 0;

        void Flush()
        {
            if (shots is { Count: > 0 })
            {
                result.Add(Build(header, scores, shots, result.Count + 1, log));
            }
            else if (shots != null)
            {
                log.Add($"string '{HeaderValue(header, "String") ?? "?"}': no shots — skipped");
            }
            shots = null;
            columns = null;
            scores = new List<string>();
        }

        while (csv.ReadLine() is { } line)
        {
            lineNo++;
            if (line.Trim().Length == 0) continue;
            string[] cells = SplitCsv(line);

            if (IsColumnHeader(cells))
            {
                columns = cells;
                shots = new List<SmShot>();
                continue;
            }

            // A non-blank first cell while we are already inside a block's shot rows means
            // this line is the NEXT block's string-header row (shot rows always start with
            // the file's leading empty column) — flush the block in progress before reading
            // it. Before any column-header row has been seen, every line is header material.
            bool isHeaderLine = columns == null || (cells.Length > 0 && cells[0].Trim().Length > 0);
            if (isHeaderLine)
            {
                if (shots != null) Flush();
                ParseHeaderLine(line, cells, header, scores);
                continue;
            }

            SmShot? shot = ReadShot(cells, columns!, shots!.Count + 1, lineNo, log);
            if (shot != null) shots.Add(shot);
        }
        Flush();
        return result;
    }

    private static bool IsColumnHeader(string[] cells) =>
        cells.Any(c => c.Trim().Equals("x mm", StringComparison.OrdinalIgnoreCase)) &&
        cells.Any(c => c.Trim().Equals("y mm", StringComparison.OrdinalIgnoreCase));

    /// <summary>Accepts two header shapes: the real export's single positional row
    /// (<c>date,name,device,"W x H",face+dist,score...</c>) and a "Key: value" style, kept
    /// so a hand-written malformed-input test doesn't need to mimic the device's exact
    /// banner/column layout. A line matching neither (the file's leading banner lines) is
    /// ignored.</summary>
    private static void ParseHeaderLine(
        string line, string[] cells, Dictionary<string, string> header, List<string> scores)
    {
        if (KeyValueLine.IsMatch(line))
        {
            foreach (Match m in KeyValuePair.Matches(line))
                header[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
            return;
        }

        if (cells.Length < 5) return; // banner line, e.g. "ShotMarker Archived Data (...)"

        header["Date"] = cells[0].Trim();
        header["String"] = cells[1].Trim();
        header["Device"] = cells[2].Trim();
        header["Target"] = cells[3].Trim();
        header["Face"] = cells[4].Trim();

        scores.Clear();
        for (int i = 5; i < cells.Length; i++)
        {
            string v = cells[i].Trim();
            if (v.Length > 0) scores.Add(v);
        }
    }

    private static SmShot? ReadShot(string[] cells, string[] columns, int number, int lineNo, IList<string> log)
    {
        double? Col(string name)
        {
            int i = Array.FindIndex(columns, c => c.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i < cells.Length &&
                   double.TryParse(cells[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
                ? v : null;
        }
        string? Text(string name)
        {
            int i = Array.FindIndex(columns, c => c.Trim().Equals(name, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i < cells.Length && cells[i].Trim().Length > 0 ? cells[i].Trim() : null;
        }

        // x/y are already whole millimetres — Col() just parses them, no rounding needed.
        double? x = Col("x mm"), y = Col("y mm");
        if (x == null || y == null)
        {
            log.Add($"line {lineNo}: no shot position — skipped");
            return null;
        }

        string tags = Text("tags") ?? "";

        // The CSV carries no per-shot invalid/error marker: "incomplete" — the only other
        // tag seen besides "sighter" — is on 150 of the fixture's 166 shot rows (every
        // record shot and most sighters), so it is a string-level status, not a per-shot
        // flag, and is not treated as IsInvalid. See task-5-report.md for the count.
        return new SmShot(
            number, x.Value, y.Value,
            Col("v fps") is { } fps and > 0 ? fps * MetresPerFoot : null,
            Text("score"), Col("temp C"),
            tags.Contains("sighter", StringComparison.OrdinalIgnoreCase),
            false);
    }

    private static SmString Build(
        IDictionary<string, string> header, List<string> scores, List<SmShot> shots, int index, IList<string> log)
    {
        string name = HeaderValue(header, "String") ?? $"String {index}";

        // "NRA Long Range FC at 1000y" — split on the LAST " at " so a face name that
        // happens to contain " at " still separates correctly from the trailing distance.
        string faceField = HeaderValue(header, "Face") ?? HeaderValue(header, "Target face") ?? "";
        string faceName = faceField;
        double dist = 0;
        string unit = "m";
        bool distFound = false;
        int atIdx = faceField.LastIndexOf(" at ", StringComparison.OrdinalIgnoreCase);
        if (atIdx >= 0)
        {
            faceName = faceField[..atIdx].Trim();
            Match dm = Distance.Match(faceField[(atIdx + 4)..]);
            if (dm.Success)
            {
                dist = double.Parse(dm.Groups[1].Value, CultureInfo.InvariantCulture);
                unit = dm.Groups[2].Value.ToLowerInvariant();
                distFound = true;
            }
        }
        if (!distFound)
            log.Add($"string '{name}': no distance found in face field '{faceField}' — assuming 0 m");

        // "1887 x 1908" (real export) or "#220 1887 x 1908" (Target: line) — either way the
        // first "<digits> x <digits>" in the field is the frame size.
        Match fm = FrameSize.Match(HeaderValue(header, "Target") ?? "");
        double w = fm.Success ? double.Parse(fm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        double h = fm.Success ? double.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture) : 0;

        DateTimeOffset ts = DateTimeOffset.TryParse(
            HeaderValue(header, "Date"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset t)
            ? t : DateTimeOffset.MinValue;

        string faceId = ResolveFaceId(faceName, log, name);

        // A multi-target string's header carries one composite score per target (two
        // columns); SmString has a single ScoreText field, so both are kept, joined, rather
        // than silently dropping the second target's score.
        string? scoreText = scores.Count == 0 ? null : string.Join(" | ", scores);

        return new SmString(
            $"csv-{index}", name, ts, faceId, dist, unit, w, h, null, scoreText, shots, null);
    }

    private static string? HeaderValue(IDictionary<string, string> h, string key) =>
        h.TryGetValue(key, out string? v) ? v : null;

    // ---- face name matching --------------------------------------------------------------

    private static readonly Lazy<List<(string Id, string Name, string ShortName)>> Faces = new(LoadFaceIndex);

    /// <summary>Reads the same embedded <c>targetfaces.json</c> resource
    /// <see cref="TargetFaceLibrary"/> loads, purely to search by name/short-name — the
    /// library itself only exposes lookup by id, and this keeps the index from ever
    /// drifting out of step with it without adding a public surface to that committed
    /// class.</summary>
    private static List<(string Id, string Name, string ShortName)> LoadFaceIndex()
    {
        var list = new List<(string, string, string)>();
        using Stream? s = typeof(TargetFaceLibrary).Assembly
            .GetManifestResourceStream("ShotMarker.Core.Faces.targetfaces.json");
        if (s == null) return list;

        using JsonDocument doc = JsonDocument.Parse(s);
        foreach (JsonProperty p in doc.RootElement.GetProperty("faces").EnumerateObject())
        {
            string id = p.Name;
            string faceName = p.Value.TryGetProperty("name", out JsonElement n) && n.ValueKind == JsonValueKind.String
                ? n.GetString()! : id;
            string shortName = p.Value.TryGetProperty("shortname", out JsonElement sn) && sn.ValueKind == JsonValueKind.String
                ? sn.GetString()! : id;
            list.Add((id, faceName, shortName));
        }
        return list;
    }

    /// <summary>Matches a CSV face display name ("NRA Long Range FC") against
    /// <see cref="TargetFaceLibrary"/>'s name/short-name, first exactly then by
    /// substring. No match logs and returns "" — the same empty-id sentinel
    /// <c>SmTarReader</c> uses for a missing/unrecognised <c>face_id</c> — so a caller
    /// already written against the .tar path (<c>TargetFaceLibrary.Find(id) ??
    /// TargetFaceLibrary.Generic(w, h)</c>) falls back to the generic face for this case
    /// too, without this reader needing to know about rendering.</summary>
    internal static string ResolveFaceId(string displayName, IList<string> log, string stringName)
    {
        string needle = displayName.Trim();
        if (needle.Length == 0) return "";

        var exact = Faces.Value.FirstOrDefault(f =>
            string.Equals(f.Name, needle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.ShortName, needle, StringComparison.OrdinalIgnoreCase));
        if (exact.Id != null) return exact.Id;

        var loose = Faces.Value.FirstOrDefault(f =>
            f.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            needle.Contains(f.Name, StringComparison.OrdinalIgnoreCase));
        if (loose.Id != null) return loose.Id;

        log.Add($"string '{stringName}': no target face matches '{displayName}' — falling back to the generic face");
        return "";
    }

    // ---- CSV parsing ------------------------------------------------------------------

    /// <summary>Splits one CSV line, honouring double-quoted cells (a shot row's last field
    /// is a quoted <c>sim_t(...)</c> blob containing commas — <c>Split(',')</c> would
    /// corrupt every row).</summary>
    private static string[] SplitCsv(string line)
    {
        var cells = new List<string>();
        var cur = new StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ',' && !quoted) { cells.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(c);
        }
        cells.Add(cur.ToString());
        return cells.ToArray();
    }
}
