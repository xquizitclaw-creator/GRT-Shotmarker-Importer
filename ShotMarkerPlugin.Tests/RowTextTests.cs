using System.Globalization;
using ShotMarker.Core.Sm;
using ShotMarker.Plugin.Ui;
using Xunit;

namespace ShotMarker.Plugin.Tests;

public class RowTextTests
{
    private static SmShot Shot(int n, double? v = 800, bool user = false, bool sighter = false) =>
        new(n, 0, 0, v, "10", null, sighter, false, null, IsExcludedByUser: user);

    private static SmString Str(IReadOnlyList<SmShot> shots, SmGroupStats? stats = null) =>
        new("id", "M6 R1", DateTimeOffset.UnixEpoch, "nra_lrfc", 1000, "yd", 0, 0, null, "200-10X",
            shots, stats);

    [Fact]
    public void CountsStruckShotsOutOfTheTotal()
    {
        Assert.Equal("3", RowText.CountedText(Str(new[] { Shot(1), Shot(2), Shot(3) })));
        Assert.Equal("2 of 3", RowText.CountedText(Str(new[] { Shot(1), Shot(2, user: true), Shot(3) })));
    }

    [Fact]
    public void MeanVelocityPrefersTheDevicesAverageThenSkipsFlyers()
    {
        var ic = CultureInfo.InvariantCulture;
        var stats = new SmGroupStats(null, null, null, 812.34, null, null);
        Assert.Equal("812.3", RowText.MeanVelocityText(Str(new[] { Shot(1) }, stats), ic));
        Assert.Equal("810.0", RowText.MeanVelocityText(
            Str(new[] { Shot(1, 800), Shot(2, 820), Shot(3, 999, user: true) }), ic));
        Assert.Equal("", RowText.MeanVelocityText(Str(new[] { Shot(1, null) }), ic));
    }

    [Fact]
    public void CaptionNamesSightersAndQualifiesTheDevicesFiguresAfterAStrikeOut()
    {
        var stats = new SmGroupStats(null, 50.0, null, null, 3.0, null);
        string c = RowText.CaptionFor(Str(new[] { Shot(1, sighter: true), Shot(2), Shot(3, user: true) }, stats));
        Assert.Contains("M6 R1", c);
        Assert.Contains("2 of 3 shots", c);
        Assert.Contains("1 sighter (red, not imported)", c);
        Assert.Contains("(full string, before strike-outs)", c);
    }

    [Fact]
    public void DeviceExclusionWordsTheReason()
    {
        Assert.Equal("sighter", RowText.DeviceExclusion(Shot(1, sighter: true)));
        Assert.Equal("no position", RowText.DeviceExclusion(Shot(1) with { IsInvalid = true }));
        Assert.Equal("excluded on device", RowText.DeviceExclusion(Shot(1) with { IsExcludedOnDevice = true }));
        Assert.Equal("not in group", RowText.DeviceExclusion(Shot(1) with { InSelectedGroup = false }));
        Assert.Equal("", RowText.DeviceExclusion(Shot(1)));
    }
}
