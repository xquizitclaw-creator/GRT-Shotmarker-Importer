using System.Globalization;
using GrtPluginKit.Grt;
using ShotMarker.Core.Sm;
using ShotMarker.Plugin.Ui;
using Xunit;

namespace ShotMarker.Plugin.Tests;

public class ImportViewModelTests
{
    [Fact]
    public async Task WithoutGrtItSaysSoAndKeepsNoLoad()
    {
        var vm = new ImportViewModel(null);
        await vm.DiscoverActiveLoadAsync();
        Assert.Contains("Not attached to GRT — the import will ask where to write.", vm.Log);
        Assert.Equal("No load open", vm.LoadText);
        Assert.False(vm.HasLoad);
    }

    [Fact]
    public async Task AttachedItNamesTheLoadOnTop()
    {
        using var load = new TempLoad();
        var vm = new ImportViewModel(new FakeGrt("284 Shehane", load.Path));
        await vm.DiscoverActiveLoadAsync();
        Assert.Equal("Load: 284 Shehane", vm.LoadText);
        Assert.Equal(load.Path, vm.LoadPath);
    }

    [Fact]
    public async Task ATabErrorIsReportedNotThrown()
    {
        var vm = new ImportViewModel(new FakeGrt("", "") { TabError = new IOException("socket closed") });
        await vm.DiscoverActiveLoadAsync();
        Assert.Contains("Could not read the active tab: socket closed", vm.Log);
    }

    [Fact]
    public void OpeningAnExportListsEveryStringTickedAndPreviewsTheFirst()
    {
        var vm = new ImportViewModel(null);
        vm.OpenExport(Fixtures.Tar);

        Assert.Equal(3, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.True(r.Selected));
        Assert.All(vm.Rows, r => Assert.Equal("", r.Charge));   // no load, nothing to pre-fill
        Assert.True(vm.ImportEnabled);
        Assert.False(vm.PointsVisible);                        // a .tar is one target per string
        Assert.Contains("3 string(s) read from SM_export_Sep_21.tar", vm.Log);

        Assert.Same(vm.Rows[0], vm.SelectedRow);
        Assert.NotNull(vm.FacePng);
        Assert.Equal(vm.Rows[0].String.Shots.Count, vm.Shots.Count);
        Assert.StartsWith(vm.Rows[0].Name, vm.Caption);
    }

    [Fact]
    public async Task EachChargeIsPrefilledFromTheOpenLoad()
    {
        using var load = new TempLoad(chargeGrams: 2.6);
        var vm = new ImportViewModel(new FakeGrt("284 Shehane", load.Path));
        await vm.DiscoverActiveLoadAsync();
        vm.OpenExport(Fixtures.Tar);

        double gr = GrtLoadDoc.Load(load.Path).PropellantChargeGr!.Value;
        string want = gr.ToString("0.0#", CultureInfo.CurrentCulture);
        Assert.All(vm.Rows, r => Assert.Equal(want, r.Charge));
        Assert.All(vm.Rows, r => Assert.Equal(double.Parse(want, CultureInfo.CurrentCulture), r.ChargeGrains));
    }

    [Fact]
    public void ATypedChargeThatIsNotANumberImportsWithout()
    {
        var vm = new ImportViewModel(null);
        vm.OpenExport(Fixtures.Tar);
        vm.Rows[0].Charge = "abc";
        vm.Rows[1].Charge = 41.5.ToString(CultureInfo.CurrentCulture);
        var sel = vm.Selection();
        Assert.Null(sel[0].ChargeGrains);
        Assert.Equal(41.5, sel[1].ChargeGrains);
    }

    [Fact]
    public void ASharedFrameOffersItsFiringPointsAndTicksOnlyMine()
    {
        var vm = new ImportViewModel(null);
        vm.OpenExport(Fixtures.Csv);

        Assert.True(vm.PointsVisible);
        Assert.Equal(new[] { "R", "L" }, vm.Points.Select(p => p.Code));
        Assert.NotNull(vm.SelectedPoint);
        Assert.Contains("This frame was shared by 2 shooters (Right, Left).", vm.Log);

        string mine = vm.SelectedPoint!.Code;
        foreach (StringRow r in vm.Rows)
            Assert.Equal(r.String.FiringPoint is null || r.String.FiringPoint == mine, r.Selected);

        // Changing the point re-ticks the whole list, hand ticks included.
        PointItem other = vm.Points.First(p => p.Code != mine);
        vm.Rows.First(r => r.String.FiringPoint == mine).Selected = false;
        vm.SelectedPoint = other;
        foreach (StringRow r in vm.Rows)
            Assert.Equal(r.String.FiringPoint is null || r.String.FiringPoint == other.Code, r.Selected);
    }

    [Fact]
    public void StrikingAShotOutCuratesTheRowAndRecounts()
    {
        var vm = new ImportViewModel(null);
        vm.OpenExport(Fixtures.Tar);
        StringRow row = vm.SelectedRow!;
        int total = row.String.Shots.Count;
        byte[]? before = vm.FacePng;

        ShotRow shot = vm.Shots.First(s => s.CanChange && s.Keep);
        shot.Keep = false;

        Assert.True(row.String.Shots.Single(s => s.Number == shot.Number).IsExcludedByUser);
        Assert.Equal($"{total - 1} of {total}", row.Shots);
        Assert.Contains($"{total - 1} of {total} shots", vm.Caption);
        Assert.NotSame(before, vm.FacePng);

        // What the row carries is what is imported.
        Assert.True(vm.Selection()[0].String.Shots.Single(s => s.Number == shot.Number).IsExcludedByUser);

        // Back and forth between strings keeps the strike-out on its row.
        vm.SelectedRow = vm.Rows[1];
        vm.SelectedRow = row;
        Assert.False(vm.Shots.Single(s => s.No == shot.No).Keep);
    }

    [Fact]
    public void ADeviceExcludedShotIsShownUntickedAndCannotBeChanged()
    {
        var sh = new SmShot(7, 1, 2, 800, "X", null, IsSighter: true, IsInvalid: false, InSelectedGroup: null);
        var row = new ShotRow(sh, CultureInfo.InvariantCulture, null);
        Assert.False(row.Keep);
        Assert.False(row.CanChange);
        Assert.Equal("sighter", row.Why);
    }

    [Fact]
    public async Task NothingTickedSaysSo()
    {
        using var load = new TempLoad();
        var vm = new ImportViewModel(new FakeGrt("x", load.Path));
        await vm.DiscoverActiveLoadAsync();
        vm.OpenExport(Fixtures.Tar);
        foreach (var r in vm.Rows) r.Selected = false;
        await vm.ImportAsync(() => throw new InvalidOperationException("must not ask"));
        Assert.Contains("Nothing ticked.", vm.Log);
    }

    [Fact]
    public async Task ImportWritesASiblingAndAsksGrtToOpenIt()
    {
        using var load = new TempLoad();
        var grt = new FakeGrt("284 Shehane", load.Path);
        var vm = new ImportViewModel(grt);
        await vm.DiscoverActiveLoadAsync();
        vm.OpenExport(Fixtures.Tar);
        vm.Rows[2].Selected = false;

        await vm.ImportAsync(() => throw new InvalidOperationException("must not ask"));

        string written = Assert.Single(grt.Opened);
        Assert.Contains("_shotmarker_", Path.GetFileName(written));
        Assert.Contains($"Wrote {written}", vm.Log);
        Assert.Equal(2, GrtLoadDoc.Load(written).ShotGroups().Count());
        Assert.True(vm.ImportEnabled);
        Assert.True(vm.BrowseEnabled);
    }

    [Fact]
    public async Task WithoutALoadItAsksWhereAndCancelIsQuiet()
    {
        var vm = new ImportViewModel(null);
        vm.OpenExport(Fixtures.Tar);
        string log = vm.Log;
        await vm.ImportAsync(() => Task.FromResult<string?>(null));
        Assert.Equal(log, vm.Log);
    }

    [Fact]
    public async Task WithoutALoadItWritesWhereTheShooterSaid()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var vm = new ImportViewModel(null);
            vm.OpenExport(Fixtures.Tar);
            await vm.ImportAsync(() => Task.FromResult<string?>(Path.Combine(dir, "new.grtload")));
            Assert.Contains("Wrote ", vm.Log);
            Assert.NotEmpty(Directory.GetFiles(dir, "*.grtload"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
