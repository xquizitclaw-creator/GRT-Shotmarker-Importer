using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ShotMarker.Plugin.Ui;

/// <summary>The smallest INotifyPropertyChanged base that does the job; the window has a
/// handful of bindable properties and no need for a toolkit.</summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
