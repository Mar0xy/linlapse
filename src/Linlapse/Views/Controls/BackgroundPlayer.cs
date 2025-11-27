using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Serilog;
using System.Runtime.InteropServices;

namespace Linlapse.Views.Controls;

/// <summary>
/// A control that displays either an image or video background using LibVLC
/// with frame-by-frame rendering to an Avalonia Image for proper scaling and z-ordering
/// </summary>
public class BackgroundPlayer : UserControl, IDisposable
{
    private static LibVLC? _sharedLibVLC;
    private static bool _libVLCInitialized;
    private static bool _libVLCAvailable;
    private static readonly object _initLock = new();

    private MediaPlayer? _mediaPlayer;
    private Image? _imageView;
    private string? _currentSource;
    private bool _currentIsVideo;
    private bool _isDisposed;

    // Frame buffer for video rendering - fixed size for performance
    private const int VideoWidth = 1920;
    private const int VideoHeight = 1080;
    private const int VideoPitch = VideoWidth * 4; // BGRA = 4 bytes per pixel
    private const int BufferSize = VideoPitch * VideoHeight;

    // Double buffering for smooth playback
    private WriteableBitmap? _videoBitmapA;
    private WriteableBitmap? _videoBitmapB;
    private bool _useBufferA = true;
    private byte[]? _videoBuffer;
    private readonly object _bufferLock = new();
    private GCHandle _bufferHandle;
    private IntPtr _bufferPtr;
    private bool _isPlayingVideo;
    private volatile bool _frameReady;

    // Timer for smooth UI updates
    private DispatcherTimer? _renderTimer;

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
        TryInitializeLibVLC();
        InitializeComponent();
    }

    private static void TryInitializeLibVLC()
    {
        lock (_initLock)
        {
            if (_libVLCInitialized) return;
            _libVLCInitialized = true;

            try
            {
                Core.Initialize();

                // Create LibVLC with minimal options
                _sharedLibVLC = new LibVLC(
                    "--no-video-title-show",
                    "--no-snapshot-preview",
                    "--no-osd"
                );

                _libVLCAvailable = true;
                Log.Information("LibVLC initialized successfully for frame-by-frame rendering");
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
        // Single Image view for both static images and video frames
        _imageView = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        Content = _imageView;

        // Pre-allocate video buffer and bitmap for better performance
        InitializeVideoBuffer();
    }

    private void InitializeVideoBuffer()
    {
        if (!_libVLCAvailable) return;

        lock (_bufferLock)
        {
            try
            {
                // Allocate buffer for video frames
                _videoBuffer = new byte[BufferSize];

                // Pin the buffer and get its address
                _bufferHandle = GCHandle.Alloc(_videoBuffer, GCHandleType.Pinned);
                _bufferPtr = _bufferHandle.AddrOfPinnedObject();

                // Create two WriteableBitmaps for double buffering
                _videoBitmapA = new WriteableBitmap(
                    new PixelSize(VideoWidth, VideoHeight),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    AlphaFormat.Premul);

                _videoBitmapB = new WriteableBitmap(
                    new PixelSize(VideoWidth, VideoHeight),
                    new Vector(96, 96),
                    Avalonia.Platform.PixelFormat.Bgra8888,
                    AlphaFormat.Premul);

                // Set up timer for smooth rendering at 60fps
                _renderTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(16) // ~60fps
                };
                _renderTimer.Tick += OnRenderTick;

                Log.Debug("Video buffer initialized: {Width}x{Height} with double buffering", VideoWidth, VideoHeight);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to initialize video buffer");
            }
        }
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
        _isPlayingVideo = false;
        _frameReady = false;
        _renderTimer?.Stop();

        if (_imageView != null)
        {
            _imageView.Source = null;
        }
    }

    private void FreeVideoBuffer()
    {
        lock (_bufferLock)
        {
            _renderTimer?.Stop();
            if (_bufferHandle.IsAllocated)
            {
                _bufferHandle.Free();
            }
            _videoBuffer = null;
            _videoBitmapA = null;
            _videoBitmapB = null;
            _bufferPtr = IntPtr.Zero;
        }
    }

    private void ShowImage(string source)
    {
        try
        {
            if (_imageView == null) return;

            StopVideo();
            _isPlayingVideo = false;

            if (File.Exists(source))
            {
                Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try
                    {
                        if (_imageView != null && !_isPlayingVideo)
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
                    if (_imageView != null && !_isPlayingVideo)
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
        if (_sharedLibVLC == null || _bufferPtr == IntPtr.Zero)
        {
            Log.Warning("LibVLC or video buffer not available for video playback");
            return;
        }

        try
        {
            _isPlayingVideo = true;
            _frameReady = false;

            // Create MediaPlayer with video callbacks for frame-by-frame rendering
            if (_mediaPlayer == null)
            {
                _mediaPlayer = new MediaPlayer(_sharedLibVLC);
                _mediaPlayer.Mute = MuteAudio;
                _mediaPlayer.EndReached += OnVideoEndReached;

                // Set up fixed video format - RV32 is BGRA which Avalonia supports
                _mediaPlayer.SetVideoFormat("RV32", VideoWidth, VideoHeight, VideoPitch);

                // Set up video callbacks for frame-by-frame rendering
                _mediaPlayer.SetVideoCallbacks(
                    LockVideo,
                    UnlockVideo,
                    DisplayVideo
                );
            }
            else
            {
                _mediaPlayer.Stop();
            }

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
                // Loop the video
                media.AddOption(":input-repeat=65535");
                if (MuteAudio)
                {
                    media.AddOption(":no-audio");
                }

                _mediaPlayer.Play(media);
                _renderTimer?.Start();
                Log.Debug("Playing background video with frame rendering: {Path}", source);
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error showing video background");
            _isPlayingVideo = false;
        }
    }

    private IntPtr LockVideo(IntPtr opaque, IntPtr planes)
    {
        // Provide the buffer address to VLC
        Marshal.WriteIntPtr(planes, _bufferPtr);
        return IntPtr.Zero;
    }

    private void UnlockVideo(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        // Frame is ready to be displayed
        _frameReady = true;
    }

    private void DisplayVideo(IntPtr opaque, IntPtr picture)
    {
        // This is called after unlock - frame is fully ready
    }

    private void OnRenderTick(object? sender, EventArgs e)
    {
        if (_isDisposed || !_isPlayingVideo || !_frameReady) return;

        try
        {
            lock (_bufferLock)
            {
                if (_videoBuffer == null || _imageView == null) return;

                // Get the current bitmap to write to
                var targetBitmap = _useBufferA ? _videoBitmapA : _videoBitmapB;
                if (targetBitmap == null) return;

                // Copy frame data to bitmap
                using (var fb = targetBitmap.Lock())
                {
                    Marshal.Copy(_videoBuffer, 0, fb.Address, BufferSize);
                }

                // Swap to the other buffer for next frame
                _useBufferA = !_useBufferA;

                // Update image source (this triggers the redraw)
                _imageView.Source = targetBitmap;
                _frameReady = false;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error rendering video frame");
        }
    }

    private void OnVideoEndReached(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_mediaPlayer != null && !_isDisposed && _isPlayingVideo)
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
            _renderTimer?.Stop();
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
        _isPlayingVideo = false;
        _frameReady = false;

        try
        {
            _renderTimer?.Stop();
            StopVideo();

            if (_mediaPlayer != null)
            {
                _mediaPlayer.EndReached -= OnVideoEndReached;
                _mediaPlayer.Dispose();
                _mediaPlayer = null;
            }

            FreeVideoBuffer();
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

