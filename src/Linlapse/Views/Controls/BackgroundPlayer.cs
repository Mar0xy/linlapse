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
                // Initialize LibVLCSharp core
                Core.Initialize();

                // Create LibVLC with minimal options - let LibVLCSharp.Avalonia handle video output
                _sharedLibVLC = new LibVLC(
                    "--no-video-title-show"
                );

                _libVLCAvailable = true;
                Log.Information("LibVLC initialized successfully");
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
        _contentGrid = new Grid();

        // Image view for static backgrounds
        _imageView = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _contentGrid.Children.Add(_imageView);

        // Pre-create VideoView - it needs to be in the visual tree before MediaPlayer is assigned
        if (_libVLCAvailable)
        {
            _videoView = new VideoView
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                IsVisible = false
            };
            _contentGrid.Children.Insert(0, _videoView);
        }

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

        if (source == _currentSource && isVideo == _currentIsVideo)
            return;

        ClearBackground();

        _currentSource = source;
        _currentIsVideo = isVideo;

        if (string.IsNullOrEmpty(source))
        {
            Log.Debug("Background source cleared");
            return;
        }

        var isActuallyVideo = isVideo || IsVideoFile(source);

        if (isActuallyVideo && _libVLCAvailable && _sharedLibVLC != null)
        {
            ShowVideo(source);
        }
        else if (isActuallyVideo && !_libVLCAvailable)
        {
            Log.Warning("Cannot display video background - LibVLC not available. " +
                "Install VLC: sudo apt install vlc (Debian/Ubuntu) or sudo dnf install vlc (Fedora)");
        }
        else
        {
            ShowImage(source);
        }
    }

    private void ClearBackground()
    {
        StopVideo();

        if (_imageView != null)
        {
            _imageView.Source = null;
            _imageView.IsVisible = true;
        }

        if (_videoView != null)
        {
            _videoView.IsVisible = false;
        }
    }

    private void ShowImage(string source)
    {
        try
        {
            if (_imageView == null) return;

            StopVideo();
            _imageView.IsVisible = true;
            if (_videoView != null) _videoView.IsVisible = false;

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
        if (_sharedLibVLC == null || _videoView == null)
        {
            Log.Warning("LibVLC or VideoView not available for video playback");
            return;
        }

        try
        {
            // Hide image, show video
            if (_imageView != null) _imageView.IsVisible = false;
            _videoView.IsVisible = true;

            // Create MediaPlayer if needed
            if (_mediaPlayer == null)
            {
                _mediaPlayer = new MediaPlayer(_sharedLibVLC);
                _mediaPlayer.Mute = MuteAudio;
                _mediaPlayer.EndReached += OnVideoEndReached;
            }
            else
            {
                _mediaPlayer.Stop();
            }

            // IMPORTANT: Assign MediaPlayer to VideoView BEFORE playing
            // This ensures LibVLCSharp.Avalonia can set up the video output correctly
            _videoView.MediaPlayer = _mediaPlayer;

            // Create media
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
                media.AddOption(":input-repeat=65535");
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
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Stop();
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

    public static void DisposeSharedResources()
    {
        _sharedLibVLC?.Dispose();
        _sharedLibVLC = null;
        _libVLCInitialized = false;
        _libVLCAvailable = false;
    }
}

