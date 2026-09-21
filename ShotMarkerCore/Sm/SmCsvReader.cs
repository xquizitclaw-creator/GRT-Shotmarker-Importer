using System.Globalization;
using System.Text;
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
///  - A block can carry TWO targets' shots interleaved (two rifles fired onto the same
///    physical frame in one session), distinguished by the shot `id` column's prefix
///    ("1,2,3.."/"S1.." vs "R1,R2,R3.."/"RS1,RS2.."). These are two different loads — a
///    group spanning both would corrupt the group stats this plugin exists to compute —
///    so each prefix becomes its OWN <see cref="SmString"/>. See the remarks on
///    <see cref="Read"/> for how they are split, named and numbered.
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

    // A shot id is "<letter prefix?><S?><number>": "1"/"S1" (no prefix) or "R1"/"RS1" (a
    // second, interleaved target). Non-greedy prefix so the optional "S" is only consumed
    // when it is actually there (see ExtractPrefix's doc comment for worked examples).
    private static readonly Regex IdPrefixPattern =
        new(@"^(?<prefix>[A-Za-z]*?)S?(?<num>\d+)$", RegexOptions.Compiled);

    /// <remarks>
    /// A block whose shot ids carry two distinct prefixes (the file's three "R1 TT11"
    /// strings) is split into one <see cref="SmString"/> per prefix rather than folded into
    /// one — each prefix is a different rifle/load, confirmed by summing each group's own
    /// per-shot <c>score</c> column (non-sighter shots only, X = 10): it reproduces one of
    /// the header's two declared composite scores exactly, and the mapping is in
    /// first-appearance row order (the file's own R-then-unprefixed order maps to the
    /// header's first-then-second score column). See task-5-report.md for the worked
    /// numbers on all three multi-target strings.
    ///
    /// Naming: the unprefixed group ("1,2,3.."/"S1..") always keeps the block's own name
    /// ("M6 R1 TT11"); a lettered-prefix group gets that name plus "[prefix]"
    /// ("M6 R1 TT11 [R]") so both are identifiable and distinct. (If a block ever has no
    /// unprefixed group at all, the first-appearing group keeps the plain name instead —
    /// not exercised by the fixture, whose two groups are always "" and "R".)
    ///
    /// <see cref="SmShot.Number"/> restarts at 1 within each emitted string (sighters
    /// included), the same convention <c>SmTarReader</c> uses — trivially collision-free
    /// since each group gets its own shot list. <see cref="SmString.Id"/> stays unique
    /// across the whole file via a single counter over every emitted string, not every
    /// block.
    /// </remarks>
    public static IReadOnlyList<SmString> Read(TextReader csv, IList<string> log)
    {
        var result = new List<SmString>();
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var scores = new List<string>();
        List<RawShotRow>? rows = null;
        string[]? columns = null;
        int lineNo = 0;

        void Flush()
        {
            if (rows is { Count: > 0 })
            {
                foreach (SmString s in BuildStrings(header, scores, rows, log))
                    result.Add(s with { Id = $"csv-{result.Count + 1}" });
            }
            else if (rows != null)
            {
                log.Add($"string '{HeaderValue(header, "String") ?? "?"}': no shots — skipped");
            }
            rows = null;
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
                rows = new List<RawShotRow>();
                continue;
            }

            // A non-blank first cell while we are already inside a block's shot rows means
            // this line is the NEXT block's string-header row (shot rows always start with
            // the file's leading empty column) — flush the block in progress before reading
            // it. Before any column-header row has been seen, every line is header material.
            bool isHeaderLine = columns == null || (cells.Length > 0 && cells[0].Trim().Length > 0);
            if (isHeaderLine)
            {
                if (rows != null) Flush();
                ParseHeaderLine(line, cells, header, scores);
                continue;
            }

            RawShotRow? row = ReadShotRow(cells, columns!, lineNo, log);
            if (row != null) rows!.Add(row);
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

    /// <summary>One shot row, not yet assigned a final <see cref="SmShot.Number"/> — that
    /// depends on which prefix group it ends up in, decided once the whole block has been
    /// read (see <see cref="BuildStrings"/>).</summary>
    private sealed record RawShotRow(
        string Prefix, double XMm, double YMm, double? VelocityMps,
        string? Score, double? TempC, bool IsSighter);

    private static RawShotRow? ReadShotRow(string[] cells, string[] columns, int lineNo, IList<string> log)
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
        return new RawShotRow(
            ExtractPrefix(Text("id") ?? ""),
            x.Value, y.Value,
            Col("v fps") is { } fps and > 0 ? fps * MetresPerFoot : null,
            Text("score"), Col("temp C"),
            tags.Contains("sighter", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Extracts the target-grouping prefix from a shot id: "S1"/"1" -> "" (no
    /// prefix), "RS1"/"R1" -> "R". An id that does not fit the pattern falls back to no
    /// prefix (grouped with the block's own/base target) rather than becoming its own
    /// group of one.</summary>
    private static string ExtractPrefix(string id)
    {
        if (id.Length == 0) return "";
        Match m = IdPrefixPattern.Match(id);
        return m.Success ? m.Groups["prefix"].Value : "";
    }

    private static List<SmString> BuildStrings(
        IDictionary<string, string> header, List<string> scores, List<RawShotRow> rows, IList<string> log)
    {
        string name = HeaderValue(header, "String") ?? "String";

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
        // first "<digits> x <digits>" in the field is the frame size. One physical frame is
        // shared by every target emitted from this block.
        Match fm = FrameSize.Match(HeaderValue(header, "Target") ?? "");
        double w = fm.Success ? double.Parse(fm.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        double h = fm.Success ? double.Parse(fm.Groups[2].Value, CultureInfo.InvariantCulture) : 0;

        DateTimeOffset ts = DateTimeOffset.TryParse(
            HeaderValue(header, "Date"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out DateTimeOffset t)
            ? t : DateTimeOffset.MinValue;

        string faceId = ResolveFaceId(faceName, log, name);

        // Group by id prefix, preserving each group's first-appearance row order — needed
        // both for Number (restarts at 1 per group, in original row order) and for matching
        // header score columns, which are declared in that same first-appearance order.
        var appearanceOrder = new List<string>();
        var byPrefix = new Dictionary<string, List<RawShotRow>>();
        foreach (RawShotRow r in rows)
        {
            if (!byPrefix.TryGetValue(r.Prefix, out List<RawShotRow>? list))
            {
                list = new List<RawShotRow>();
                byPrefix[r.Prefix] = list;
                appearanceOrder.Add(r.Prefix);
            }
            list.Add(r);
        }

        string baseGroup = byPrefix.ContainsKey("") ? "" : appearanceOrder[0];

        if (appearanceOrder.Count > 1 && scores.Count < appearanceOrder.Count)
            log.Add($"string '{name}': {appearanceOrder.Count} targets but only {scores.Count} declared score column(s)");

        // Emission order: the base group (plain name) first, then any additional lettered
        // groups in their first-appearance order. Cosmetic only — does not affect Number or
        // which score column a group gets (that is appearanceOrder, independent of this).
        var emissionOrder = new List<string> { baseGroup };
        emissionOrder.AddRange(appearanceOrder.Where(p => p != baseGroup));

        var result = new List<SmString>();
        foreach (string prefix in emissionOrder)
        {
            List<RawShotRow> groupRows = byPrefix[prefix];
            var shots = new List<SmShot>();
            foreach (RawShotRow r in groupRows)
                // The CSV carries no group-membership information at all — null, never a
                // guessed true/false (task 9b).
                shots.Add(new SmShot(shots.Count + 1, r.XMm, r.YMm, r.VelocityMps, r.Score, r.TempC, r.IsSighter, false, null));

            string stringName = prefix == baseGroup ? name : $"{name} [{prefix}]";
            int scoreIdx = appearanceOrder.IndexOf(prefix);
            string? scoreText = scoreIdx >= 0 && scoreIdx < scores.Count ? scores[scoreIdx] : null;

            // Id is a placeholder here — Read()'s Flush() overwrites it with a counter that
            // runs over every emitted string in the whole file, not just this block's.
            result.Add(new SmString("", stringName, ts, faceId, dist, unit, w, h, null, scoreText, shots, null));
        }
        return result;
    }

    private static string? HeaderValue(IDictionary<string, string> h, string key) =>
        h.TryGetValue(key, out string? v) ? v : null;

    /// <summary>Matches a CSV face display name ("NRA Long Range FC") against
    /// <see cref="TargetFaceLibrary.FindByName"/>. No match logs and returns "" — the same
    /// empty-id sentinel <c>SmTarReader</c> uses for a missing/unrecognised <c>face_id</c>
    /// — so a caller already written against the .tar path (<c>TargetFaceLibrary.Find(id)
    /// ?? TargetFaceLibrary.Generic(w, h)</c>) falls back to the generic face for this case
    /// too, without this reader needing to know about rendering.</summary>
    internal static string ResolveFaceId(string displayName, IList<string> log, string stringName)
    {
        TargetFace? match = TargetFaceLibrary.FindByName(displayName);
        if (match != null) return match.Id;

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
