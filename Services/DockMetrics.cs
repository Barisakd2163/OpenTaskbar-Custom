using System.Windows;
using System.Windows.Controls.Primitives;
using OpenTaskbar.Models;

namespace OpenTaskbar.Services;

/// <summary>
/// Single source of truth for dock geometry and motion. XAML reads these through x:Static in Design/Theme.xaml.
/// </summary>
public static class DockMetrics
{
    // Vertical layout (DIP).
    public const double Headroom = 24;
    public const double ShelfHeight = 66;
    public const double WindowHeight = Headroom + ShelfHeight;
    public const double BottomGap = 6;
    public const double SideInset = 12;
    public const double FullWidthSideInset = 6;
    public const double EdgeTriggerHeight = 3;

    // Cells and icons (DIP).
    public const double CellSize = 52;
    public const double MinCellSize = 24;
    public const double IconSize = 42;

    // Magnification.
    public const double HoverScale = 1.28;
    public const double InfluenceRadius = 90;

    // Previews.
    public const double PreviewGap = 18;

    // Motion (ms).
    public const double AutoHideDelayMs = 400;
    public const double RevealMs = 220;
    public const double HideMs = 220;
    public const double LaunchBounceMs = 420;
    public const double LaunchBounceHeight = 12;
    public const double HideMinimumOffset = 72;

    public static Thickness Inset { get; } = new(SideInset, 0, SideInset, BottomGap);
    public static Thickness Padding { get; } = new(10, 4, 10, 4);

    public static CustomPopupPlacementCallback TooltipPlacementCallback { get; } = PlaceDockTooltip;

    public static CustomPopupPlacement[] PlaceDockTooltip(Size popupSize, Size targetSize, Point offset)
    {
        return DockFlyout.Edge switch
        {
            DockEdge.Left =>
            [
                new CustomPopupPlacement(new Point(targetSize.Width + 8, (targetSize.Height - popupSize.Height) / 2), PopupPrimaryAxis.None),
                new CustomPopupPlacement(new Point(-popupSize.Width - 8, (targetSize.Height - popupSize.Height) / 2), PopupPrimaryAxis.None)
            ],
            DockEdge.Right =>
            [
                new CustomPopupPlacement(new Point(-popupSize.Width - 8, (targetSize.Height - popupSize.Height) / 2), PopupPrimaryAxis.None),
                new CustomPopupPlacement(new Point(targetSize.Width + 8, (targetSize.Height - popupSize.Height) / 2), PopupPrimaryAxis.None)
            ],
            _ =>
            [
                new CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, -popupSize.Height - 8), PopupPrimaryAxis.None),
                new CustomPopupPlacement(new Point((targetSize.Width - popupSize.Width) / 2, targetSize.Height + 8), PopupPrimaryAxis.None)
            ]
        };
    }

    /// <summary>Natural widget card height; smaller dock sizes scale the widget strip down from it.</summary>
    public const double WidgetDesignHeight = 46;

    /// <summary>
    /// Geometry for each dock size. Surface = cell + 2 × vertical padding + 2 (border); it plus the bottom gap fits the shelf.
    /// Compact matches the Windows 11 taskbar height (48 DIP), so it needs no extra reserved space.
    /// </summary>
    public static DockSizeProfile For(Models.DockSize size) => size switch
    {
        Models.DockSize.Compact => new(Shelf: 48, BottomGap: 3, Cell: 38, VerticalPadding: 2, Icon: 28, Radius: 16),
        Models.DockSize.Medium => new(Shelf: 58, BottomGap: 5, Cell: 44, VerticalPadding: 3, Icon: 34, Radius: 19),
        _ => new(Shelf: ShelfHeight, BottomGap: BottomGap, Cell: 50, VerticalPadding: 4, Icon: IconSize, Radius: 22)
    };
}

public readonly record struct DockSizeProfile(double Shelf, double BottomGap, double Cell, double VerticalPadding, double Icon, double Radius)
{
    public double SurfaceHeight => Cell + VerticalPadding * 2 + 2;
    public double WidgetScale => Math.Min(1, (Cell - 4) / DockMetrics.WidgetDesignHeight);
}
