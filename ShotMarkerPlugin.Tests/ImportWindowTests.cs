using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ShotMarker.Plugin.Ui;
using Xunit;

namespace ShotMarker.Plugin.Tests;

/// <summary>Smoke test: the window builds, its controls are there with the 0.1 texts, and the
/// list shows an export once one is opened.</summary>
public class ImportWindowTests
{
    [AvaloniaFact]
    public void BuildsWithTheKeyControls()
    {
        var w = new ImportWindow(new ImportViewModel(null));
        w.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            Assert.Equal("ShotMarker import", w.Title);
            Assert.Equal("Open export…", w.FindControl<Button>("BrowseButton")!.Content);
            var import = w.FindControl<Button>("ImportButton")!;
            Assert.Equal("Import selected", import.Content);
            Assert.False(import.IsEnabled);
            Assert.False(w.FindControl<StackPanel>("PointBox")!.IsVisible);
            Assert.Equal("No load open", w.FindControl<TextBlock>("LoadLabel")!.Text);

            var grid = w.FindControl<DataGrid>("StringGrid")!;
            Assert.Equal(
                new[] { "", "String", "When", "Distance", "Face", "Shots", "Score", "Mean v", "SD", "ES", "Charge (gr)" },
                grid.Columns.Select(c => c.Header?.ToString() ?? ""));
            Assert.Equal(
                new[] { "", "#", "Score", "m/s", "x, y (mm)", "Excluded" },
                w.FindControl<DataGrid>("ShotGrid")!.Columns.Select(c => c.Header?.ToString() ?? ""));

            // Opened without GRT: the log says the import will ask where to write.
            Assert.Contains("Not attached to GRT", w.FindControl<TextBox>("LogBox")!.Text);
        }
        finally { w.Close(); }
    }

    [AvaloniaFact]
    public void ShowsAnOpenedExportAndItsPreview()
    {
        var vm = new ImportViewModel(null);
        var w = new ImportWindow(vm);
        w.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            vm.OpenExport(Fixtures.Tar);
            Dispatcher.UIThread.RunJobs();

            Assert.True(w.FindControl<Button>("ImportButton")!.IsEnabled);
            Assert.Same(vm.Rows[0], w.FindControl<DataGrid>("StringGrid")!.SelectedItem);
            Assert.NotNull(w.FindControl<Image>("FaceImage")!.Source);
            Assert.StartsWith(vm.Rows[0].Name, w.FindControl<TextBlock>("CaptionText")!.Text);
        }
        finally { w.Close(); }
    }
}
