using System.Diagnostics;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for launching games using Wine/Proton on Linux
/// </summary>
public class GameLauncherService
{
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

        var winePrefix = gameSettings?.UseCustomWinePrefix == true
            ? gameSettings.CustomWinePrefixPath
            : settings.WinePrefixPath;

        var winePath = settings.UseSystemWine
            ? "wine"
            : settings.WineExecutablePath ?? "wine";

        // Ensure wine prefix exists
        if (!string.IsNullOrEmpty(winePrefix))
        {
            Directory.CreateDirectory(winePrefix);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = winePath,
            Arguments = $"\"{executablePath}\" {gameSettings?.CustomLaunchArgs ?? ""}",
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
        }

        startInfo.Environment["WINEDLLOVERRIDES"] = "mscoree,mshtml=";
        startInfo.Environment["DXVK_HUD"] = "compiler";

        // Apply custom environment variables
        if (gameSettings?.EnvironmentVariables != null)
        {
            foreach (var (key, value) in gameSettings.EnvironmentVariables)
            {
                startInfo.Environment[key] = value;
            }
        }

        Log.Information("Starting game with command: {Wine} \"{Exe}\"", winePath, executablePath);
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

        try
        {
            var winePath = _settingsService.Settings.UseSystemWine
                ? "wine"
                : _settingsService.Settings.WineExecutablePath ?? "wine";

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = winePath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.Start();
            info.Version = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            info.IsInstalled = true;
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
    public string Version { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
}
