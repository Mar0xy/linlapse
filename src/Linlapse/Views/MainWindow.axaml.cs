using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Linlapse.Views.Controls;

namespace Linlapse.Views;

public partial class MainWindow : Window
{
    private object? _savedSelection;
    
    public MainWindow()
    {
        InitializeComponent();
        
        // Handle window closing to properly dispose LibVLC resources
        Closing += OnWindowClosing;
        
        // Prevent right-click from changing selection in the game list
        // We save the selection before the right-click and restore it after
        GameListBox.AddHandler(PointerPressedEvent, OnGameListPointerPressed, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        GameListBox.AddHandler(PointerReleasedEvent, OnGameListPointerReleased, Avalonia.Interactivity.RoutingStrategies.Bubble);
    }
    
    private void OnGameListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // If it's a right-click, save the current selection
        if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
        {
            _savedSelection = GameListBox.SelectedItem;
        }
        else
        {
            _savedSelection = null;
        }
    }
    
    private void OnGameListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        // If we saved a selection from a right-click, restore it
        if (_savedSelection != null && e.InitialPressMouseButton == MouseButton.Right)
        {
            // Use Dispatcher to restore selection after the ListBox has processed the event
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (_savedSelection != null)
                {
                    GameListBox.SelectedItem = _savedSelection;
                    _savedSelection = null;
                }
            });
        }
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