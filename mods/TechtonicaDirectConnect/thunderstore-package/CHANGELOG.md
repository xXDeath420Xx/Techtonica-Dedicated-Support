# Changelog

## 1.0.39
- **FIX**: Use Time.unscaledDeltaTime instead of Time.deltaTime
- Game sets timeScale=0 during loading, making deltaTime=0
- unscaledDeltaTime ignores timeScale and works during loading!
- Timer should now properly increment and trigger OnFinishLoading after 3 seconds

## 1.0.38
- **DEBUG**: Added timer increment debug logging
- Shows before/after values and deltaTime to confirm increment is happening
- This will tell us if Time.deltaTime is 0 or if timer is being reset

## 1.0.37
- **FIX**: Moved timer increment outside else block
- Timer now increments every frame when isActive=true (not just when monitorActive=true)
- Added deltaTime to progress log for debugging
- This should fix the timer not incrementing issue

## 1.0.36
- **DEBUG**: Changed isActive detection to use _loaded field
- Now checks: gameObject active AND _loaded==false (still loading)
- Added logging every 60 frames showing goActive, _loaded, isActive, timer
- This will show why the timer keeps resetting

## 1.0.35
- **DEBUG**: Added HEARTBEAT log every 5 seconds in Update()
- Shows frame count, monitor state, timer, and finishCalled status
- This will confirm if Update() is still running after the NRE

## 1.0.34
- **DEBUG**: Added logging at every exit point in CheckLoadingMonitor
- Logs when NetworkClient becomes inactive
- Logs when LoadingUI instance becomes null
- Logs when loading screen becomes inactive
- Always logs exceptions (not suppressed)

## 1.0.33
- **DEBUG**: Improved logging frequency (every second instead of every 2)
- Added logging when loading screen becomes INACTIVE (to understand why monitor stops)
- Reduced timeout to 3 seconds (was 5)
- Fixed second-tracking logic for progress logs

## 1.0.32
- **DEBUG**: Added comprehensive logging to loading monitor
- Tries multiple ways to find LoadingUI instance (field, property, FindObjectOfType)
- Lists all available fields/methods when reflection fails
- Logs loading progress every 2 seconds
- Starts monitoring immediately when loading screen is active (not waiting for specific state)

## 1.0.31
- **FIX**: Fixed AccessTools.Property -> AccessTools.Field for LoadingUI.instance
- LoadingUI.instance is a field, not a property - was causing reflection to fail
- Both TryCallOnFinishLoading() and CheckLoadingMonitor() now use correct reflection
- Updated version in PluginInfo to match package version

## 1.0.30
- **FIX**: Added loading monitor that detects stuck loading screen
- Automatically calls LoadingUI.OnFinishLoading() after 5 seconds if stuck on "Generating Machines"
- Properly handles NullReferenceException in NetworkMessageRelay.instance (dedicated server mode)
- Monitor runs in Update() to catch the issue regardless of where the NRE occurs

## 1.0.29
- Added RequestCurrentSimTick_Prefix to handle null NetworkMessageRelay.instance
- Added RequestCurrentSimTick_Finalizer to catch exceptions and complete loading
- Calls LoadingUI.OnFinishLoading() directly when server doesn't have NetworkMessageRelay

## 1.0.26
- **CRITICAL FIX**: Removed NetworkedPlayer patches that were breaking save sync!
- Client was skipping OnStartLocalPlayer which prevented RequestInitialSaveData() from being called
- Server was never receiving the client's request for world data (causing "Timing out for strata")
- Now properly receives save/world data from server

## 1.0.25
- **CRITICAL FIX**: Set NetworkManager.networkAddress BEFORE calling JoinGameAsClient
- The game was trying to connect but didn't know where (caused timeout)
- Removed redundant ConnectAfterSceneLoad coroutine - game handles connection internally

## 1.0.24
- Patched FizzyFacepunch (Steam transport) to prevent NullReferenceException spam
- Disabled FizzyFacepunch.ClientEarlyUpdate/ClientLateUpdate/ServerEarlyUpdate/ServerLateUpdate
- Enables KCP transport before calling JoinGameAsClient to prevent Steam transport errors
- Now works without Steam networking active!

## 1.0.23
- Fixed FlowManager.JoinGameAsClient call - now calls it directly instead of via reflection
- Properly loads game scenes when connecting from main menu
- Removed unnecessary instance check that was causing "FlowManager.instance is null" error

## 1.0.22
- **NEW: "Join Multiplayer" button in main menu!** Uses the game's hidden button (no more F11 required)
- Patched MainMenuUI.RefreshHiddenButtonState to show the multiplayer button
- Patched MainMenuUI.JoinMultiplayerAsClient to show our connection dialog
- Added Assembly-CSharp reference for direct game type access
- F11 hotkey still works as a fallback

## 1.0.21
- Added join-from-main-menu support using FlowManager.JoinGameAsClient
- Pressing F11 and Connect from main menu now automatically loads game scenes
- Added DoConnect helper to reduce code duplication
- Added ConnectAfterSceneLoad and WaitForGameWorldAndConnect coroutines
- No longer requires loading a save first - connects like joining a friend!

## 1.0.20
- Added patch for NetworkMessageRelay.SendNetworkAction
- Suppresses NullReferenceException when quest system tries to send network actions
- Fixes error spam when playing in single player before connecting

## 1.0.19
- Fixed crash when connecting from main menu
- Simplified scene detection - just warns user instead of trying auto-load
- Clear message: "Load a game first, then press F11"

## 1.0.18
- Added scene detection - checks if you're in game world before connecting
- Logs all loaded scenes for debugging

## 1.0.17
- Added patches for NetworkedPlayer.OnStartClient() and OnStartLocalPlayer()
- These methods crash when connecting because the player spawns before scene objects exist
- Now patches 5 methods total to prevent NullReferenceException spam

## 1.0.16
- Added null safety patches to prevent error spam when connecting
- Patches NetworkedPlayer.Update() to skip (prevents NullReferenceException)
- Patches ThirdPersonDisplayAnimator.Update() to skip (prevents NullReferenceException)
- Patches ThirdPersonDisplayAnimator.UpdateSillyStuff() to skip (prevents NullReferenceException)
- These errors occurred because the game creates player objects before they're fully initialized

## 1.0.15
- Added NetworkClient.Ready() call after connection
- Added NetworkClient.AddPlayer() to request player spawning
- Increased connection timeout to 15 seconds
- Added detailed logging for connection/ready/spawn sequence

## 1.0.14
- Fixed double-toggle bug (OnGUI is called multiple times per frame)
- Added frame guard to ensure UI only toggles once per key press
- Event.current now properly detected and working!

## 1.0.13
- Complete rewrite following ConsoleCommands mod pattern
- Update/OnGUI now run directly on plugin (like working mods)
- Added [BepInProcess("Techtonica.exe")] attribute
- Added HideFlags.HideAndDontSave to persist gameObject
- Triple input detection: Unity Input, Windows API, and Event.current
- Removed separate MonoBehaviour (was not initializing properly)

## 1.0.12
- Added robust logging for DirectConnectBehaviour lifecycle (Awake, Start)
- Added fallback F11 detection via Unity's Event.current in OnGUI
- Added error handling to prevent silent failures
- Fixed potential null reference issues in Update/OnGUI

## 1.0.11
- Updated changelog with all version history

## 1.0.10
- Updated mod icon

## 1.0.9
- Fixed Update/OnGUI not being called by creating dedicated MonoBehaviour
- Changed keybind from F8 to F11 (F1-F10 are used in-game)

## 1.0.8
- Added debug logging to diagnose input detection issues

## 1.0.7
- Added CHANGELOG.md to package

## 1.0.6
- Attempted changelog fix

## 1.0.5
- Fixed input detection for games using Rewired input system
- Now uses Windows API (GetAsyncKeyState) to detect key presses
- Works regardless of game's input system

## 1.0.4
- Added alternative input detection via OnGUI events
- Added debug logging for UI toggle

## 1.0.3
- Version bump to force Thunderstore cache refresh

## 1.0.2
- Added missing kcp2k.dll dependency (fixes mod not loading)

## 1.0.1
- Updated README with server hosting information
- Fixed GitHub repository links

## 1.0.0
- Initial release
- Direct IP connection to Techtonica dedicated servers
- In-game UI with F8 hotkey
- KCP transport for reliable UDP connections
- Auto-saves last connected server
