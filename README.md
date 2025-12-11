# Linlapse

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Linux-green" alt="Platform"/>
  <img src="https://img.shields.io/badge/.NET-9.0-purple" alt=".NET Version"/>
  <img src="https://img.shields.io/badge/UI-Avalonia-blue" alt="UI Framework"/>
  <img src="https://img.shields.io/badge/License-MIT-orange" alt="License"/>
</p>

**Linlapse** is a Linux game launcher inspired by [Collapse Launcher](https://github.com/CollapseLauncher/Collapse), designed to manage and launch Windows games on Linux using Wine/Proton. Currently supports HoYoverse games (Genshin Impact, Honkai: Star Rail, Zenless Zone Zero, and Honkai Impact 3rd), with an extensible architecture for adding support for other game publishers.

## Features

### Game Management
- 🎮 **Multi-Game Support**: Manage multiple games from various publishers
- 🏢 **HoYoverse Games**: Full support for Honkai Impact 3rd, Genshin Impact, Honkai: Star Rail, and Zenless Zone Zero
- 🔍 **Auto-Detection**: Automatically scan and detect installed games
- 📊 **Game Status Tracking**: Track installation state, version, and play time
- 🔧 **Extensible Architecture**: Easy to add support for games from other publishers

### Download & Installation
- 📥 **Game Downloads**: Download games directly from official publisher APIs
- 🚀 **Multi-Session Downloads**: Fast parallel downloads with resume support
- 📦 **Archive Extraction**: Support for ZIP and other archive formats
- ⏸️ **Pause/Resume/Cancel**: Full control over downloads with cancellation support
- 🔄 **Speed Limiting**: Configurable download speed limits
- 🎙️ **Voice Pack Selection**: Choose voice language packs during installation (where supported)
- ✅ **Download Verification**: MD5 hash verification of downloaded files

### Game Repair & Verification
- 🔧 **File Verification**: Verify game file integrity using checksums
- 🩹 **Auto-Repair**: Automatically download and replace corrupted files
- 📋 **Manifest Support**: Parse and verify against game manifests (pkg_version)

### Update Management
- 🔄 **Update Checking**: Check for game updates via official publisher APIs
- 📥 **Delta Patches**: Support for smaller delta patch updates (where available)
- 📦 **Full Updates**: Fall back to full package downloads when needed
- ⏬ **Preloading**: Download upcoming updates before they release (where supported)

### Cache Management
- 🗑️ **Cache Clearing**: Clear game caches to free disk space
- 📊 **Cache Info**: View cache size and file count per game
- 🎯 **Selective Clearing**: Clear specific cache types (shader cache, web cache, etc.)

### Game Settings
- ⚙️ **Graphics Settings**: Configure resolution, fullscreen, VSync, FPS limit
- 🔊 **Audio Settings**: Adjust volume levels and voice language
- 🎙️ **Voice Packs**: Manage and delete voice language packs

### Linux Integration
- 🐧 **Native Linux**: Built with Avalonia UI for a native Linux experience
- 🍷 **Wine Integration**: Seamlessly launch Windows games using Wine or Proton
- 🔧 **Custom Wine Prefixes**: Use isolated Wine prefixes for each game
- 🌍 **Environment Variables**: Set custom environment variables per game

### User Interface
- 🎨 **Modern UI**: Dark-themed, modern interface inspired by Collapse Launcher
- 🖼️ **Dynamic Backgrounds**: Game-specific background images fetched from official APIs (where available)
- 🎬 **Video Background Support**: Framework ready for video backgrounds (requires LibVLC)
- 📝 **Status Updates**: Real-time progress and status information
- 📊 **Progress Tracking**: Visual progress bars for downloads and operations

## Architecture

Linlapse is built with an extensible architecture that separates game-specific logic from core functionality:

- **Game Configuration Service**: Centralized game configurations stored in JSON, making it easy to add new games
- **Company-Agnostic Services**: Core services (download, update, repair) work with any game
- **API Abstraction**: Publisher-specific APIs are configured per-game, not hardcoded
- **Modular Design**: Services are loosely coupled and can be extended independently

This design makes it straightforward to add support for games from other publishers beyond HoYoverse.

## System Requirements

- **OS**: Linux (any distribution with GTK support)
- **Runtime**: .NET 9.0 Runtime
- **Wine**: Wine 7.0+ or Proton (for running Windows games)
- **Dependencies**: GTK3, libX11
- **Optional**: libvlc (for video backgrounds)

## Installation

### AppImage (Recommended)

Download the latest AppImage from the [Releases](https://github.com/Mar0xy/linlapse/releases) page:

```bash
# Download AppImage
wget https://github.com/Mar0xy/linlapse/releases/latest/download/Linlapse-x86_64.AppImage

# Make it executable
chmod +x Linlapse-x86_64.AppImage

# Run
./Linlapse-x86_64.AppImage
```

### From Source

```bash
# Clone the repository
git clone https://github.com/Mar0xy/linlapse.git
cd linlapse

# Build the project
dotnet build src/Linlapse/Linlapse.csproj -c Release

# Run the application
dotnet run --project src/Linlapse/Linlapse.csproj
```

### Prerequisites

1. **Install .NET 9.0 SDK**:
   ```bash
   # Fedora/RHEL
   sudo dnf install dotnet-sdk-9.0

   # Ubuntu/Debian
   sudo apt-get install dotnet-sdk-9.0

   # Arch Linux
   sudo pacman -S dotnet-sdk
   ```

2. **Install Wine**:
   ```bash
   # Fedora
   sudo dnf install wine

   # Ubuntu/Debian
   sudo apt-get install wine

   # Arch Linux
   sudo pacman -S wine
   ```

3. **Install libvlc (optional, for video backgrounds)**:
   ```bash
   # Fedora
   sudo dnf install vlc-devel

   # Ubuntu/Debian
   sudo apt-get install libvlc-dev vlc

   # Arch Linux
   sudo pacman -S vlc
   ```

## Usage

1. **Launch Linlapse**
2. **Select a game** from the sidebar
3. **Configure game settings** if needed (install path, Wine settings)
4. **Click "Launch Game"** to start playing

### Setting Up Games

1. Install your games using their official installers (via Wine)
2. Click "Refresh Games" to scan for installed games
3. If a game isn't detected, you can manually set its install path

### Wine Configuration

Linlapse uses Wine to run Windows games. You can configure:

- **System Wine**: Use the system-installed Wine
- **Custom Wine Path**: Specify a custom Wine executable
- **Wine Prefix**: Use isolated Wine prefixes for each game
- **Environment Variables**: Set custom environment variables per game

### Game Features

| Feature | Honkai Impact 3rd | Genshin Impact | Star Rail | Zenless Zone Zero |
|---------|-------------------|----------------|-----------|-------------------|
| Launch | ✅ | ✅ | ✅ | ✅ |
| Update Check | ✅ | ✅ | ✅ | ✅ |
| File Repair | ✅ | ✅ | ✅ | ✅ |
| Cache Clear | ✅ | ✅ | ✅ | ✅ |
| Graphics Settings | ✅ | ✅ | ✅ | ✅ |
| Audio Settings | ✅ | ✅ | ✅ | ✅ |
| Voice Packs | ❌ | ✅ | ✅ | ✅ |

## Project Structure

```
linlapse/
├── src/
│   └── Linlapse/
│       ├── Models/           # Data models
│       │   ├── GameInfo.cs             # Game information model
│       │   ├── GameConfiguration.cs    # Game configuration model
│       │   ├── AppSettings.cs          # Application settings
│       │   └── DownloadProgress.cs     # Progress tracking models
│       ├── Services/         # Business logic services
│       │   ├── SettingsService.cs            # Settings management
│       │   ├── GameService.cs                # Game management
│       │   ├── GameConfigurationService.cs   # Game configurations from JSON
│       │   ├── GameLauncherService.cs        # Wine/game launching
│       │   ├── WineRunnerService.cs          # Wine/Proton runner detection
│       │   ├── DownloadService.cs            # Multi-session downloads
│       │   ├── GameDownloadService.cs        # Game download orchestration
│       │   ├── SophonDownloadService.cs      # Sophon protocol downloads
│       │   ├── InstallationService.cs        # Game installation
│       │   ├── RepairService.cs              # File verification/repair
│       │   ├── CacheService.cs               # Cache management
│       │   ├── UpdateService.cs              # Update checking/applying
│       │   ├── GameSettingsService.cs        # Graphics/audio settings
│       │   └── BackgroundService.cs          # Background image management
│       ├── ViewModels/       # MVVM view models
│       │   ├── MainWindowViewModel.cs        # Main window logic
│       │   ├── GameCardViewModel.cs          # Game card display
│       │   ├── GameSettingsViewModel.cs      # Game settings dialog
│       │   ├── SettingsViewModel.cs          # Application settings
│       │   └── WineRunnerDialogViewModel.cs  # Wine runner selection
│       ├── Views/            # Avalonia XAML views
│       │   ├── MainWindow.axaml              # Main window UI
│       │   └── Controls/
│       │       └── BackgroundPlayer.cs       # Video background player
│       ├── Converters/       # Value converters for UI bindings
│       ├── Assets/           # Application resources
│       └── Program.cs        # Application entry point
├── README.md
├── LICENSE
└── Linlapse.sln
```

## Configuration

Settings are stored in XDG-compliant directories:
- **Config**: `~/.config/linlapse/settings.json`
- **Data**: `~/.local/share/linlapse/`
- **Cache**: `~/.cache/linlapse/`
- **Logs**: `~/.local/share/linlapse/logs/`

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

1. Fork the repository
2. Create your feature branch (`git checkout -b feature/AmazingFeature`)
3. Commit your changes (`git commit -m 'Add some AmazingFeature'`)
4. Push to the branch (`git push origin feature/AmazingFeature`)
5. Open a Pull Request

## Acknowledgments

- [Collapse Launcher](https://github.com/CollapseLauncher/Collapse) - The original Windows launcher that inspired this project
- [Avalonia UI](https://avaloniaui.net/) - Cross-platform .NET UI framework
- [Wine](https://www.winehq.org/) - Enabling Windows games to run on Linux
- [Serilog](https://serilog.net/) - Structured logging for .NET
- [CommunityToolkit.Mvvm](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/) - MVVM framework

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Disclaimer

This project is **NOT AFFILIATED** with any game publishers or companies whose games are supported by this launcher. Linlapse is an open-source community project that provides a convenient way to manage and launch Windows games on Linux. All game content, trademarks, and copyrights belong to their respective owners.

Currently supported publishers:
- HoYoverse (COGNOSPHERE PTE. LTD.) / miHoYo Co., Ltd.