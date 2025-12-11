using System.Text.Json;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for managing game configurations from multiple companies.
/// This allows the launcher to support games from different publishers.
/// </summary>
public class GameConfigurationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _configurationsPath;
    private Dictionary<string, GameConfiguration> _configurations;

    public GameConfigurationService()
    {
        _configurationsPath = Path.Combine(SettingsService.GetDataDirectory(), "game-configurations.json");
        _configurations = LoadConfigurations();

        if (_configurations.Count == 0)
        {
            InitializeDefaultConfigurations();
        }
    }

    private Dictionary<string, GameConfiguration> LoadConfigurations()
    {
        try
        {
            if (File.Exists(_configurationsPath))
            {
                var json = File.ReadAllText(_configurationsPath);
                var configs = JsonSerializer.Deserialize<Dictionary<string, GameConfiguration>>(json, JsonOptions);
                if (configs != null)
                {
                    Log.Information("Loaded {Count} game configurations", configs.Count);
                    return configs;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load game configurations");
        }

        return new Dictionary<string, GameConfiguration>();
    }

    private void SaveConfigurations()
    {
        try
        {
            var json = JsonSerializer.Serialize(_configurations, JsonOptions);
            File.WriteAllText(_configurationsPath, json);
            Log.Information("Saved {Count} game configurations", _configurations.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save game configurations");
        }
    }

    /// <summary>
    /// Initialize default game configurations for HoYoverse games
    /// </summary>
    private void InitializeDefaultConfigurations()
    {
        _configurations = GetDefaultHoYoverseConfigurations();
        SaveConfigurations();
        Log.Information("Initialized default game configurations");
    }

    /// <summary>
    /// Get default configurations for HoYoverse games
    /// </summary>
    private static Dictionary<string, GameConfiguration> GetDefaultHoYoverseConfigurations()
    {
        // Create default parser config for HoYoverse games
        var hoyoverseBackgroundParser = new BackgroundParserConfig
        {
            ParserType = BackgroundParserType.HoYoverse,
            DataRootPath = "data",
            GameListPath = "game_info_list",
            GameIdentifierField = "game.biz",
            BackgroundsArrayPath = "backgrounds",
            UrlFields = new Dictionary<string, string>
            {
                { "video", "video.url" },
                { "image", "background.url" },
                { "theme", "theme.url" },
                { "icon", "game.icon.url" }
            }
        };
        
        var hoyoverseDownloadParser = new DownloadParserConfig
        {
            ParserType = DownloadParserType.HoYoverse,
            DataRootPath = "data",
            GameDataPath = "game",
            GamePackagesPath = "game_packages",
            GameIdentifierField = "game.biz",
            FieldPaths = new Dictionary<string, string>
            {
                { "version", "main.major.version" },
                { "packages_version", "main.major.version" },
                { "packages_array", "main.major.game_pkgs" },
                { "audio_packs", "main.major.audio_pkgs" }
            }
        };
        
        return new Dictionary<string, GameConfiguration>
        {
            ["hi3-global"] = new()
            {
                Id = "hi3-global",
                Name = "honkai3rd",
                DisplayName = "Honkai Impact 3rd",
                GameType = GameType.HonkaiImpact3rd,
                Region = GameRegion.Global,
                Company = GameCompany.HoYoverse,
                ApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGamePackages?launcher_id=VYTpXlbWo8",
                SupportsSophonDownloads = false,
                ExecutableNames = new List<string> { "BH3.exe", "honkai3rd.exe" },
                GameBizIdentifier = "bh3_global",
                BackgroundApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=VYTpXlbWo8&language=en-us",
                IconApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGames?launcher_id=VYTpXlbWo8&language=en-us",
                BackgroundParser = hoyoverseBackgroundParser,
                DownloadParser = hoyoverseDownloadParser
            },
            ["gi-global"] = new()
            {
                Id = "gi-global",
                Name = "genshin",
                DisplayName = "Genshin Impact",
                GameType = GameType.GenshinImpact,
                Region = GameRegion.Global,
                Company = GameCompany.HoYoverse,
                ApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGamePackages?launcher_id=VYTpXlbWo8",
                BranchUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameBranches?game_ids[]=1Z8W5NHUQb&launcher_id=VYTpXlbWo8",
                SophonChunkApiUrl = "https://sg-public-api.hoyoverse.com/downloader/sophon_chunk/api/getBuild",
                SupportsSophonDownloads = false,
                ExecutableNames = new List<string> { "GenshinImpact.exe", "YuanShen.exe" },
                GameBizIdentifier = "hk4e_global",
                BackgroundApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=VYTpXlbWo8&language=en-us",
                IconApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGames?launcher_id=VYTpXlbWo8&language=en-us",
                BackgroundParser = hoyoverseBackgroundParser,
                DownloadParser = hoyoverseDownloadParser,
            },
            ["gi-cn"] = new()
            {
                Id = "gi-cn",
                Name = "yuanshen",
                DisplayName = "Genshin Impact",
                GameType = GameType.GenshinImpact,
                Region = GameRegion.China,
                Company = GameCompany.HoYoverse,
                ApiUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGamePackages?launcher_id=jGHBHlcOq1",
                BranchUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches?game_ids[]=T2S0Gz4Dr2&launcher_id=jGHBHlcOq1",
                SophonChunkApiUrl = "https://api-takumi.mihoyo.com/downloader/sophon_chunk/api/getBuild",
                SupportsSophonDownloads = false,
                ExecutableNames = new List<string> { "YuanShen.exe", "GenshinImpact.exe" },
                GameBizIdentifier = "hk4e_cn",
                BackgroundApiUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=jGHBHlcOq1&language=zh-cn",
                IconApiUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGames?launcher_id=jGHBHlcOq1&language=zh-cn",
                BackgroundParser = hoyoverseBackgroundParser,
                DownloadParser = hoyoverseDownloadParser
            },
            ["hsr-global"] = new()
            {
                Id = "hsr-global",
                Name = "starrail",
                DisplayName = "Honkai: Star Rail",
                GameType = GameType.HonkaiStarRail,
                Region = GameRegion.Global,
                Company = GameCompany.HoYoverse,
                ApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGamePackages?launcher_id=VYTpXlbWo8",
                SupportsSophonDownloads = false,
                ExecutableNames = new List<string> { "StarRail.exe" },
                GameBizIdentifier = "hkrpg_global",
                BackgroundApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=VYTpXlbWo8&language=en-us",
                IconApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGames?launcher_id=VYTpXlbWo8&language=en-us",
                BackgroundParser = hoyoverseBackgroundParser,
                DownloadParser = hoyoverseDownloadParser
            },
            ["hsr-cn"] = new()
            {
                Id = "hsr-cn",
                Name = "starrail",
                DisplayName = "Honkai: Star Rail",
                GameType = GameType.HonkaiStarRail,
                Region = GameRegion.China,
                Company = GameCompany.HoYoverse,
                ApiUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGamePackages?launcher_id=jGHBHlcOq1",
                SupportsSophonDownloads = false,
                ExecutableNames = new List<string> { "StarRail.exe" },
                GameBizIdentifier = "hkrpg_cn",
                BackgroundApiUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=jGHBHlcOq1&language=zh-cn",
                IconApiUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGames?launcher_id=jGHBHlcOq1&language=zh-cn",
                BackgroundParser = hoyoverseBackgroundParser,
                DownloadParser = hoyoverseDownloadParser
            },
            ["zzz-global"] = new()
            {
                Id = "zzz-global",
                Name = "zenless",
                DisplayName = "Zenless Zone Zero",
                GameType = GameType.ZenlessZoneZero,
                Region = GameRegion.Global,
                Company = GameCompany.HoYoverse,
                ApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGamePackages?launcher_id=VYTpXlbWo8",
                BranchUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameBranches?game_ids[]=U5hbdsT9W7&launcher_id=VYTpXlbWo8",
                SophonChunkApiUrl = "https://sg-public-api.hoyoverse.com/downloader/sophon_chunk/api/getBuild",
                SupportsSophonDownloads = true,
                ExecutableNames = new List<string> { "ZenlessZoneZero.exe" },
                GameBizIdentifier = "nap_global",
                BackgroundApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=VYTpXlbWo8&language=en-us",
                IconApiUrl = "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGames?launcher_id=VYTpXlbWo8&language=en-us",
                BackgroundParser = hoyoverseBackgroundParser,
                DownloadParser = hoyoverseDownloadParser
            },
            ["zzz-cn"] = new()
            {
                Id = "zzz-cn",
                Name = "zenless",
                DisplayName = "Zenless Zone Zero",
                GameType = GameType.ZenlessZoneZero,
                Region = GameRegion.China,
                Company = GameCompany.HoYoverse,
                ApiUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGamePackages?launcher_id=jGHBHlcOq1",
                BranchUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches?game_ids[]=x6znKlJ0xK&launcher_id=jGHBHlcOq1",
                SophonChunkApiUrl = "https://api-takumi.mihoyo.com/downloader/sophon_chunk/api/getBuild",
                SupportsSophonDownloads = true,
                ExecutableNames = new List<string> { "ZenlessZoneZero.exe" },
                GameBizIdentifier = "nap_cn",
                BackgroundApiUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getAllGameBasicInfo?launcher_id=jGHBHlcOq1&language=zh-cn",
                IconApiUrl = "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGames?launcher_id=jGHBHlcOq1&language=zh-cn",
                BackgroundParser = hoyoverseBackgroundParser,
                DownloadParser = hoyoverseDownloadParser
            },
            ["ww-global"] = new()
            {
                Id = "ww-global",
                Name = "wutheringwaves",
                DisplayName = "Wuthering Waves",
                GameType = GameType.WutheringWaves,
                Region = GameRegion.Global,
                Company = GameCompany.Kuro,
                ApiUrl = "https://prod-alicdn-gamestarter.kurogame.com/launcher/game/G153/50004_obOHXFrFanqsaIEOmuKroCcbZkQRBC7c/index.json",
                SupportsSophonDownloads = false,
                ExecutableNames = new List<string> { "Wuthering Waves.exe", "Client-Win64-Shipping.exe" },
                GameBizIdentifier = "G153",
                BackgroundApiUrl = "https://prod-alicdn-gamestarter.kurogame.com/launcher/launcher/50004_obOHXFrFanqsaIEOmuKroCcbZkQRBC7c/G153/index.json",
                ApiEndpoints = new Dictionary<string, string>
                {
                    { "background_base_url", "https://prod-alicdn-gamestarter.kurogame.com/launcher/50004_obOHXFrFanqsaIEOmuKroCcbZkQRBC7c/G153/background/" },
                    { "download_base_url", "https://hw-pcdownload-aws.aki-game.net/" }
                },
                BackgroundParser = new BackgroundParserConfig
                {
                    ParserType = BackgroundParserType.Kuro,
                    DataRootPath = "functionCode",
                    GameListPath = "", // Not used - single game response
                    GameIdentifierField = "", // Not used
                    BackgroundsArrayPath = "background", // Path to background string
                    UrlFields = new Dictionary<string, string>
                    {
                        { "slogan", "slogan" }, // From background metadata JSON
                        { "backgroundFile", "backgroundFile" } // From background metadata JSON
                    }
                },
                DownloadParser = new DownloadParserConfig
                {
                    ParserType = DownloadParserType.Kuro,
                    DataRootPath = "config",
                    GameDataPath = "", // Not used for Kuro
                    GamePackagesPath = "", // Not used for Kuro
                    GameIdentifierField = "", // Not used for Kuro
                    FieldPaths = new Dictionary<string, string>
                    {
                        { "version", "version" },
                        { "indexFile", "indexFile" },
                        { "baseUrl", "baseUrl" }
                    }
                }
            }
        };
    }

    /// <summary>
    /// Get configuration for a specific game
    /// </summary>
    public GameConfiguration? GetConfiguration(string gameId)
    {
        return _configurations.TryGetValue(gameId, out var config) ? config : null;
    }

    /// <summary>
    /// Get all configurations for a specific company
    /// </summary>
    public IEnumerable<GameConfiguration> GetConfigurationsByCompany(GameCompany company)
    {
        return _configurations.Values.Where(c => c.Company == company);
    }

    /// <summary>
    /// Get all game configurations
    /// </summary>
    public IEnumerable<GameConfiguration> GetAllConfigurations()
    {
        return _configurations.Values;
    }

    /// <summary>
    /// Add or update a game configuration
    /// </summary>
    public void SetConfiguration(GameConfiguration configuration)
    {
        _configurations[configuration.Id] = configuration;
        SaveConfigurations();
        Log.Information("Saved configuration for {GameId}", configuration.Id);
    }

    /// <summary>
    /// Remove a game configuration
    /// </summary>
    public void RemoveConfiguration(string gameId)
    {
        if (_configurations.Remove(gameId))
        {
            SaveConfigurations();
            Log.Information("Removed configuration for {GameId}", gameId);
        }
    }

    /// <summary>
    /// Check if a game supports Sophon downloads based on its configuration
    /// </summary>
    public bool SupportsSophonDownloads(string gameId)
    {
        var config = GetConfiguration(gameId);
        return config?.SupportsSophonDownloads ?? false;
    }

    /// <summary>
    /// Get the API URL for a game
    /// </summary>
    public string? GetApiUrl(string gameId)
    {
        var config = GetConfiguration(gameId);
        return config?.ApiUrl;
    }

    /// <summary>
    /// Get the branch URL for a game (used for Sophon downloads)
    /// </summary>
    public string? GetBranchUrl(string gameId)
    {
        var config = GetConfiguration(gameId);
        return config?.BranchUrl;
    }

    /// <summary>
    /// Get the Sophon chunk API URL for a game
    /// </summary>
    public string? GetSophonChunkApiUrl(string gameId)
    {
        var config = GetConfiguration(gameId);
        return config?.SophonChunkApiUrl;
    }
}
