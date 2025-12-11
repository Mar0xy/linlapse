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
    public string? GameBizIdentifier { get; set; }
    public string? BackgroundApiUrl { get; set; }
    public string? IconApiUrl { get; set; }
    public BackgroundParserConfig? BackgroundParser { get; set; }
    public DownloadParserConfig? DownloadParser { get; set; }
}

/// <summary>
/// Configuration for parsing background/icon data from API responses
/// Supports different API structures from different game publishers
/// </summary>
public class BackgroundParserConfig
{
    /// <summary>
    /// Parser type to use
    /// </summary>
    public BackgroundParserType ParserType { get; set; } = BackgroundParserType.HoYoverse;
    
    /// <summary>
    /// JSON path to the data root (e.g., "data" for HoYoverse)
    /// </summary>
    public string DataRootPath { get; set; } = "data";
    
    /// <summary>
    /// JSON path to the game list array (e.g., "game_info_list" for HoYoverse getAllGameBasicInfo)
    /// </summary>
    public string GameListPath { get; set; } = "game_info_list";
    
    /// <summary>
    /// Field name to match game identifier (e.g., "biz" for HoYoverse)
    /// </summary>
    public string GameIdentifierField { get; set; } = "biz";
    
    /// <summary>
    /// Path to backgrounds array within game object
    /// </summary>
    public string BackgroundsArrayPath { get; set; } = "backgrounds";
    
    /// <summary>
    /// Field paths for extracting URLs (using JSON path notation)
    /// </summary>
    public Dictionary<string, string> UrlFields { get; set; } = new()
    {
        { "video", "video.url" },
        { "image", "background.url" },
        { "theme", "theme.url" },
        { "icon", "game.icon.url" }
    };
}

public enum BackgroundParserType
{
    /// <summary>
    /// HoYoverse/HoYoPlay API format
    /// </summary>
    HoYoverse,
    
    /// <summary>
    /// Kuro Games API format (requires two-step fetching)
    /// </summary>
    Kuro,
    
    /// <summary>
    /// Generic/custom parser - uses field paths directly
    /// </summary>
    Generic,
    
    /// <summary>
    /// No background support
    /// </summary>
    None
}

/// <summary>
/// Configuration for parsing download/package data from API responses
/// </summary>
public class DownloadParserConfig
{
    /// <summary>
    /// Parser type to use
    /// </summary>
    public DownloadParserType ParserType { get; set; } = DownloadParserType.HoYoverse;
    
    /// <summary>
    /// JSON path to the data root (e.g., "data" for HoYoverse)
    /// </summary>
    public string DataRootPath { get; set; } = "data";
    
    /// <summary>
    /// Primary game data path (e.g., "game" for older HoYoverse API)
    /// </summary>
    public string? GameDataPath { get; set; } = "game";
    
    /// <summary>
    /// Alternative game packages array path (e.g., "game_packages" for newer HoYoPlay API)
    /// </summary>
    public string? GamePackagesPath { get; set; } = "game_packages";
    
    /// <summary>
    /// Field name to match game identifier within packages
    /// </summary>
    public string GameIdentifierField { get; set; } = "game.biz";
    
    /// <summary>
    /// Field paths for extracting download information
    /// </summary>
    public Dictionary<string, string> FieldPaths { get; set; } = new()
    {
        { "version", "latest.version" },
        { "url", "latest.path" },
        { "size", "latest.size" },
        { "md5", "latest.md5" },
        { "voice_packs", "latest.voice_packs" },
        // HoYoPlay format
        { "packages_version", "main.major.version" },
        { "packages_array", "main.major.game_pkgs" },
        { "audio_packs", "main.major.audio_pkgs" }
    };
}

public enum DownloadParserType
{
    /// <summary>
    /// HoYoverse API format (supports both old and new HoYoPlay formats)
    /// </summary>
    HoYoverse,
    
    /// <summary>
    /// Kuro Games API format (requires index file resolution)
    /// </summary>
    Kuro,
    
    /// <summary>
    /// Generic/custom parser
    /// </summary>
    Generic,
    
    /// <summary>
    /// No download API support
    /// </summary>
    None
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
    /// Kuro Games - Publisher of Wuthering Waves, Punishing: Gray Raven
    /// </summary>
    Kuro,
    
    /// <summary>
    /// Custom or third-party games
    /// </summary>
    Custom,
    
    /// <summary>
    /// Unknown or unspecified company
    /// </summary>
    Unknown
}
