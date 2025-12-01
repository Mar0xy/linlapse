using System.Net.Http;
using System.Text.Json;
using Hi3Helper.Sophon;
using Hi3Helper.Sophon.Structs;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for downloading games using Hi3Helper.Sophon for optimized chunk-based downloads.
/// Sophon allows files to be downloaded into several chunks, which results in more efficient,
/// faster and less error-prone download process compared to conventional archive file download method.
/// </summary>
public class SophonDownloadService : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;
    private readonly GameService _gameService;
    
    // Game branch API URLs - used to get branch, password, and package_id
    private static readonly Dictionary<string, string> GameBranchUrls = new()
    {
        // Genshin Impact - Global
        { "genshin-global", "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameBranches?game_ids[]=1Z8W5NHUQb&launcher_id=VYTpXlbWo8" },
        // Genshin Impact - China
        { "genshin-cn", "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches?game_ids[]=T2S0Gz4Dr2&launcher_id=jGHBHlcOq1" },
        // Zenless Zone Zero - Global
        { "zzz-global", "https://sg-hyp-api.hoyoverse.com/hyp/hyp-connect/api/getGameBranches?game_ids[]=U5hbdsT9W7&launcher_id=VYTpXlbWo8" },
        // Zenless Zone Zero - China
        { "zzz-cn", "https://hyp-api.mihoyo.com/hyp/hyp-connect/api/getGameBranches?game_ids[]=x6znKlJ0xK&launcher_id=jGHBHlcOq1" }
    };
    
    // Sophon chunk API base URLs
    private const string SophonChunkApiGlobal = "https://sg-public-api.hoyoverse.com/downloader/sophon_chunk/api/getBuild";
    private const string SophonChunkApiChina = "https://api-takumi.mihoyo.com/downloader/sophon_chunk/api/getBuild";
    
    // Voice pack language matching fields used by Sophon
    private static readonly Dictionary<VoiceLanguage, string> VoiceLanguageMatchingFields = new()
    {
        { VoiceLanguage.English, "en-us" },
        { VoiceLanguage.Japanese, "ja-jp" },
        { VoiceLanguage.Korean, "ko-kr" },
        { VoiceLanguage.ChineseSimplified, "zh-cn" },
        { VoiceLanguage.ChineseTraditional, "zh-tw" }
    };
    
    public event EventHandler<SophonDownloadProgress>? DownloadProgressChanged;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<(string GameId, Exception Error)>? DownloadFailed;
    
    // Configuration constants
    private const int DefaultMaxConnectionsPerServer = 32;
    private const int DefaultMaxParallelChunks = 8;
    private const string GameMatchingField = "game";
    
    /// <summary>
    /// Get the matching field for a voice language
    /// </summary>
    public static string? GetVoiceLanguageMatchingField(VoiceLanguage language)
    {
        return VoiceLanguageMatchingFields.TryGetValue(language, out var field) ? field : null;
    }
    
    /// <summary>
    /// Get all supported voice languages
    /// </summary>
    public static IEnumerable<VoiceLanguage> GetSupportedVoiceLanguages()
    {
        return VoiceLanguageMatchingFields.Keys;
    }
    
    public SophonDownloadService(SettingsService settingsService, GameService gameService)
    {
        _settingsService = settingsService;
        _gameService = gameService;
        _httpClient = new HttpClient(new HttpClientHandler
        {
            MaxConnectionsPerServer = DefaultMaxConnectionsPerServer
        });
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");
    }
    
    /// <summary>
    /// Check if a game supports Sophon downloads.
    /// Sophon is supported for Genshin Impact and Zenless Zone Zero.
    /// </summary>
    public static bool SupportsSophon(GameType gameType)
    {
        // According to Hi3Helper.Sophon README, Sophon is currently supported for:
        // - Genshin Impact
        // - Zenless Zone Zero
        return gameType == GameType.GenshinImpact || gameType == GameType.ZenlessZoneZero;
    }
    
    /// <summary>
    /// Check if a game supports Sophon downloads
    /// </summary>
    public static bool SupportsSophon(GameInfo game)
    {
        return SupportsSophon(game.GameType);
    }
    
    /// <summary>
    /// Get the game branch URL for fetching branch info
    /// </summary>
    private static string? GetGameBranchUrl(GameInfo game)
    {
        var key = game.GameType switch
        {
            GameType.GenshinImpact => game.Region == GameRegion.China ? "genshin-cn" : "genshin-global",
            GameType.ZenlessZoneZero => game.Region == GameRegion.China ? "zzz-cn" : "zzz-global",
            _ => null
        };
        
        return key != null && GameBranchUrls.TryGetValue(key, out var url) ? url : null;
    }
    
    /// <summary>
    /// Get the Sophon chunk API base URL based on region.
    /// China region uses api-takumi.mihoyo.com, all other regions (Global, SEA, Europe, America, Asia, TW_HK_MO)
    /// use sg-public-api.hoyoverse.com as they all connect to HoYoverse's global infrastructure.
    /// </summary>
    private static string GetSophonChunkApiUrl(GameRegion region)
    {
        return region == GameRegion.China ? SophonChunkApiChina : SophonChunkApiGlobal;
    }
    
    /// <summary>
    /// Fetch branch info (branch, password, package_id) from the game branches API
    /// </summary>
    private async Task<SophonBranchInfo?> GetBranchInfoAsync(GameInfo game, CancellationToken cancellationToken)
    {
        var branchUrl = GetGameBranchUrl(game);
        if (string.IsNullOrEmpty(branchUrl))
        {
            Log.Warning("No branch URL found for game: {GameId}", game.Id);
            return null;
        }
        
        try
        {
            Log.Information("Fetching branch info for {GameId} from {Url}", game.Id, branchUrl);
            
            var response = await _httpClient.GetStringAsync(branchUrl, cancellationToken);
            using var doc = JsonDocument.Parse(response);
            
            var root = doc.RootElement;
            
            // Check for successful response
            if (root.TryGetProperty("retcode", out var retcode) && retcode.GetInt32() != 0)
            {
                Log.Warning("Branch API returned error for {GameId}: {Retcode}", game.Id, retcode.GetInt32());
                return null;
            }
            
            // Navigate to data.game_branches[0].main
            if (!root.TryGetProperty("data", out var data))
            {
                Log.Warning("No data property in branch response for {GameId}", game.Id);
                return null;
            }
            
            if (!data.TryGetProperty("game_branches", out var branches) || branches.GetArrayLength() == 0)
            {
                Log.Warning("No game_branches in branch response for {GameId}", game.Id);
                return null;
            }
            
            var firstBranch = branches[0];
            if (!firstBranch.TryGetProperty("main", out var main))
            {
                Log.Warning("No main property in branch for {GameId}", game.Id);
                return null;
            }
            
            // The properties can be directly under 'main' or under 'main.major'
            // Try to get the source element - prefer 'main' directly, fallback to 'main.major'
            JsonElement sourceElement = main;
            bool hasMajor = main.TryGetProperty("major", out var major);
            
            // Extract branch, password, and package_id
            string? branch = null;
            string? password = null;
            string? packageId = null;
            
            // First try to get directly from 'main'
            if (main.TryGetProperty("branch", out var mainBranch))
                branch = mainBranch.GetString();
            if (main.TryGetProperty("password", out var mainPwd))
                password = mainPwd.GetString();
            if (main.TryGetProperty("package_id", out var mainPkgId))
                packageId = mainPkgId.GetString();
            
            // If not found, try from 'main.major' (older API structure)
            if (hasMajor)
            {
                if (string.IsNullOrEmpty(branch) && major.TryGetProperty("branch", out var majorBranch))
                    branch = majorBranch.GetString();
                if (string.IsNullOrEmpty(password) && major.TryGetProperty("password", out var majorPwd))
                    password = majorPwd.GetString();
                if (string.IsNullOrEmpty(packageId) && major.TryGetProperty("package_id", out var majorPkgId))
                    packageId = majorPkgId.GetString();
                
                // Try extracting branch from URLs in major
                if (string.IsNullOrEmpty(branch))
                {
                    if (major.TryGetProperty("res_list_url", out var resListUrl))
                        branch = ExtractBranchFromUrl(resListUrl.GetString());
                    
                    if (string.IsNullOrEmpty(branch) && major.TryGetProperty("game_pkgs", out var gamePkgs) && gamePkgs.GetArrayLength() > 0)
                    {
                        var firstPkg = gamePkgs[0];
                        if (firstPkg.TryGetProperty("url", out var pkgUrl))
                            branch = ExtractBranchFromUrl(pkgUrl.GetString());
                    }
                }
            }
            
            // Also check firstBranch.branch structure as another fallback
            if (firstBranch.TryGetProperty("branch", out var branchProp))
            {
                if (string.IsNullOrEmpty(password) && branchProp.TryGetProperty("password", out var pwd))
                    password = pwd.GetString();
                if (string.IsNullOrEmpty(packageId) && branchProp.TryGetProperty("package_id", out var pkgId))
                    packageId = pkgId.GetString();
                if (string.IsNullOrEmpty(branch) && branchProp.TryGetProperty("branch", out var br))
                    branch = br.GetString();
            }
            
            if (string.IsNullOrEmpty(branch) || string.IsNullOrEmpty(password) || string.IsNullOrEmpty(packageId))
            {
                Log.Warning("Missing required branch info for {GameId}: branch={Branch}, password={HasPassword}, package_id={HasPackageId}", 
                    game.Id, branch, !string.IsNullOrEmpty(password), !string.IsNullOrEmpty(packageId));
                return null;
            }
            
            Log.Information("Got branch info for {GameId}: branch={Branch}, package_id={PackageId}", 
                game.Id, branch, packageId);
            
            return new SophonBranchInfo
            {
                Branch = branch,
                Password = password,
                PackageId = packageId
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to fetch branch info for {GameId}", game.Id);
            return null;
        }
    }
    
    /// <summary>
    /// Extract branch name from a URL (helper method)
    /// </summary>
    private static string? ExtractBranchFromUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        
        // Try to extract branch from URL patterns like .../branch_name/...
        var segments = url.Split('/');
        // Look for segment that looks like a branch name (typically after 'output' or contains version info)
        foreach (var segment in segments)
        {
            if (segment.Contains("_") && !segment.Contains(".") && segment.Length > 5)
            {
                return segment;
            }
        }
        return null;
    }
    
    /// <summary>
    /// Build the Sophon getBuild URL from branch info
    /// </summary>
    private static string BuildSophonGetBuildUrl(GameInfo game, SophonBranchInfo branchInfo)
    {
        var baseUrl = GetSophonChunkApiUrl(game.Region);
        return $"{baseUrl}?branch={branchInfo.Branch}&password={branchInfo.Password}&package_id={branchInfo.PackageId}";
    }
    
    /// <summary>
    /// Get the install path for voice packs (same directory as the game)
    /// </summary>
    private string GetVoicePackInstallPath(GameInfo game)
    {
        // Voice packs should be installed in the same directory as the game
        if (!string.IsNullOrEmpty(game.InstallPath))
        {
            return game.InstallPath;
        }
        
        // Fallback to default path if game is not yet installed
        var defaultBasePath = _settingsService.Settings.DefaultGameInstallPath 
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games");
        
        return Path.Combine(defaultBasePath, GameService.GetInstallFolderName(game));
    }
    
    /// <summary>
    /// Get Sophon download information for a game (base game files)
    /// </summary>
    public async Task<SophonDownloadInfo?> GetSophonDownloadInfoAsync(string gameId, CancellationToken cancellationToken = default)
    {
        return await GetSophonDownloadInfoAsync(gameId, GameMatchingField, cancellationToken);
    }
    
    /// <summary>
    /// Get Sophon download information for a voice pack
    /// </summary>
    public async Task<SophonDownloadInfo?> GetSophonVoicePackDownloadInfoAsync(string gameId, VoiceLanguage language, CancellationToken cancellationToken = default)
    {
        var matchingField = GetVoiceLanguageMatchingField(language);
        if (matchingField == null)
        {
            Log.Warning("Unsupported voice language: {Language}", language);
            return null;
        }
        
        var info = await GetSophonDownloadInfoAsync(gameId, matchingField, cancellationToken);
        if (info != null)
        {
            info.VoiceLanguage = language;
            info.IsVoicePack = true;
        }
        return info;
    }
    
    /// <summary>
    /// Get all available voice pack languages for a game
    /// </summary>
    public async Task<List<VoiceLanguage>> GetAvailableVoicePacksAsync(string gameId, CancellationToken cancellationToken = default)
    {
        var availableLanguages = new List<VoiceLanguage>();
        
        var game = _gameService.GetGame(gameId);
        if (game == null || !SupportsSophon(game))
        {
            return availableLanguages;
        }
        
        try
        {
            var branchInfo = await GetBranchInfoAsync(game, cancellationToken);
            if (branchInfo == null)
            {
                return availableLanguages;
            }
            
            var sophonUrl = BuildSophonGetBuildUrl(game, branchInfo);
            
            // Try each voice language to see which are available
            foreach (var language in GetSupportedVoiceLanguages())
            {
                var matchingField = GetVoiceLanguageMatchingField(language);
                if (matchingField == null) continue;
                
                try
                {
                    var manifestPair = await SophonManifest.CreateSophonChunkManifestInfoPair(
                        _httpClient,
                        sophonUrl,
                        matchingField,
                        cancellationToken);
                    
                    if (manifestPair.IsFound)
                    {
                        availableLanguages.Add(language);
                        Log.Debug("Voice pack available for {GameId}: {Language}", gameId, language);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "Voice pack not available for {GameId}: {Language}", gameId, language);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get available voice packs for {GameId}", gameId);
        }
        
        return availableLanguages;
    }
    
    /// <summary>
    /// Get Sophon download information with specific matching field
    /// </summary>
    private async Task<SophonDownloadInfo?> GetSophonDownloadInfoAsync(string gameId, string matchingField, CancellationToken cancellationToken)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Warning("Game not found: {GameId}", gameId);
            return null;
        }
        
        if (!SupportsSophon(game))
        {
            Log.Debug("Game {GameId} does not support Sophon downloads", gameId);
            return null;
        }
        
        try
        {
            // Step 1: Get branch info from getGameBranches API
            var branchInfo = await GetBranchInfoAsync(game, cancellationToken);
            if (branchInfo == null)
            {
                Log.Warning("Failed to get branch info for {GameId}", gameId);
                return null;
            }
            
            // Step 2: Build the Sophon getBuild URL and fetch manifest
            var sophonUrl = BuildSophonGetBuildUrl(game, branchInfo);
            Log.Information("Fetching Sophon manifest for {GameId} (field: {Field}) from {Url}", gameId, matchingField, sophonUrl);
            
            var manifestPair = await SophonManifest.CreateSophonChunkManifestInfoPair(
                _httpClient,
                sophonUrl,
                matchingField,
                cancellationToken);
            
            if (!manifestPair.IsFound)
            {
                Log.Warning("Sophon manifest not found for {GameId} (field: {Field}): {Message}", gameId, matchingField, manifestPair.ReturnMessage);
                return null;
            }
            
            var chunksInfo = manifestPair.ChunksInfo;
            
            return new SophonDownloadInfo
            {
                GameId = gameId,
                TotalSize = chunksInfo.TotalSize,
                TotalCompressedSize = chunksInfo.TotalCompressedSize,
                FileCount = chunksInfo.FilesCount,
                ChunkCount = chunksInfo.ChunksCount,
                ManifestPair = manifestPair,
                IsAvailable = true,
                MatchingField = matchingField
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get Sophon download info for {GameId} (field: {Field})", gameId, matchingField);
            return null;
        }
    }
    
    /// <summary>
    /// Download and install a game using Sophon
    /// </summary>
    public async Task<bool> DownloadAndInstallGameAsync(
        string gameId,
        string? installPath = null,
        IProgress<SophonDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }
        
        if (!SupportsSophon(game))
        {
            Log.Warning("Game {GameId} does not support Sophon downloads, falling back to regular download", gameId);
            return false;
        }
        
        // Use default install path if not specified
        installPath ??= Path.Combine(
            _settingsService.Settings.DefaultGameInstallPath ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games"),
            GameService.GetInstallFolderName(game));
        
        var downloadProgress = new SophonDownloadProgress
        {
            GameId = gameId,
            State = SophonDownloadState.FetchingInfo
        };
        
        try
        {
            _gameService.UpdateGameState(gameId, GameState.Installing);
            progress?.Report(downloadProgress);
            
            // Get Sophon download info
            var downloadInfo = await GetSophonDownloadInfoAsync(gameId, cancellationToken);
            if (downloadInfo == null || !downloadInfo.IsAvailable)
            {
                Log.Warning("Sophon download not available for {GameId}", gameId);
                return false;
            }
            
            downloadProgress.TotalSize = downloadInfo.TotalSize;
            downloadProgress.TotalFiles = downloadInfo.FileCount;
            
            // Create install directory
            Directory.CreateDirectory(installPath);
            
            downloadProgress.State = SophonDownloadState.Downloading;
            progress?.Report(downloadProgress);
            DownloadProgressChanged?.Invoke(this, downloadProgress);
            
            Log.Information("Starting Sophon download for {GameId}: {FileCount} files, {TotalSize} bytes", 
                gameId, downloadInfo.FileCount, downloadInfo.TotalSize);
            
            long totalDownloaded = 0;
            int filesProcessed = 0;
            
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(DefaultMaxParallelChunks, Environment.ProcessorCount)
            };
            
            // Enumerate and download assets
            await foreach (var asset in SophonManifest.EnumerateAsync(
                _httpClient,
                downloadInfo.ManifestPair,
                null, // No speed limiter
                cancellationToken))
            {
                if (asset.IsDirectory)
                {
                    // Create directory
                    var dirPath = Path.Combine(installPath, asset.AssetName);
                    Directory.CreateDirectory(dirPath);
                    continue;
                }
                
                var outputPath = Path.Combine(installPath, asset.AssetName);
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }
                
                downloadProgress.CurrentFile = asset.AssetName;
                progress?.Report(downloadProgress);
                
                // Download the asset using Sophon's chunk-based download
                await asset.WriteToStreamAsync(
                    _httpClient,
                    () => new FileStream(outputPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite),
                    parallelOptions,
                    read =>
                    {
                        // Progress callback for bytes written
                        Interlocked.Add(ref totalDownloaded, read);
                        downloadProgress.DownloadedBytes = totalDownloaded;
                        progress?.Report(downloadProgress);
                        DownloadProgressChanged?.Invoke(this, downloadProgress);
                    },
                    (_, _) =>
                    {
                        // Completion callback for individual file
                        Interlocked.Increment(ref filesProcessed);
                        downloadProgress.ProcessedFiles = filesProcessed;
                        Log.Debug("Downloaded: {FileName} ({Processed}/{Total})", 
                            asset.AssetName, filesProcessed, downloadInfo.FileCount);
                    });
            }
            
            // Update game info
            _gameService.UpdateGameInstallPath(gameId, installPath);
            
            downloadProgress.State = SophonDownloadState.Completed;
            progress?.Report(downloadProgress);
            DownloadCompleted?.Invoke(this, gameId);
            
            Log.Information("Sophon download completed for {GameId}: {Files} files, {Bytes} bytes", 
                gameId, filesProcessed, totalDownloaded);
            
            return true;
        }
        catch (OperationCanceledException)
        {
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);
            Log.Information("Sophon download cancelled for {GameId}", gameId);
            return false;
        }
        catch (Exception ex)
        {
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);
            downloadProgress.State = SophonDownloadState.Failed;
            downloadProgress.ErrorMessage = ex.Message;
            progress?.Report(downloadProgress);
            
            Log.Error(ex, "Sophon download failed for {GameId}", gameId);
            DownloadFailed?.Invoke(this, (gameId, ex));
            return false;
        }
    }
    
    /// <summary>
    /// Download and install a voice pack using Sophon
    /// </summary>
    public async Task<bool> DownloadAndInstallVoicePackAsync(
        string gameId,
        VoiceLanguage language,
        string? installPath = null,
        IProgress<SophonDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }
        
        if (!SupportsSophon(game))
        {
            Log.Warning("Game {GameId} does not support Sophon downloads", gameId);
            return false;
        }
        
        // Use default install path if not specified (voice packs go in the same directory as the game)
        installPath ??= GetVoicePackInstallPath(game);
        
        var downloadProgress = new SophonDownloadProgress
        {
            GameId = gameId,
            State = SophonDownloadState.FetchingInfo,
            VoiceLanguage = language,
            IsVoicePack = true
        };
        
        try
        {
            progress?.Report(downloadProgress);
            
            // Get Sophon voice pack download info
            var downloadInfo = await GetSophonVoicePackDownloadInfoAsync(gameId, language, cancellationToken);
            if (downloadInfo == null || !downloadInfo.IsAvailable)
            {
                Log.Warning("Sophon voice pack download not available for {GameId} ({Language})", gameId, language);
                return false;
            }
            
            downloadProgress.TotalSize = downloadInfo.TotalSize;
            downloadProgress.TotalFiles = downloadInfo.FileCount;
            
            // Create install directory
            Directory.CreateDirectory(installPath);
            
            downloadProgress.State = SophonDownloadState.Downloading;
            progress?.Report(downloadProgress);
            DownloadProgressChanged?.Invoke(this, downloadProgress);
            
            Log.Information("Starting Sophon voice pack download for {GameId} ({Language}): {FileCount} files, {TotalSize} bytes", 
                gameId, language, downloadInfo.FileCount, downloadInfo.TotalSize);
            
            long totalDownloaded = 0;
            int filesProcessed = 0;
            
            var parallelOptions = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Min(DefaultMaxParallelChunks, Environment.ProcessorCount)
            };
            
            // Enumerate and download voice pack assets
            await foreach (var asset in SophonManifest.EnumerateAsync(
                _httpClient,
                downloadInfo.ManifestPair,
                null,
                cancellationToken))
            {
                if (asset.IsDirectory)
                {
                    var dirPath = Path.Combine(installPath, asset.AssetName);
                    Directory.CreateDirectory(dirPath);
                    continue;
                }
                
                var outputPath = Path.Combine(installPath, asset.AssetName);
                var outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir))
                {
                    Directory.CreateDirectory(outputDir);
                }
                
                downloadProgress.CurrentFile = asset.AssetName;
                progress?.Report(downloadProgress);
                
                await asset.WriteToStreamAsync(
                    _httpClient,
                    () => new FileStream(outputPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite),
                    parallelOptions,
                    read =>
                    {
                        Interlocked.Add(ref totalDownloaded, read);
                        downloadProgress.DownloadedBytes = totalDownloaded;
                        progress?.Report(downloadProgress);
                        DownloadProgressChanged?.Invoke(this, downloadProgress);
                    },
                    (_, _) =>
                    {
                        Interlocked.Increment(ref filesProcessed);
                        downloadProgress.ProcessedFiles = filesProcessed;
                        Log.Debug("Downloaded voice pack file: {FileName} ({Processed}/{Total})", 
                            asset.AssetName, filesProcessed, downloadInfo.FileCount);
                    });
            }
            
            downloadProgress.State = SophonDownloadState.Completed;
            progress?.Report(downloadProgress);
            
            Log.Information("Sophon voice pack download completed for {GameId} ({Language}): {Files} files, {Bytes} bytes", 
                gameId, language, filesProcessed, totalDownloaded);
            
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Information("Sophon voice pack download cancelled for {GameId} ({Language})", gameId, language);
            return false;
        }
        catch (Exception ex)
        {
            downloadProgress.State = SophonDownloadState.Failed;
            downloadProgress.ErrorMessage = ex.Message;
            progress?.Report(downloadProgress);
            
            Log.Error(ex, "Sophon voice pack download failed for {GameId} ({Language})", gameId, language);
            DownloadFailed?.Invoke(this, (gameId, ex));
            return false;
        }
    }
    
    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

/// <summary>
/// Sophon download information
/// </summary>
public class SophonDownloadInfo
{
    /// <summary>
    /// Default matching field for game files (corresponds to SophonDownloadService.GameMatchingField)
    /// </summary>
    public const string DefaultMatchingField = "game";
    
    public string GameId { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public long TotalCompressedSize { get; set; }
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
    public bool IsAvailable { get; set; }
    public SophonChunkManifestInfoPair? ManifestPair { get; set; }
    public string MatchingField { get; set; } = DefaultMatchingField;
    public bool IsVoicePack { get; set; }
    public VoiceLanguage? VoiceLanguage { get; set; }
}

/// <summary>
/// Sophon download progress
/// </summary>
public class SophonDownloadProgress
{
    public string GameId { get; set; } = string.Empty;
    public SophonDownloadState State { get; set; } = SophonDownloadState.Idle;
    public long TotalSize { get; set; }
    public long DownloadedBytes { get; set; }
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public double PercentComplete => TotalSize > 0 ? (double)DownloadedBytes / TotalSize * 100 : 0;
    public bool IsVoicePack { get; set; }
    public VoiceLanguage? VoiceLanguage { get; set; }
}

/// <summary>
/// Sophon download state
/// </summary>
public enum SophonDownloadState
{
    Idle,
    FetchingInfo,
    Downloading,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Voice pack languages supported by Sophon
/// </summary>
public enum VoiceLanguage
{
    English,
    Japanese,
    Korean,
    ChineseSimplified,
    ChineseTraditional
}

/// <summary>
/// Branch info from getGameBranches API
/// </summary>
public class SophonBranchInfo
{
    public string Branch { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string PackageId { get; init; } = string.Empty;
}
