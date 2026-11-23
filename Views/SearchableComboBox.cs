using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace OpenTaskbar.Views;

/// <summary>
/// Editable combo with its own template: the leading glyph lives in a dedicated column so typed text
/// never starts underneath it, and the item list filters live while the user types. Stays a ComboBox
/// so existing ItemsSource/SelectedItem/Text bindings and automation names keep working.
/// </summary>
public sealed class SearchableComboBox : ComboBox
{
    static SearchableComboBox()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SearchableComboBox),
            new FrameworkPropertyMetadata(typeof(SearchableComboBox)));
    }

    private TextBox? _editor;
    private ICollectionView? _view;

    public SearchableComboBox()
    {
        DropDownOpened += (_, _) =>
        {
            ClearFilter();
            // A selected value is still in the editor; select it so typing immediately starts a new search.
            if (_editor is not null && !string.IsNullOrEmpty(_editor.Text))
                Dispatcher.BeginInvoke(new Action(() => _editor.SelectAll()), System.Windows.Threading.DispatcherPriority.Input);
        };
        DropDownClosed += (_, _) => ClearFilter();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        if (_editor is not null) _editor.TextChanged -= Editor_TextChanged;
        _editor = GetTemplateChild("PART_EditableTextBox") as TextBox;
        if (_editor is not null) _editor.TextChanged += Editor_TextChanged;
    }

    protected override void OnItemsSourceChanged(IEnumerable oldValue, IEnumerable newValue)
    {
        ClearFilter();
        _view = newValue is null ? null : CollectionViewSource.GetDefaultView(newValue);
        base.OnItemsSourceChanged(oldValue, newValue);
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsDropDownOpen) ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_view is null) return;
        var query = Text?.Trim();
        if (string.IsNullOrEmpty(query))
        {
            ClearFilter();
            return;
        }
        _view.Filter = item => item is null || item.ToString()?.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0;
        _view.Refresh();
    }

    private void ClearFilter()
    {
        if (_view is null || _view.Filter is null) return;
        _view.Filter = null;
        _view.Refresh();
    }
}
