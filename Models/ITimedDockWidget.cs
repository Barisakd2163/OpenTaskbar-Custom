using System.ComponentModel;

namespace OpenTaskbar.Models;

public interface ITimedDockWidget : INotifyPropertyChanged
{
    bool IsRunning { get; }
    bool CanToggle { get; }
    void Toggle();
    void Reset();
    void Refresh();
}
