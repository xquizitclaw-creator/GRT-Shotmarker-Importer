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
/// <param name="DrawShotNumbers">The number printed inside each shot disc. GRT draws its own
/// label beside every point it holds, in <c>color_shotgroup_group</c> (bright green by
/// default), and that is not something a .grtload or a plugin can switch off. So on the
/// picture handed to GRT these would be a second set of numerals over the first — the same
/// figure twice, in two colours, slightly offset. Off for the import, on for the preview,
/// where nothing else is drawing them and the shooter needs them to pick a shot out.</param>
/// <param name="DrawSighters">The sighters' own red discs. Off for the picture GRT gets,
/// because the points written into the load leave the sighters out — see
/// <see cref="Sm.SmShot.IsImported"/> for why there is nothing to write them as. The two have
/// to move together or the face grows discs with no hit under them. On for the preview, where
/// the shot list beside it names them as sighters and the caption says they are not imported,
/// so the shooter can see where they went without being shown a target they will not get.
/// Dropping them also keeps a sighter thrown a metre wide from rescaling a picture it does not
/// appear on.</param>
public sealed record RenderOptions(
    double PixelsPerMm = 0.6, double MarginMm = 25, bool DrawFurniture = true,
    bool DrawText = true, bool DrawShotNumbers = true, bool DrawSighters = true)
{
    /// <summary>The picture written into the load. GRT numbers the points itself, so this one
    /// does not, and the load holds no sighters, so this one draws none.</summary>
    public static RenderOptions ForGrt { get; } =
        new() { DrawShotNumbers = false, DrawSighters = false };

    /// <summary>The picture shown in the import window. Identical to <see cref="ForGrt"/> but
    /// for the disc numbers, which only this one draws because here there is no GRT underneath
    /// to draw them, and the sighters, which only this one draws because here they are
    /// something to look at rather than something to import.</summary>
    public static RenderOptions ForPreview { get; } = new();
}

public sealed record RenderedTarget(byte[] Png, int Width, int Height, TargetProjection Projection);
