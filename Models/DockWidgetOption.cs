using System.ComponentModel;

namespace OpenTaskbar.Models;

public sealed class DockWidgetOption(string id, string label, string description, bool isInstalled, bool isEnabled) : INotifyPropertyChanged
{
    private bool _isInstalled = isInstalled;
    private bool _isEnabled = isEnabled;

    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Description { get; } = description;

    public bool IsInstalled
    {
        get => _isInstalled;
        set => Set(ref _isInstalled, value, nameof(IsInstalled));
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value, nameof(IsEnabled));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, string propertyName)
    {
        if (field == value) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
