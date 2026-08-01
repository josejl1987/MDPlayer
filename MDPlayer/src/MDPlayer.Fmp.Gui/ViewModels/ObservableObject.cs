using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Fmp.Gui.ViewModels;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base (no external MVVM package).
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Sets a backing field and raises <see cref="PropertyChanged"/> on change.</summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected void OnPropertyChanged(params string[] propertyNames)
    {
        foreach (string name in propertyNames)
            OnPropertyChanged(name);
    }
}
