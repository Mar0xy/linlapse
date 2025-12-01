using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Linlapse.Models;
using Linlapse.Services;
using Serilog;

namespace Linlapse.ViewModels;

/// <summary>
/// ViewModel for per-game settings dialog
/// </summary>
public partial class GameSettingsViewModel : ViewModelBase
{
    private readonly SettingsService _settingsService;
    private readonly WineRunnerService _runnerService;
    private readonly GameInfo _game;
    
    [ObservableProperty]
    private string _gameName = string.Empty;
    
    [ObservableProperty]
    private GameRegion _selectedRegion;
    
    [ObservableProperty]
    private bool _useCustomRunner;
    
    [ObservableProperty]
    private bool _useProton;
    
    [ObservableProperty]
    private string? _customWineExecutablePath;
    
    [ObservableProperty]
    private string? _customProtonPath;
    
    [ObservableProperty]
    private bool _useCustomWinePrefix;
    
    [ObservableProperty]
    private string? _customWinePrefixPath;
    
    [ObservableProperty]
    private string? _customLaunchArgs;
    
    [ObservableProperty]
    private InstalledRunner? _selectedWineRunner;
    
    [ObservableProperty]
    private InstalledRunner? _selectedProtonRunner;
    
    public ObservableCollection<GameRegion> AvailableRegions { get; } = new()
    {
        GameRegion.Global,
        GameRegion.China
    };
    
    public ObservableCollection<InstalledRunner> InstalledWineRunners { get; } = new();
    public ObservableCollection<InstalledRunner> InstalledProtonRunners { get; } = new();
    
    /// <summary>
    /// Event fired when settings are saved, includes the selected region
    /// </summary>
    public event EventHandler<GameSettingsSavedEventArgs>? SettingsSaved;
    public event EventHandler? SettingsClosed;
    
    /// <summary>
    /// The game type being configured
    /// </summary>
    public GameType GameType => _game.GameType;
    
    /// <summary>
    /// The original region of the game before any changes
    /// </summary>
    public GameRegion OriginalRegion => _game.Region;
    
    public GameSettingsViewModel(GameInfo game, SettingsService settingsService, WineRunnerService runnerService)
    {
        _game = game;
        _settingsService = settingsService;
        _runnerService = runnerService;
        
        GameName = game.DisplayName;
        
        LoadSettings();
        LoadInstalledRunners();
    }
    
    private void LoadSettings()
    {
        var settings = _settingsService.Settings;
        var gameSettings = settings.GameSpecificSettings.GetValueOrDefault(_game.Id);
        
        // Load region
        SelectedRegion = _game.Region;
        
        // Load runner settings
        if (gameSettings != null)
        {
            UseCustomRunner = gameSettings.UseCustomRunner;
            UseProton = gameSettings.UseProton ?? settings.UseProton;
            CustomWineExecutablePath = gameSettings.CustomWineExecutablePath;
            CustomProtonPath = gameSettings.CustomProtonPath;
            UseCustomWinePrefix = gameSettings.UseCustomWinePrefix;
            CustomWinePrefixPath = gameSettings.CustomWinePrefixPath;
            CustomLaunchArgs = gameSettings.CustomLaunchArgs;
        }
        else
        {
            // Use global settings as defaults
            UseProton = settings.UseProton;
            CustomWineExecutablePath = settings.WineExecutablePath;
            CustomProtonPath = settings.ProtonPath;
            CustomWinePrefixPath = settings.WinePrefixPath;
        }
    }
    
    private void LoadInstalledRunners()
    {
        InstalledWineRunners.Clear();
        InstalledProtonRunners.Clear();
        
        var installedRunners = _runnerService.GetInstalledRunners();
        
        foreach (var runner in installedRunners)
        {
            if (runner.Type == WineRunnerType.Wine)
            {
                InstalledWineRunners.Add(runner);
            }
            else
            {
                InstalledProtonRunners.Add(runner);
            }
        }
        
        // Set selected runners based on current paths - use StartsWith for accurate path matching
        if (!string.IsNullOrEmpty(CustomWineExecutablePath))
        {
            SelectedWineRunner = InstalledWineRunners.FirstOrDefault(r => 
                CustomWineExecutablePath.StartsWith(r.InstallPath, StringComparison.OrdinalIgnoreCase) ||
                r.ExecutablePath.Equals(CustomWineExecutablePath, StringComparison.OrdinalIgnoreCase));
        }
        
        if (!string.IsNullOrEmpty(CustomProtonPath))
        {
            SelectedProtonRunner = InstalledProtonRunners.FirstOrDefault(r => 
                CustomProtonPath.StartsWith(r.InstallPath, StringComparison.OrdinalIgnoreCase) ||
                r.InstallPath.Equals(CustomProtonPath, StringComparison.OrdinalIgnoreCase));
        }
    }
    
    partial void OnSelectedWineRunnerChanged(InstalledRunner? value)
    {
        if (value != null)
        {
            CustomWineExecutablePath = value.ExecutablePath;
        }
    }
    
    partial void OnSelectedProtonRunnerChanged(InstalledRunner? value)
    {
        if (value != null)
        {
            CustomProtonPath = value.InstallPath;
        }
    }
    
    [RelayCommand]
    private void SaveSettings()
    {
        _settingsService.UpdateSettings(settings =>
        {
            if (!settings.GameSpecificSettings.ContainsKey(_game.Id))
            {
                settings.GameSpecificSettings[_game.Id] = new GameSettings { GameId = _game.Id };
            }
            
            var gameSettings = settings.GameSpecificSettings[_game.Id];
            
            gameSettings.UseCustomRunner = UseCustomRunner;
            gameSettings.UseProton = UseProton;
            gameSettings.CustomWineExecutablePath = CustomWineExecutablePath;
            gameSettings.CustomProtonPath = CustomProtonPath;
            gameSettings.UseCustomWinePrefix = UseCustomWinePrefix;
            gameSettings.CustomWinePrefixPath = CustomWinePrefixPath;
            gameSettings.CustomLaunchArgs = CustomLaunchArgs;
            gameSettings.SelectedRegion = SelectedRegion.ToString();
            
            // Also update the region per game type
            settings.SelectedRegionPerGame[_game.GameType.ToString()] = SelectedRegion.ToString();
        });
        
        Log.Information("Game settings saved for {Game} with region {Region}", _game.DisplayName, SelectedRegion);
        SettingsSaved?.Invoke(this, new GameSettingsSavedEventArgs(GameType, SelectedRegion));
    }
    
    [RelayCommand]
    private void Close()
    {
        SettingsClosed?.Invoke(this, EventArgs.Empty);
    }
    
    [RelayCommand]
    private void ResetToGlobalSettings()
    {
        var settings = _settingsService.Settings;
        
        UseCustomRunner = false;
        UseProton = settings.UseProton;
        CustomWineExecutablePath = settings.WineExecutablePath;
        CustomProtonPath = settings.ProtonPath;
        UseCustomWinePrefix = false;
        CustomWinePrefixPath = settings.WinePrefixPath;
        CustomLaunchArgs = null;
        SelectedWineRunner = null;
        SelectedProtonRunner = null;
    }
}

/// <summary>
/// Event arguments for when game settings are saved
/// </summary>
public class GameSettingsSavedEventArgs : EventArgs
{
    public GameType GameType { get; }
    public GameRegion SelectedRegion { get; }
    
    public GameSettingsSavedEventArgs(GameType gameType, GameRegion selectedRegion)
    {
        GameType = gameType;
        SelectedRegion = selectedRegion;
    }
}
