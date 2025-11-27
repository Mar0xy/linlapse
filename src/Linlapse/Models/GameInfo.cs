using System.Text.Json.Serialization;

namespace Linlapse.Models;

/// <summary>
/// Represents a supported game in the launcher
/// </summary>
public class GameInfo
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public GameType GameType { get; set; }
    public GameRegion Region { get; set; }
    public string InstallPath { get; set; } = string.Empty;
    public string ExecutablePath { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string? BackgroundImagePath { get; set; }
    public string? LogoImagePath { get; set; }
    public bool IsInstalled { get; set; }
    public long InstallSize { get; set; }
    public DateTime? LastPlayed { get; set; }
    public TimeSpan TotalPlayTime { get; set; }
    public GameState State { get; set; } = GameState.NotInstalled;
}

/// <summary>
/// Types of supported games
/// </summary>
public enum GameType
{
    HonkaiImpact3rd,
    GenshinImpact,
    HonkaiStarRail,
    ZenlessZoneZero,
    Custom
}

/// <summary>
/// Game server regions
/// </summary>
public enum GameRegion
{
    Global,
    China,
    SEA,
    Europe,
    America,
    Asia,
    TW_HK_MO
}

/// <summary>
/// Current state of the game
/// </summary>
public enum GameState
{
    NotInstalled,
    NeedsUpdate,
    Ready,
    Installing,
    Updating,
    Repairing,
    Running,
    Preloading
}
