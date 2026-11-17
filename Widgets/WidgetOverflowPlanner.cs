namespace OpenTaskbar.Widgets;

/// <summary>
/// Decides which visible widgets move into the overflow flyout when they exceed the width the dock reserves for them.
/// Widgets with the most important at-a-glance state (quick settings, clock) leave last; display order never changes.
/// </summary>
public static class WidgetOverflowPlanner
{
    /// <summary>Widgets leave the dock in this order. Widgets missing from the list leave first.</summary>
    private static readonly string[] OverflowOrder =
    [
        "Media",
        "Desktops",
        "SystemStats",
        "Focus",
        "Audio",
        "Weather",
        "Tray",
        "Clock",
        "QuickSettings"
    ];

    /// <summary>Share of the dock content width reserved for widgets; apps keep the rest.</summary>
    public const double BudgetRatio = 0.4;

    /// <summary>Returns the ids that must overflow so the remaining widgets, plus the overflow button, fit the budget.</summary>
    public static HashSet<string> Plan(IReadOnlyList<(string Id, double Width)> visible, double budget, double buttonWidth)
    {
        var overflow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var total = visible.Sum(widget => widget.Width);
        if (total <= budget) return overflow;

        var limit = Math.Max(0, budget - buttonWidth);
        var candidates = visible
            .Select((widget, index) => (widget.Id, widget.Width, Index: index))
            .OrderBy(widget => PriorityOf(widget.Id))
            .ThenByDescending(widget => widget.Index);
        foreach (var widget in candidates)
        {
            if (total <= limit) break;
            overflow.Add(widget.Id);
            total -= widget.Width;
        }

        return overflow;
    }

    private static int PriorityOf(string id) =>
        Array.FindIndex(OverflowOrder, known => string.Equals(known, id, StringComparison.OrdinalIgnoreCase));
}
