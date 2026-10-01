using ShotMarker.Core.Sm;
using Xunit;

namespace ShotMarker.Core.Tests;

/// <summary>The position codes and words here are ShotMarker's own, read out of the device's
/// interface: a two-up frame labels slots 1 and 2 "Right" and "Left", a three-up "Right",
/// "Middle", "Left", a team frame "One" through "Four", and the matching shot-id prefixes are
/// R/L, R/M/L and A/B/C/D. The fixture only exercises the two-up case, so the rest is covered
/// here rather than left to be discovered on a three-up mound.</summary>
public class SmFiringPointTests
{
    [Theory]
    [InlineData("R", "Right")]
    [InlineData("M", "Middle")]
    [InlineData("L", "Left")]
    [InlineData("A", "One")]
    [InlineData("D", "Four")]
    public void EveryPositionCodeReadsAsTheWordTheDeviceShows(string code, string word) =>
        Assert.Equal(word, SmFiringPoint.Word(code));

    [Fact]
    public void AnUnknownCodeIsShownAsItselfRatherThanGuessedAt()
    {
        // A letter in brackets is a poor label but an honest one. Inventing a side would put
        // a shooter on the wrong part of the mound, which is worse than saying "Z".
        Assert.Equal("Z", SmFiringPoint.Word("Z"));
    }

    [Theory]
    // Two-up: one prefix, so the bare group is the other point.
    [InlineData(2, "R", "L")]
    [InlineData(2, "L", "R")]
    // Three-up: two prefixes, so the bare group is the one seat left.
    [InlineData(3, "R,M", "L")]
    [InlineData(3, "M,L", "R")]
    [InlineData(3, "R,L", "M")]
    // Team frame, same rule.
    [InlineData(4, "A,B,C", "D")]
    public void TheUnprefixedGroupIsTheSeatTheOthersLeaveEmpty(int points, string prefixed, string expected) =>
        Assert.Equal(expected, SmFiringPoint.ByElimination(prefixed.Split(','), points));

    [Theory]
    // A lone target has no position at all — nothing to eliminate towards.
    [InlineData(1, "")]
    // Two groups but the prefix belongs to a three-up: the bare one could be Right or Left,
    // so it is left unnamed. Guessing here is how a shooter ends up told the next lane's
    // shots are theirs.
    [InlineData(2, "M")]
    // Every seat prefixed: there is no bare group for this question to be about.
    [InlineData(2, "R,L")]
    // More groups than any scheme the device has.
    [InlineData(5, "R,M,L,A")]
    public void AnAmbiguousFrameYieldsNoPointRatherThanAGuess(int points, string prefixed) =>
        Assert.Null(SmFiringPoint.ByElimination(
            prefixed.Length == 0 ? System.Array.Empty<string>() : prefixed.Split(','), points));

    [Fact]
    public void PointsAreListedInTheDevicesSlotOrderNotAlphabeticallyOrInFiringOrder()
    {
        // Left is slot 2 of a two-up but slot 3 of a three-up, so the scheme has to be chosen
        // from the whole set. Resolving each code on its own puts Left ahead of Middle.
        Assert.Equal(new[] { "R", "L" }, SmFiringPoint.InSlotOrder(new[] { "L", "R" }));
        Assert.Equal(new[] { "R", "M", "L" }, SmFiringPoint.InSlotOrder(new[] { "L", "R", "M" }));
        Assert.Equal(new[] { "R", "M" }, SmFiringPoint.InSlotOrder(new[] { "M", "R" }));
        Assert.Equal(new[] { "A", "B", "C" }, SmFiringPoint.InSlotOrder(new[] { "C", "A", "B" }));
    }

    [Fact]
    public void ARepeatedPointIsListedOnce()
    {
        // The caller passes one code per string, and a match gives a shooter several strings
        // from the same point.
        Assert.Equal(new[] { "R", "L" }, SmFiringPoint.InSlotOrder(new[] { "L", "R", "L", "R", "L" }));
    }
}
