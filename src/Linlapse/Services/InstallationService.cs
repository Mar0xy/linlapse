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
            var entries = archive.Entries.ToList();
            installProgress.TotalFiles = entries.Count;
            installProgress.TotalBytes = entries.Sum(e => e.Length);

            // Get the full destination path for security validation
            var fullDestinationPath = Path.GetFullPath(destinationPath);

            // First pass: create all directories (must be sequential to avoid race conditions)
            var directories = entries
                .Where(e => string.IsNullOrEmpty(e.Name))
                .Select(e => Path.Combine(destinationPath, e.FullName))
                .Distinct()
                .ToList();

            foreach (var dir in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Security: validate directory path
                var fullDir = Path.GetFullPath(dir);
                if (!fullDir.StartsWith(fullDestinationPath, StringComparison.Ordinal))
                {
                    Log.Warning("Skipping potentially malicious directory entry: {Dir}", dir);
                    continue;
                }
                Directory.CreateDirectory(dir);
            }

            // Also pre-create directories for files
            var fileDirectories = entries
                .Where(e => !string.IsNullOrEmpty(e.Name))
                .Select(e => Path.GetDirectoryName(Path.Combine(destinationPath, e.FullName)))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToList();

            foreach (var dir in fileDirectories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                
                // Security: validate directory path
                var fullDir = Path.GetFullPath(dir!);
                if (!fullDir.StartsWith(fullDestinationPath, StringComparison.Ordinal))
                {
                    continue;
                }
                Directory.CreateDirectory(dir!);
            }

            // Second pass: extract files in parallel for better performance
            var fileEntries = entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            var processedFiles = 0;
            var processedBytes = 0L;
            var progressLock = new object();

            // Use parallel extraction with degree of parallelism based on CPU cores
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount),
                CancellationToken = cancellationToken
            };

            Parallel.ForEach(fileEntries, parallelOptions, entry =>
            {
                var destinationFileName = Path.Combine(destinationPath, entry.FullName);
                
                // Security: validate file path
                var fullPath = Path.GetFullPath(destinationFileName);
                if (!fullPath.StartsWith(fullDestinationPath, StringComparison.Ordinal))
                {
                    Log.Warning("Skipping potentially malicious archive entry: {Entry}", entry.FullName);
                    return;
                }

                entry.ExtractToFile(destinationFileName, overwrite: true);

                lock (progressLock)
                {
                    processedFiles++;
                    processedBytes += entry.Length;
                    installProgress.ProcessedFiles = processedFiles;
                    installProgress.ProcessedBytes = processedBytes;
                    installProgress.CurrentFile = entry.FullName;
                    
                    // Report progress less frequently to reduce overhead (every 100 files or every 10MB)
                    if (processedFiles % 100 == 0 || processedBytes % (10 * 1024 * 1024) < entry.Length)
                    {
                        progress?.Report(installProgress);
                        InstallProgressChanged?.Invoke(this, installProgress);
                    }
                }
            });

            // Final progress report
            installProgress.ProcessedFiles = processedFiles;
            installProgress.ProcessedBytes = processedBytes;
            progress?.Report(installProgress);
            InstallProgressChanged?.Invoke(this, installProgress);
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

            // Get the full path of destination for validation
            var fullDestinationPath = Path.GetFullPath(destinationPath);

            // Pre-create all directories to avoid repeated creation during extraction
            var directories = fileEntries
                .Select(e => Path.GetDirectoryName(Path.Combine(destinationPath, e.Key ?? string.Empty)))
                .Where(d => !string.IsNullOrEmpty(d))
                .Distinct()
                .ToList();

            foreach (var dir in directories)
            {
                // Security: validate directory path
                var fullDir = Path.GetFullPath(dir!);
                if (fullDir.StartsWith(fullDestinationPath, StringComparison.Ordinal))
                {
                    Directory.CreateDirectory(dir!);
                }
            }

            var lastProgressReport = DateTime.UtcNow;
            var progressReportInterval = TimeSpan.FromMilliseconds(100); // Report every 100ms

            foreach (var entry in fileEntries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var entryKey = entry.Key ?? string.Empty;
                
                // Validate entry key to prevent path traversal attacks
                // Use case-sensitive comparison (Ordinal) for proper security on Linux file systems
                var destinationFileName = Path.GetFullPath(Path.Combine(destinationPath, entryKey));
                if (!destinationFileName.StartsWith(fullDestinationPath, StringComparison.Ordinal))
                {
                    Log.Warning("Skipping potentially malicious archive entry: {EntryKey}", entryKey);
                    continue;
                }

                entry.WriteToFile(destinationFileName, new ExtractionOptions
                {
                    ExtractFullPath = false,
                    Overwrite = true
                });

                installProgress.ProcessedFiles++;
                installProgress.ProcessedBytes += entry.Size;
                installProgress.CurrentFile = entryKey;
                
                // Rate-limited progress reporting for better performance
                var now = DateTime.UtcNow;
                if (now - lastProgressReport >= progressReportInterval)
                {
                    progress?.Report(installProgress);
                    InstallProgressChanged?.Invoke(this, installProgress);
                    lastProgressReport = now;
                }
            }

            // Final progress report
            progress?.Report(installProgress);
            InstallProgressChanged?.Invoke(this, installProgress);
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
