using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace OpenTaskbar.Services;

/// <summary>
/// Displays native Windows Toast notifications for updates and system events.
/// Uses the registered AUMID to integrate seamlessly with the Windows Action Center.
/// </summary>
public static class NotificationService
{
    public const string AppId = "OpenTaskbar";
    private static bool _registered;
    private static readonly List<ToastNotification> _activeToasts = [];

    /// <summary>
    /// Raised when an update toast is clicked or its update action is triggered.
    /// </summary>
    public static event Action? UpdateRequested;

    public static void EnsureRegistered()
    {
        if (_registered) return;
        try
        {
            var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{AppId}");
            key?.SetValue("DisplayName", "OpenTaskbar");
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                key?.SetValue("IconUri", exePath);
            }
            key?.SetValue("ShowInSettings", 1, RegistryValueKind.DWord);
            _registered = true;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            AppLog.Warning($"AUMID registration failed: {ex.Message}");
        }
    }

    public static void ShowUpdateToast(string versionTag, string releaseUrl)
    {
        try
        {
            EnsureRegistered();

            var title = UiText.Get("UpdateAvailableTitle");
            var body = UiText.Format("UpdateToastBody", versionTag);
            var actionText = UiText.Format("UpdateDownload", versionTag);

            var escapedTitle = SecurityElement.Escape(title);
            var escapedBody = SecurityElement.Escape(body);
            var escapedAction = SecurityElement.Escape(actionText);

            var xmlString = $"""
            <toast launch="action=update">
                <visual>
                    <binding template="ToastGeneric">
                        <text>{escapedTitle}</text>
                        <text>{escapedBody}</text>
                    </binding>
                </visual>
                <actions>
                    <action content="{escapedAction}" arguments="action=update" />
                </actions>
            </toast>
            """;

            var xml = new XmlDocument();
            xml.LoadXml(xmlString);

            var toast = new ToastNotification(xml);
            toast.Activated += (s, _) =>
            {
                lock (_activeToasts) _activeToasts.Remove(s);
                AppLog.Info("Update toast activated.");
                UpdateRequested?.Invoke();
            };
            toast.Dismissed += (s, _) =>
            {
                lock (_activeToasts) _activeToasts.Remove(s);
            };
            toast.Failed += (s, _) =>
            {
                lock (_activeToasts) _activeToasts.Remove(s);
            };

            lock (_activeToasts) _activeToasts.Add(toast);

            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            notifier.Show(toast);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Update toast failed: {ex.Message}");
        }
    }

    public static void ShowInfoToast(string message)
    {
        try
        {
            EnsureRegistered();

            var title = "OpenTaskbar";
            var escapedTitle = SecurityElement.Escape(title);
            var escapedMessage = SecurityElement.Escape(message);

            var xmlString = $"""
            <toast>
                <visual>
                    <binding template="ToastGeneric">
                        <text>{escapedTitle}</text>
                        <text>{escapedMessage}</text>
                    </binding>
                </visual>
            </toast>
            """;

            var xml = new XmlDocument();
            xml.LoadXml(xmlString);

            var toast = new ToastNotification(xml);
            toast.Dismissed += (s, _) =>
            {
                lock (_activeToasts) _activeToasts.Remove(s);
            };
            toast.Failed += (s, _) =>
            {
                lock (_activeToasts) _activeToasts.Remove(s);
            };

            lock (_activeToasts) _activeToasts.Add(toast);

            var notifier = ToastNotificationManager.CreateToastNotifier(AppId);
            notifier.Show(toast);
        }
        catch (Exception ex)
        {
            AppLog.Warning($"Info toast failed: {ex.Message}");
        }
    }
}
