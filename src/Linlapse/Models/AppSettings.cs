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
    
    /// <summary>
    /// List of installed custom wine/proton runners
    /// </summary>
    public List<InstalledRunner> InstalledRunners { get; set; } = new();
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
    
    /// <summary>
    /// Whether to use a custom wine/proton runner for this game instead of global settings
    /// </summary>
    public bool UseCustomRunner { get; set; } = false;
    
    /// <summary>
    /// Custom wine executable path for this game (overrides global)
    /// </summary>
    public string? CustomWineExecutablePath { get; set; }
    
    /// <summary>
    /// Custom proton path for this game (overrides global)
    /// </summary>
    public string? CustomProtonPath { get; set; }
    
    /// <summary>
    /// Whether to use Proton instead of Wine for this game (overrides global when UseCustomRunner is true)
    /// </summary>
    public bool? UseProton { get; set; }
    
    /// <summary>
    /// Selected region for this game (overrides global when set)
    /// </summary>
    public string? SelectedRegion { get; set; }
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

/// <summary>
/// Represents a downloadable Wine/Proton runner
/// </summary>
public class WineRunner
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public WineRunnerType Type { get; set; }
    public string DownloadUrl { get; set; } = string.Empty;
    /// <summary>
    /// MD5 checksum of the download file for verification (lowercase hex string)
    /// </summary>
    public string? Md5Checksum { get; set; }
    public bool IsInstalled { get; set; }
    public string? InstallPath { get; set; }
}

/// <summary>
/// Type of wine runner
/// </summary>
public enum WineRunnerType
{
    Wine,
    Proton
}

/// <summary>
/// List of installed custom runners stored in settings
/// </summary>
public class InstalledRunner
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public WineRunnerType Type { get; set; }
    public string InstallPath { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
}
