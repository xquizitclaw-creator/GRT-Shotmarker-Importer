namespace ShotMarker.Core.Faces;

/// <summary>A point on the face, millimetres from centre, y up (ShotMarker's convention).</summary>
public sealed record TargetPoint(double XMm, double YMm);

/// <summary>One scoring ring. <paramref name="Color"/> is ShotMarker's code:
/// w/b/g white, black, grey, and the "l" suffix (wl/bl/gl) meaning line-only.</summary>
public sealed record TargetRing(double DiamMm, string Color, double LineMm, string? Score,
                                double XMm = 0, double YMm = 0);

public sealed record TargetPoly(string Color, double LineMm, IReadOnlyList<TargetPoint> Points);

public sealed record TargetText(double XMm, double YMm, double SizeMm, string Color, string Text);

public sealed record TargetFace(
    string Id, string Name, string ShortName,
    double BoardWidthMm, double BoardHeightMm, double BoardLineMm,
    IReadOnlyList<TargetRing> Rings,
    IReadOnlyList<TargetPoly> Polys,
    IReadOnlyList<TargetText> Texts);
