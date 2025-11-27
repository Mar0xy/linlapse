using Avalonia.Controls;
using Linlapse.Views.Controls;

namespace Linlapse.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        // Handle window closing to properly dispose LibVLC resources
        Closing += OnWindowClosing;
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Dispose the background player to stop video and clean up LibVLC
        if (BackgroundPlayer != null)
        {
            BackgroundPlayer.Dispose();
        }
        
        // Also dispose shared LibVLC resources
        Controls.BackgroundPlayer.DisposeSharedResources();
    }
}