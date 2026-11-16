using System.ComponentModel;
using System.Globalization;

namespace OpenTaskbar.Models;

public sealed class DockWidgetOption(string id, string label, string description, bool isInstalled, bool isEnabled, DockWidget preview) : INotifyPropertyChanged
{
    private bool _isInstalled = isInstalled;
    private bool _isEnabled = isEnabled;
    private string _label = label;
    private string _description = description;

    public string Id { get; } = id;
    public string Label => _label;
    public string Description => _description;
    public DockWidget Preview { get; } = preview;

    public bool MatchesSearch(string query, CultureInfo culture) =>
        culture.CompareInfo.IndexOf(Label, query, CompareOptions.IgnoreCase) >= 0
        || culture.CompareInfo.IndexOf(Description, query, CompareOptions.IgnoreCase) >= 0;

    public void UpdateText(string label, string description)
    {
        if (_label != label)
        {
            _label = label;
            Preview.Label = label;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
        }
        if (_description != description)
        {
            _description = description;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Description)));
        }
    }

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
