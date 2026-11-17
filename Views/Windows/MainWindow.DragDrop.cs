using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OpenTaskbar.Models;

namespace OpenTaskbar;

public partial class MainWindow
{
    private void DockApp_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // Snapshot before Button focus handling and preview dismissal can change foreground activation.
        var foreground = OpenTaskbar.Services.NativeWindowTracker.GetForegroundApplicationWindow();
        _foregroundAtAppPress = foreground != 0 ? foreground : _foregroundWindow;

        if (sender is Button { DataContext: DockApp app } && _pinnedApps.Contains(app))
        {
            _dragStartPoint = e.GetPosition(this);
            _draggedApp = app;
        }
        else
        {
            _draggedApp = null;
        }
    }

    private void DockApp_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _draggedApp = null;
    }

    private void DockApp_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _draggedApp is null || sender is not Button button)
        {
            return;
        }

        var currentPoint = e.GetPosition(this);
        var diff = _dragStartPoint - currentPoint;
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var app = _draggedApp;
            _draggedApp = null;
            _foregroundAtAppPress = null;
            _previewCloseTimer.Stop();
            _thumbnailPreview.Hide();
            ResetMagnification();
            AnimateAppDrag(button, app);
        }
    }

    private void AnimateAppDrag(Button button, DockApp app)
    {
        var icon = GetButtonIcon(button);
        var (scale, translate) = icon != null ? GetIconTransforms(icon) : (null, null);
        var animDuration = TimeSpan.FromMilliseconds(130);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        button.Opacity = 0.55;
        if (scale is not null && translate is not null && _preferences.Current.AnimationsEnabled && SystemParameters.ClientAreaAnimation)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.10, animDuration) { EasingFunction = easeOut });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.10, animDuration) { EasingFunction = easeOut });
            translate.BeginAnimation(IsVertical ? TranslateTransform.XProperty : TranslateTransform.YProperty,
                new DoubleAnimation(-4 * BounceDirection, animDuration) { EasingFunction = easeOut });
        }

        try
        {
            DragDrop.DoDragDrop(button, app, DragDropEffects.Move);
        }
        finally
        {
            button.Opacity = 1.0;
            ResetDisplacements();
            if (scale is not null && translate is not null)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, animDuration) { EasingFunction = easeOut });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, animDuration) { EasingFunction = easeOut });
                translate.BeginAnimation(IsVertical ? TranslateTransform.XProperty : TranslateTransform.YProperty,
                    new DoubleAnimation(0, animDuration) { EasingFunction = easeOut });
            }
        }
    }

    private void UpdateDragDisplacements(Button targetButton, DragEventArgs e)
    {
        var icon = GetButtonIcon(targetButton);
        if (icon is null) return;

        var pos = e.GetPosition(targetButton);
        var isPastHalf = IsVertical ? pos.Y > targetButton.ActualHeight / 2 : pos.X > targetButton.ActualWidth / 2;
        var shift = isPastHalf ? -12.0 : 12.0;

        var (_, translate) = GetIconTransforms(icon);
        var prop = IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;

        var anim = new DoubleAnimation(shift, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        translate.BeginAnimation(prop, anim);
        _displacedIcons.Add(icon);
    }

    private void ResetIconDisplacement(FrameworkElement icon)
    {
        if (!_displacedIcons.Remove(icon)) return;
        var (_, translate) = GetIconTransforms(icon);
        var prop = IsVertical ? TranslateTransform.YProperty : TranslateTransform.XProperty;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        translate.BeginAnimation(prop, anim);
    }

    private void ResetDisplacements()
    {
        foreach (var icon in _displacedIcons)
        {
            var (_, translate) = GetIconTransforms(icon);
            translate.BeginAnimation(TranslateTransform.XProperty, null);
            translate.BeginAnimation(TranslateTransform.YProperty, null);
            translate.X = 0;
            translate.Y = 0;
        }
        _displacedIcons.Clear();
    }

    private void AnimateDropSettle(Button button)
    {
        if (!_preferences.Current.AnimationsEnabled || !SystemParameters.ClientAreaAnimation)
        {
            return;
        }

        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var icon = GetButtonIcon(button);
            if (icon is null) return;

            var (scale, _) = GetIconTransforms(icon);
            var settleScale = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(200),
                KeyFrames =
                {
                    new EasingDoubleKeyFrame(1.12, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                    new EasingDoubleKeyFrame(0.96, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), new CubicEase { EasingMode = EasingMode.EaseOut }),
                    new EasingDoubleKeyFrame(1.0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)), new CubicEase { EasingMode = EasingMode.EaseOut })
                }
            };

            scale.BeginAnimation(ScaleTransform.ScaleXProperty, settleScale);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, settleScale);
        });
    }

    private void DockApp_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is Button button && GetButtonIcon(button) is { } icon)
        {
            ResetIconDisplacement(icon);
        }
    }

    private void DockApp_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = false;
            return;
        }

        if (e.Data.GetDataPresent(typeof(DockApp))
            && sender is Button targetButton
            && targetButton.DataContext is DockApp targetApp
            && _pinnedApps.Contains(targetApp))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;

            if (_preferences.Current.AnimationsEnabled && SystemParameters.ClientAreaAnimation)
            {
                UpdateDragDisplacements(targetButton, e);
            }
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void DockApp_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Handled = false;
            return;
        }

        if (e.Data.GetData(typeof(DockApp)) is DockApp sourceApp
            && sender is Button targetButton
            && targetButton.DataContext is DockApp targetApp
            && _pinnedApps.Contains(sourceApp)
            && _pinnedApps.Contains(targetApp)
            && !ReferenceEquals(sourceApp, targetApp))
        {
            var oldIndex = _pinnedApps.IndexOf(sourceApp);
            var newIndex = _pinnedApps.IndexOf(targetApp);
            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                ResetDisplacements();
                _pinnedApps.Move(oldIndex, newIndex);
                SavePinOrder();
                _replacementSession?.RefreshPosition();
                AnimateDropSettle(targetButton);
            }
        }

        ResetDisplacements();
        e.Handled = true;
    }

    // ---- Start Button Drag & Drop ----

    private Point _startDragStartPoint;
    private bool _isStartPressing;

    private void StartButton_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _startDragStartPoint = e.GetPosition(this);
        _isStartPressing = true;
    }

    private void StartButton_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isStartPressing = false;
    }

    private void StartButton_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || !_isStartPressing) return;

        var currentPoint = e.GetPosition(this);
        var diff = _startDragStartPoint - currentPoint;
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance
            || Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            _isStartPressing = false;
            _previewCloseTimer.Stop();
            _thumbnailPreview.Hide();
            ResetMagnification();

            StartButton.Opacity = 0.55;
            try
            {
                DragDrop.DoDragDrop(StartButton, new StartButtonDragData(), DragDropEffects.Move);
            }
            finally
            {
                StartButton.Opacity = 1.0;
            }
        }
    }

    private void DockContents_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartButtonDragData)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
    }

    private void DockContents_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartButtonDragData))) return;

        var pos = e.GetPosition(DockContents);
        var totalLength = IsVertical ? DockContents.ActualHeight : DockContents.ActualWidth;
        var currentCoord = IsVertical ? pos.Y : pos.X;

        var newPlacement = currentCoord > totalLength / 2 ? StartButtonPlacement.End : StartButtonPlacement.Start;

        if (_preferences.Current.StartPlacement != newPlacement)
        {
            _preferences.Save(_preferences.Current with { StartPlacement = newPlacement });
            ApplyStartPlacement();
            AnimateDropSettle(StartButton);
        }

        e.Handled = true;
    }
}

public sealed class StartButtonDragData;
