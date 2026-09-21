namespace ShotMarker.Core.Render;

/// <summary>
/// The single place millimetres become image coordinates. ShotMarker measures from the
/// target centre with y up; GRT stores fractions of the picture with y down. Every
/// conversion in this plugin goes through here so the two conventions meet exactly once.
///
/// <see cref="PixelsPerMm"/> is the exact scale this projection was built with. A caller
/// (Task 9's writer) reads it back rather than recomputing it from board size and pixel
/// dimensions — two independent derivations of the same number is how a plot and its
/// calibration silently drift apart.
/// </summary>
/// <param name="LeftMm">Target-space x of the image's left edge.</param>
/// <param name="TopMm">Target-space y of the image's top edge.</param>
public sealed record TargetProjection(
    double LeftMm, double TopMm, double WidthMm, double HeightMm, double PixelsPerMm)
{
    public int PixelWidth => (int)Math.Round(WidthMm * PixelsPerMm);
    public int PixelHeight => (int)Math.Round(HeightMm * PixelsPerMm);

    public (double X, double Y) ToFraction(double xMm, double yMm) =>
        ((xMm - LeftMm) / WidthMm, (TopMm - yMm) / HeightMm);

    public (float X, float Y) ToPixel(double xMm, double yMm) =>
        ((float)((xMm - LeftMm) * PixelsPerMm), (float)((TopMm - yMm) * PixelsPerMm));

    public float Px(double mm) => (float)(mm * PixelsPerMm);
}
