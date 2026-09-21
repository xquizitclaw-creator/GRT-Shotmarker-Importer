using ShotMarker.Core.Faces;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;
using Xunit;

namespace ShotMarker.Core.Tests;

public class TargetRendererTests
{
    private static SmString FirstString()
    {
        var log = new List<string>();
        return SmExportReader.Read(Fixtures.Path("shotmarker/SM_export_Sep_21.tar"), log).First();
    }

    [Fact]
    public void ProducesAPngBigEnoughToShowTheFace()
    {
        SmString s = FirstString();
        var r = TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!);

        Assert.Equal(0x89, r.Png[0]);
        Assert.Equal((byte)'P', r.Png[1]);
        Assert.True(r.Width >= 800, $"image only {r.Width} px wide");
        Assert.Equal(r.Width, r.Projection.PixelWidth);
        Assert.Equal(r.Height, r.Projection.PixelHeight);
    }

    [Fact]
    public void EveryShotLandsInsideThePicture()
    {
        SmString s = FirstString();
        var r = TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!);
        foreach (SmShot sh in s.Shots)
        {
            var (x, y) = r.Projection.ToFraction(sh.XMm, sh.YMm);
            Assert.InRange(x, 0, 1);
            Assert.InRange(y, 0, 1);
        }
    }

    [Fact]
    public void TheFaceIsDrawnAtTrueScale()
    {
        SmString s = FirstString();
        TargetFace f = TargetFaceLibrary.Find("NRA_LRFC")!;
        var r = TargetRenderer.Render(s, f);

        // The 5 inch X ring must measure 5 inches on the picture, via the projection.
        // Selected by Score == "X" (ruling F8) rather than Rings[0]: ring order in the
        // library is draw order, not score order, and happens to coincide here only by luck.
        TargetRing xRing = f.Rings.First(ring => ring.Score == "X");
        var (left, _) = r.Projection.ToFraction(-xRing.DiamMm / 2, 0);
        var (right, _) = r.Projection.ToFraction(xRing.DiamMm / 2, 0);
        double ringMm = (right - left) * r.Projection.WidthMm;
        Assert.Equal(5 * 25.4, ringMm, 1);
    }

    [Fact]
    public void AnUnknownFaceStillRenders()
    {
        SmString s = FirstString();
        var r = TargetRenderer.Render(s, TargetFaceLibrary.Generic(s.FrameWidthMm, s.FrameHeightMm));
        Assert.True(r.Png.Length > 0);
    }

    [Fact]
    public void FurnitureCanBeTurnedOff()
    {
        // GRT draws its own group box and statistics over the picture, so a clean face
        // has to stay one option away.
        SmString s = FirstString();
        TargetFace f = TargetFaceLibrary.Find(s.FaceId)!;
        var withFurniture = TargetRenderer.Render(s, f, new RenderOptions(DrawFurniture: true));
        var without = TargetRenderer.Render(s, f, new RenderOptions(DrawFurniture: false));
        Assert.NotEqual(withFurniture.Png.Length, without.Png.Length);
        Assert.Equal(withFurniture.Width, without.Width);
    }

    [Fact]
    public void RenderingIsDeterministic()
    {
        // A golden-image test is only worth writing if the renderer repeats itself.
        SmString s = FirstString();
        TargetFace f = TargetFaceLibrary.Find(s.FaceId)!;
        Assert.Equal(TargetRenderer.Render(s, f).Png, TargetRenderer.Render(s, f).Png);
    }

    [Fact]
    public void InvalidShotsWithNaNCoordinatesDoNotPoisonTheRender()
    {
        // Errored/fake shots carry double.NaN coordinates (see SmShot.IsInvalid). A single
        // NaN reaching the bounding-box computation must not turn the whole canvas into
        // NaN x NaN and silently destroy the scale for every other shot on the picture.
        SmString s = FirstString();
        var withInvalid = s with
        {
            Shots = s.Shots
                .Append(new SmShot(9001, double.NaN, double.NaN, null, null, null, false, true))
                .ToList(),
        };
        TargetFace f = TargetFaceLibrary.Find(s.FaceId)!;

        var r = TargetRenderer.Render(withInvalid, f);

        Assert.InRange(r.Width, 1, 100_000);
        Assert.InRange(r.Height, 1, 100_000);
        Assert.False(double.IsNaN(r.Projection.WidthMm));
        Assert.False(double.IsNaN(r.Projection.HeightMm));
        foreach (SmShot sh in withInvalid.Shots.Where(sh => !sh.IsInvalid))
        {
            var (x, y) = r.Projection.ToFraction(sh.XMm, sh.YMm);
            Assert.InRange(x, 0, 1);
            Assert.InRange(y, 0, 1);
        }
    }

    [Fact]
    public void MatchesTheGoldenImage()
    {
        SmString s = FirstString();
        var r = TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!);
        string golden = Fixtures.Path("golden/nra_lrfc_m6r1.png");

        if (Environment.GetEnvironmentVariable("SHOTMARKER_WRITE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllBytes(golden, r.Png);
        }

        Assert.True(File.Exists(golden), "run once with SHOTMARKER_WRITE_GOLDEN=1, then eyeball the PNG");
        Assert.Equal(File.ReadAllBytes(golden), r.Png);
    }
}
