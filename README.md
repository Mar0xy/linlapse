# Linlapse

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Linux-green" alt="Platform"/>
  <img src="https://img.shields.io/badge/.NET-9.0-purple" alt=".NET Version"/>
  <img src="https://img.shields.io/badge/UI-Avalonia-blue" alt="UI Framework"/>
  <img src="https://img.shields.io/badge/License-MIT-orange" alt="License"/>
</p>

**Linlapse** is a Linux game launcher inspired by [Collapse Launcher](https://github.com/CollapseLauncher/Collapse), designed to manage and launch miHoYo/HoYoverse games on Linux using Wine/Proton.

## Features

- 🎮 **Multi-Game Support**: Manage Honkai Impact 3rd, Genshin Impact, Honkai: Star Rail, and Zenless Zone Zero
- 🐧 **Native Linux**: Built with Avalonia UI for a native Linux experience
- 🍷 **Wine Integration**: Seamlessly launch Windows games using Wine or Proton
- ⚙️ **Game Settings**: Configure game-specific launch options and Wine prefixes
- 📥 **Game Management**: Scan for installed games and manage installations
- 🎨 **Modern UI**: Dark-themed, modern interface inspired by Collapse Launcher
- 📝 **Logging**: Comprehensive logging for troubleshooting

## System Requirements

- **OS**: Linux (any distribution with GTK support)
- **Runtime**: .NET 9.0 Runtime
- **Wine**: Wine 7.0+ or Proton (for running Windows games)
- **Dependencies**: GTK3, libX11

## Installation

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

## Project Structure

```
linlapse/
├── src/
│   └── Linlapse/
│       ├── Models/           # Data models
│       ├── Services/         # Business logic services
│       ├── ViewModels/       # MVVM view models
│       ├── Views/            # Avalonia XAML views
│       └── Assets/           # Application resources
└── README.md
```

## Supported Games

| Game | Status |
|------|--------|
| Honkai Impact 3rd | ✅ Supported |
| Genshin Impact | ✅ Supported |
| Honkai: Star Rail | ✅ Supported |
| Zenless Zone Zero | ✅ Supported |

## Configuration

Settings are stored in:
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

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Disclaimer

This project is **NOT AFFILIATED** with miHoYo (miHoYo Co., Ltd.) or HoYoverse (COGNOSPHERE PTE. LTD.) by any means. This is an open-source community project.