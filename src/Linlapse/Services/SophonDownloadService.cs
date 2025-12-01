using System.Net.Http;
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
    
    // Sophon API URLs for different games/regions
    private static readonly Dictionary<string, string> SophonBuildUrls = new()
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
    
    public event EventHandler<SophonDownloadProgress>? DownloadProgressChanged;
    public event EventHandler<string>? DownloadCompleted;
    public event EventHandler<(string GameId, Exception Error)>? DownloadFailed;
    
    // Configuration constants
    private const int DefaultMaxConnectionsPerServer = 32;
    private const int DefaultMaxParallelChunks = 8;
    private const string DefaultMatchingField = "game";
    
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
    /// Check if a game supports Sophon downloads
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
    /// Get the Sophon build URL for a game
    /// </summary>
    private static string? GetSophonBuildUrl(GameInfo game)
    {
        var key = game.GameType switch
        {
            GameType.GenshinImpact => game.Region == GameRegion.China ? "genshin-cn" : "genshin-global",
            GameType.ZenlessZoneZero => game.Region == GameRegion.China ? "zzz-cn" : "zzz-global",
            _ => null
        };
        
        return key != null && SophonBuildUrls.TryGetValue(key, out var url) ? url : null;
    }
    
    /// <summary>
    /// Get Sophon download information for a game
    /// </summary>
    public async Task<SophonDownloadInfo?> GetSophonDownloadInfoAsync(string gameId, CancellationToken cancellationToken = default)
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
        
        var sophonUrl = GetSophonBuildUrl(game);
        if (string.IsNullOrEmpty(sophonUrl))
        {
            Log.Warning("No Sophon URL found for game: {GameId}", gameId);
            return null;
        }
        
        try
        {
            Log.Information("Fetching Sophon manifest for {GameId} from {Url}", gameId, sophonUrl);
            
            var manifestPair = await SophonManifest.CreateSophonChunkManifestInfoPair(
                _httpClient,
                sophonUrl,
                DefaultMatchingField,
                cancellationToken);
            
            if (!manifestPair.IsFound)
            {
                Log.Warning("Sophon manifest not found for {GameId}: {Message}", gameId, manifestPair.ReturnMessage);
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
                IsAvailable = true
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to get Sophon download info for {GameId}", gameId);
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
            game.Name);
        
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
    public string GameId { get; set; } = string.Empty;
    public long TotalSize { get; set; }
    public long TotalCompressedSize { get; set; }
    public int FileCount { get; set; }
    public int ChunkCount { get; set; }
    public bool IsAvailable { get; set; }
    public SophonChunkManifestInfoPair? ManifestPair { get; set; }
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
