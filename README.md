# Techtonica Dedicated Server

**Status: WORKING!** - Successfully tested January 2026

Run 24/7 Techtonica multiplayer servers without Steam dependency.

[![Status](https://img.shields.io/badge/Status-WORKING-brightgreen)](https://github.com/xXDeath420Xx/Techtonica)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://opensource.org/licenses/MIT)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-blue)](https://github.com/xXDeath420Xx/Techtonica)

## Features

- **Steam-Free Hosting** - No Steam account required on server
- **Direct IP Connections** - Players connect via IP:Port
- **Cross-Platform** - Server runs on Windows or Linux (via Wine)
- **Headless Operation** - No GPU needed for dedicated hosting
- **Auto-Start** - Load saves and start hosting automatically
- **Web Admin Panel** - Manage server from browser

## Quick Start

### For Players (Connecting to Servers)

1. Install [r2modman](https://thunderstore.io/package/ebkr/r2modman/)
2. Search for "TechtonicaDirectConnect" and install
3. Click "Join Multiplayer" from main menu OR press F11 to connect to a server

### For Server Hosts

**Linux:**
```bash
wget https://github.com/xXDeath420Xx/Techtonica/releases/latest/download/TechtonicaDedicatedServer-Linux.zip
unzip TechtonicaDedicatedServer-Linux.zip && cd TechtonicaDedicatedServer-Linux
./install.sh
```

**Windows:**
1. Download `TechtonicaDedicatedServer-Windows.zip`
2. Extract and run `install.bat`
3. Run `start-server.bat`

## Documentation

See [DOCUMENTATION.md](DOCUMENTATION.md) for complete setup instructions.

## Downloads

| Package | Description |
|---------|-------------|
| [TechtonicaDirectConnect](https://thunderstore.io/c/techtonica/p/CertiFried/TechtonicaDirectConnect/) | Client mod for players (Thunderstore) |
| [TechtonicaDedicatedServer-Linux](deployment/linux-server/) | Linux server package |
| [TechtonicaDedicatedServer-Windows](deployment/windows-server/) | Windows server package |

## Requirements

**Server:**
- 4GB+ RAM
- Techtonica game files
- Port 6968 UDP open

**Client:**
- Techtonica (Steam)
- BepInEx 5.4.21+

## Project Structure

```
techtonica-server/
├── mods/
│   ├── TechtonicaPreloader/      # Steam bypass (patcher)
│   ├── TechtonicaDedicatedServer/ # Server plugin
│   └── TechtonicaDirectConnect/   # Client mod
├── admin-panel/                   # Web admin UI
├── deployment/
│   ├── linux-server/             # Linux package
│   └── windows-server/           # Windows package
├── saves/                        # Server saves
└── DOCUMENTATION.md              # Full documentation
```

## Configuration

Edit `BepInEx/config/com.community.techtonicadedicatedserver.cfg`:

```ini
[Server]
EnableDirectConnect = true
ServerPort = 6968
MaxPlayers = 8
AutoStart = true
AutoLoadSavePath = /path/to/save.dat
```

## Admin Panel

Access the web-based admin panel at `http://your-server:6969`

Default credentials:
- Username: `admin`
- Password: `TechtonicaAdmin2024!`

Features:
- Server status monitoring
- Start/stop/restart controls
- Log viewer
- Configuration editor
- Save management

## Ports

| Port | Protocol | Purpose |
|------|----------|---------|
| 6968 | UDP | Game traffic |
| 6969 | TCP | Admin panel |

## Support

- **Issues:** [GitHub Issues](https://github.com/xXDeath420Xx/Techtonica/issues)
- **Discord:** [CertiFried Community](https://discord.com/invite/mJfbDgWA7z)
- **Website:** [certifriedmultitool.com](https://certifriedmultitool.com)

## Credits

Developed by the **CertiFried Community**

- BepInEx Team - Mod framework
- Mirror Networking - Transport layer
- Techtonica Community - Testing

## License

MIT License - See [LICENSE](LICENSE)

---

*Requires legitimate Techtonica game ownership. Not affiliated with Fire Hose Games.*
