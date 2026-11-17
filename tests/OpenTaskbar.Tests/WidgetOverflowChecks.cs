using OpenTaskbar.Widgets;

internal static class WidgetOverflowChecks
{
    public static int Run()
    {
        var checks = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        const double Button = 30;
        (string, double)[] widgets = [("Media", 200), ("Desktops", 100), ("QuickSettings", 150), ("Clock", 120)];

        Check(WidgetOverflowPlanner.Plan(widgets, 570, Button).Count == 0, "Widgets that fit the budget must stay in the dock.");

        var overflow = WidgetOverflowPlanner.Plan(widgets, 400, Button);
        Check(overflow.SetEquals(["Media"]), "The least important widget must overflow first, only as far as needed.");

        overflow = WidgetOverflowPlanner.Plan(widgets, 300, Button);
        Check(overflow.SetEquals(["Media", "Desktops"]), "Overflow must continue in priority order until the rest fits beside the button.");

        overflow = WidgetOverflowPlanner.Plan([("QuickSettings", 150), ("Clock", 120)], 100, Button);
        Check(overflow.SetEquals(["QuickSettings", "Clock"]), "Quick settings and the clock must overflow only when nothing else is left.");

        overflow = WidgetOverflowPlanner.Plan([("QuickSettings", 150), ("Clock", 120)], 190, Button);
        Check(overflow.SetEquals(["Clock"]), "The clock must leave before quick settings.");

        overflow = WidgetOverflowPlanner.Plan([("Custom", 150), ("Clock", 120)], 190, Button);
        Check(overflow.SetEquals(["Custom"]), "Unknown widgets must overflow before known ones.");

        Check(WidgetOverflowPlanner.Plan(widgets, Button - 1, Button).Count == widgets.Length,
            "A budget smaller than the overflow button must move every widget into the flyout.");
        Check(WidgetOverflowPlanner.Plan([], 100, Button).Count == 0, "No visible widgets must produce no overflow.");
        return checks;
    }
}
