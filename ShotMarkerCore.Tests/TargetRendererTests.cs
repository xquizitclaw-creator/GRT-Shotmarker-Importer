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

    /// <summary>
    /// GRT prints its own number beside every point it holds, in bright green, and nothing
    /// in the .grtload or the plugin API turns that off. So the picture written into the load
    /// must not carry numbers of its own, or the shooter reads every figure twice, in two
    /// colours, slightly apart. The preview keeps them: nothing else is drawing them there.
    ///
    /// <para>The sighters' discs are the other difference: the load holds no sighter
    /// (SmShot.IsImported), so the picture it carries draws none either.</para>
    ///
    /// <para>Asserting the difference is confined to the discs is the point — "the two
    /// pictures differ" would also pass if the numbers were still there and something else
    /// had changed.</para>
    /// </summary>
    [Fact]
    public void TheImportPictureCarriesNoShotNumbersOrSightersWhileThePreviewDoes()
    {
        SmString s = FirstString();
        TargetFace face = TargetFaceLibrary.Find(s.FaceId)!;

        var forGrt = TargetRenderer.Render(s, face, RenderOptions.ForGrt);
        var forPreview = TargetRenderer.Render(s, face, RenderOptions.ForPreview);

        using SKBitmap grt = SKBitmap.Decode(forGrt.Png);
        using SKBitmap preview = SKBitmap.Decode(forPreview.Png);
        // Same canvas: every shot in this fixture, sighters included, lands inside the board,
        // so leaving the sighters out of the import's picture does not change the extent. A
        // sighter thrown clear of the board legitimately would — it grows the preview it is
        // drawn on and not the import it is absent from — and that is a difference this
        // comparison could not make sense of, which is why it is pinned on this fixture.
        Assert.Equal(preview.Width, grt.Width);
        Assert.Equal(preview.Height, grt.Height);

        // The renderer's own disc geometry: the filled circle, the white edge stroke drawn
        // centred on its rim (so half of it lies outside the radius), and a pixel of
        // antialiasing slack. A whole disc missing from one picture reaches further than the
        // numeral inside it ever did.
        TargetProjection p = forGrt.Projection;
        float discRadius = Math.Max(p.Px((s.BulletDiameterMm ?? 7.2) / 2), 8);
        float reach = discRadius + Math.Max(1, discRadius / 6) / 2 + 1;
        var centres = s.Shots
            .Where(sh => !sh.IsInvalid && double.IsFinite(sh.XMm) && double.IsFinite(sh.YMm))
            .Select(sh => p.ToPixel(sh.XMm, sh.YMm))
            .ToList();

        int differing = 0;
        for (int y = 0; y < grt.Height; y++)
            for (int x = 0; x < grt.Width; x++)
            {
                if (grt.GetPixel(x, y) == preview.GetPixel(x, y)) continue;
                differing++;
                Assert.True(
                    centres.Any(c => Math.Abs(c.X - x) <= reach && Math.Abs(c.Y - y) <= reach),
                    $"pixel {x},{y} differs but is not on a shot disc — the two option sets "
                    + "differ in something other than the disc numbers");
            }

        Assert.True(differing > 0, "the import picture is identical to the preview — the "
                                 + "numbers are still being drawn into the load");

        // Sighter discs are the larger half of that difference, and they are gone from the
        // import picture entirely: the pixel at each sighter's centre is the face underneath,
        // not a disc. Without this the test would still pass with the discs intact and only
        // their numerals removed.
        foreach (SmShot sighter in s.Shots.Where(sh => sh.IsSighter && sh.IsPlottable))
        {
            var (x, y) = p.ToPixel(sighter.XMm, sighter.YMm);
            Assert.NotEqual(preview.GetPixel((int)x, (int)y), grt.GetPixel((int)x, (int)y));
        }
    }

    /// <summary>The presets are the only two callers should need, so the defaults have to be
    /// the preview's: a bare <c>new RenderOptions()</c> is what every existing test and the
    /// CLI already use, and it must keep drawing the numbers it always drew.</summary>
    [Fact]
    public void NumbersAreDrawnUnlessTheGrtPresetIsAsked()
    {
        Assert.True(new RenderOptions().DrawShotNumbers);
        Assert.True(RenderOptions.ForPreview.DrawShotNumbers);
        Assert.False(RenderOptions.ForGrt.DrawShotNumbers);
        Assert.True(new RenderOptions().DrawSighters);
        Assert.True(RenderOptions.ForPreview.DrawSighters);
        Assert.False(RenderOptions.ForGrt.DrawSighters);

        // Only those two flags differ — the preview must be the same picture, same face, same
        // furniture, or the shooter is approving a target they will not get. The two it may
        // differ in are both things GRT's own tab supplies or refuses: it draws its own numbers,
        // and it cannot hold a sighter at all (SmShot.IsImported). The window says so in its
        // caption, which is what keeps the extra discs from being a surprise.
        Assert.Equal(RenderOptions.ForPreview with { DrawShotNumbers = false, DrawSighters = false },
                     RenderOptions.ForGrt);
    }

    /// <summary>
    /// A coordinate that is not a number, on a shot the device did NOT flag. Filtering on
    /// IsInvalid alone let that through, and one of them sized the canvas to nothing:
    /// Math.Max(x, NaN) is NaN, NaN mm became 0 px, SKSurface.Create returned null and the
    /// render died on a NullReferenceException — taking the whole import with it, not just
    /// the one string. SmShot.IsPlottable is the predicate that actually holds.
    /// </summary>
    [Fact]
    public void ANonFiniteCoordinateTheDeviceDidNotFlagDoesNotPoisonThePicture()
    {
        SmString s = FirstString();
        SmString holed = s with
        {
            Shots = s.Shots.Select((sh, i) => i == 2 ? sh with { XMm = double.NaN } : sh).ToList(),
        };
        TargetFace face = TargetFaceLibrary.Find(s.FaceId)!;

        var clean = TargetRenderer.Render(s, face, RenderOptions.ForGrt);
        var r = TargetRenderer.Render(holed, face, RenderOptions.ForGrt);

        // Same canvas: the bad shot is gone, not merely survived. A NaN that reached the
        // extent would have changed every dimension here.
        Assert.Equal(clean.Width, r.Width);
        Assert.Equal(clean.Height, r.Height);
        Assert.Equal(clean.Projection.WidthMm, r.Projection.WidthMm, 9);

        // And off the banner, whose figures are a min/max over the same coordinates.
        Assert.DoesNotContain("NaN", TargetRenderer.StatsBanner(holed));
    }
}
