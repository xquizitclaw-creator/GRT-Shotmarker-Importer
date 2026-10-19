using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ShotMarker.Plugin.Ui;

namespace ShotMarker.Plugin;

public sealed class App : Application
{
    /// <summary>Set by Program before the app starts; null when not attached to GRT.</summary>
    internal static IGrtHost? Grt { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ImportWindow(new ImportViewModel(Grt));
        base.OnFrameworkInitializationCompleted();
    }
}
