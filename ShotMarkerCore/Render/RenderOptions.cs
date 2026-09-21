namespace ShotMarker.Core.Render;

/// <param name="PixelsPerMm">Drawing scale. A 72 inch board at 0.6 px/mm is about 1100 px,
/// which is legible in GRT without making the base64 payload enormous.</param>
/// <param name="MarginMm">Clearance added beyond a shot that falls outside the board, so it
/// still appears with room around it rather than sitting on the canvas edge (sizing-reference
/// ruling S1). The base extent is the board's own true mm size — this margin only comes into
/// play once a shot needs the canvas to grow past that.</param>
/// <param name="DrawFurniture">ShotMarker's own overlay: numbered discs, group box and
/// statistics. GRT draws its own on top, so this can be turned off for a clean face.</param>
/// <param name="DrawText">Every piece of lettering: the face's own ring numerals, the number
/// on each shot disc, and the statistics banner. Ruling F41: SkiaSharp hands glyph
/// rasterization to the platform font host — CoreText on macOS, DirectWrite on Windows — so
/// the same embedded typeface still produces different ink on each machine. Turning text off
/// leaves only geometry, which the two platforms agree on to within one bit of antialiasing
/// coverage, and that is what makes a portable golden image possible. Nothing but the golden
/// test should ever set this false: a picture with no numbers on it is useless to a shooter.
/// </param>
public sealed record RenderOptions(
    double PixelsPerMm = 0.6, double MarginMm = 25, bool DrawFurniture = true, bool DrawText = true);

public sealed record RenderedTarget(byte[] Png, int Width, int Height, TargetProjection Projection);
