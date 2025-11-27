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
    private static readonly object _initLock = new();

    private MediaPlayer? _mediaPlayer;
    private Image? _imageView;
    private Border? _overlayBorder;
    private VideoView? _videoView;
    private string? _currentSource;
    private bool _isDisposed;

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<BackgroundPlayer, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsVideoProperty =
        AvaloniaProperty.Register<BackgroundPlayer, bool>(nameof(IsVideo));

    public static readonly StyledProperty<double> OverlayOpacityProperty =
        AvaloniaProperty.Register<BackgroundPlayer, double>(nameof(OverlayOpacity), 0.4);

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

    public double OverlayOpacity
    {
        get => GetValue(OverlayOpacityProperty);
        set => SetValue(OverlayOpacityProperty, value);
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

            try
            {
                // Initialize LibVLCSharp core - on Linux this uses system libvlc
                Core.Initialize();

                // Create shared LibVLC instance with options for background video
                _sharedLibVLC = new LibVLC(
                    "--no-xlib",           // Don't use Xlib threading
                    "--quiet",             // Reduce logging
                    "--no-video-title-show" // Don't show title on video
                );

                _libVLCInitialized = true;
                Log.Information("LibVLC initialized successfully");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to initialize LibVLC - video backgrounds will not be available. " +
                    "Install libvlc: sudo apt install libvlc-dev (Debian/Ubuntu) or sudo dnf install vlc-devel (Fedora)");
                _libVLCInitialized = false;
            }
        }
    }

    private void InitializeComponent()
    {
        // Create the grid to hold background and overlay
        var grid = new Grid();

        // Create image view (default/fallback)
        _imageView = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(_imageView);

        // Create overlay for darkening effect
        _overlayBorder = new Border
        {
            Background = new SolidColorBrush(Colors.Black),
            Opacity = OverlayOpacity
        };
        grid.Children.Add(_overlayBorder);

        Content = grid;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == SourceProperty || change.Property == IsVideoProperty)
        {
            UpdateBackground();
        }
        else if (change.Property == OverlayOpacityProperty && _overlayBorder != null)
        {
            _overlayBorder.Opacity = OverlayOpacity;
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

        if (string.IsNullOrEmpty(source) || source == _currentSource)
            return;

        _currentSource = source;

        if (isVideo && _sharedLibVLC != null)
        {
            ShowVideo(source);
        }
        else
        {
            ShowImage(source);
        }
    }

    private void ShowImage(string source)
    {
        try
        {
            // Stop any playing video
            StopVideo();

            if (_imageView == null) return;

            // Show image view
            _imageView.IsVisible = true;

            if (File.Exists(source))
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        var bitmap = new Bitmap(source);
                        _imageView.Source = bitmap;
                        Log.Debug("Loaded background image: {Path}", source);
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
        if (_sharedLibVLC == null)
        {
            Log.Warning("LibVLC not available, falling back to image");
            ShowImage(source);
            return;
        }

        try
        {
            // Hide image view
            if (_imageView != null)
                _imageView.IsVisible = false;

            // Create media player if needed
            if (_mediaPlayer == null)
            {
                _mediaPlayer = new MediaPlayer(_sharedLibVLC);
                _mediaPlayer.Mute = MuteAudio;
                _mediaPlayer.EnableHardwareDecoding = true;
                _mediaPlayer.EndReached += OnVideoEndReached;
            }

            // Create VideoView from LibVLCSharp.Avalonia if needed
            if (_videoView == null && Content is Grid grid)
            {
                _videoView = new VideoView
                {
                    MediaPlayer = _mediaPlayer,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Stretch
                };
                // Insert at beginning so it's behind the overlay
                grid.Children.Insert(0, _videoView);
            }
            else if (_videoView != null)
            {
                _videoView.MediaPlayer = _mediaPlayer;
            }

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
                // Add options for smooth looping and muting
                media.AddOption(":input-repeat=65535"); // Loop many times
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
            Log.Error(ex, "Error showing video background, falling back to image");
            ShowImage(source);
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

            if (_videoView != null && Content is Grid grid)
            {
                grid.Children.Remove(_videoView);
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
    }
}

