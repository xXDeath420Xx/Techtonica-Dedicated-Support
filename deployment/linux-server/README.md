# Techtonica Dedicated Server - Linux Installation

**Status: WORKING!** - Successfully tested January 2026

Run a 24/7 Techtonica dedicated server on Linux using Wine.

## Quick Start (Automated Installation)

```bash
# Download and extract the server package
unzip TechtonicaDedicatedServer-Linux.zip
cd TechtonicaDedicatedServer-Linux

# Run the installer
./install.sh

# Copy your game files
# (See "Game Files" section below)

# Start the server
~/techtonica-server/start-server.sh
```

## Prerequisites

### Required Packages

**Ubuntu/Debian:**
```bash
sudo dpkg --add-architecture i386
sudo apt update
sudo apt install wine wine64 xvfb unzip wget
```

**Fedora:**
```bash
sudo dnf install wine xorg-x11-server-Xvfb unzip wget
```

**Arch Linux:**
```bash
sudo pacman -S wine xorg-server-xvfb unzip wget
```

### Hardware Requirements

- **CPU:** 2+ cores recommended
- **RAM:** 4GB minimum, 8GB recommended
- **Disk:** 15GB for game files + saves
- **Network:** Stable connection, port 6968 UDP open

## Game Files

You need a legitimate copy of Techtonica to run the server.

1. On a Windows machine with Steam:
   - Right-click Techtonica in your library
   - Properties > Local Files > Browse

2. Copy these files/folders to `~/techtonica-server/game/Techtonica/`:
   - `Techtonica.exe`
   - `Techtonica_Data/` (entire folder)
   - `MonoBleedingEdge/` (entire folder)
   - `UnityCrashHandler64.exe`
   - `UnityPlayer.dll`
   - Any other DLL files in the root

## Manual Installation

If you prefer manual installation:

### 1. Install BepInEx

```bash
# Download BepInEx
wget https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip

# Extract to game directory
unzip BepInEx_win_x64_5.4.23.2.zip -d ~/techtonica-server/game/Techtonica/
```

### 2. Install Server Mods

```bash
# Copy preloader (for Steam bypass)
cp mods/TechtonicaPreloader.dll ~/techtonica-server/game/Techtonica/BepInEx/patchers/

# Copy dedicated server plugin
cp mods/TechtonicaDedicatedServer.dll ~/techtonica-server/game/Techtonica/BepInEx/plugins/
```

### 3. Initialize Wine

```bash
export WINEPREFIX=~/techtonica-server/wine/prefix
export WINEARCH=win64
wineboot --init
```

### 4. Create Startup Script

```bash
#!/bin/bash
export DISPLAY=:98
export WINEPREFIX=~/techtonica-server/wine/prefix
export WINEDLLOVERRIDES="winhttp=n,b"

# Start virtual display
Xvfb :98 -screen 0 1024x768x24 &
sleep 2

cd ~/techtonica-server/game/Techtonica
wine Techtonica.exe -batchmode
```

## Configuration

Edit `BepInEx/config/com.community.techtonicadedicatedserver.cfg`:

```ini
[Server]
EnableDirectConnect = true
ServerPort = 6968
MaxPlayers = 8
AutoStart = true
AutoLoadSavePath = /path/to/your/save.dat

[Debug]
VerboseLogging = false
```

### Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| EnableDirectConnect | true | Enable direct IP connections |
| ServerPort | 6968 | UDP port for game traffic |
| MaxPlayers | 8 | Maximum concurrent players |
| AutoStart | true | Auto-start server on game launch |
| AutoLoadSavePath | (empty) | Path to save file to load on start |

## Running as a Service

To run the server automatically on system boot:

```bash
# Copy service file
sudo cp techtonica-server.service /etc/systemd/system/

# Enable and start
sudo systemctl enable techtonica-server
sudo systemctl start techtonica-server

# Check status
sudo systemctl status techtonica-server

# View logs
journalctl -u techtonica-server -f
```

## Firewall Configuration

Open UDP port 6968:

**UFW (Ubuntu):**
```bash
sudo ufw allow 6968/udp
```

**firewalld (Fedora/CentOS):**
```bash
sudo firewall-cmd --permanent --add-port=6968/udp
sudo firewall-cmd --reload
```

**iptables:**
```bash
sudo iptables -A INPUT -p udp --dport 6968 -j ACCEPT
```

## Log Files

- **BepInEx Log:** `game/Techtonica/BepInEx/LogOutput.log`
- **Game Log:** `game.log`
- **Debug Log:** `debug.log`

## Troubleshooting

### Server won't start

1. Check Wine is working:
   ```bash
   wine --version
   ```

2. Verify game files are complete:
   ```bash
   ls -la ~/techtonica-server/game/Techtonica/Techtonica.exe
   ```

3. Check BepInEx is installed:
   ```bash
   ls -la ~/techtonica-server/game/Techtonica/BepInEx/
   ```

### Clients can't connect

1. Verify server is running:
   ```bash
   ss -ulnp | grep 6968
   ```

2. Check firewall allows UDP 6968

3. Verify clients have the DirectConnect mod installed

### High CPU usage

- This is normal for Wine-emulated games
- Consider using `nice` to lower priority:
  ```bash
  nice -n 19 ./start-server.sh
  ```

### "Steam not found" errors

- The preloader mod should bypass Steam
- Check `BepInEx/LogOutput.log` for "[SteamBypassPatcher]" messages
- Ensure `TechtonicaPreloader.dll` is in `BepInEx/patchers/`

## Admin Panel (Optional)

An admin web panel is available for server management:

```bash
cd admin-panel
npm install
npm start
```

Access at `http://your-server:6969`

## Support

- **GitHub:** https://github.com/xXDeath420Xx/Techtonica
- **Discord:** https://discord.com/invite/mJfbDgWA7z
- **Issues:** https://github.com/xXDeath420Xx/Techtonica/issues

## License

MIT License - See LICENSE file
