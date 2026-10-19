using GrtPluginKit.Ipc;

namespace ShotMarker.Plugin.Ui;

/// <summary>The two things the window asks of GRT. An interface so the view model can be tested
/// without a GRT on the other end of a socket.</summary>
internal interface IGrtHost
{
    Task<(long handle, string caption, string file)> GetTabOnTopAsync();
    Task LoadFileAsync(string path);
}

internal sealed class GrtClientHost(GrtClient client) : IGrtHost
{
    public Task<(long handle, string caption, string file)> GetTabOnTopAsync() => client.GetTabOnTopAsync();
    public Task LoadFileAsync(string path) => client.LoadFileAsync(path);
}
