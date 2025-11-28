using System.Text.Json;
using System.Text.Json.Serialization;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for managing game installations and configurations
/// </summary>
public class GameService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _gamesFilePath;
    private readonly SettingsService _settingsService;
    private List<GameInfo> _games;

    public IReadOnlyList<GameInfo> Games => _games.AsReadOnly();
    public event EventHandler<GameInfo>? GameStateChanged;
    public event EventHandler? GamesListChanged;

    public GameService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _gamesFilePath = Path.Combine(SettingsService.GetDataDirectory(), "games.json");
        _games = LoadGames();

        // Initialize with known games if empty, or ensure all known games exist
        if (_games.Count == 0)
        {
            InitializeKnownGames();
        }
        else
        {
            // Ensure all known games exist (in case new regions were added)
            EnsureAllKnownGamesExist();
        }
    }

    private List<GameInfo> LoadGames()
    {
        try
        {
            if (File.Exists(_gamesFilePath))
            {
                var json = File.ReadAllText(_gamesFilePath);
                var games = JsonSerializer.Deserialize<List<GameInfo>>(json, JsonOptions);
                if (games != null)
                {
                    Log.Information("Loaded {Count} games from {Path}", games.Count, _gamesFilePath);
                    return games;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to load games from {Path}", _gamesFilePath);
        }

        return new List<GameInfo>();
    }

    private void SaveGames()
    {
        try
        {
            var json = JsonSerializer.Serialize(_games, JsonOptions);
            File.WriteAllText(_gamesFilePath, json);
            Log.Information("Saved {Count} games to {Path}", _games.Count, _gamesFilePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to save games to {Path}", _gamesFilePath);
        }
    }

    /// <summary>
    /// Ensure all known games exist in the games list (handles adding new regions/games)
    /// </summary>
    private void EnsureAllKnownGamesExist()
    {
        var knownGames = GetAllKnownGames();
        var added = false;

        foreach (var knownGame in knownGames)
        {
            if (!_games.Any(g => g.Id == knownGame.Id))
            {
                _games.Add(knownGame);
                added = true;
                Log.Information("Added missing game: {Name} ({Id})", knownGame.DisplayName, knownGame.Id);
            }
        }

        if (added)
        {
            SaveGames();
        }
    }

    private static List<GameInfo> GetAllKnownGames() => new()
    {
        // Global Region Games
        new()
        {
            Id = "hi3-global",
            Name = "honkai3rd",
            DisplayName = "Honkai Impact 3rd",
            GameType = GameType.HonkaiImpact3rd,
            Region = GameRegion.Global,
            State = GameState.NotInstalled
        },
        new()
        {
            Id = "gi-global",
            Name = "genshin",
            DisplayName = "Genshin Impact",
            GameType = GameType.GenshinImpact,
            Region = GameRegion.Global,
            State = GameState.NotInstalled
        },
        new()
        {
            Id = "hsr-global",
            Name = "starrail",
            DisplayName = "Honkai: Star Rail",
            GameType = GameType.HonkaiStarRail,
            Region = GameRegion.Global,
            State = GameState.NotInstalled
        },
        new()
        {
            Id = "zzz-global",
            Name = "zenless",
            DisplayName = "Zenless Zone Zero",
            GameType = GameType.ZenlessZoneZero,
            Region = GameRegion.Global,
            State = GameState.NotInstalled
        },
        // China Region Games
        new()
        {
            Id = "hi3-cn",
            Name = "honkai3rd",
            DisplayName = "Honkai Impact 3rd",
            GameType = GameType.HonkaiImpact3rd,
            Region = GameRegion.China,
            State = GameState.NotInstalled
        },
        new()
        {
            Id = "gi-cn",
            Name = "yuanshen",
            DisplayName = "Genshin Impact",
            GameType = GameType.GenshinImpact,
            Region = GameRegion.China,
            State = GameState.NotInstalled
        },
        new()
        {
            Id = "hsr-cn",
            Name = "starrail",
            DisplayName = "Honkai: Star Rail",
            GameType = GameType.HonkaiStarRail,
            Region = GameRegion.China,
            State = GameState.NotInstalled
        },
        new()
        {
            Id = "zzz-cn",
            Name = "zenless",
            DisplayName = "Zenless Zone Zero",
            GameType = GameType.ZenlessZoneZero,
            Region = GameRegion.China,
            State = GameState.NotInstalled
        }
    };

    private void InitializeKnownGames()
    {
        _games = GetAllKnownGames();
        SaveGames();
        Log.Information("Initialized with {Count} known games", _games.Count);
    }

    /// <summary>
    /// Get games filtered by region for a specific game type
    /// </summary>
    public IEnumerable<GameInfo> GetGamesByType(GameType gameType) =>
        _games.Where(g => g.GameType == gameType);

    /// <summary>
    /// Get game by type and region
    /// </summary>
    public GameInfo? GetGameByTypeAndRegion(GameType gameType, GameRegion region) =>
        _games.FirstOrDefault(g => g.GameType == gameType && g.Region == region);

    public GameInfo? GetGame(string id) => _games.FirstOrDefault(g => g.Id == id);

    public void AddGame(GameInfo game)
    {
        if (_games.Any(g => g.Id == game.Id))
        {
            Log.Warning("Game {Id} already exists", game.Id);
            return;
        }

        _games.Add(game);
        SaveGames();
        GamesListChanged?.Invoke(this, EventArgs.Empty);
        Log.Information("Added game: {Name} ({Id})", game.DisplayName, game.Id);
    }

    public void RemoveGame(string id)
    {
        var game = _games.FirstOrDefault(g => g.Id == id);
        if (game != null)
        {
            _games.Remove(game);
            SaveGames();
            GamesListChanged?.Invoke(this, EventArgs.Empty);
            Log.Information("Removed game: {Name} ({Id})", game.DisplayName, id);
        }
    }

    public void UpdateGameState(string id, GameState state)
    {
        var game = _games.FirstOrDefault(g => g.Id == id);
        if (game != null)
        {
            game.State = state;
            SaveGames();
            GameStateChanged?.Invoke(this, game);
            Log.Information("Game {Id} state changed to {State}", id, state);
        }
    }

    public void UpdateGameInstallPath(string id, string installPath)
    {
        var game = _games.FirstOrDefault(g => g.Id == id);
        if (game != null)
        {
            game.InstallPath = installPath;
            // Check if both directory exists AND game executable is present
            game.IsInstalled = Directory.Exists(installPath) && IsGameDirectory(installPath, game);
            game.State = game.IsInstalled ? GameState.Ready : GameState.NotInstalled;
            SaveGames();
            GameStateChanged?.Invoke(this, game);
            Log.Information("Game {Id} install path set to {Path}, IsInstalled: {IsInstalled}", id, installPath, game.IsInstalled);
        }
    }

    public async Task ScanForInstalledGamesAsync()
    {
        Log.Information("Scanning for installed games...");

        var searchPaths = new List<string>
        {
            _settingsService.Settings.DefaultGameInstallPath ?? "",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Games"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "steam", "steamapps", "common")
        };

        searchPaths.AddRange(_settingsService.Settings.GameInstallPaths);
        searchPaths = searchPaths.Where(p => !string.IsNullOrEmpty(p) && Directory.Exists(p)).Distinct().ToList();

        foreach (var basePath in searchPaths)
        {
            await ScanDirectoryForGamesAsync(basePath);
        }

        SaveGames();
        GamesListChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task ScanDirectoryForGamesAsync(string basePath)
    {
        return Task.Run(() =>
        {
            try
            {
                var directories = Directory.GetDirectories(basePath);
                foreach (var dir in directories)
                {
                    var dirName = Path.GetFileName(dir).ToLowerInvariant();

                    // Check for known game signatures
                    foreach (var game in _games.Where(g => !g.IsInstalled))
                    {
                        if (IsGameDirectory(dir, game))
                        {
                            game.InstallPath = dir;
                            game.IsInstalled = true;
                            game.State = GameState.Ready;
                            Log.Information("Found installed game: {Name} at {Path}", game.DisplayName, dir);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Error scanning directory {Path}", basePath);
            }
        });
    }

    private bool IsGameDirectory(string path, GameInfo game)
    {
        // Check for game-specific executables or config files
        return game.GameType switch
        {
            GameType.HonkaiImpact3rd => File.Exists(Path.Combine(path, "BH3.exe")) ||
                                        File.Exists(Path.Combine(path, "Games", "BH3.exe")),
            GameType.GenshinImpact => File.Exists(Path.Combine(path, "GenshinImpact.exe")) ||
                                      File.Exists(Path.Combine(path, "YuanShen.exe")),
            GameType.HonkaiStarRail => File.Exists(Path.Combine(path, "StarRail.exe")),
            GameType.ZenlessZoneZero => File.Exists(Path.Combine(path, "ZenlessZoneZero.exe")),
            _ => false
        };
    }
}
