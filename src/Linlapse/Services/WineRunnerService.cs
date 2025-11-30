using System.IO.Compression;
using System.Text.Json;
using Linlapse.Models;
using Serilog;
using SharpCompress.Archives;

namespace Linlapse.Services;

/// <summary>
/// Service for managing Wine/Proton runner downloads and installations
/// </summary>
public class WineRunnerService
{
    private readonly SettingsService _settingsService;
    private readonly DownloadService _downloadService;
    private readonly string _runnersDirectory;
    
    // Use a single shared HttpClient for all downloads to avoid socket exhaustion
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromMinutes(30)
    };
    
    // Progress constants for clarity
    private const double DownloadProgressMax = 50.0;
    private const double ExtractionProgressStart = 50.0;
    private const double ExtractionProgressMax = 100.0;

    /// <summary>
    /// Predefined list of popular wine/proton runners available for download.
    /// Note: Versions are pinned for stability. Check GitHub releases for newer versions.
    /// </summary>
    private static readonly List<WineRunner> AvailableRunners = new()
    {
        // Wine runners
        new WineRunner
        {
            Id = "wine-spritz",
            Name = "Spritz Wine",
            Version = "10.15",
            Description = "Wine with additional patches",
            Type = WineRunnerType.Wine,
            DownloadUrl = "https://github.com/NelloKudo/Wine-Builds/releases/download/wine-tkg-aagl-v10.15-7/spritz-wine-tkg-staging-wow64-10.15-7-x86_64.tar.xz",
            Size = 300_000_000
        },
        // Proton runners
        new WineRunner
        {
            Id = "proton-cachyos",
            Name = "CachyOS Proton",
            Version = "10.0-20251126",
            Description = "CachyOS optimized Proton build with performance improvements",
            Type = WineRunnerType.Proton,
            DownloadUrl = "https://github.com/CachyOS/proton-cachyos/releases/download/cachyos-10.0-20251126-slr/proton-cachyos-10.0-20251126-slr-x86_64.tar.xz",
            Size = 289_000_000
        },
        new WineRunner
        {
            Id = "proton-dw",
            Name = "dwproton",
            Version = "10.0-8",
            Description = "Special proton made by dawn winery",
            Type = WineRunnerType.Proton,
            DownloadUrl = "https://dawn.wine/dawn-winery/dwproton/releases/download/dwproton-10.0-8/dwproton-10.0-8-x86_64.tar.xz",
            Size = 289_000_000
        }
    };

    public event EventHandler<(string RunnerId, double Progress)>? DownloadProgressChanged;

    public WineRunnerService(SettingsService settingsService, DownloadService downloadService)
    {
        _settingsService = settingsService;
        _downloadService = downloadService;
        _runnersDirectory = GetRunnersDirectory();
    }

    private static string GetRunnersDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dir = Path.Combine(home, ".local", "share", "linlapse", "runners");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Get list of available runners with installation status updated
    /// </summary>
    public List<WineRunner> GetAvailableRunners()
    {
        var installedRunners = _settingsService.Settings.InstalledRunners;
        var runners = new List<WineRunner>();

        foreach (var runner in AvailableRunners)
        {
            var copy = new WineRunner
            {
                Id = runner.Id,
                Name = runner.Name,
                Version = runner.Version,
                Description = runner.Description,
                Type = runner.Type,
                DownloadUrl = runner.DownloadUrl,
                Size = runner.Size
            };

            var installed = installedRunners.FirstOrDefault(r => r.Id == runner.Id);
            if (installed != null && Directory.Exists(installed.InstallPath))
            {
                copy.IsInstalled = true;
                copy.InstallPath = installed.InstallPath;
            }

            runners.Add(copy);
        }

        return runners;
    }

    /// <summary>
    /// Get list of installed runners
    /// </summary>
    public List<InstalledRunner> GetInstalledRunners()
    {
        var installedRunners = _settingsService.Settings.InstalledRunners
            .Where(r => Directory.Exists(r.InstallPath))
            .ToList();

        return installedRunners;
    }

    /// <summary>
    /// Download and install a runner
    /// </summary>
    public async Task<bool> InstallRunnerAsync(string runnerId, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var runner = AvailableRunners.FirstOrDefault(r => r.Id == runnerId);
        if (runner == null)
        {
            Log.Error("Runner not found: {RunnerId}", runnerId);
            return false;
        }

        var installDir = Path.Combine(_runnersDirectory, runner.Type.ToString().ToLower(), runnerId);

        try
        {
            Log.Information("Downloading runner: {Name} {Version}", runner.Name, runner.Version);

            // Create download directory
            Directory.CreateDirectory(installDir);

            // Determine file extension and download path
            var fileName = Path.GetFileName(new Uri(runner.DownloadUrl).AbsolutePath);
            var downloadPath = Path.Combine(_runnersDirectory, fileName);

            // Download the runner archive using the shared HttpClient
            using var request = new HttpRequestMessage(HttpMethod.Get, runner.DownloadUrl);
            request.Headers.Add("User-Agent", "Linlapse/1.0");

            using var response = await SharedHttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? runner.Size;
            var downloadedBytes = 0L;

            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;

                    var downloadProgress = (double)downloadedBytes / totalBytes * DownloadProgressMax;
                    progress?.Report(downloadProgress);
                    DownloadProgressChanged?.Invoke(this, (runnerId, downloadProgress));
                }
            }

            Log.Information("Extracting runner to {Path}", installDir);

            // Extract the archive
            await ExtractArchiveAsync(downloadPath, installDir, progress, cancellationToken);

            // Find the executable path
            var executablePath = FindRunnerExecutable(installDir, runner.Type);
            if (string.IsNullOrEmpty(executablePath))
            {
                Log.Warning("Could not find executable in extracted runner at {Path}", installDir);
                // Still continue as the executable might be in a subdirectory
            }

            // Register the installed runner
            var installedRunner = new InstalledRunner
            {
                Id = runner.Id,
                Name = runner.Name,
                Version = runner.Version,
                Type = runner.Type,
                InstallPath = installDir,
                ExecutablePath = executablePath ?? installDir
            };

            _settingsService.UpdateSettings(settings =>
            {
                settings.InstalledRunners.RemoveAll(r => r.Id == runnerId);
                settings.InstalledRunners.Add(installedRunner);
            });

            // Clean up download file
            if (File.Exists(downloadPath))
            {
                File.Delete(downloadPath);
            }

            Log.Information("Runner installed successfully: {Name} {Version}", runner.Name, runner.Version);
            progress?.Report(100);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to install runner: {RunnerId}", runnerId);

            // Clean up on failure
            if (Directory.Exists(installDir))
            {
                try { Directory.Delete(installDir, true); } catch { }
            }

            return false;
        }
    }

    private async Task ExtractArchiveAsync(string archivePath, string destDir, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        await Task.Run(() =>
        {
            Exception? sharpCompressException = null;
            
            try
            {
                using var archive = ArchiveFactory.Open(archivePath);
                var entries = archive.Entries.Where(e => !e.IsDirectory).ToList();
                var totalEntries = entries.Count;
                var processedEntries = 0;

                foreach (var entry in entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var destinationPath = Path.Combine(destDir, entry.Key ?? "");
                    var directory = Path.GetDirectoryName(destinationPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    entry.WriteToDirectory(destDir, new SharpCompress.Common.ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });

                    processedEntries++;
                    var extractProgress = ExtractionProgressStart + (double)processedEntries / totalEntries * (ExtractionProgressMax - ExtractionProgressStart);
                    progress?.Report(extractProgress);
                }
            }
            catch (Exception ex)
            {
                sharpCompressException = ex;
                Log.Warning(ex, "SharpCompress extraction failed, trying system tar");
                
                try
                {
                    // Fallback to system tar for .tar.xz, .tar.gz, .tar.zst files
                    ExtractWithSystemTar(archivePath, destDir);
                }
                catch (Exception tarEx)
                {
                    // Include both exceptions for debugging
                    throw new AggregateException(
                        $"Failed to extract archive. SharpCompress error: {sharpCompressException.Message}. System tar error: {tarEx.Message}",
                        sharpCompressException, tarEx);
                }
            }
        }, cancellationToken);
    }

    private static void ExtractWithSystemTar(string archivePath, string destDir)
    {
        var args = archivePath.EndsWith(".tar.zst", StringComparison.OrdinalIgnoreCase)
            ? $"--zstd -xf \"{archivePath}\" -C \"{destDir}\""
            : $"-xf \"{archivePath}\" -C \"{destDir}\"";

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "tar",
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        process.Start();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            var error = process.StandardError.ReadToEnd();
            throw new Exception($"tar extraction failed: {error}");
        }
    }

    private static string? FindRunnerExecutable(string installDir, WineRunnerType type)
    {
        // Look for common executable locations
        var searchPatterns = type == WineRunnerType.Wine
            ? new[] { "**/bin/wine", "**/bin/wine64" }
            : new[] { "**/proton", "**/proton.py" };

        foreach (var pattern in searchPatterns)
        {
            var files = Directory.EnumerateFiles(installDir, Path.GetFileName(pattern), SearchOption.AllDirectories);
            var executable = files.FirstOrDefault();
            if (!string.IsNullOrEmpty(executable))
            {
                return executable;
            }
        }

        // Direct search in subdirectories
        if (type == WineRunnerType.Wine)
        {
            var wineExe = Directory.EnumerateFiles(installDir, "wine*", SearchOption.AllDirectories)
                .FirstOrDefault(f => Path.GetFileName(f) == "wine" || Path.GetFileName(f) == "wine64");
            if (!string.IsNullOrEmpty(wineExe))
                return wineExe;
        }
        else
        {
            var protonExe = Directory.EnumerateFiles(installDir, "proton", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (!string.IsNullOrEmpty(protonExe))
                return protonExe;
        }

        return null;
    }

    /// <summary>
    /// Uninstall a runner
    /// </summary>
    public async Task<bool> UninstallRunnerAsync(string runnerId)
    {
        var installed = _settingsService.Settings.InstalledRunners.FirstOrDefault(r => r.Id == runnerId);
        if (installed == null)
        {
            Log.Warning("Runner not installed: {RunnerId}", runnerId);
            return false;
        }

        try
        {
            Log.Information("Uninstalling runner: {Name}", installed.Name);

            // Delete the installation directory
            if (Directory.Exists(installed.InstallPath))
            {
                await Task.Run(() => Directory.Delete(installed.InstallPath, true));
            }

            // Remove from settings
            _settingsService.UpdateSettings(settings =>
            {
                settings.InstalledRunners.RemoveAll(r => r.Id == runnerId);
            });

            Log.Information("Runner uninstalled: {Name}", installed.Name);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to uninstall runner: {RunnerId}", runnerId);
            return false;
        }
    }

    /// <summary>
    /// Get the executable path for a specific installed runner
    /// </summary>
    public string? GetRunnerExecutablePath(string runnerId)
    {
        var installed = _settingsService.Settings.InstalledRunners.FirstOrDefault(r => r.Id == runnerId);
        if (installed == null || !Directory.Exists(installed.InstallPath))
        {
            return null;
        }

        // If we have a stored executable path, verify it exists
        if (!string.IsNullOrEmpty(installed.ExecutablePath) && File.Exists(installed.ExecutablePath))
        {
            return installed.ExecutablePath;
        }

        // Try to find the executable
        var runner = AvailableRunners.FirstOrDefault(r => r.Id == runnerId);
        if (runner != null)
        {
            return FindRunnerExecutable(installed.InstallPath, runner.Type);
        }

        return null;
    }
}
