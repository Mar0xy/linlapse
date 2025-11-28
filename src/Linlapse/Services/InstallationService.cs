using System.IO.Compression;
using System.Security.Cryptography;
using Linlapse.Models;
using Serilog;
using SharpCompress.Archives;
using SharpCompress.Common;

namespace Linlapse.Services;

/// <summary>
/// Service for installing game files
/// </summary>
public class InstallationService
{
    private readonly SettingsService _settingsService;
    private readonly DownloadService _downloadService;
    private readonly GameService _gameService;

    public event EventHandler<InstallProgress>? InstallProgressChanged;
    public event EventHandler<string>? InstallCompleted;
    public event EventHandler<(string GameId, Exception Error)>? InstallFailed;

    public InstallationService(
        SettingsService settingsService,
        DownloadService downloadService,
        GameService gameService)
    {
        _settingsService = settingsService;
        _downloadService = downloadService;
        _gameService = gameService;
    }

    /// <summary>
    /// Install a game from a zip archive
    /// </summary>
    public async Task<bool> InstallFromArchiveAsync(
        string gameId,
        string archivePath,
        string installPath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }

        try
        {
            _gameService.UpdateGameState(gameId, GameState.Installing);

            var installProgress = new InstallProgress
            {
                GameId = gameId,
                State = InstallState.Extracting
            };

            Directory.CreateDirectory(installPath);

            // Determine archive type and extract
            var extension = Path.GetExtension(archivePath).ToLowerInvariant();

            switch (extension)
            {
                case ".zip":
                    await ExtractZipAsync(archivePath, installPath, installProgress, progress, cancellationToken);
                    break;
                case ".7z":
                    await Extract7zAsync(archivePath, installPath, installProgress, progress, cancellationToken);
                    break;
                default:
                    throw new NotSupportedException($"Archive format not supported: {extension}");
            }

            // Update game info
            _gameService.UpdateGameInstallPath(gameId, installPath);
            _gameService.UpdateGameState(gameId, GameState.Ready);

            installProgress.State = InstallState.Completed;
            progress?.Report(installProgress);
            InstallCompleted?.Invoke(this, gameId);

            Log.Information("Installation completed: {GameId}", gameId);
            return true;
        }
        catch (OperationCanceledException)
        {
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);
            Log.Information("Installation cancelled: {GameId}", gameId);
            return false;
        }
        catch (Exception ex)
        {
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);
            Log.Error(ex, "Installation failed: {GameId}", gameId);
            InstallFailed?.Invoke(this, (gameId, ex));
            return false;
        }
    }

    private async Task ExtractZipAsync(
        string archivePath,
        string destinationPath,
        InstallProgress installProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(archivePath);
            installProgress.TotalFiles = archive.Entries.Count;
            installProgress.TotalBytes = archive.Entries.Sum(e => e.Length);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var destinationFileName = Path.Combine(destinationPath, entry.FullName);

                // Handle directories
                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationFileName);
                    continue;
                }

                // Ensure directory exists
                var directory = Path.GetDirectoryName(destinationFileName);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                entry.ExtractToFile(destinationFileName, overwrite: true);

                installProgress.ProcessedFiles++;
                installProgress.ProcessedBytes += entry.Length;
                installProgress.CurrentFile = entry.FullName;
                progress?.Report(installProgress);
                InstallProgressChanged?.Invoke(this, installProgress);
            }
        }, cancellationToken);
    }

    private async Task Extract7zAsync(
        string archivePath,
        string destinationPath,
        InstallProgress installProgress,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            using var archive = ArchiveFactory.Open(archivePath);
            var fileEntries = archive.Entries.Where(e => !e.IsDirectory).ToList();
            installProgress.TotalFiles = fileEntries.Count;
            installProgress.TotalBytes = fileEntries.Sum(e => e.Size);

            foreach (var entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Validate entry key to prevent path traversal attacks
                var entryKey = entry.Key ?? string.Empty;
                if (entryKey.Contains("..") || Path.IsPathRooted(entryKey))
                {
                    Log.Warning("Skipping potentially malicious archive entry: {EntryKey}", entryKey);
                    continue;
                }

                var destinationFileName = Path.Combine(destinationPath, entryKey);

                // Handle directories
                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(destinationFileName);
                    continue;
                }

                // Ensure directory exists
                var directory = Path.GetDirectoryName(destinationFileName);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                entry.WriteToFile(destinationFileName, new ExtractionOptions
                {
                    ExtractFullPath = false,
                    Overwrite = true
                });

                installProgress.ProcessedFiles++;
                installProgress.ProcessedBytes += entry.Size;
                installProgress.CurrentFile = entryKey;
                progress?.Report(installProgress);
                InstallProgressChanged?.Invoke(this, installProgress);
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Download and install a game
    /// </summary>
    public async Task<bool> DownloadAndInstallAsync(
        string gameId,
        string downloadUrl,
        string installPath,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }

        var installProgress = new InstallProgress
        {
            GameId = gameId,
            State = InstallState.Downloading
        };

        try
        {
            _gameService.UpdateGameState(gameId, GameState.Installing);

            // Create temp directory for download
            var tempDir = Path.Combine(SettingsService.GetCacheDirectory(), "downloads", gameId);
            Directory.CreateDirectory(tempDir);
            var archivePath = Path.Combine(tempDir, "game.zip");

            // Download
            var downloadProgress = new Progress<DownloadProgress>(dp =>
            {
                installProgress.DownloadProgress = dp;
                installProgress.ProcessedBytes = dp.BytesDownloaded;
                installProgress.TotalBytes = dp.TotalBytes;
                progress?.Report(installProgress);
                InstallProgressChanged?.Invoke(this, installProgress);
            });

            var downloadSuccess = await _downloadService.DownloadFileAsync(
                downloadUrl,
                archivePath,
                downloadProgress,
                cancellationToken);

            if (!downloadSuccess)
            {
                _gameService.UpdateGameState(gameId, GameState.NotInstalled);
                return false;
            }

            // Install
            installProgress.State = InstallState.Extracting;
            progress?.Report(installProgress);

            var installSuccess = await InstallFromArchiveAsync(
                gameId,
                archivePath,
                installPath,
                progress,
                cancellationToken);

            // Cleanup temp file
            try
            {
                File.Delete(archivePath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to cleanup temp file: {Path}", archivePath);
            }

            return installSuccess;
        }
        catch (Exception ex)
        {
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);
            Log.Error(ex, "Download and install failed: {GameId}", gameId);
            InstallFailed?.Invoke(this, (gameId, ex));
            return false;
        }
    }

    /// <summary>
    /// Uninstall a game
    /// </summary>
    public async Task<bool> UninstallGameAsync(string gameId, bool deleteFiles = true)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }

        try
        {
            if (deleteFiles && !string.IsNullOrEmpty(game.InstallPath) && Directory.Exists(game.InstallPath))
            {
                await Task.Run(() =>
                {
                    Directory.Delete(game.InstallPath, recursive: true);
                });
                Log.Information("Deleted game files: {Path}", game.InstallPath);
            }

            _gameService.UpdateGameInstallPath(gameId, string.Empty);
            _gameService.UpdateGameState(gameId, GameState.NotInstalled);

            Log.Information("Game uninstalled: {GameId}", gameId);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to uninstall game: {GameId}", gameId);
            return false;
        }
    }
}

/// <summary>
/// Installation progress information
/// </summary>
public class InstallProgress
{
    public string GameId { get; set; } = string.Empty;
    public InstallState State { get; set; } = InstallState.Idle;
    public int TotalFiles { get; set; }
    public int ProcessedFiles { get; set; }
    public long TotalBytes { get; set; }
    public long ProcessedBytes { get; set; }
    public string CurrentFile { get; set; } = string.Empty;
    public DownloadProgress? DownloadProgress { get; set; }
    public double PercentComplete => TotalBytes > 0 ? (double)ProcessedBytes / TotalBytes * 100 : 0;
}

/// <summary>
/// Installation state
/// </summary>
public enum InstallState
{
    Idle,
    Downloading,
    Verifying,
    Extracting,
    Configuring,
    Completed,
    Failed,
    Cancelled
}
