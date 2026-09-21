namespace ShotMarker.Core.Render;

/// <param name="PixelsPerMm">Drawing scale. A 72 inch board at 0.6 px/mm is about 1100 px,
/// which is legible in GRT without making the base64 payload enormous.</param>
/// <param name="MarginMm">Space beyond the board, so a shot off the edge still appears.</param>
/// <param name="DrawFurniture">ShotMarker's own overlay: numbered discs, group box and
/// statistics. GRT draws its own on top, so this can be turned off for a clean face.</param>
public sealed record RenderOptions(
    double PixelsPerMm = 0.6, double MarginMm = 60, bool DrawFurniture = true);

public sealed record RenderedTarget(byte[] Png, int Width, int Height, TargetProjection Projection);
