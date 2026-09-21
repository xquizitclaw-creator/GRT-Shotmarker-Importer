using System.Globalization;
using ShotMarker.Core.Faces;
using ShotMarker.Core.Sm;
using SkiaSharp;

namespace ShotMarker.Core.Render;

/// <summary>
/// Draws a ShotMarker string onto its scoring face and returns the picture together with
/// the projection used. The projection is returned rather than recomputed by the caller
/// because two independent derivations of the same mapping is how a plot silently drifts
/// out of scale.
/// </summary>
public static class TargetRenderer
{
    // Ruling F35: Helvetica only exists on macOS. GRT ships on Windows, where SkiaSharp
    // would silently substitute some other face — a render the user never approved.
    // Liberation Sans is embedded and loaded once per process so every machine renders the
    // same bytes forever. A typeface that fails to load is a bug in the build, not a
    // runtime condition to paper over, so this throws rather than falling back to a family
    // name — a silent fallback is the exact defect this exists to remove.
    private static readonly SKTypeface RegularTypeface = LoadEmbeddedTypeface("LiberationSans-Regular.ttf");
    private static readonly SKTypeface BoldTypeface = LoadEmbeddedTypeface("LiberationSans-Bold.ttf");

    private static SKTypeface LoadEmbeddedTypeface(string fileName)
    {
        string resourceName = $"ShotMarker.Core.Render.Fonts.{fileName}";
        Stream stream = typeof(TargetRenderer).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"{resourceName} resource missing");
        // SKTypeface.FromStream takes ownership of the stream it is given, so it is not
        // wrapped in a `using` here — these typefaces are static readonly and live for the
        // process, which is correct; do not dispose them (or their stream) per-render.
        return SKTypeface.FromStream(stream)
            ?? throw new InvalidOperationException($"{resourceName} failed to load as a typeface");
    }

    public static RenderedTarget Render(SmString s, TargetFace face, RenderOptions? options = null)
    {
        RenderOptions o = options ?? new RenderOptions();

        // Errored/fake shots carry double.NaN coordinates (see SmShot.IsInvalid). Filter
        // them out before XMm/YMm is touched anywhere — a bounding box or a plotted point
        // is not a place a NaN can pass through quietly; Math.Max(x, NaN) is NaN, and one
        // such shot would silently poison the extent (and therefore the scale) for the
        // entire picture, sighters and valid record shots included.
        var plottable = s.Shots.Where(sh => !sh.IsInvalid).ToList();

        // Ruling S1: the base extent is the board's own true mm size — no padding added
        // unconditionally. It only grows if a shot needs more room than the board gives it.
        double widthMm = face.BoardWidthMm;
        double heightMm = face.BoardHeightMm;
        // Never clip a hit: grow the canvas if a shot lies outside the board.
        foreach (SmShot sh in plottable)
        {
            widthMm = Math.Max(widthMm, 2 * (Math.Abs(sh.XMm) + o.MarginMm));
            heightMm = Math.Max(heightMm, 2 * (Math.Abs(sh.YMm) + o.MarginMm));
        }

        var proj = new TargetProjection(-widthMm / 2, heightMm / 2, widthMm, heightMm, o.PixelsPerMm);

        var info = new SKImageInfo(proj.PixelWidth, proj.PixelHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.White);

        DrawBoard(canvas, proj, face);
        DrawRings(canvas, proj, face);
        DrawPolys(canvas, proj, face);
        if (o.DrawText) DrawTexts(canvas, proj, face);
        DrawShots(canvas, proj, plottable, s.BulletDiameterMm, o.DrawText);
        if (o.DrawFurniture) DrawFurniture(canvas, proj, s, o.DrawText);

        using SKImage image = surface.Snapshot();
        using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new RenderedTarget(data.ToArray(), proj.PixelWidth, proj.PixelHeight, proj);
    }

    /// <summary>ShotMarker's colour codes. The "l" suffix means the ring is a line only,
    /// not a filled disc — the difference between an outline and a black centre.</summary>
    private static SKColor Fill(string code) => code switch
    {
        "b" => new SKColor(0x20, 0x20, 0x20),
        "g" => new SKColor(0x80, 0x80, 0x80),
        _ => SKColors.White,
    };

    private static bool LineOnly(string code) => code.EndsWith("l", StringComparison.Ordinal);

    private static SKColor Stroke(string code) => code switch
    {
        "wl" => SKColors.White,
        "gl" => new SKColor(0x60, 0x60, 0x60),
        // Ruling F18/F18a: "b" rings are filled the same near-black as the default stroke
        // below, so their outline would otherwise be invisible against their own fill
        // (X/10/9/8/7 on NRA_LRFC). Only "b" collides like this — "w" and "g" already
        // contrast against the default stroke — so this is the one narrow case added,
        // not a general luminance-based contrast rule.
        "b" => SKColors.White,
        _ => new SKColor(0x20, 0x20, 0x20),
    };

    private static void DrawBoard(SKCanvas c, TargetProjection p, TargetFace f)
    {
        var (x, y) = p.ToPixel(-f.BoardWidthMm / 2, f.BoardHeightMm / 2);
        var rect = new SKRect(x, y, x + p.Px(f.BoardWidthMm), y + p.Px(f.BoardHeightMm));
        using var fill = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
        using var edge = new SKPaint
        {
            Color = new SKColor(0x40, 0x40, 0x40), Style = SKPaintStyle.Stroke,
            StrokeWidth = Math.Max(1, p.Px(f.BoardLineMm)), IsAntialias = true,
        };
        c.DrawRect(rect, fill);
        c.DrawRect(rect, edge);
    }

    private static void DrawRings(SKCanvas c, TargetProjection p, TargetFace f)
    {
        // Largest first, so the small high-scoring rings end up on top.
        foreach (TargetRing r in f.Rings.OrderByDescending(r => r.DiamMm))
        {
            var (cx, cy) = p.ToPixel(r.XMm, r.YMm);
            float radius = p.Px(r.DiamMm / 2);
            if (!LineOnly(r.Color))
            {
                using var fill = new SKPaint { Color = Fill(r.Color), Style = SKPaintStyle.Fill, IsAntialias = true };
                c.DrawCircle(cx, cy, radius, fill);
            }
            if (r.LineMm > 0)
            {
                using var edge = new SKPaint
                {
                    Color = Stroke(r.Color), Style = SKPaintStyle.Stroke,
                    StrokeWidth = Math.Max(1, p.Px(r.LineMm)), IsAntialias = true,
                };
                c.DrawCircle(cx, cy, radius, edge);
            }
        }
    }

    private static void DrawPolys(SKCanvas c, TargetProjection p, TargetFace f)
    {
        foreach (TargetPoly poly in f.Polys)
        {
            if (poly.Points.Count < 2) continue;
            using var path = new SKPath();
            var (x0, y0) = p.ToPixel(poly.Points[0].XMm, poly.Points[0].YMm);
            path.MoveTo(x0, y0);
            foreach (TargetPoint pt in poly.Points.Skip(1))
            {
                var (x, y) = p.ToPixel(pt.XMm, pt.YMm);
                path.LineTo(x, y);
            }
            path.Close();
            using var paint = new SKPaint
            {
                Color = LineOnly(poly.Color) ? Stroke(poly.Color) : Fill(poly.Color),
                Style = LineOnly(poly.Color) ? SKPaintStyle.Stroke : SKPaintStyle.Fill,
                StrokeWidth = Math.Max(1, p.Px(poly.LineMm)), IsAntialias = true,
            };
            c.DrawPath(path, paint);
        }
    }

    private static void DrawTexts(SKCanvas c, TargetProjection p, TargetFace f)
    {
        foreach (TargetText t in f.Texts)
        {
            if (string.IsNullOrEmpty(t.Text)) continue;
            var (x, y) = p.ToPixel(t.XMm, t.YMm);
            using var paint = new SKPaint
            {
                Color = Stroke(t.Color), IsAntialias = true,
                TextSize = Math.Max(6, p.Px(t.SizeMm)), TextAlign = SKTextAlign.Center,
                Typeface = RegularTypeface,
            };
            c.DrawText(t.Text, x, y + paint.TextSize / 3, paint);
        }
    }

    /// <param name="shots">Already filtered to exclude <see cref="SmShot.IsInvalid"/> shots
    /// — their coordinates are double.NaN and cannot be plotted at all.</param>
    private static void DrawShots(SKCanvas c, TargetProjection p, IReadOnlyList<SmShot> shots, double? bulletDiameterMm, bool drawText)
    {
        float radius = p.Px((bulletDiameterMm ?? 7.2) / 2);
        float discRadius = Math.Max(radius, 8);

        foreach (SmShot sh in shots)
        {
            var (x, y) = p.ToPixel(sh.XMm, sh.YMm);
            // Styling is settled (task 9b, section 6): sighters red, record shots orange —
            // deliberately sh.IsSighter, not sh.IsFlyer. IsFlyer now also covers a record
            // shot ShotMarker's own group left out (SmShot.InSelectedGroup == false), and
            // excluding a shot from the statistics is not a reason to draw it differently;
            // it still gets its numbered orange disc like every other record shot.
            using var fill = new SKPaint
            {
                Color = sh.IsSighter ? new SKColor(0xD0, 0x30, 0x30) : new SKColor(0xF0, 0x70, 0x20),
                Style = SKPaintStyle.Fill, IsAntialias = true,
            };
            using var edge = new SKPaint
            {
                Color = SKColors.White, Style = SKPaintStyle.Stroke,
                StrokeWidth = Math.Max(1, discRadius / 6), IsAntialias = true,
            };
            c.DrawCircle(x, y, discRadius, fill);
            c.DrawCircle(x, y, discRadius, edge);

            if (!drawText) continue;
            using var label = new SKPaint
            {
                Color = SKColors.White, IsAntialias = true,
                TextSize = discRadius * 1.2f, TextAlign = SKTextAlign.Center,
                Typeface = BoldTypeface,
            };
            c.DrawText(sh.Number.ToString(CultureInfo.InvariantCulture), x, y + label.TextSize / 3, label);
        }
    }

    private static void DrawFurniture(SKCanvas c, TargetProjection p, SmString s, bool drawText)
    {
        if (ScoringExtentMm(s) is not { } extent) return;
        var (x0, x1, y0, y1) = extent;

        var (left, top) = p.ToPixel(x0, y1);
        var (right, bottom) = p.ToPixel(x1, y0);

        using var box = new SKPaint
        {
            Color = new SKColor(0x20, 0xA0, 0x20), Style = SKPaintStyle.Stroke,
            StrokeWidth = 2, IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 8f, 6f }, 0),
        };
        c.DrawRect(new SKRect(left, top, right, bottom), box);

        if (!drawText) return;
        string stats = Stats(s, x1 - x0, y1 - y0);
        using var text = new SKPaint
        {
            Color = new SKColor(0x20, 0x20, 0x20), IsAntialias = true, TextSize = 22,
            Typeface = RegularTypeface,
        };
        using var plate = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0xE0), Style = SKPaintStyle.Fill };
        float w = text.MeasureText(stats);
        c.DrawRect(new SKRect(10, 10, 20 + w, 46), plate);
        c.DrawText(stats, 15, 36, text);
    }

    /// <summary>The bounding box of the shots the group box is drawn around, or null when the
    /// string has none. !IsFlyer always excludes IsInvalid (IsFlyer => IsSighter || IsInvalid
    /// || ...), so this never touches a NaN coordinate — whatever else IsFlyer comes to
    /// include over time (task 9b added InSelectedGroup == false).</summary>
    private static (double X0, double X1, double Y0, double Y1)? ScoringExtentMm(SmString s)
    {
        var scoring = s.Shots.Where(sh => !sh.IsFlyer).ToList();
        if (scoring.Count == 0) return null;
        return (scoring.Min(sh => sh.XMm), scoring.Max(sh => sh.XMm),
                scoring.Min(sh => sh.YMm), scoring.Max(sh => sh.YMm));
    }

    /// <summary>The statistics banner exactly as <see cref="Render"/> paints it, or an empty
    /// string when the group box is not drawn. Ruling F41 made the golden image text-free, so
    /// this is how the banner's wording and figures are asserted — as the string logic they
    /// are, with no pixels involved.</summary>
    public static string StatsBanner(SmString s) =>
        ScoringExtentMm(s) is { } e ? Stats(s, e.X1 - e.X0, e.Y1 - e.Y0) : string.Empty;

    private static string Stats(SmString s, double widthMm, double heightMm)
    {
        var ic = CultureInfo.InvariantCulture;
        string size = s.Stats?.GroupSizeMm is { } g
            ? g.ToString("0.0", ic)
            : Math.Sqrt(widthMm * widthMm + heightMm * heightMm).ToString("0.0", ic);
        var parts = new List<string> { $"size {size} mm", $"w {widthMm.ToString("0.0", ic)}", $"h {heightMm.ToString("0.0", ic)}" };
        if (s.Stats?.MeanRadiusMm is { } mr) parts.Add($"mr {mr.ToString("0.0", ic)}");
        if (s.Stats?.VelocityAvgMps is { } v) parts.Add($"v {v.ToString("0", ic)} m/s");
        if (s.Stats?.VelocitySdMps is { } sd) parts.Add($"sd {sd.ToString("0.0", ic)}");
        if (s.Stats?.VelocityEsMps is { } es) parts.Add($"es {es.ToString("0.0", ic)}");
        return string.Join("  ", parts);
    }
}
