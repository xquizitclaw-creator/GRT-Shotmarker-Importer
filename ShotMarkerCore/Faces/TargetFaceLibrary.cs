using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace ShotMarker.Core.Faces;

/// <summary>
/// The 208 built-in ShotMarker target faces, read once from the generated
/// <c>targetfaces.json</c> resource. See <c>tools/extract-targetfaces.js</c> for how
/// that file is produced from an archived copy of the device's web bundle.
/// </summary>
public static class TargetFaceLibrary
{
    private static readonly Dictionary<string, TargetFace> Faces = Load();

    public static int Count => Faces.Count;

    public static TargetFace? Find(string faceId) =>
        Faces.TryGetValue(faceId, out var f) ? f : null;

    /// <summary>Matches a face by its display <c>Name</c> or <c>ShortName</c> — what a
    /// source that names faces in prose rather than by id (e.g. the ShotMarker CSV export,
    /// "NRA Long Range FC") has to go on. Exact match (either field, case-insensitive)
    /// first, then either string containing the other, so "NRA Long Range FC" still finds
    /// a face whose recorded name has extra punctuation or a parenthetical. Returns null
    /// when nothing matches; callers fall back to <see cref="Generic"/>.</summary>
    public static TargetFace? FindByName(string displayName)
    {
        string needle = displayName?.Trim() ?? "";
        if (needle.Length == 0) return null;

        TargetFace? exact = Faces.Values.FirstOrDefault(f =>
            string.Equals(f.Name, needle, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(f.ShortName, needle, StringComparison.OrdinalIgnoreCase));
        if (exact != null) return exact;

        return Faces.Values.FirstOrDefault(f =>
            f.Name.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
            needle.Contains(f.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A blank board of the given size: shots, centre cross and scale bar only.
    /// What an unrecognised face_id renders as, so an unknown target never aborts an import.</summary>
    public static TargetFace Generic(double widthMm, double heightMm) =>
        new("GENERIC", "Unknown target", "Unknown", widthMm, heightMm, 2,
            Array.Empty<TargetRing>(), Array.Empty<TargetPoly>(), Array.Empty<TargetText>());

    private static Dictionary<string, TargetFace> Load()
    {
        using Stream s = typeof(TargetFaceLibrary).Assembly
            .GetManifestResourceStream("ShotMarker.Core.Faces.targetfaces.json")
            ?? throw new InvalidOperationException("targetfaces.json resource missing");
        using JsonDocument doc = JsonDocument.Parse(s);

        var result = new Dictionary<string, TargetFace>(StringComparer.Ordinal);
        foreach (JsonProperty p in doc.RootElement.GetProperty("faces").EnumerateObject())
            result[p.Name] = ReadFace(p.Name, p.Value);
        return result;
    }

    private static TargetFace ReadFace(string id, JsonElement e)
    {
        JsonElement board = e.GetProperty("board");
        return new TargetFace(
            id,
            Str(e, "name") ?? id,
            Str(e, "shortname") ?? id,
            Num(board, "w"), Num(board, "h"), Num(board, "line", 2),
            ReadRings(e), ReadPolys(e), ReadTexts(e));
    }

    private static IReadOnlyList<TargetRing> ReadRings(JsonElement e)
    {
        if (!e.TryGetProperty("rings", out JsonElement rings)) return Array.Empty<TargetRing>();
        var list = new List<TargetRing>();
        foreach (JsonElement r in rings.EnumerateArray())
            list.Add(new TargetRing(Num(r, "diam"), Str(r, "color") ?? "b", Num(r, "line", 1),
                                    Score(r), Num(r, "x"), Num(r, "y")));
        return list;
    }

    private static IReadOnlyList<TargetPoly> ReadPolys(JsonElement e)
    {
        if (!e.TryGetProperty("poly", out JsonElement polys)) return Array.Empty<TargetPoly>();
        var list = new List<TargetPoly>();
        foreach (JsonElement p in polys.EnumerateArray())
        {
            var pts = new List<TargetPoint>();
            if (p.TryGetProperty("points", out JsonElement points))
                foreach (JsonElement pt in points.EnumerateArray())
                    pts.Add(new TargetPoint(Num(pt, "x"), Num(pt, "y")));
            list.Add(new TargetPoly(Str(p, "color") ?? "b", Num(p, "line", 1), pts));
        }
        return list;
    }

    private static IReadOnlyList<TargetText> ReadTexts(JsonElement e)
    {
        if (!e.TryGetProperty("text", out JsonElement texts)) return Array.Empty<TargetText>();
        var list = new List<TargetText>();
        foreach (JsonElement t in texts.EnumerateArray())
            list.Add(new TargetText(Num(t, "x"), Num(t, "y"), Num(t, "size", 20),
                                    Str(t, "color") ?? "gl", Score(t) ?? ""));
        return list;
    }

    /// <summary>Ring and text scores are numbers ("10") or letters ("X", "V"), so both come back as text.</summary>
    private static string? Score(JsonElement e)
    {
        foreach (string key in new[] { "score", "text" })
            if (e.TryGetProperty(key, out JsonElement v))
                return v.ValueKind switch
                {
                    JsonValueKind.String => v.GetString(),
                    JsonValueKind.Number => v.GetDouble().ToString("0.###", CultureInfo.InvariantCulture),
                    _ => null,
                };
        return null;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double Num(JsonElement e, string name, double fallback = 0) =>
        e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
}
