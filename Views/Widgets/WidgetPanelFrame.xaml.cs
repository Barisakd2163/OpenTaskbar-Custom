using System.Windows;
using System.Windows.Controls;

namespace OpenTaskbar.Views.Widgets;

public partial class WidgetPanelFrame : UserControl
{
    public static readonly DependencyProperty HeadingProperty = DependencyProperty.Register(
        nameof(Heading), typeof(string), typeof(WidgetPanelFrame), new PropertyMetadata(string.Empty));
    public static readonly DependencyProperty BodyProperty = DependencyProperty.Register(
        nameof(Body), typeof(FrameworkElement), typeof(WidgetPanelFrame));

    public WidgetPanelFrame() => InitializeComponent();

    public string Heading { get => (string)GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public FrameworkElement? Body { get => (FrameworkElement?)GetValue(BodyProperty); set => SetValue(BodyProperty, value); }
    public event EventHandler? CloseRequested;
    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);
}
