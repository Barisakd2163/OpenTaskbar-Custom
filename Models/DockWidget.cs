using System.ComponentModel;
namespace OpenTaskbar.Models;

public class DockWidget(string id, string label) : INotifyPropertyChanged
{
    private string _value = string.Empty;
    private string _label = label;

    public string Id { get; } = id;
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
