# Techtonica Dedicated Server - Windows Installation

**Status: WORKING!** - Successfully tested January 2026

Run a Techtonica dedicated server on Windows (Home PC or Windows Server).

## Quick Start

1. Extract the server package to a folder (e.g., `C:\TechtonicaServer\`)
2. Copy your Techtonica game files to the `Techtonica` subfolder
3. Run `install.bat` as Administrator
4. Run `start-server.bat` to start the server

## Requirements

- **Windows 10/11** or **Windows Server 2016+**
- **Techtonica game files** (from Steam)
- **.NET Framework 4.7.2** or higher (usually pre-installed)
- **4GB+ RAM**
- **Port 6968 UDP** open in firewall

## Installation

### Step 1: Extract Package

Extract the ZIP file to your desired location:
- `C:\TechtonicaServer\`
- `D:\Games\TechtonicaServer\`

### Step 2: Copy Game Files

Copy your Techtonica game files from Steam:

1. Open Steam Library
2. Right-click Techtonica > Properties > Local Files > Browse
3. Copy ALL files to `<Server>\Techtonica\`:
   - `Techtonica.exe`
   - `Techtonica_Data\` (folder)
   - `MonoBleedingEdge\` (folder)
   - `UnityCrashHandler64.exe`
   - `UnityPlayer.dll`
   - All `.dll` files in root

### Step 3: Run Installer

Double-click `install.bat` (run as Administrator if needed)

The installer will:
- Install BepInEx mod framework
- Install server mods
- Create default configuration
- Create startup scripts

### Step 4: Configure Server

Edit `Techtonica\BepInEx\config\com.community.techtonicadedicatedserver.cfg`:

```ini
[Server]
EnableDirectConnect = true
ServerPort = 6968
MaxPlayers = 8
AutoStart = true
AutoLoadSavePath = C:\TechtonicaServer\saves\mysave.dat

[Debug]
VerboseLogging = false
```

### Step 5: Configure Firewall

Open UDP port 6968:

**Option A: Windows Defender Firewall**
```powershell
netsh advfirewall firewall add rule name="Techtonica Server" dir=in action=allow protocol=UDP localport=6968
```

**Option B: GUI**
1. Windows Defender Firewall > Advanced Settings
2. Inbound Rules > New Rule
3. Port > UDP > 6968 > Allow > All profiles
4. Name: "Techtonica Server"

### Step 6: Start Server

Double-click `start-server.bat`

Or run in background: `start-server-background.bat`

## Configuration Options

| Option | Default | Description |
|--------|---------|-------------|
| EnableDirectConnect | true | Enable direct IP connections |
| ServerPort | 6968 | UDP port for game traffic |
| MaxPlayers | 8 | Maximum concurrent players |
| AutoStart | true | Auto-start server on game launch |
| AutoLoadSavePath | (empty) | Full path to save file |
| VerboseLogging | false | Enable detailed logging |

## Running as Windows Service

To run the server as a Windows service:

### Using NSSM (Non-Sucking Service Manager)

1. Download NSSM: https://nssm.cc/download
2. Extract and open Command Prompt as Admin:

```cmd
nssm install TechtonicaServer "C:\TechtonicaServer\start-server.bat"
nssm set TechtonicaServer DisplayName "Techtonica Dedicated Server"
nssm set TechtonicaServer Start SERVICE_AUTO_START
nssm start TechtonicaServer
```

To manage the service:
```cmd
nssm status TechtonicaServer
nssm stop TechtonicaServer
nssm restart TechtonicaServer
```

### Using Task Scheduler

1. Open Task Scheduler
2. Create Basic Task
3. Trigger: At startup
4. Action: Start a program
5. Program: `C:\TechtonicaServer\start-server-background.bat`
6. Check "Run with highest privileges"

## Folder Structure

```
TechtonicaServer\
├── Techtonica\           # Game files
│   ├── Techtonica.exe
│   ├── Techtonica_Data\
│   ├── BepInEx\
│   │   ├── config\       # Configuration files
│   │   ├── plugins\      # Server plugin
│   │   ├── patchers\     # Preloader patcher
│   │   └── LogOutput.log # BepInEx log
│   └── ...
├── mods\                 # Mod DLLs (installer copies these)
├── saves\                # Save files
├── config\               # Additional configs
├── server.log            # Server output log
├── install.bat           # Installation script
├── start-server.bat      # Start script
└── README.md             # This file
```

## Log Files

- **BepInEx Log:** `Techtonica\BepInEx\LogOutput.log`
- **Server Log:** `server.log`

Check these for errors or connection issues.

## Port Forwarding (Home Networks)

If running on a home network behind a router:

1. Access your router admin page (usually 192.168.1.1)
2. Find Port Forwarding / NAT settings
3. Add rule:
   - Service Port: 6968
   - Protocol: UDP
   - Internal IP: Your PC's local IP
   - Internal Port: 6968

Find your local IP:
```cmd
ipconfig | findstr IPv4
```

## Connecting to Your Server

Players need:
1. The **TechtonicaDirectConnect** mod (from Thunderstore/r2modman)
2. Your server's IP address
3. Port number (default: 6968)

**Public IP:** Google "what is my ip" or use whatismyip.com
**LAN IP:** Use `ipconfig` command

Give players: `your.ip.address:6968`

## Troubleshooting

### Server won't start

1. Check game files are complete
2. Run install.bat again
3. Check BepInEx\LogOutput.log for errors
4. Try running as Administrator

### "Steam not found" errors

- The preloader mod bypasses Steam
- Check that `TechtonicaPreloader.dll` is in `BepInEx\patchers\`
- Look for "[SteamBypassPatcher]" in logs

### Players can't connect

1. Verify server is running (window shows "Server started")
2. Check Windows Firewall allows port 6968 UDP
3. Verify port forwarding if behind router
4. Ensure players have DirectConnect mod
5. Try connecting from same LAN first

### High RAM/CPU usage

- Normal for game servers
- Close other applications
- Consider dedicated hardware for 24/7 operation

### Server crashes on load

- Verify save file isn't corrupted
- Try starting without AutoLoadSavePath
- Check logs for specific error

## Multiple Servers

To run multiple servers:

1. Copy entire folder to new location
2. Change `ServerPort` in config (e.g., 6969, 6970)
3. Open additional firewall ports
4. Run each `start-server.bat` separately

## Updating

1. Stop the server
2. Backup `BepInEx\config\` folder
3. Copy new mod DLLs to `mods\` folder
4. Run `install.bat` again
5. Restore any custom config
6. Start server

## Support

- **GitHub:** https://github.com/xXDeath420Xx/Techtonica
- **Discord:** https://discord.com/invite/mJfbDgWA7z
- **Issues:** https://github.com/xXDeath420Xx/Techtonica/issues

## License

MIT License - See LICENSE file
