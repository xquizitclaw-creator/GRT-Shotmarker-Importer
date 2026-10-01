namespace ShotMarker.Core.Sm;

/// <summary>One shot. Position is millimetres from target centre with y up, as ShotMarker
/// reports it; velocity is normalised to m/s at the reader boundary whatever the source said.</summary>
/// <param name="InSelectedGroup">Whether ShotMarker counted this shot in the group it had
/// selected. Null when the source does not say — the CSV export carries no group
/// information.</param>
/// <param name="IsExcludedOnDevice">The shooter (or the device) marked this shot as one that
/// does not count: hidden or off-target. It still has real coordinates, so this plugin plots
/// it, but none of ShotMarker's own group or velocity statistics include it. (How the device
/// itself treats it varies — a hidden shot is greyed, an off-target one is suppressed unless
/// off_target_mode is "show" — which is why the decision here rests on the statistics
/// functions rather than on what the screen does.) A `simulated` shot is deliberately NOT
/// covered: ShotMarker's statistics count it, so this plugin counts it too.</param>
/// <param name="IsExcludedByUser">The shooter struck this shot out in the import window. Never
/// set by a reader — no export carries it, because it is a decision made here, after the
/// export was written. Deliberately separate from <see cref="IsExcludedOnDevice"/>: what the
/// device decided and what the shooter decided are different facts about the shot, and the
/// note has to be able to report them apart. Like a device exclusion it keeps its real
/// coordinates and is still plotted — struck out, not deleted — so the shooter can see what
/// they excluded.</param>
public sealed record SmShot(
    int Number, double XMm, double YMm, double? VelocityMps,
    string? Score, double? TempC, bool IsSighter, bool IsInvalid,
    bool? InSelectedGroup, bool IsExcludedOnDevice = false, bool IsExcludedByUser = false)
{
    /// <summary>Shots GRT should exclude from group statistics: sighters, rejects, shots the
    /// device itself marks as not counting, and — when the source says so — shots ShotMarker
    /// left out of the group it had selected. <c>InSelectedGroup == false</c>, not
    /// <c>!= true</c>: a null (the source does not say, e.g. the CSV export) must never make a
    /// shot a flyer, or every group would silently lose every shot.
    ///
    /// <see cref="IsExcludedOnDevice"/> is checked directly rather than left to group
    /// membership. A hidden shot is normally absent from the selected group too, so the two
    /// usually agree — but only when a group exists at all. With no group selected,
    /// <c>InSelectedGroup</c> is null for every shot, and relying on it alone imported a
    /// deliberately hidden cross-fire as an ordinary scoring hit.</summary>
    public bool IsFlyer => IsSighter || IsInvalid || IsExcludedOnDevice || IsExcludedByUser
                           || InSelectedGroup == false;

    /// <summary>Whether this shot has a position anything can draw or measure.
    ///
    /// <para><see cref="IsInvalid"/> alone is not that test. It is the device's own flag on an
    /// errored or fake shot, and a shot it did not flag can still reach us with a coordinate
    /// that is not a number. One such coordinate poisons every bounding box it enters —
    /// <c>Math.Max(x, NaN)</c> is NaN — which silently rescales the whole picture, or sizes
    /// the canvas to nothing and kills the render outright. So both facts are one predicate,
    /// checked in the one place, by everything that reads <see cref="XMm"/>.</para></summary>
    public bool IsPlottable => !IsInvalid && double.IsFinite(XMm) && double.IsFinite(YMm);

    /// <summary>Whether this shot is one of the hits the import hands GRT: a shot with a
    /// drawable position that is not a sighter.
    ///
    /// <para>A GRT shot point carries one exclusion bit and nothing else — scoring shot or
    /// flyer, with flyer being GRT's own word for a reject it prints beside the hit as
    /// "Flyer #N". There is no third state to write a sighter as. Written as scoring shots
    /// they would wreck the group; written as flyers they would label shots the shooter fired
    /// on purpose, to find the wind, as rejects — and in a Match 1 string, where sighters are
    /// unlimited, that labelling is most of the tab. So they are left out of the load instead,
    /// and GRT's tab holds the record shots alone.</para>
    ///
    /// <para>The renderer filters on this too (<c>RenderOptions.DrawSighters</c> is off for
    /// the picture GRT gets), which is what keeps the discs drawn and the points written the
    /// same set. Sighters stay in <see cref="SmString.Shots"/> throughout: the import window
    /// lists them, its preview draws them, and the note counts them.</para></summary>
    public bool IsImported => IsPlottable && !IsSighter;
}

/// <summary>ShotMarker's own computed statistics for a group, carried through unaltered
/// so the note this plugin writes says what the device said.</summary>
public sealed record SmGroupStats(
    double? MeanRadiusMm, double? GroupSizeMm, double? CtcMm,
    double? VelocityAvgMps, double? VelocitySdMps, double? VelocityEsMps);

/// <summary>The firing points of a pair- or triple-fire frame, in ShotMarker's own
/// vocabulary. One sensor frame can carry several shooters' targets, and it tells them apart
/// by prefixing each shot id with a position code — the letter the device puts in front of
/// the shot number for every shooter except the one currently selected on its screen.
///
/// <para>The codes and the words are ShotMarker's, not ours, read out of the device's own
/// interface: a two-up frame labels its score columns "Right" and "Left" for slots 1 and 2,
/// a three-up "Right", "Middle", "Left", and a team frame "One" through "Four", while the
/// matching shot-id prefixes are R/L, R/M/L and A/B/C/D. Mapping them any other way would
/// put a shooter on the wrong side of the mound.</para></summary>
public static class SmFiringPoint
{
    // Index is the device's slot number minus one, so the code and the word at the same
    // index are the same firing point. A frame's shooters are always the first N of a row.
    private static readonly string[][] Codes =
    {
        new[] { "R", "L" },
        new[] { "R", "M", "L" },
        new[] { "A", "B", "C", "D" },
    };

    private static readonly string[][] Words =
    {
        new[] { "Right", "Left" },
        new[] { "Right", "Middle", "Left" },
        new[] { "One", "Two", "Three", "Four" },
    };

    /// <summary>The word a shooter would see on the device for a position code, or the code
    /// itself if the device ever uses one this does not know — a letter in brackets is a
    /// poor label but an honest one, where inventing a side would not be.</summary>
    public static string Word(string code)
    {
        for (int scheme = 0; scheme < Codes.Length; scheme++)
        {
            int i = Array.IndexOf(Codes[scheme], code);
            if (i >= 0) return Words[scheme][i];
        }
        return code;
    }

    /// <summary>The distinct codes given, ordered by the device's own slot numbering — Right
    /// before Left on a two-up, Right/Middle/Left on a three-up — so a list of them reads the
    /// way the tablet's score columns do rather than in firing order. A code from no known
    /// scheme sorts last rather than being dropped.</summary>
    public static List<string> InSlotOrder(IEnumerable<string> codes)
    {
        var present = codes.Distinct().ToList();

        // The scheme has to be chosen from the whole set, not per code: "L" is slot 2 of a
        // two-up but slot 3 of a three-up, so asking each code for its slot on its own puts
        // Left ahead of Middle.
        foreach (string[] scheme in Codes)
            if (present.All(scheme.Contains))
                return present.OrderBy(c => Array.IndexOf(scheme, c)).ToList();

        return present.OrderBy(c => c, StringComparer.Ordinal).ToList();
    }

    /// <summary>The position of the one group whose shots the export left unprefixed, worked
    /// out from the positions that are prefixed: on a frame of <paramref name="pointCount"/>
    /// shooters, the bare group is whichever position is not accounted for. Null when that
    /// cannot be settled — a lone target with no positions at all, or a frame where some
    /// position did not fire, leaving two candidates for the empty seat. Null is the right
    /// answer there and a guess is not: it decides which shots the shooter is told are
    /// theirs.</summary>
    public static string? ByElimination(IReadOnlyCollection<string> prefixed, int pointCount)
    {
        foreach (string[] scheme in Codes)
        {
            if (scheme.Length != pointCount) continue;
            if (!prefixed.All(p => scheme.Contains(p))) continue;
            var missing = scheme.Where(c => !prefixed.Contains(c)).ToList();
            if (missing.Count == 1) return missing[0];
        }
        return null;
    }
}

/// <summary>One shooting string, the same shape whether it came from a .tar or a .csv.</summary>
/// <param name="FiringPoint">Which point on the mound shot this string, as a
/// <see cref="SmFiringPoint"/> code ("R", "L", "M"), or null when the frame carried one
/// target or the point could not be established. Everything read from a .tar is null, since
/// an archived session file holds one target per string.
///
/// <para>The fact, not the "[Right]" in <see cref="Name"/>, which is only how this is shown.
/// The import window reads this to decide what to tick, and whose shots a shooter takes home
/// is not something to settle by searching a display string for a bracket.</para></param>
/// <param name="WasSelectedOnDevice">True for the one group per block whose shot ids carry no
/// position prefix at all — the shooter the tablet had selected when the export was made.
/// That is weak evidence but it is real evidence, and on an export a shooter made themselves
/// it is nearly always them, so the import window opens on that point rather than on a
/// coin-toss. False for every prefixed group, and false for all of them on the rare export
/// where no position was selected.</param>
public sealed record SmString(
    string Id, string Name, DateTimeOffset Timestamp,
    string FaceId, double DistanceValue, string DistanceUnit,
    double FrameWidthMm, double FrameHeightMm,
    double? BulletDiameterMm, string? ScoreText,
    IReadOnlyList<SmShot> Shots, SmGroupStats? Stats,
    string? FiringPoint = null, bool WasSelectedOnDevice = false)
{
    private const double MetresPerYard = 0.9144;

    public double DistanceMetres =>
        DistanceUnit.StartsWith("y", StringComparison.OrdinalIgnoreCase)
            ? DistanceValue * MetresPerYard
            : DistanceValue;
}
