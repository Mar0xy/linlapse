using System.Text.Json.Serialization;

namespace Linlapse.Models;

/// <summary>
/// Application settings persisted to disk
/// </summary>
public class AppSettings
{
    public string? DefaultGameInstallPath { get; set; }
    public string? WinePrefixPath { get; set; }
    public string? WineExecutablePath { get; set; }
    public string? ProtonPath { get; set; }
    public bool UseSystemWine { get; set; } = true;
    public bool UseProton { get; set; } = false;
    public string PreferredVoiceLanguage { get; set; } = "en-us";
    public List<string> SelectedVoiceLanguages { get; set; } = new() { "en-us" };
    public bool EnableDiscordRpc { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool StartMinimized { get; set; } = false;
    public bool CheckUpdatesOnStartup { get; set; } = true;
    public bool EnableLogging { get; set; } = true;
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    public string Language { get; set; } = "en-US";
    public int DownloadSpeedLimit { get; set; } = 0; // 0 = unlimited
    public int MaxConcurrentDownloads { get; set; } = 4;
    public List<string> GameInstallPaths { get; set; } = new();
    public Dictionary<string, GameSettings> GameSpecificSettings { get; set; } = new();
    
    /// <summary>
    /// Selected region per game type (key = GameType enum name, value = GameRegion enum name)
    /// </summary>
    public Dictionary<string, string> SelectedRegionPerGame { get; set; } = new();
    
    /// <summary>
    /// Path to Jadeite executable for launching HSR and HI3 with anti-cheat bypass
    /// </summary>
    public string? JadeiteExecutablePath { get; set; }
}

/// <summary>
/// Theme mode options
/// </summary>
public enum ThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>
/// Per-game specific settings
/// </summary>
public class GameSettings
{
    public string GameId { get; set; } = string.Empty;
    public string? CustomLaunchArgs { get; set; }
    public bool UseCustomWinePrefix { get; set; } = false;
    public string? CustomWinePrefixPath { get; set; }
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new();
    public GraphicsSettings? Graphics { get; set; }
    public AudioSettings? Audio { get; set; }
}

/// <summary>
/// Graphics settings for games
/// </summary>
public class GraphicsSettings
{
    public int ResolutionWidth { get; set; } = 1920;
    public int ResolutionHeight { get; set; } = 1080;
    public bool Fullscreen { get; set; } = true;
    public bool Borderless { get; set; } = false;
    public bool VSync { get; set; } = true;
    public int FpsLimit { get; set; } = 60;
    public GraphicsQuality Quality { get; set; } = GraphicsQuality.Medium;
}

/// <summary>
/// Audio settings for games
/// </summary>
public class AudioSettings
{
    public int MasterVolume { get; set; } = 100;
    public int MusicVolume { get; set; } = 100;
    public int SfxVolume { get; set; } = 100;
    public int VoiceVolume { get; set; } = 100;
    public string VoiceLanguage { get; set; } = "en";
}

/// <summary>
/// Graphics quality preset
/// </summary>
public enum GraphicsQuality
{
    Lowest,
    Low,
    Medium,
    High,
    Highest,
    Custom
}
