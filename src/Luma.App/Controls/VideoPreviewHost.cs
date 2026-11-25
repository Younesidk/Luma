using Luma.App.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace Luma.App.Controls;

/// <summary>
/// Plays a local video inside Luma's window: muted, looping, letterboxed (<c>Uniform</c>), no transport controls.
/// It is a normal XAML element (no child HWND), so overlays can be drawn on top.
/// The <see cref="MediaPlayer"/> is disposed on <c>Unloaded</c> and whenever <see cref="Source"/> changes, so the file
/// is never left locked. Playback errors are reported through <see cref="Error"/> (bind it TwoWay).
/// </summary>
public sealed partial class VideoPreviewHost : UserControl
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(string), typeof(VideoPreviewHost), new PropertyMetadata(null, (d, _) => ((VideoPreviewHost)d).OnSourceChanged()));

    public static readonly DependencyProperty ErrorProperty = DependencyProperty.Register(
        nameof(Error), typeof(string), typeof(VideoPreviewHost), new PropertyMetadata(null));

    private readonly MediaPlayerElement _element;
    private MediaPlayer? _player;
    private MediaSource? _media;

    public VideoPreviewHost()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        _element = new MediaPlayerElement
        {
            AreTransportControlsEnabled = false,
            AutoPlay = true,
            Stretch = Stretch.Uniform,
            IsTabStop = false
        };
        Content = _element;
        Loaded += (_, _) => Start();
        Unloaded += (_, _) =>
        {
            if (!IsLoaded) Stop();
        };
    }

    /// <summary>Full path of the local video file to play.</summary>
    public string? Source { get => (string?)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    /// <summary>Set by the control when the video can't be played (null while playing fine).</summary>
    public string? Error { get => (string?)GetValue(ErrorProperty); set => SetValue(ErrorProperty, value); }

    private void OnSourceChanged()
    {
        Stop();
        if (IsLoaded) Start();
    }

    private void Start()
    {
        if (_player is not null || Source is not { Length: > 0 } path) return;
        Error = null;
        try
        {
            var media = MediaSource.CreateFromUri(new Uri(Path.GetFullPath(path)));
            var player = new MediaPlayer
            {
                IsLoopingEnabled = true,
                IsMuted = true,
                AutoPlay = true,
                Source = media
            };
            player.MediaFailed += OnMediaFailed;
            _media = media;
            _player = player;
            _element.SetMediaPlayer(player);
        }
        catch (Exception ex)
        {
            Log(ex, "The video preview couldn't be created");
            Error = "The preview couldn't be started.";
            Stop();
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        // Raised on a media thread.
        var message = string.IsNullOrWhiteSpace(args.ErrorMessage) ? "This video can't be played." : args.ErrorMessage;
        Log(args.ExtendedErrorCode, $"Video preview failed: {args.Error}");
        void Report()
        {
            if (ReferenceEquals(_player, sender)) Error = message;
        }
        try
        {
            LumaServices.Get<IUiDispatcher>().Post(Report);
        }
        catch (InvalidOperationException)
        {
            DispatcherQueue?.TryEnqueue(Report);
        }
    }

    private void Stop()
    {
        var player = _player;
        var media = _media;
        _player = null;
        _media = null;
        _element.SetMediaPlayer(null);
        if (player is not null)
        {
            player.MediaFailed -= OnMediaFailed;
            try
            {
                player.Pause();
                player.Source = null;
            }
            catch (Exception)
            {
                // already torn down
            }
            player.Dispose();
        }
        media?.Dispose();
    }

    private static void Log(Exception? ex, string message)
    {
        try
        {
            LumaServices.Get<ILogger<VideoPreviewHost>>().LogError(ex, "{Message}", message);
        }
        catch (InvalidOperationException)
        {
            // logging unavailable
        }
    }
}
