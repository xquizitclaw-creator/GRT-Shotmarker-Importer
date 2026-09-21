namespace ShotMarker.Core.Sm;

/// <summary>One shot. Position is millimetres from target centre with y up, as ShotMarker
/// reports it; velocity is normalised to m/s at the reader boundary whatever the source said.</summary>
/// <param name="InSelectedGroup">Whether ShotMarker counted this shot in the group it had
/// selected. Null when the source does not say — the CSV export carries no group
/// information.</param>
public sealed record SmShot(
    int Number, double XMm, double YMm, double? VelocityMps,
    string? Score, double? TempC, bool IsSighter, bool IsInvalid,
    bool? InSelectedGroup)
{
    /// <summary>Shots GRT should exclude from group statistics: sighters, rejects, and —
    /// when the source says so — shots ShotMarker itself left out of the group it had
    /// selected on the device. <c>InSelectedGroup == false</c>, not <c>!= true</c>: a null
    /// (the source does not say, e.g. the CSV export) must never make a shot a flyer, or
    /// every group would silently lose every shot.</summary>
    public bool IsFlyer => IsSighter || IsInvalid || InSelectedGroup == false;
}

/// <summary>ShotMarker's own computed statistics for a group, carried through unaltered
/// so the note this plugin writes says what the device said.</summary>
public sealed record SmGroupStats(
    double? MeanRadiusMm, double? GroupSizeMm, double? CtcMm,
    double? VelocityAvgMps, double? VelocitySdMps, double? VelocityEsMps);

/// <summary>One shooting string, the same shape whether it came from a .tar or a .csv.</summary>
public sealed record SmString(
    string Id, string Name, DateTimeOffset Timestamp,
    string FaceId, double DistanceValue, string DistanceUnit,
    double FrameWidthMm, double FrameHeightMm,
    double? BulletDiameterMm, string? ScoreText,
    IReadOnlyList<SmShot> Shots, SmGroupStats? Stats)
{
    private const double MetresPerYard = 0.9144;

    public double DistanceMetres =>
        DistanceUnit.StartsWith("y", StringComparison.OrdinalIgnoreCase)
            ? DistanceValue * MetresPerYard
            : DistanceValue;
}
