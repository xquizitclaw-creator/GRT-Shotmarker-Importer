using ShotMarker.Core.Faces;
using Xunit;

namespace ShotMarker.Core.Tests;

public class TargetFaceTests
{
    [Fact]
    public void LoadsEveryFaceFromTheBundle()
    {
        Assert.Equal(208, TargetFaceLibrary.Count);
    }

    [Fact]
    public void NraLongRangeFClassMatchesThePublishedSpecification()
    {
        var f = TargetFaceLibrary.Find("NRA_LRFC");
        Assert.NotNull(f);

        const double mmPerInch = 25.4;
        Assert.Equal(72 * mmPerInch, f!.BoardWidthMm, 1);
        Assert.Equal(72 * mmPerInch, f.BoardHeightMm, 1);

        // X=5", 10=10", 9=20", 8=30", 7=44", 6=60" — the user confirmed the 5" X ring.
        var byScore = f.Rings
            .Where(r => r.Score is not null)
            .GroupBy(r => r.Score!)
            .ToDictionary(g => g.Key, g => g.Last().DiamMm / mmPerInch);
        Assert.Equal(5, byScore["X"], 1);
        Assert.Equal(10, byScore["10"], 1);
        Assert.Equal(20, byScore["9"], 1);
        Assert.Equal(30, byScore["8"], 1);
        Assert.Equal(44, byScore["7"], 1);
        Assert.Equal(60, byScore["6"], 1);
    }

    [Fact]
    public void FacesWithOffsetRingsAndPolygonsSurvive()
    {
        // IBS 100yd BR has 14 rings placed off-centre; it is the face an expression-blind
        // extractor loses, so it stands guard over the extraction method.
        var ibs = TargetFaceLibrary.Find("IBS100BR");
        Assert.NotNull(ibs);
        Assert.Equal(14, ibs!.Rings.Count);
        Assert.Contains(ibs.Rings, r => r.XMm != 0 || r.YMm != 0);
    }

    [Fact]
    public void UnknownFaceIdReturnsNull()
    {
        Assert.Null(TargetFaceLibrary.Find("NO_SUCH_FACE"));
    }

    [Fact]
    public void GenericFallbackHasNoRings()
    {
        var g = TargetFaceLibrary.Generic(1887, 1908);
        Assert.Empty(g.Rings);
        Assert.Equal(1887, g.BoardWidthMm, 1);
    }
}
