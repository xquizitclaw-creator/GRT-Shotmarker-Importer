using System.Globalization;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Render;
using ShotMarker.Core.Sm;
using SkiaSharp;
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
                .Append(new SmShot(9001, double.NaN, double.NaN, null, null, null, false, true, null))
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

    [Theory]
    [InlineData("ShotMarker.Core.Render.Fonts.LiberationSans-Regular.ttf")]
    [InlineData("ShotMarker.Core.Render.Fonts.LiberationSans-Bold.ttf")]
    public void TheEmbeddedFontResourceLoadsAsATypeface(string resourceName)
    {
        // Ruling F35: the regression guard for someone later renaming the .ttf file or
        // changing the csproj's EmbeddedResource entries. Without this, that mistake's
        // failure mode is silent — TargetRenderer would go back to resolving a platform
        // font, exactly the defect this fix exists to remove.
        using Stream? stream = typeof(TargetRenderer).Assembly.GetManifestResourceStream(resourceName);
        Assert.NotNull(stream);

        using SKTypeface? typeface = SKTypeface.FromStream(stream);
        Assert.NotNull(typeface);
        Assert.Equal("Liberation Sans", typeface!.FamilyName);
    }

    [Fact]
    public void MatchesTheGoldenImage()
    {
        // Ruling F41: the golden is rendered with text suppressed and compared with a +/-2
        // per-channel tolerance, because SkiaSharp is not byte-reproducible across platforms
        // for *any* content. Measured on the same tree and the same fixture, macOS against
        // Windows: with text drawn, 11587 pixels differ by up to 217; with text suppressed,
        // 6570 differ by exactly 1 and the geometry is pixel-identical. So +/-2 is portable
        // with margin, and still a real tripwire — a disc that moves, recolours or resizes
        // shifts its pixels by 100 or more.
        //
        // A percentage-of-pixels threshold was rejected on arithmetic: one moved 20px-radius
        // disc touches ~0.2% of the image, which is *under* the ~1% platform noise, so the
        // threshold loose enough to tolerate the noise is blind to the regression.
        SmString s = FirstString();
        var r = TargetRenderer.Render(s, TargetFaceLibrary.Find(s.FaceId)!, new RenderOptions(DrawText: false));
        string golden = Fixtures.Path("golden/nra_lrfc_m1r2.png");

        if (Environment.GetEnvironmentVariable("SHOTMARKER_WRITE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(golden)!);
            File.WriteAllBytes(golden, r.Png);
        }

        Assert.True(File.Exists(golden), "run once with SHOTMARKER_WRITE_GOLDEN=1, then eyeball the PNG");
        AssertPixelsMatch(File.ReadAllBytes(golden), r.Png, tolerance: 2);
    }

    /// <summary>Compares two PNGs allowing each channel to differ by <paramref name="tolerance"/>.
    /// Dimensions are asserted exactly first — a size change is a real regression and must not
    /// be absorbed by the tolerance — and a failure reports how many pixels were out and the
    /// worst delta, because "arrays differ" is what made this expensive to diagnose.</summary>
    private static void AssertPixelsMatch(byte[] expectedPng, byte[] actualPng, int tolerance)
    {
        using SKBitmap expected = SKBitmap.Decode(expectedPng);
        using SKBitmap actual = SKBitmap.Decode(actualPng);

        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));

        int outOfTolerance = 0, worst = 0;
        (int X, int Y) worstAt = (0, 0);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                SKColor e = expected.GetPixel(x, y), a = actual.GetPixel(x, y);
                int delta = Math.Max(
                    Math.Max(Math.Abs(e.Red - a.Red), Math.Abs(e.Green - a.Green)),
                    Math.Max(Math.Abs(e.Blue - a.Blue), Math.Abs(e.Alpha - a.Alpha)));
                if (delta <= tolerance) continue;
                outOfTolerance++;
                if (delta > worst) (worst, worstAt) = (delta, (x, y));
            }
        }

        Assert.True(outOfTolerance == 0,
            $"{outOfTolerance} of {expected.Width * expected.Height} pixels differ by more than "
            + $"{tolerance}; worst delta {worst} at ({worstAt.X}, {worstAt.Y}). "
            + "Rerun with SHOTMARKER_WRITE_GOLDEN=1 and eyeball the PNG if this change was intended.");
    }

    [Fact]
    public void TheStatsBannerReadsTheDeviceFigures()
    {
        // Ruling F41 took every glyph out of the golden image, so the banner's wording and
        // figures are asserted here instead — as the string logic they actually are. These
        // are ShotMarker's own numbers for M1 R2 TT11, straight out of the .tar.
        SmString s = FirstString();
        string banner = TargetRenderer.StatsBanner(s);

        Assert.StartsWith("size ", banner);
        Assert.Contains(" mm", banner);
        Assert.Contains("w ", banner);
        Assert.Contains("h ", banner);
        Assert.Contains($"size {s.Stats!.GroupSizeMm!.Value.ToString("0.0", CultureInfo.InvariantCulture)} mm", banner);
        Assert.Contains($"mr {s.Stats.MeanRadiusMm!.Value.ToString("0.0", CultureInfo.InvariantCulture)}", banner);
        Assert.Contains($"v {s.Stats.VelocityAvgMps!.Value.ToString("0", CultureInfo.InvariantCulture)} m/s", banner);
        Assert.Contains($"sd {s.Stats.VelocitySdMps!.Value.ToString("0.0", CultureInfo.InvariantCulture)}", banner);
        Assert.Contains($"es {s.Stats.VelocityEsMps!.Value.ToString("0.0", CultureInfo.InvariantCulture)}", banner);
    }

    [Fact]
    public void SuppressingTextActuallyRemovesInk()
    {
        // The guard against someone later defaulting DrawText off by accident and silently
        // shipping pictures with no shot numbers on them — which the golden, now text-free,
        // would no longer notice.
        SmString s = FirstString();
        TargetFace f = TargetFaceLibrary.Find(s.FaceId)!;
        var withText = TargetRenderer.Render(s, f, new RenderOptions(DrawText: true));
        var without = TargetRenderer.Render(s, f, new RenderOptions(DrawText: false));

        Assert.Equal(withText.Width, without.Width);
        Assert.Equal(withText.Height, without.Height);
        Assert.NotEqual(withText.Png, without.Png);
        Assert.True(new RenderOptions().DrawText, "text must stay on by default");
    }
}
