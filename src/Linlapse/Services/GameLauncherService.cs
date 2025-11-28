using System.Diagnostics;
using System.IO.Compression;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for launching games using Wine/Proton on Linux
/// </summary>
public class GameLauncherService
{
    private const string JadeiteDownloadUrl = "https://codeberg.org/mkrsym1/jadeite/releases/download/v5.0.1/v5.0.1.zip";
    private const string JadeiteExeName = "jadeite.exe";
    
    private readonly SettingsService _settingsService;
    private readonly GameService _gameService;
    private readonly Dictionary<string, Process> _runningGames = new();

    public event EventHandler<GameInfo>? GameStarted;
    public event EventHandler<GameInfo>? GameStopped;

    public GameLauncherService(SettingsService settingsService, GameService gameService)
    {
        _settingsService = settingsService;
        _gameService = gameService;
    }

    public bool IsGameRunning(string gameId) => _runningGames.ContainsKey(gameId);

    /// <summary>
    /// Check if Jadeite is downloaded and available
    /// </summary>
    public bool IsJadeiteAvailable()
    {
        var jadeiteDir = GetJadeiteDirectory();
        var jadeitePath = Path.Combine(jadeiteDir, JadeiteExeName);
        return File.Exists(jadeitePath);
    }

    /// <summary>
    /// Get the path to Jadeite executable
    /// </summary>
    public string GetJaditePath()
    {
        var customPath = _settingsService.Settings.JadeiteExecutablePath;
        if (!string.IsNullOrEmpty(customPath) && File.Exists(customPath))
        {
            return customPath;
        }
        
        var jadeiteDir = GetJadeiteDirectory();
        return Path.Combine(jadeiteDir, JadeiteExeName);
    }

    private static string GetJadeiteDirectory()
    {
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "linlapse", "jadeite");
        Directory.CreateDirectory(configDir);
        return configDir;
    }

    /// <summary>
    /// Download and extract Jadeite for HSR/HI3 anti-cheat bypass
    /// </summary>
    public async Task<bool> DownloadJadeiteAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var jadeiteDir = GetJadeiteDirectory();
            var zipPath = Path.Combine(jadeiteDir, "jadeite.zip");
            var jadeitePath = Path.Combine(jadeiteDir, JadeiteExeName);

            Log.Information("Downloading Jadeite from {Url}", JadeiteDownloadUrl);

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");

            using var response = await httpClient.GetAsync(JadeiteDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            var downloadedBytes = 0L;

            // Download to temp file first, then move
            var tempZipPath = zipPath + ".tmp";
            
            await using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var fileStream = new FileStream(tempZipPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
            {
                var buffer = new byte[8192];
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    downloadedBytes += bytesRead;

                    if (totalBytes > 0)
                    {
                        progress?.Report((double)downloadedBytes / totalBytes * 100);
                    }
                }
            }
            
            // Move temp file to final location (stream is now closed)
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            File.Move(tempZipPath, zipPath);

            Log.Information("Extracting Jadeite to {Path}", jadeiteDir);

            // Extract all files from the zip (jadeite.exe, game_payload.dll, etc.)
            ZipFile.ExtractToDirectory(zipPath, jadeiteDir, overwriteFiles: true);
            
            // Verify that jadeite.exe was extracted
            if (!File.Exists(jadeitePath))
            {
                Log.Error("Could not find {File} after extraction", JadeiteExeName);
                return false;
            }
            
            Log.Information("Extracted Jadeite files to {Path}", jadeiteDir);

            // Clean up zip file
            File.Delete(zipPath);

            Log.Information("Jadeite downloaded and extracted successfully");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download Jadeite");
            return false;
        }
    }

    /// <summary>
    /// Check if a game requires Jadeite to run
    /// </summary>
    public static bool RequiresJadeite(GameType gameType) =>
        gameType is GameType.HonkaiStarRail or GameType.HonkaiImpact3rd;

    public async Task<bool> LaunchGameAsync(string gameId)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null)
        {
            Log.Error("Game not found: {GameId}", gameId);
            return false;
        }

        if (!game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            Log.Error("Game not installed: {GameId}", gameId);
            return false;
        }

        if (IsGameRunning(gameId))
        {
            Log.Warning("Game already running: {GameId}", gameId);
            return false;
        }

        try
        {
            var executablePath = GetGameExecutable(game);
            if (string.IsNullOrEmpty(executablePath) || !File.Exists(executablePath))
            {
                Log.Error("Game executable not found: {Path}", executablePath);
                return false;
            }

            var process = await StartGameProcessAsync(game, executablePath);
            if (process != null)
            {
                _runningGames[gameId] = process;
                _gameService.UpdateGameState(gameId, GameState.Running);

                game.LastPlayed = DateTime.UtcNow;

                // Monitor process exit
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => OnGameExited(game);

                GameStarted?.Invoke(this, game);
                Log.Information("Game started: {Name}", game.DisplayName);
                return true;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to launch game: {GameId}", gameId);
        }

        return false;
    }

    private string? GetGameExecutable(GameInfo game)
    {
        var basePath = game.InstallPath;

        return game.GameType switch
        {
            GameType.HonkaiImpact3rd => FindExecutable(basePath, "BH3.exe", "Games/BH3.exe"),
            GameType.GenshinImpact => FindExecutable(basePath, "GenshinImpact.exe", "YuanShen.exe"),
            GameType.HonkaiStarRail => FindExecutable(basePath, "StarRail.exe", "Games/StarRail.exe"),
            GameType.ZenlessZoneZero => FindExecutable(basePath, "ZenlessZoneZero.exe"),
            GameType.Custom => game.ExecutablePath,
            _ => null
        };
    }

    private static string? FindExecutable(string basePath, params string[] possiblePaths)
    {
        foreach (var relativePath in possiblePaths)
        {
            var fullPath = Path.Combine(basePath, relativePath);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }
        return null;
    }

    private async Task<Process?> StartGameProcessAsync(GameInfo game, string executablePath)
    {
        var settings = _settingsService.Settings;
        var gameSettings = settings.GameSpecificSettings.GetValueOrDefault(game.Id);

        // Determine whether to use Proton or Wine
        string winePath;
        bool isProton = false;
        bool useProtonScript = false; // True when using the proton script (not wine binary inside Proton)
        
        if (settings.UseProton && !string.IsNullOrEmpty(settings.ProtonPath))
        {
            // When Proton is enabled, always use the proton script (not wine binary inside Proton)
            // The proton script handles all the Proton magic (DXVK, VKD3D, etc.)
            var protonScriptPath = Path.Combine(settings.ProtonPath, "proton");
            if (File.Exists(protonScriptPath))
            {
                winePath = protonScriptPath;
                isProton = true;
                useProtonScript = true;
                Log.Information("Using Proton script from {Path}", protonScriptPath);
            }
            else
            {
                Log.Warning("Proton path configured but proton script not found at {Path}, falling back to Wine", settings.ProtonPath);
                winePath = settings.UseSystemWine ? "wine" : settings.WineExecutablePath ?? "wine";
            }
        }
        else
        {
            winePath = settings.UseSystemWine ? "wine" : settings.WineExecutablePath ?? "wine";
        }

        // Determine wine prefix - use per-game prefix by default
        var winePrefix = gameSettings?.UseCustomWinePrefix == true
            ? gameSettings.CustomWinePrefixPath
            : GetGameWinePrefixPath(game);

        // Ensure wine prefix directory exists
        if (!string.IsNullOrEmpty(winePrefix))
        {
            Directory.CreateDirectory(winePrefix);
        }

        // Initialize wine prefix with required components (only for Wine, not Proton)
        // Proton handles its own prefix initialization with DXVK, VKD3D, etc.
        if (!isProton && !string.IsNullOrEmpty(winePrefix))
        {
            await EnsureWinePrefixInitializedAsync(winePrefix, winePath);
        }

        // Determine if we need to use Jadeite for this game
        var useJadeite = RequiresJadeite(game.GameType);
        string arguments;
        
        if (useProtonScript)
        {
            // Proton script uses: proton run "executable" [args]
            if (useJadeite && IsJadeiteAvailable())
            {
                var jadeitePath = GetJaditePath();
                arguments = $"run \"{jadeitePath}\" \"{executablePath}\" {gameSettings?.CustomLaunchArgs ?? ""}";
                Log.Information("Using Jadeite launcher with Proton script for {Game}", game.DisplayName);
            }
            else
            {
                if (useJadeite && !IsJadeiteAvailable())
                {
                    Log.Warning("Jadeite is required for {Game} but not installed. Download it from Settings.", game.DisplayName);
                }
                arguments = $"run \"{executablePath}\" {gameSettings?.CustomLaunchArgs ?? ""}";
            }
        }
        else if (useJadeite && IsJadeiteAvailable())
        {
            var jadeitePath = GetJaditePath();
            // Launch with Jadeite: wine jadeite.exe "game_executable.exe"
            arguments = $"\"{jadeitePath}\" \"{executablePath}\" {gameSettings?.CustomLaunchArgs ?? ""}";
            Log.Information("Using Jadeite launcher for {Game}", game.DisplayName);
        }
        else if (useJadeite && !IsJadeiteAvailable())
        {
            Log.Warning("Jadeite is required for {Game} but not installed. Download it from Settings.", game.DisplayName);
            // Still try to launch, but it may not work
            arguments = $"\"{executablePath}\" {gameSettings?.CustomLaunchArgs ?? ""}";
        }
        else
        {
            arguments = $"\"{executablePath}\" {gameSettings?.CustomLaunchArgs ?? ""}";
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = winePath,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(executablePath),
            UseShellExecute = false,
            CreateNoWindow = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        // Set environment variables
        if (!string.IsNullOrEmpty(winePrefix))
        {
            startInfo.Environment["WINEPREFIX"] = winePrefix;
            if (isProton)
            {
                startInfo.Environment["STEAM_COMPAT_DATA_PATH"] = winePrefix;
            }
        }

        startInfo.Environment["WINEDLLOVERRIDES"] = "mscoree,mshtml=";
        startInfo.Environment["DXVK_HUD"] = "compiler";
        
        // Proton-specific environment variables
        if (isProton)
        {
            startInfo.Environment["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = 
                Environment.GetEnvironmentVariable("HOME") + "/.steam/steam";
            startInfo.Environment["PROTON_NO_ESYNC"] = "1";
            startInfo.Environment["PROTON_NO_FSYNC"] = "1";
        }

        // Apply custom environment variables
        if (gameSettings?.EnvironmentVariables != null)
        {
            foreach (var (key, value) in gameSettings.EnvironmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        Log.Information("Starting game with command: {Wine} {Args}", winePath, arguments);
        Log.Debug("Wine prefix: {Prefix}", winePrefix);

        var process = new Process { StartInfo = startInfo };

        // Log output
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Log.Debug("[{Game}] {Output}", game.Name, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                Log.Debug("[{Game}] {Error}", game.Name, e.Data);
        };

        await Task.Run(() => process.Start());
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return process;
    }

    /// <summary>
    /// Get the wine prefix path for a specific game
    /// </summary>
    private static string GetGameWinePrefixPath(GameInfo game)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Sanitize game name for use in path (remove invalid characters)
        var safeName = string.Join("_", game.Name.Split(Path.GetInvalidFileNameChars()));
        return Path.Combine(home, ".local", "share", "linlapse", "wine-prefixes", safeName);
    }

    /// <summary>
    /// Get the path to winetricks (downloads if not present)
    /// </summary>
    private static string GetWinetricksPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "linlapse", "winetricks");
    }

    /// <summary>
    /// Download winetricks from the official repository
    /// </summary>
    private static async Task<bool> EnsureWinetricksDownloadedAsync()
    {
        const string winetricksUrl = "https://raw.githubusercontent.com/Winetricks/winetricks/refs/heads/master/src/winetricks";
        var winetricksPath = GetWinetricksPath();

        // Check if already downloaded
        if (File.Exists(winetricksPath))
        {
            return true;
        }

        Log.Information("Downloading winetricks from {Url}", winetricksUrl);

        try
        {
            // Ensure directory exists
            var directory = Path.GetDirectoryName(winetricksPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Add("User-Agent", "Linlapse/1.0");
            
            var content = await httpClient.GetStringAsync(winetricksUrl);
            await File.WriteAllTextAsync(winetricksPath, content);

            // Make it executable (chmod +x)
            var chmodProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "chmod",
                    Arguments = $"+x \"{winetricksPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            chmodProcess.Start();
            await chmodProcess.WaitForExitAsync();

            Log.Information("Winetricks downloaded successfully to {Path}", winetricksPath);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to download winetricks");
            return false;
        }
    }

    /// <summary>
    /// Ensure the wine prefix is initialized with required components
    /// </summary>
    private async Task EnsureWinePrefixInitializedAsync(string winePrefix, string winePath)
    {
        // Check if prefix is already initialized (has system.reg file)
        var systemRegPath = Path.Combine(winePrefix, "system.reg");
        var markerPath = Path.Combine(winePrefix, ".linlapse_initialized");
        
        if (File.Exists(systemRegPath) && File.Exists(markerPath))
        {
            Log.Debug("Wine prefix already initialized: {Prefix}", winePrefix);
            return;
        }

        Log.Information("Initializing wine prefix: {Prefix}", winePrefix);

        try
        {
            // Step 1: Initialize the prefix with wineboot
            await RunWineCommandAsync(winePath, "wineboot", "--init", winePrefix);
            Log.Information("Wine prefix created with wineboot");

            // Step 2: Download winetricks if needed and install required components
            if (await EnsureWinetricksDownloadedAsync())
            {
                Log.Information("Installing wine components with winetricks...");
                
                var winetricksPath = GetWinetricksPath();
                
                // Install corefonts (required for proper text rendering)
                await RunWinetricksAsync(winetricksPath, winePrefix, winePath, "corefonts");
                
                // Install DXVK (DirectX 9/10/11 to Vulkan translation)
                await RunWinetricksAsync(winetricksPath, winePrefix, winePath, "dxvk");
                
                // Install VKD3D (DirectX 12 to Vulkan translation)
                await RunWinetricksAsync(winetricksPath, winePrefix, winePath, "vkd3d");
                
                Log.Information("Wine components installed successfully");
            }
            else
            {
                Log.Warning("Failed to download winetricks. Games may not run correctly without DXVK, VKD3D, and corefonts.");
            }

            // Create marker file to indicate prefix is initialized
            await File.WriteAllTextAsync(markerPath, $"Initialized by Linlapse on {DateTime.UtcNow:O}");
            Log.Information("Wine prefix initialization complete: {Prefix}", winePrefix);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to initialize wine prefix: {Prefix}", winePrefix);
            // Don't throw - allow the game to try to launch anyway
        }
    }

    /// <summary>
    /// Run a wine command with the specified prefix
    /// </summary>
    private static async Task RunWineCommandAsync(string winePath, string command, string args, string winePrefix)
    {
        // Find the command in the same directory as wine
        // e.g., if winePath is /usr/bin/wine, look for /usr/bin/wineboot
        var wineDir = Path.GetDirectoryName(winePath);
        var commandPath = !string.IsNullOrEmpty(wineDir) 
            ? Path.Combine(wineDir, command) 
            : command;
        
        // If the command doesn't exist in wine's directory, try it as a standalone command
        if (!File.Exists(commandPath))
        {
            commandPath = command;
        }
        
        Log.Debug("Running wine command: {Command} {Args}", commandPath, args);
        
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = commandPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        
        process.StartInfo.Environment["WINEPREFIX"] = winePrefix;
        process.StartInfo.Environment["WINE"] = winePath;
        
        process.Start();
        await process.WaitForExitAsync();
        
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            Log.Warning("Wine command '{Command} {Args}' exited with code {ExitCode}: {Error}", 
                commandPath, args, process.ExitCode, error);
        }
    }

    /// <summary>
    /// Run winetricks with the specified prefix and verb
    /// </summary>
    private static async Task RunWinetricksAsync(string winetricksPath, string winePrefix, string winePath, string verb)
    {
        Log.Debug("Running winetricks {Verb} for prefix {Prefix} with wine {Wine}", verb, winePrefix, winePath);
        
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = winetricksPath,
                Arguments = $"-q {verb}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        
        process.StartInfo.Environment["WINEPREFIX"] = winePrefix;
        process.StartInfo.Environment["WINE"] = winePath;
        process.StartInfo.Environment["WINE64"] = winePath + "64";
        
        process.Start();
        await process.WaitForExitAsync();
        
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync();
            Log.Warning("winetricks {Verb} exited with code {ExitCode}: {Error}", 
                verb, process.ExitCode, error);
        }
        else
        {
            Log.Debug("winetricks {Verb} completed successfully", verb);
        }
    }

    private void OnGameExited(GameInfo game)
    {
        if (_runningGames.TryGetValue(game.Id, out var process))
        {
            _runningGames.Remove(game.Id);
            process.Dispose();
        }

        _gameService.UpdateGameState(game.Id, GameState.Ready);
        GameStopped?.Invoke(this, game);
        Log.Information("Game stopped: {Name}", game.DisplayName);
    }

    public void StopGame(string gameId)
    {
        if (_runningGames.TryGetValue(gameId, out var process))
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    Log.Information("Game forcefully stopped: {GameId}", gameId);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error stopping game: {GameId}", gameId);
            }
        }
    }

    public async Task<WineInfo> GetWineInfoAsync()
    {
        var info = new WineInfo();
        var settings = _settingsService.Settings;

        try
        {
            string winePath;
            bool isProton = false;
            
            // Check if Proton is configured and should be used
            if (settings.UseProton && !string.IsNullOrEmpty(settings.ProtonPath))
            {
                // When Proton is enabled, always use the proton script
                var protonScriptPath = Path.Combine(settings.ProtonPath, "proton");
                if (File.Exists(protonScriptPath))
                {
                    // For version info with Proton, we check if the script exists and report Proton version from path
                    info.IsInstalled = true;
                    info.IsProton = true;
                    info.Path = protonScriptPath;
                    
                    // Try to extract version from the Proton path (e.g., "Proton 9.0" from path)
                    var protonDirName = Path.GetFileName(settings.ProtonPath);
                    info.Version = $"Proton ({protonDirName})";
                    return info;
                }
                else
                {
                    // Proton path configured but script not found
                    info.IsInstalled = false;
                    info.Version = $"Proton configured but proton script not found at {settings.ProtonPath}";
                    return info;
                }
            }
            else
            {
                winePath = settings.UseSystemWine
                    ? "wine"
                    : settings.WineExecutablePath ?? "wine";
            }

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = winePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            info.Version = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            info.IsInstalled = true;
            info.IsProton = isProton;
            info.Path = winePath;
        }
        catch
        {
            info.IsInstalled = false;
        }

        return info;
    }
}

public class WineInfo
{
    public bool IsInstalled { get; set; }
    public bool IsProton { get; set; }
    public string Version { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
