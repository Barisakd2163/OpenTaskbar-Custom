using System.Windows.Media.Imaging;

namespace OpenTaskbar.Models;

public sealed record TrackedWindow(nint Handle, string Title, BitmapSource? Icon, bool HasNotification = false);
