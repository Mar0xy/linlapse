using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Serilog;

namespace Linlapse.Views.Controls;

/// <summary>
/// A control that displays background images (video support can be added with GStreamer/FFmpeg integration)
/// </summary>
public class BackgroundPlayer : UserControl, IDisposable
{
    private Image? _imageView;
    private Border? _overlayBorder;
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
    }

    private void InitializeComponent()
    {
        // Create the grid to hold background and overlay
        var grid = new Grid();

        // Create image view
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
    }

    private void UpdateBackground()
    {
        var source = Source;
        var isVideo = IsVideo;

        if (string.IsNullOrEmpty(source) || source == _currentSource)
            return;

        _currentSource = source;

        // For now, show images. Video backgrounds are noted in IsVideo property
        // but we display the first frame/thumbnail from the API instead
        ShowImage(source);
        
        if (isVideo)
        {
            Log.Debug("Video background detected, showing static image. Video playback requires LibVLC.");
        }
    }

    private void ShowImage(string source)
    {
        try
        {
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

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        
        // Cleanup if needed
    }
}

