using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OpenTaskbar.Services;
using Windows.Foundation;
using Windows.Media.Control;
using MediaSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using MediaSessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace OpenTaskbar.Widgets;

/// <summary>
/// Shows and controls the system's current media session (Spotify, browsers, players) through the
/// Global System Media Transport Controls. Collapses itself when nothing is playing.
/// </summary>
public partial class MediaWidget : DockWidget
{
    private MediaSessionManager? _manager;
    private MediaSession? _session;
    private int _refreshVersion;
    private string? _artKey;

    public MediaWidget()
    {
        InitializeComponent();
        HasData = false;
    }

    protected override async void OnActivated()
    {
        try
        {
            var manager = await MediaSessionManager.RequestAsync();
            if (!IsActive) return;
            _manager = manager;
            _manager.CurrentSessionChanged += Manager_CurrentSessionChanged;
            BindSession(_manager.GetCurrentSession());
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            AppLog.Warning($"Media session manager unavailable: {ex.Message}");
            HasData = false;
        }
    }

    protected override void OnDeactivated()
    {
        BindSession(null);
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= Manager_CurrentSessionChanged;
            _manager = null;
        }
    }

    private void Manager_CurrentSessionChanged(MediaSessionManager sender, CurrentSessionChangedEventArgs args) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsActive && _manager is not null) BindSession(_manager.GetCurrentSession());
        });

    private void BindSession(MediaSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged -= Session_PlaybackInfoChanged;
        }

        _session = session;
        if (_session is not null)
        {
            _session.MediaPropertiesChanged += Session_MediaPropertiesChanged;
            _session.PlaybackInfoChanged += Session_PlaybackInfoChanged;
        }

        _artKey = null;
        Refresh(includeProperties: true);
    }

    private void Session_MediaPropertiesChanged(MediaSession sender, MediaPropertiesChangedEventArgs args) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Refresh(includeProperties: true));

    private void Session_PlaybackInfoChanged(MediaSession sender, PlaybackInfoChangedEventArgs args) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Refresh(includeProperties: false));

    private async void Refresh(bool includeProperties)
    {
        // Only property fetches are versioned; a playback-only update must not cancel an in-flight title fetch.
        var version = includeProperties ? ++_refreshVersion : _refreshVersion;
        var session = _session;
        if (session is null)
        {
            HasData = false;
            return;
        }

        try
        {
            var playback = session.GetPlaybackInfo();
            var controls = playback.Controls;
            var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            PlayPauseGlyph.Content = FindResource(playing ? "IconPauseFill" : "IconPlayFill");
            PlayPauseButton.IsEnabled = controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled;
            PreviousButton.IsEnabled = controls.IsPreviousEnabled;
            NextButton.IsEnabled = controls.IsNextEnabled;
            if (!includeProperties) return;

            var properties = await session.TryGetMediaPropertiesAsync();
            if (version != _refreshVersion || !IsActive) return;
            var title = properties?.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                HasData = false;
                return;
            }

            TitleText.Text = title;
            ArtistText.Text = string.IsNullOrWhiteSpace(properties!.Artist) ? properties.AlbumTitle : properties.Artist;
            Card.ToolTip = string.IsNullOrWhiteSpace(ArtistText.Text) ? title : $"{title}\n{ArtistText.Text}";
            HasData = true;
            await LoadArtAsync(properties, version);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // Sessions vanish while their owning app exits; the next CurrentSessionChanged rebinds.
            AppLog.Warning($"Media session refresh failed: {ex.Message}");
            if (version == _refreshVersion) HasData = false;
        }
    }

    private async Task LoadArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties properties, int version)
    {
        var key = $"{properties.Title}|{properties.Artist}|{properties.AlbumTitle}";
        if (key == _artKey) return;
        // Players often publish artwork in a later event with the same title, so the key is recorded only on success.
        if (properties.Thumbnail is null)
        {
            ArtImage.Source = null;
            ArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        try
        {
            using var stream = await properties.Thumbnail.OpenReadAsync();
            if (version != _refreshVersion) return;
            using var managed = stream.AsStreamForRead();
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 96;
            image.StreamSource = managed;
            image.EndInit();
            image.Freeze();
            ArtImage.Source = image;
            ArtPlaceholder.Visibility = Visibility.Collapsed;
            _artKey = key;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException or NotSupportedException or FileFormatException)
        {
            AppLog.Warning($"Media artwork unavailable: {ex.Message}");
        }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await RunAsync(s => s.TrySkipPreviousAsync());
    private async void PlayPause_Click(object sender, RoutedEventArgs e) => await RunAsync(s => s.TryTogglePlayPauseAsync());
    private async void Next_Click(object sender, RoutedEventArgs e) => await RunAsync(s => s.TrySkipNextAsync());

    private async Task RunAsync(Func<MediaSession, IAsyncOperation<bool>> command)
    {
        if (_session is null) return;
        try
        {
            if (!await command(_session)) Report("Medya oturumu kontrol edilemedi.");
        }
        catch (System.Runtime.InteropServices.COMException ex)
        {
            AppLog.Warning($"Media command failed: {ex.Message}");
            Report("Medya oturumu kontrol edilemedi.");
        }
    }
}
