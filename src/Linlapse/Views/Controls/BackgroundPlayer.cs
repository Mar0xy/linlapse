using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.Avalonia;
using Serilog;

namespace Linlapse.Views.Controls;

/// <summary>
/// A control that displays either an image or video background using LibVLC
/// </summary>
public class BackgroundPlayer : UserControl, IDisposable
{
    private static LibVLC? _sharedLibVLC;
    private static bool _libVLCInitialized;
    private static bool _libVLCAvailable;
    private static readonly object _initLock = new();

    private MediaPlayer? _mediaPlayer;
    private Image? _imageView;
    private VideoView? _videoView;
    private Grid? _contentGrid;
    private string? _currentSource;
    private bool _currentIsVideo;
    private bool _isDisposed;

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<BackgroundPlayer, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsVideoProperty =
        AvaloniaProperty.Register<BackgroundPlayer, bool>(nameof(IsVideo));

    public static readonly StyledProperty<bool> MuteAudioProperty =
        AvaloniaProperty.Register<BackgroundPlayer, bool>(nameof(MuteAudio), true);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsVideo
    {
        get => GetValue(IsVideoProperty);
        set => SetValue(IsVideoProperty, value);
    }

    public bool MuteAudio
    {
        get => GetValue(MuteAudioProperty);
        set => SetValue(MuteAudioProperty, value);
    }

    public BackgroundPlayer()
    {
        InitializeComponent();
        TryInitializeLibVLC();
    }

    private static void TryInitializeLibVLC()
    {
        lock (_initLock)
        {
            if (_libVLCInitialized) return;
            _libVLCInitialized = true;

            try
            {
                // Initialize LibVLCSharp core - on Linux this uses system libvlc
                Core.Initialize();

                // Create shared LibVLC instance with options optimized for embedded playback
                // These options ensure the video is rendered inside our control, not in a separate window
                _sharedLibVLC = new LibVLC(
                    "--quiet",                      // Reduce logging
                    "--no-video-title-show",        // Don't show title on video
                    "--no-xlib",                    // Disable Xlib threading (safer for embedded)
                    "--vout=xcb_x11",               // Use X11 output (better embedding on Linux)
                    "--avcodec-hw=none"             // Disable hardware decoding for compatibility
                );

                _libVLCAvailable = true;
                Log.Information("LibVLC initialized successfully for embedded video playback");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to initialize LibVLC - video backgrounds will not be available. " +
                    "Install libvlc: sudo apt install vlc (Debian/Ubuntu) or sudo dnf install vlc (Fedora)");
                _libVLCAvailable = false;
            }
        }
    }

    private void InitializeComponent()
    {
        // Create the grid to hold background
        _contentGrid = new Grid();

        // Create image view (default/fallback)
        _imageView = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _contentGrid.Children.Add(_imageView);

        Content = _contentGrid;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty || change.Property == IsVideoProperty)
        {
            UpdateBackground();
        }
        else if (change.Property == MuteAudioProperty && _mediaPlayer != null)
        {
            _mediaPlayer.Mute = MuteAudio;
        }
    }

    private void UpdateBackground()
    {
        var source = Source;
        var isVideo = IsVideo;

        // Always update if source changed OR if video state changed
        if (source == _currentSource && isVideo == _currentIsVideo)
            return;

        // Clear previous state
        ClearBackground();

        _currentSource = source;
        _currentIsVideo = isVideo;

        if (string.IsNullOrEmpty(source))
        {
            Log.Debug("Background source cleared");
            return;
        }

        // Auto-detect video from file extension if IsVideo is false
        var isActuallyVideo = isVideo || IsVideoFile(source);

        if (isActuallyVideo && _libVLCAvailable && _sharedLibVLC != null)
        {
            ShowVideo(source);
        }
        else if (isActuallyVideo && !_libVLCAvailable)
        {
            // Video file but LibVLC not available - show nothing or fallback
            Log.Warning("Cannot display video background - LibVLC not available. " +
                "Install VLC: sudo apt install vlc (Debian/Ubuntu) or sudo dnf install vlc (Fedora)");
            // Don't try to load as image - it will fail
        }
        else
        {
            ShowImage(source);
        }
    }

    private void ClearBackground()
    {
        // Stop video playback
        StopVideo();

        // Clear image
        if (_imageView != null)
        {
            _imageView.Source = null;
            _imageView.IsVisible = true;
        }
    }

    private void ShowImage(string source)
    {
        try
        {
            if (_imageView == null || _contentGrid == null) return;

            // Make sure video is stopped and removed
            StopVideo();

            // Show image view
            _imageView.IsVisible = true;

            if (File.Exists(source))
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        if (_imageView != null)
                        {
                            var bitmap = new Bitmap(source);
                            _imageView.Source = bitmap;
                            Log.Debug("Loaded background image: {Path}", source);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Failed to load background image: {Path}", source);
                    }
                });
            }
            else if (source.StartsWith("http://") || source.StartsWith("https://"))
            {
                // Load from URL asynchronously
                _ = LoadImageFromUrlAsync(source);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error showing image background");
        }
    }

    private static bool IsVideoFile(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        return extension is ".webm" or ".mp4" or ".mkv" or ".avi" or ".mov" or ".wmv" or ".flv";
    }

    private async Task LoadImageFromUrlAsync(string url)
    {
        try
        {
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");
            var bytes = await httpClient.GetByteArrayAsync(url);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try
                {
                    using var stream = new MemoryStream(bytes);
                    var bitmap = new Bitmap(stream);
                    if (_imageView != null)
                    {
                        _imageView.Source = bitmap;
                        Log.Debug("Loaded background image from URL: {Url}", url);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Failed to decode background image from URL: {Url}", url);
                }
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download background image from URL: {Url}", url);
        }
    }

    private void ShowVideo(string source)
    {
        if (_sharedLibVLC == null || _contentGrid == null)
        {
            Log.Warning("LibVLC not available for video playback");
            return;
        }

        try
        {
            // Hide image view when showing video
            if (_imageView != null)
                _imageView.IsVisible = false;

            // Stop any existing playback first
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Stop();
            }

            // Create VideoView first, then assign MediaPlayer
            // This order is important for proper embedding on Linux
            if (_videoView == null)
            {
                _videoView = new VideoView
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                // Insert at beginning so it's behind other content
                _contentGrid.Children.Insert(0, _videoView);
                Log.Debug("Created VideoView for embedded playback");
            }

            // Create media player if needed and assign to VideoView
            if (_mediaPlayer == null)
            {
                _mediaPlayer = new MediaPlayer(_sharedLibVLC);
                _mediaPlayer.Mute = MuteAudio;
                _mediaPlayer.EnableHardwareDecoding = false; // Disable for better Linux compatibility
                _mediaPlayer.EndReached += OnVideoEndReached;
            }
            
            // Assign MediaPlayer to VideoView (important for embedding)
            _videoView.MediaPlayer = _mediaPlayer;

            // Create and play media
            Media? media = null;
            if (source.StartsWith("http://") || source.StartsWith("https://"))
            {
                media = new Media(_sharedLibVLC, new Uri(source));
            }
            else if (File.Exists(source))
            {
                media = new Media(_sharedLibVLC, source, FromType.FromPath);
            }

            if (media != null)
            {
                // Add options for looping, muting
                media.AddOption(":input-repeat=65535"); // Loop many times
                media.AddOption(":no-video-title-show");
                if (MuteAudio)
                {
                    media.AddOption(":no-audio");
                }

                _mediaPlayer.Play(media);
                Log.Debug("Playing background video: {Path}", source);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error showing video background");
        }
    }

    private void OnVideoEndReached(object? sender, EventArgs e)
    {
        // Loop the video by restarting from beginning
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_mediaPlayer != null && !_isDisposed)
            {
                _mediaPlayer.Stop();
                _mediaPlayer.Play();
            }
        });
    }

    private void StopVideo()
    {
        try
        {
            _mediaPlayer?.Stop();

            if (_videoView != null && _contentGrid != null)
            {
                _contentGrid.Children.Remove(_videoView);
                _videoView = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error stopping video");
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        try
        {
            StopVideo();

            if (_mediaPlayer != null)
            {
                _mediaPlayer.EndReached -= OnVideoEndReached;
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error disposing BackgroundPlayer");
        }
    }

    // Note: Don't dispose _sharedLibVLC here as it's shared across instances
    public static void DisposeSharedResources()
    {
        _sharedLibVLC?.Dispose();
        _sharedLibVLC = null;
        _libVLCInitialized = false;
        _libVLCAvailable = false;
    }
}

