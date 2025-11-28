using Avalonia.Controls;
using Avalonia.Input;
using Linlapse.Views.Controls;

namespace Linlapse.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        
        // Handle window closing to properly dispose LibVLC resources and clean up event handlers
        Closing += OnWindowClosing;
        
        // Prevent right-click from changing selection in the game list
        // Handle in tunnel phase to intercept before ListBox processes the event
        GameListBox.AddHandler(PointerPressedEvent, OnGameListPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }
    
    private void OnGameListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // If it's a right-click, mark as handled to prevent selection change
        // The context menu will still open because ContextMenu handles it separately
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            e.Handled = true;
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // Unsubscribe from event handlers to prevent memory leaks
        Closing -= OnWindowClosing;
        GameListBox.RemoveHandler(PointerPressedEvent, OnGameListPointerPressed);
        
        // Dispose the background player to stop video and clean up LibVLC
        if (BackgroundPlayer != null)
        {
            BackgroundPlayer.Dispose();
        }
        
        // Also dispose shared LibVLC resources
        Controls.BackgroundPlayer.DisposeSharedResources();
    }
}