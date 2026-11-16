using System.ComponentModel;
namespace OpenTaskbar.Models;

public class DockWidget(string id, string label) : INotifyPropertyChanged
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
            NotifyChanged(nameof(Value));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void NotifyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
