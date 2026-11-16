using System.ComponentModel;

namespace OpenTaskbar.Models;

public sealed class DockWidgetOption(string id, string label, bool isEnabled) : INotifyPropertyChanged
{
    private bool _isEnabled = isEnabled;

    public string Id { get; } = id;
    public string Label { get; } = label;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
