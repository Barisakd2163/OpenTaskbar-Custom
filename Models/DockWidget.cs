using System.ComponentModel;
namespace OpenTaskbar.Models;

public sealed class DockWidget(string id, string label) : INotifyPropertyChanged
{
    private string _value = string.Empty;

    public string Id { get; } = id;
    public string Label { get; } = label;
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
