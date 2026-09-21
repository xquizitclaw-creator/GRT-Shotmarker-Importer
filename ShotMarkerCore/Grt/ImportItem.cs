using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;

namespace ShotMarker.Core.Grt;

/// <summary>One string queued for import: what was shot, the picture drawn of it, and the
/// charge behind it.</summary>
/// <param name="String">The parsed ShotMarker string. Coordinates are millimetres from the
/// target centre with y up; they are never converted anywhere but through
/// <paramref name="Render"/>'s projection.</param>
/// <param name="Render">The picture GRT will show, together with the exact projection it was
/// drawn at. Ruling S2: the writer reads its scale from here rather than deriving one of its
/// own, because two derivations of the same number is how a plot and its calibration silently
/// drift apart.</param>
/// <param name="ChargeGrains">The powder charge this string was shot with. A ShotMarker
/// string is normally one charge; a ladder shot across several strings gives each its own,
/// which is why this is per-item and editable rather than read from the load.</param>
public sealed record ImportItem(SmString String, RenderedTarget Render, double? ChargeGrains);
