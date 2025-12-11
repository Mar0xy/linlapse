using CommunityToolkit.Mvvm.ComponentModel;
using Linlapse.Models;

namespace Linlapse.ViewModels;

public partial class GameCardViewModel : ViewModelBase
{
    [ObservableProperty]
    private GameInfo _game;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private double _downloadProgress;

    [ObservableProperty]
    private bool _isDownloading;

    public GameCardViewModel(GameInfo game)
    {
        _game = game;
        UpdateStatusText();
    }

    public void UpdateStatusText()
    {
        StatusText = Game.State switch
        {
            GameState.NotInstalled => "Not Installed",
            GameState.Installing => "Installing...",
            GameState.Updating => "Updating...",
            GameState.Ready => Game.Version ?? "Ready to Play",
            GameState.Running => "Running",
            GameState.Repairing => "Repairing...",
            GameState.NeedsUpdate => "Update Available",
            GameState.Preloading => "Preloading...",
            _ => "Unknown"
        };
    }

    public string GameIcon => Game.GameType switch
    {
        GameType.HonkaiImpact3rd => "🔥",
        GameType.GenshinImpact => "⭐",
        GameType.HonkaiStarRail => "🚂",
        GameType.ZenlessZoneZero => "⚡",
        GameType.WutheringWaves => "🌊",
        GameType.Custom => "🎮",
        _ => "🎯"
    };
}
