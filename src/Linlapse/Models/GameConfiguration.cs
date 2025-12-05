namespace Linlapse.Models;

/// <summary>
/// Configuration for a specific game from any company
/// </summary>
public class GameConfiguration
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public GameType GameType { get; set; }
    public GameRegion Region { get; set; }
    public GameCompany Company { get; set; }
    public string? ApiUrl { get; set; }
    public string? BranchUrl { get; set; }
    public string? SophonChunkApiUrl { get; set; }
    public bool SupportsSophonDownloads { get; set; }
    public List<string> ExecutableNames { get; set; } = new();
    public Dictionary<string, string> ApiEndpoints { get; set; } = new();
}

/// <summary>
/// Represents a game publishing company
/// </summary>
public enum GameCompany
{
    /// <summary>
    /// miHoYo/HoYoverse (COGNOSPHERE PTE. LTD.) - Publisher of Genshin Impact, Honkai series, ZZZ
    /// </summary>
    HoYoverse,
    
    /// <summary>
    /// Custom or third-party games
    /// </summary>
    Custom,
    
    /// <summary>
    /// Unknown or unspecified company
    /// </summary>
    Unknown
}
