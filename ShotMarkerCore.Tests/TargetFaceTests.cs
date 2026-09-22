using System.Globalization;
using System.Linq;
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

    /// <summary>The generic face has no scoring rings — it stands in for a target we have no
    /// geometry for — but it must still carry a centre cross and a labelled scale bar. Without
    /// them an unknown face_id renders as shots floating on blank white, with nothing to read
    /// group size or point of aim against, which is the one thing the fallback exists to
    /// preserve.</summary>
    [Fact]
    public void GenericFallbackHasNoRingsButDoesCarryACentreCrossAndALabelledScaleBar()
    {
        var g = TargetFaceLibrary.Generic(1887, 1908);

        Assert.Empty(g.Rings);
        Assert.Equal(1887, g.BoardWidthMm, 1);

        // A cross through dead centre: one poly spanning x either side of 0 at y == 0, and
        // one spanning y either side of 0 at x == 0.
        Assert.Contains(g.Polys, p => p.Points.All(q => q.YMm == 0)
                                      && p.Points.Any(q => q.XMm < 0) && p.Points.Any(q => q.XMm > 0));
        Assert.Contains(g.Polys, p => p.Points.All(q => q.XMm == 0)
                                      && p.Points.Any(q => q.YMm < 0) && p.Points.Any(q => q.YMm > 0));

        // The bar is labelled with its own length, so the number on screen is checkable
        // against the drawing rather than merely decorative.
        TargetText label = Assert.Single(g.Texts);
        Assert.EndsWith(" mm", label.Text);
        double stated = double.Parse(label.Text[..^3], CultureInfo.InvariantCulture);
        Assert.Contains(g.Polys, p => p.Points.Count == 2
                                      && Math.Abs(Math.Abs(p.Points[0].XMm - p.Points[1].XMm) - stated) < 1e-9
                                      && p.Points[0].YMm == p.Points[1].YMm);
        Assert.True(stated <= g.BoardWidthMm * 0.25, "the scale bar must fit well inside the board");
    }
}
