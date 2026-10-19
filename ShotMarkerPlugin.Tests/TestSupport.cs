using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using ShotMarker.Plugin;
using ShotMarker.Plugin.Ui;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(ShotMarker.Plugin.Tests.TestAppBuilder))]

namespace ShotMarker.Plugin.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .AfterSetup(_ => SynchronizationContext.SetSynchronizationContext(
            new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal)));
}

/// <summary>The committed sample files, found by walking up to the solution.</summary>
public static class Fixtures
{
    public static string Root { get; } = FindRoot();

    public static string Path(string relative) => System.IO.Path.Combine(Root, "fixtures", relative);

    public static string Tar => Path("shotmarker/SM_export_Sep_21.tar");

    /// <summary>A two-up frame: strings from two firing points.</summary>
    public static string Csv => Path("shotmarker/SM_shotslog_Sep_21.csv");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir != null && !File.Exists(System.IO.Path.Combine(dir.FullName, "GrtShotMarker.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}

/// <summary>A GRT that answers the two questions the window asks, and records what it was told.</summary>
internal sealed class FakeGrt(string caption, string file) : IGrtHost
{
    public List<string> Opened { get; } = new();
    public Exception? TabError { get; set; }

    public Task<(long handle, string caption, string file)> GetTabOnTopAsync() =>
        TabError is { } e ? Task.FromException<(long, string, string)>(e) : Task.FromResult((1L, caption, file));

    public Task LoadFileAsync(string path) { Opened.Add(path); return Task.CompletedTask; }
}

/// <summary>A temp folder holding a load with a propellant charge, deleted afterwards.</summary>
internal sealed class TempLoad : IDisposable
{
    public string Dir { get; } = Directory.CreateTempSubdirectory().FullName;
    public string Path { get; }

    /// <param name="chargeGrams">GRT stores the charge in grams (input "mc").</param>
    public TempLoad(double? chargeGrams = 2.6)
    {
        Path = System.IO.Path.Combine(Dir, "Krieger-2 284 Shehane.grtload");
        string mc = chargeGrams is { } g
            ? $"<input name=\"mc\" value=\"{g.ToString(System.Globalization.CultureInfo.InvariantCulture)}\" />" : "";
        File.WriteAllText(Path,
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>\n" +
            "<GordonsReloadingTool version=\"2021.2030-NIGHTLY\">\n  <InnerBallistikInput>\n" +
            "    <title>284%20Shehane</title>\n" +
            "    <caliber><input name=\"CaliberName\" value=\"Generic\" /></caliber>\n" +
            "    <gun><input name=\"GunName\" value=\"Generic\" /></gun>\n" +
            "    <projectile><input name=\"ProjectileName\" value=\"Generic\" /></projectile>\n" +
            $"    <propellant><input name=\"pname\" value=\"Generic\" />{mc}</propellant>\n" +
            "    <appendix>\n    </appendix>\n  </InnerBallistikInput>\n</GordonsReloadingTool>");
    }

    public void Dispose() => Directory.Delete(Dir, recursive: true);
}
