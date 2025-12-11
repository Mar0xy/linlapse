using Linlapse.Models;
using Linlapse.Services;
using Serilog;

namespace Linlapse.ViewModels;

/// <summary>
/// Event handlers for MainWindowViewModel
/// </summary>
public partial class MainWindowViewModel
{
    private void RefreshGamesCollection()
    {
        // Remember the currently selected game type (not ID, since ID changes with region)
        var selectedGameType = SelectedGame?.GameType;

        Games.Clear();
        
        // Get saved region preferences per game type
        var settings = _settingsService.Settings;
        var gameTypes = new[] { GameType.HonkaiImpact3rd, GameType.GenshinImpact, GameType.HonkaiStarRail, GameType.ZenlessZoneZero, GameType.WutheringWaves };
        
        foreach (var gameType in gameTypes)
        {
            // Use the saved region preference for this game type, default to Global
            GameRegion regionToUse = GameRegion.Global;
            if (settings.SelectedRegionPerGame.TryGetValue(gameType.ToString(), out var savedRegion) &&
                Enum.TryParse<GameRegion>(savedRegion, out var parsedRegion))
            {
                regionToUse = parsedRegion;
            }
            
            var game = _gameService.GetGameByTypeAndRegion(gameType, regionToUse);
            if (game != null)
            {
                Games.Add(game);
                // Load icon in background
                _ = LoadGameIconAsync(game);
            }
        }

        // Restore selection by finding the game with the same type
        if (selectedGameType != null)
        {
            var gameToSelect = Games.FirstOrDefault(g => g.GameType == selectedGameType);
            if (gameToSelect != null)
            {
                SelectedGame = gameToSelect;
                return;
            }
        }

        // If no previous selection or game not found, select first game
        if (SelectedGame == null && Games.Count > 0)
        {
            SelectedGame = Games[0];
        }
    }

    private void OnInstallProgress(object? sender, InstallProgress progress)
    {
        ProgressPercent = progress.PercentComplete;
        
        // For extraction, just show "Extracting..." without file counts since 7z progress is unreliable
        if (progress.State == InstallState.Extracting)
        {
            ProgressText = "Extracting...";
        }
        else
        {
            ProgressText = $"Installing: {progress.ProcessedFiles}/{progress.TotalFiles} files";
        }
    }

    private void OnRepairProgress(object? sender, RepairProgress progress)
    {
        ProgressPercent = progress.PercentComplete;
        ProgressText = $"Verifying: {progress.ProcessedFiles}/{progress.TotalFiles} files";
    }

    private void OnUpdateProgress(object? sender, UpdateProgress progress)
    {
        ProgressPercent = progress.PercentComplete;
        ProgressText = progress.State switch
        {
            UpdateState.DownloadingPatch => $"Downloading patch: {progress.PercentComplete:F1}%",
            UpdateState.DownloadingFull => $"Downloading update: {progress.PercentComplete:F1}%",
            UpdateState.ApplyingPatch => "Applying patch...",
            UpdateState.Extracting => $"Extracting: {progress.ProcessedFiles}/{progress.TotalFiles} files",
            _ => $"Updating: {progress.PercentComplete:F1}%"
        };
    }

    private void OnGameSettingsSaved(object? sender, GameSettingsSavedEventArgs e)
    {
        StatusMessage = "Game settings saved successfully";
        
        // Switch to the game variant for the selected region
        var newGame = _gameService.GetGameByTypeAndRegion(e.GameType, e.SelectedRegion);
        if (newGame != null)
        {
            // Refresh games collection and select the new game variant
            RefreshGamesCollectionForRegion(e.GameType, e.SelectedRegion);
            
            // Load the background for the new game
            _ = LoadBackgroundAsync(newGame.Id);
        }
        
        // Close the settings dialog
        CloseGameSettings();
    }

    private void RefreshGamesCollectionForRegion(GameType gameType, GameRegion region)
    {
        Games.Clear();
        
        // Get saved region preferences for each game type
        var settings = _settingsService.Settings;
        var gameTypes = new[] { GameType.HonkaiImpact3rd, GameType.GenshinImpact, GameType.HonkaiStarRail, GameType.ZenlessZoneZero, GameType.WutheringWaves };
        
        GameInfo? gameToSelect = null;
        
        foreach (var gt in gameTypes)
        {
            // For the changed game type, use the new region; for others, use saved preferences
            GameRegion regionToUse;
            if (gt == gameType)
            {
                regionToUse = region;
            }
            else if (settings.SelectedRegionPerGame.TryGetValue(gt.ToString(), out var savedRegion) &&
                     Enum.TryParse<GameRegion>(savedRegion, out var parsedRegion))
            {
                regionToUse = parsedRegion;
            }
            else
            {
                regionToUse = GameRegion.Global;
            }
            
            var game = _gameService.GetGameByTypeAndRegion(gt, regionToUse);
            if (game != null)
            {
                Games.Add(game);
                _ = LoadGameIconAsync(game);
                
                // Track the game we want to select
                if (gt == gameType)
                {
                    gameToSelect = game;
                }
            }
        }
        
        // Select the game for the changed type
        if (gameToSelect != null)
        {
            SelectedGame = gameToSelect;
        }
        else if (Games.Count > 0)
        {
            SelectedGame = Games[0];
        }
    }

    private void OnGameSettingsClosed(object? sender, EventArgs e)
    {
        CloseGameSettings();
    }

    private void OnWineRunnerDialogClosed(object? sender, EventArgs e)
    {
        CloseWineRunnerDialog();
    }

    private void OnRunnersUpdated(object? sender, EventArgs e)
    {
        // Re-check wine version when runners are updated
        _ = UpdateWineInfoAsync();
    }

}
