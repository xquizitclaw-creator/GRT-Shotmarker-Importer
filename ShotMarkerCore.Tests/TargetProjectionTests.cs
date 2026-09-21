using ShotMarker.Core.Render;
using Xunit;

namespace ShotMarker.Core.Tests;

public class TargetProjectionTests
{
    // A 1000 x 800 mm canvas whose top-left is 500 mm left of and 400 mm above centre.
    private static readonly TargetProjection P = new(-500, 400, 1000, 800, 0.5);

    [Fact]
    public void CentreOfTheTargetIsTheCentreOfTheImage()
    {
        var (x, y) = P.ToFraction(0, 0);
        Assert.Equal(0.5, x, 9);
        Assert.Equal(0.5, y, 9);
    }

    [Fact]
    public void ImageYGrowsDownwardWhileTargetYGrowsUp()
    {
        // A hit 200 mm ABOVE centre must land ABOVE the middle of the image, i.e. y < 0.5.
        var (_, high) = P.ToFraction(0, 200);
        var (_, low) = P.ToFraction(0, -200);
        Assert.Equal(0.25, high, 9);
        Assert.Equal(0.75, low, 9);
    }

    [Fact]
    public void CornersMapToTheUnitSquare()
    {
        Assert.Equal((0.0, 0.0), P.ToFraction(-500, 400));
        Assert.Equal((1.0, 1.0), P.ToFraction(500, -400));
    }

    [Fact]
    public void PixelsFollowTheScale()
    {
        var (x, y) = P.ToPixel(0, 0);
        Assert.Equal(250f, x, 3);   // 1000 mm * 0.5 px/mm / 2
        Assert.Equal(200f, y, 3);
    }
}
