using System.ComponentModel;

namespace OpenTaskbar.Models;

/// <summary>
/// Data model item representing a dock widget tile (e.g. clock, battery, countdown, stopwatch).
/// </summary>
public class DockWidgetItem(string id, string label) : INotifyPropertyChanged
{
    private string _value = string.Empty;
    private string _label = label;
    private bool _isCompact;

    public string Id { get; } = id;

    public bool IsCompact
    {
        get => _isCompact;
        set
        {
            if (_isCompact == value) return;
            _isCompact = value;
            NotifyChanged(nameof(IsCompact));
        }
    }

    public string Label
    {
        get => _label;
        set
        {
            if (_label == value) return;
            _label = value;
            NotifyChanged(nameof(Label));
        }
    }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            NotifyChanged(nameof(Value));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void NotifyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// Alias for <see cref="DockWidgetItem"/> to preserve compatibility.
/// </summary>
public class DockWidget(string id, string label) : DockWidgetItem(id, label)
{
}
