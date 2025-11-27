using System.Text.Json;
using System.Text.RegularExpressions;
using Linlapse.Models;
using Serilog;

namespace Linlapse.Services;

/// <summary>
/// Service for managing game-specific graphics and audio settings
/// </summary>
public class GameSettingsService
{
    private readonly GameService _gameService;

    public GameSettingsService(GameService gameService)
    {
        _gameService = gameService;
    }

    /// <summary>
    /// Get current graphics settings for a game
    /// </summary>
    public async Task<GraphicsSettings?> GetGraphicsSettingsAsync(string gameId)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            return null;
        }

        return await Task.Run(() =>
        {
            try
            {
                var configPath = GetConfigPath(game);
                if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
                    return new GraphicsSettings();

                var content = File.ReadAllText(configPath);
                return ParseGraphicsSettings(game.GameType, content);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read graphics settings for {GameId}", gameId);
                return new GraphicsSettings();
            }
        });
    }

    /// <summary>
    /// Set graphics settings for a game
    /// </summary>
    public async Task<bool> SetGraphicsSettingsAsync(string gameId, GraphicsSettings settings)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                var configPath = GetConfigPath(game);
                if (string.IsNullOrEmpty(configPath))
                    return false;

                var directory = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var content = GenerateGraphicsConfig(game.GameType, settings);
                File.WriteAllText(configPath, content);
                
                Log.Information("Graphics settings saved for {GameId}", gameId);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save graphics settings for {GameId}", gameId);
                return false;
            }
        });
    }

    /// <summary>
    /// Get current audio settings for a game
    /// </summary>
    public async Task<AudioSettings?> GetAudioSettingsAsync(string gameId)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            return null;
        }

        return await Task.Run(() =>
        {
            try
            {
                var configPath = GetAudioConfigPath(game);
                if (string.IsNullOrEmpty(configPath) || !File.Exists(configPath))
                    return new AudioSettings();

                var content = File.ReadAllText(configPath);
                return ParseAudioSettings(game.GameType, content);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to read audio settings for {GameId}", gameId);
                return new AudioSettings();
            }
        });
    }

    /// <summary>
    /// Set audio settings for a game
    /// </summary>
    public async Task<bool> SetAudioSettingsAsync(string gameId, AudioSettings settings)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                var configPath = GetAudioConfigPath(game);
                if (string.IsNullOrEmpty(configPath))
                    return false;

                var directory = Path.GetDirectoryName(configPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                var content = GenerateAudioConfig(game.GameType, settings);
                File.WriteAllText(configPath, content);
                
                Log.Information("Audio settings saved for {GameId}", gameId);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to save audio settings for {GameId}", gameId);
                return false;
            }
        });
    }

    /// <summary>
    /// Get available voice language packs for a game
    /// </summary>
    public async Task<List<VoicePackInfo>> GetVoicePacksAsync(string gameId)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            return new List<VoicePackInfo>();
        }

        var voicePacks = new List<VoicePackInfo>();
        var allLanguages = new[] 
        { 
            ("en", "English"),
            ("ja", "Japanese"),
            ("ko", "Korean"),
            ("zh-cn", "Chinese (Simplified)")
        };

        await Task.Run(() =>
        {
            var voicePath = GetVoicePackPath(game);
            if (string.IsNullOrEmpty(voicePath))
                return;

            foreach (var (code, name) in allLanguages)
            {
                var langPath = Path.Combine(voicePath, GetVoiceFolderName(game.GameType, code));
                var isInstalled = Directory.Exists(langPath);
                long size = 0;

                if (isInstalled)
                {
                    try
                    {
                        size = new DirectoryInfo(langPath)
                            .EnumerateFiles("*", SearchOption.AllDirectories)
                            .Sum(f => f.Length);
                    }
                    catch { }
                }

                voicePacks.Add(new VoicePackInfo
                {
                    LanguageCode = code,
                    LanguageName = name,
                    IsInstalled = isInstalled,
                    InstalledSize = size
                });
            }
        });

        return voicePacks;
    }

    /// <summary>
    /// Delete a voice pack to free up space
    /// </summary>
    public async Task<bool> DeleteVoicePackAsync(string gameId, string languageCode)
    {
        var game = _gameService.GetGame(gameId);
        if (game == null || !game.IsInstalled || string.IsNullOrEmpty(game.InstallPath))
        {
            return false;
        }

        return await Task.Run(() =>
        {
            try
            {
                var voicePath = GetVoicePackPath(game);
                if (string.IsNullOrEmpty(voicePath))
                    return false;

                var langPath = Path.Combine(voicePath, GetVoiceFolderName(game.GameType, languageCode));
                if (!Directory.Exists(langPath))
                    return false;

                Directory.Delete(langPath, recursive: true);
                Log.Information("Deleted voice pack {Language} for {GameId}", languageCode, gameId);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to delete voice pack {Language} for {GameId}", languageCode, gameId);
                return false;
            }
        });
    }

    private string? GetConfigPath(GameInfo game)
    {
        return game.GameType switch
        {
            GameType.HonkaiImpact3rd => Path.Combine(game.InstallPath, "Games", "BH3_Data", "config.ini"),
            GameType.GenshinImpact => GetGenshinConfigPath(game),
            GameType.HonkaiStarRail => GetStarRailConfigPath(game),
            GameType.ZenlessZoneZero => GetZZZConfigPath(game),
            _ => null
        };
    }

    private string GetGenshinConfigPath(GameInfo game)
    {
        // Genshin stores config in registry on Windows, we'll use a local file
        var dataFolder = game.Region == GameRegion.China ? "YuanShen_Data" : "GenshinImpact_Data";
        return Path.Combine(game.InstallPath, dataFolder, "Persistent", "config.json");
    }

    private string GetStarRailConfigPath(GameInfo game)
    {
        return Path.Combine(game.InstallPath, "StarRail_Data", "Persistent", "GraphicsSettings.json");
    }

    private string GetZZZConfigPath(GameInfo game)
    {
        return Path.Combine(game.InstallPath, "ZenlessZoneZero_Data", "Persistent", "LocalStorage", "GraphicsSettings.json");
    }

    private string? GetAudioConfigPath(GameInfo game)
    {
        return game.GameType switch
        {
            GameType.HonkaiImpact3rd => Path.Combine(game.InstallPath, "Games", "BH3_Data", "config.ini"),
            GameType.GenshinImpact => GetGenshinConfigPath(game),
            GameType.HonkaiStarRail => Path.Combine(game.InstallPath, "StarRail_Data", "Persistent", "AudioSettings.json"),
            GameType.ZenlessZoneZero => Path.Combine(game.InstallPath, "ZenlessZoneZero_Data", "Persistent", "LocalStorage", "AudioSettings.json"),
            _ => null
        };
    }

    private string? GetVoicePackPath(GameInfo game)
    {
        return game.GameType switch
        {
            GameType.GenshinImpact => Path.Combine(game.InstallPath, 
                game.Region == GameRegion.China ? "YuanShen_Data" : "GenshinImpact_Data", 
                "StreamingAssets", "AudioAssets"),
            GameType.HonkaiStarRail => Path.Combine(game.InstallPath, "StarRail_Data", "Persistent", "Audio"),
            GameType.ZenlessZoneZero => Path.Combine(game.InstallPath, "ZenlessZoneZero_Data", "StreamingAssets", "Audio"),
            _ => null
        };
    }

    private string GetVoiceFolderName(GameType gameType, string languageCode)
    {
        return gameType switch
        {
            GameType.GenshinImpact => languageCode switch
            {
                "en" => "English(US)",
                "ja" => "Japanese",
                "ko" => "Korean",
                "zh-cn" => "Chinese",
                _ => languageCode
            },
            _ => languageCode
        };
    }

    private GraphicsSettings ParseGraphicsSettings(GameType gameType, string content)
    {
        var settings = new GraphicsSettings();

        try
        {
            if (gameType == GameType.HonkaiImpact3rd)
            {
                // INI format parsing
                var widthMatch = Regex.Match(content, @"Screenmanager Resolution Width_h\d+\s*=\s*(\d+)");
                var heightMatch = Regex.Match(content, @"Screenmanager Resolution Height_h\d+\s*=\s*(\d+)");
                var fullscreenMatch = Regex.Match(content, @"Screenmanager Fullscreen mode_h\d+\s*=\s*(\d+)");

                if (widthMatch.Success) settings.ResolutionWidth = int.Parse(widthMatch.Groups[1].Value);
                if (heightMatch.Success) settings.ResolutionHeight = int.Parse(heightMatch.Groups[1].Value);
                if (fullscreenMatch.Success) settings.Fullscreen = fullscreenMatch.Groups[1].Value == "1";
            }
            else
            {
                // JSON format parsing
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.TryGetProperty("graphicsData", out var graphicsData) || 
                    root.TryGetProperty("GraphicsSettings", out graphicsData))
                {
                    if (graphicsData.TryGetProperty("ResolutionWidth", out var width))
                        settings.ResolutionWidth = width.GetInt32();
                    if (graphicsData.TryGetProperty("ResolutionHeight", out var height))
                        settings.ResolutionHeight = height.GetInt32();
                    if (graphicsData.TryGetProperty("Fullscreen", out var fullscreen))
                        settings.Fullscreen = fullscreen.GetBoolean();
                    if (graphicsData.TryGetProperty("VSync", out var vsync))
                        settings.VSync = vsync.GetBoolean();
                    if (graphicsData.TryGetProperty("FPSLimit", out var fps))
                        settings.FpsLimit = fps.GetInt32();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error parsing graphics settings");
        }

        return settings;
    }

    private AudioSettings ParseAudioSettings(GameType gameType, string content)
    {
        var settings = new AudioSettings();

        try
        {
            if (gameType == GameType.HonkaiImpact3rd)
            {
                // INI format
                var masterMatch = Regex.Match(content, @"Audio_Volume_h\d+\s*=\s*(\d+(?:\.\d+)?)");
                if (masterMatch.Success)
                    settings.MasterVolume = (int)(float.Parse(masterMatch.Groups[1].Value) * 100);
            }
            else
            {
                // JSON format
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.TryGetProperty("audioData", out var audioData) || 
                    root.TryGetProperty("AudioSettings", out audioData))
                {
                    if (audioData.TryGetProperty("MasterVolume", out var master))
                        settings.MasterVolume = (int)(master.GetDouble() * 100);
                    if (audioData.TryGetProperty("MusicVolume", out var music))
                        settings.MusicVolume = (int)(music.GetDouble() * 100);
                    if (audioData.TryGetProperty("SFXVolume", out var sfx))
                        settings.SfxVolume = (int)(sfx.GetDouble() * 100);
                    if (audioData.TryGetProperty("VoiceVolume", out var voice))
                        settings.VoiceVolume = (int)(voice.GetDouble() * 100);
                    if (audioData.TryGetProperty("VoiceLanguage", out var lang))
                        settings.VoiceLanguage = lang.GetString() ?? "en";
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error parsing audio settings");
        }

        return settings;
    }

    private string GenerateGraphicsConfig(GameType gameType, GraphicsSettings settings)
    {
        if (gameType == GameType.HonkaiImpact3rd)
        {
            return $@"[General]
Screenmanager Resolution Width_h182942802={settings.ResolutionWidth}
Screenmanager Resolution Height_h2627697771={settings.ResolutionHeight}
Screenmanager Fullscreen mode_h3630240806={(settings.Fullscreen ? 1 : 0)}
";
        }

        return JsonSerializer.Serialize(new
        {
            graphicsData = new
            {
                ResolutionWidth = settings.ResolutionWidth,
                ResolutionHeight = settings.ResolutionHeight,
                Fullscreen = settings.Fullscreen,
                Borderless = settings.Borderless,
                VSync = settings.VSync,
                FPSLimit = settings.FpsLimit
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private string GenerateAudioConfig(GameType gameType, AudioSettings settings)
    {
        if (gameType == GameType.HonkaiImpact3rd)
        {
            return $@"[Audio]
Audio_Volume_h182942802={settings.MasterVolume / 100.0f}
";
        }

        return JsonSerializer.Serialize(new
        {
            audioData = new
            {
                MasterVolume = settings.MasterVolume / 100.0,
                MusicVolume = settings.MusicVolume / 100.0,
                SFXVolume = settings.SfxVolume / 100.0,
                VoiceVolume = settings.VoiceVolume / 100.0,
                VoiceLanguage = settings.VoiceLanguage
            }
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}

/// <summary>
/// Voice pack information
/// </summary>
public class VoicePackInfo
{
    public string LanguageCode { get; set; } = string.Empty;
    public string LanguageName { get; set; } = string.Empty;
    public bool IsInstalled { get; set; }
    public long InstalledSize { get; set; }
    public long DownloadSize { get; set; }
}
