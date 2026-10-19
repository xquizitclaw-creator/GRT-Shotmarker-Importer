using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace ShotMarker.Plugin.Ui;

/// <summary>The import window. Everything it decides is in <see cref="ImportViewModel"/>; this
/// file only shows the file dialogs, decodes the preview picture and greys device-excluded
/// shots.</summary>
public sealed partial class ImportWindow : Window
{
    private readonly ImportViewModel _vm;

    /// <summary>For the XAML designer and loader only.</summary>
    public ImportWindow() : this(new ImportViewModel(null)) { }

    internal ImportWindow(ImportViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        AvaloniaXamlLoader.Load(this);

        vm.PropertyChanged += OnVmChanged;
        this.FindControl<DataGrid>("ShotGrid")!.LoadingRow += OnShotRowLoading;

        Opened += async (_, _) =>
        {
            // A plugin started by GRT is a bare executable, not an app bundle; on macOS it would
            // otherwise open behind GRT's window.
            Activate();
            await _vm.DiscoverActiveLoadAsync();
        };
        Closed += (_, _) => (this.FindControl<Image>("FaceImage")!.Source as IDisposable)?.Dispose();
    }

    internal ImportViewModel ViewModel => _vm;

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ImportViewModel.FacePng)) ShowFace(_vm.FacePng);
        else if (e.PropertyName == nameof(ImportViewModel.Log))
        {
            // Keep the newest line in view, as AppendText did.
            var log = this.FindControl<TextBox>("LogBox")!;
            log.CaretIndex = log.Text?.Length ?? 0;
        }
    }

    private void ShowFace(byte[]? png)
    {
        var image = this.FindControl<Image>("FaceImage")!;
        var old = image.Source as IDisposable;
        image.Source = null;
        if (png != null)
        {
            try
            {
                using var ms = new MemoryStream(png);
                image.Source = new Bitmap(ms);
            }
            catch (Exception ex) { _vm.Note($"The preview could not be shown ({ex.Message})"); }
        }
        old?.Dispose();
    }

    /// <summary>A shot the device excluded is shown in grey text. Rows are recycled as the table
    /// scrolls, so the colour is set (or cleared) every time a row is handed a shot.</summary>
    private static void OnShotRowLoading(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.DataContext is ShotRow { CanChange: false })
            e.Row.Foreground = Brushes.Gray;
        else
            e.Row.ClearValue(ForegroundProperty);
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "ShotMarker export",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("ShotMarker exports (*.tar;*.csv)") { Patterns = new[] { "*.tar", "*.csv" } },
                new FilePickerFileType("All files (*.*)") { Patterns = new[] { "*" } },
            },
        });
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path == null) return;
        _vm.OpenExport(path);
    }

    private async void OnImport(object? sender, RoutedEventArgs e) => await _vm.ImportAsync(AskTargetAsync);

    private async Task<string?> AskTargetAsync()
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Write the import to",
            DefaultExtension = "grtload",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("GRT loads (*.grtload)") { Patterns = new[] { "*.grtload" } },
            },
        });
        return file?.TryGetLocalPath();
    }
}
