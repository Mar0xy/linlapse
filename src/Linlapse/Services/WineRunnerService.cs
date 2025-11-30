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

    /// <summary>
    /// Predefined list of popular wine/proton runners available for download
    /// </summary>
    private static readonly List<WineRunner> AvailableRunners = new()
    {
        // Wine runners
        new WineRunner
        {
            Id = "wine-ge-proton-latest",
            Name = "Wine-GE-Proton",
            Version = "9-25",
            Description = "Custom Wine build with patches for gaming, includes DXVK and VKD3D",
            Type = WineRunnerType.Wine,
            DownloadUrl = "https://github.com/GloriousEggroll/wine-ge-custom/releases/download/GE-Proton9-25/wine-lutris-GE-Proton9-25-x86_64.tar.xz",
            Size = 450_000_000
        },
        new WineRunner
        {
            Id = "wine-staging",
            Name = "Wine Staging",
            Version = "9.0",
            Description = "Wine with additional patches that haven't made it upstream yet",
            Type = WineRunnerType.Wine,
            DownloadUrl = "https://github.com/Kron4ek/Wine-Builds/releases/download/9.21/wine-9.21-staging-tkg-amd64.tar.xz",
            Size = 300_000_000
        },
        // Proton runners
        new WineRunner
        {
            Id = "proton-ge-latest",
            Name = "GE-Proton",
            Version = "9-25",
            Description = "Custom Proton build by GloriousEggroll with extra game fixes and features",
            Type = WineRunnerType.Proton,
            DownloadUrl = "https://github.com/GloriousEggroll/proton-ge-custom/releases/download/GE-Proton9-25/GE-Proton9-25.tar.gz",
            Size = 500_000_000
        },
        new WineRunner
        {
            Id = "proton-cachyos",
            Name = "CachyOS Proton",
            Version = "9.0-4",
            Description = "CachyOS optimized Proton build with performance improvements",
            Type = WineRunnerType.Proton,
            DownloadUrl = "https://github.com/CachyOS/proton-cachyos/releases/download/proton-cachyos-9.0-4/proton-cachyos-9.0-4.tar.zst",
            Size = 520_000_000
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

            // Download the runner archive
            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");
            httpClient.Timeout = TimeSpan.FromMinutes(30);

            using var response = await httpClient.GetAsync(runner.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
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

                    var downloadProgress = (double)downloadedBytes / totalBytes * 50; // 50% for download
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
                    var extractProgress = 50 + (double)processedEntries / totalEntries * 50; // 50-100% for extraction
                    progress?.Report(extractProgress);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SharpCompress extraction failed, trying system tar");
                
                // Fallback to system tar for .tar.xz, .tar.gz, .tar.zst files
                ExtractWithSystemTar(archivePath, destDir);
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
