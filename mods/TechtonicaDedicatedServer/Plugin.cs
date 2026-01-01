using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;
using TechtonicaDedicatedServer.Networking;
using TechtonicaDedicatedServer.Patches;

namespace TechtonicaDedicatedServer
{
    /// <summary>
    /// Techtonica Dedicated Server Mod
    /// Enables direct IP connections and dedicated server hosting without Steam lobbies.
    /// </summary>
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Techtonica.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public static Plugin Instance { get; private set; }
        public static ManualLogSource Log { get; private set; }

        private Harmony _harmony;

        // Unity main thread synchronization context (captured during Awake)
        private static SynchronizationContext _unitySyncContext;

        // Configuration
        public static ConfigEntry<bool> EnableDirectConnect;
        public static ConfigEntry<int> ServerPort;
        public static ConfigEntry<int> MaxPlayers;
        public static ConfigEntry<string> ServerPassword;
        public static ConfigEntry<bool> HeadlessMode;
        public static ConfigEntry<bool> AutoStartServer;
        public static ConfigEntry<string> AutoLoadSave;
        public static ConfigEntry<int> AutoLoadSlot;
        public static ConfigEntry<string> ConnectAddress;
        public static ConfigEntry<string> PublicAddress;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // Capture Unity's synchronization context for thread-safe main thread execution
            _unitySyncContext = SynchronizationContext.Current;
            DebugLog($"[Plugin] Captured SynchronizationContext: {_unitySyncContext?.GetType().Name ?? "NULL"}");

            // Load configuration
            LoadConfig();

            // Apply Harmony patches
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);

            try
            {
                // Manually patch SteamPlatform constructor first (before PatchAll)
                PatchSteamPlatform();

                _harmony.PatchAll(Assembly.GetExecutingAssembly());
                Log.LogInfo($"[{PluginInfo.PLUGIN_NAME}] Harmony patches applied successfully!");
            }
            catch (Exception ex)
            {
                Log.LogError($"[{PluginInfo.PLUGIN_NAME}] Failed to apply Harmony patches: {ex}");
            }

            // Initialize the direct connect system
            DirectConnectManager.Initialize();

            // Register console commands
            ConsoleCommands.Register();

            // Apply headless patches if configured
            HeadlessPatches.ApplyPatches(_harmony);

            // Apply auto-start patches (hooks FlowManager.Start to trigger auto-load)
            AutoStartPatches.ApplyPatches(_harmony);

            // Initialize the ServerConnectionHandler (bypasses Harmony for save data sending)
            if (HeadlessMode.Value)
            {
                ServerConnectionHandler.Initialize();
            }

            Log.LogInfo($"[{PluginInfo.PLUGIN_NAME}] v{PluginInfo.PLUGIN_VERSION} loaded!");
            Log.LogInfo($"[{PluginInfo.PLUGIN_NAME}] Hotkeys: F8=Connect, F9=Host, F10=Status, F11=Stop");

            // Auto-start server if configured (for headless mode)
            if (AutoStartServer.Value)
            {
                Log.LogInfo($"[{PluginInfo.PLUGIN_NAME}] Auto-start enabled, will start server on port {ServerPort.Value}");
                DebugLog("[Awake] Auto-start enabled, setting up auto-load...");

                // Register for Unity's onBeforeRender callback - this fires every frame
                Application.onBeforeRender += OnBeforeRender;
                DebugLog("[Awake] Registered Application.onBeforeRender callback");

                // Start auto-load in a background thread (bypasses Unity coroutine issues)
                StartCoroutine(AutoLoadCoroutine());

                // Also try thread-based approach as backup
                var autoLoadThread = new Thread(AutoLoadThreadMethod);
                autoLoadThread.IsBackground = true;
                autoLoadThread.Start();
            }
        }

        private static int _beforeRenderCount = 0;
        private static bool _beforeRenderFirstCall = true;

        private static void OnBeforeRender()
        {
            _beforeRenderCount++;

            if (_beforeRenderFirstCall)
            {
                _beforeRenderFirstCall = false;
                DebugLog("[OnBeforeRender] FIRST CALL! Application.onBeforeRender is working!");
            }

            // Log every 300 calls
            if (_beforeRenderCount % 300 == 0)
            {
                DebugLog($"[OnBeforeRender] Called {_beforeRenderCount} times");
            }

            // Trigger auto-load after 15 seconds
            if (_threadTriggeredAutoLoad && !Instance._autoLoadStarted)
            {
                Instance._autoLoadStarted = true;
                DebugLog("[OnBeforeRender] Triggering auto-load from onBeforeRender!");
                AutoLoadManager.TryAutoLoad();
            }

            // Check for scheduled server start (Update() isn't being called in headless mode)
            Networking.AutoLoadManager.Update();

            // Check for player connections and send save data (InvokeRepeating doesn't work under Wine)
            Networking.ServerConnectionHandler.CheckFromCallback();
        }

        private void Start()
        {
            DebugLog("[Start] Plugin Start() method called - MonoBehaviour is active!");
            Log.LogInfo("[Start] Plugin Start() called - lifecycle methods should work now");
        }

        private static bool _firstUpdateLogged = false;

        private void AutoLoadThreadMethod()
        {
            try
            {
                DebugLog("[AutoLoad-Thread] Starting background thread for auto-load...");

                // Wait 15 seconds using actual thread sleep
                for (int i = 0; i < 15; i++)
                {
                    Thread.Sleep(1000);
                    if (i % 5 == 0)
                    {
                        DebugLog($"[AutoLoad-Thread] Waiting... {15 - i} seconds remaining");
                    }
                }

                DebugLog("[AutoLoad-Thread] Wait complete, posting to main thread via SynchronizationContext...");

                // Try posting to Unity's synchronization context
                if (_unitySyncContext != null)
                {
                    DebugLog("[AutoLoad-Thread] Using SynchronizationContext.Post()...");
                    _unitySyncContext.Post(_ =>
                    {
                        DebugLog("[AutoLoad-SyncContext] Executing on main thread via Post!");
                        AutoLoadManager.TryAutoLoad();
                    }, null);
                    DebugLog("[AutoLoad-Thread] Post() called successfully");

                    // Also set flag as failsafe - if Post() callback never executes,
                    // LateUpdate/FixedUpdate/OnGUI will trigger auto-load instead
                    _threadTriggeredAutoLoad = true;

                    // Note: Cannot call Unity APIs directly from background thread - will crash
                    // Relying on Update/OnGUI hooks to pick up _threadTriggeredAutoLoad flag
                    DebugLog("[AutoLoad-Thread] Flag set, waiting for main thread to pick it up...");
                }
                else
                {
                    DebugLog("[AutoLoad-Thread] WARNING: SynchronizationContext is null! Setting flag instead...");
                    _threadTriggeredAutoLoad = true;
                }
            }
            catch (Exception ex)
            {
                DebugLog($"[AutoLoad-Thread] Error: {ex.Message}");
            }
        }

        private static volatile bool _threadTriggeredAutoLoad = false;

        // Debug logging to file (bypasses BepInEx buffering)
        private static string _debugLogPath = "/home/death/techtonica-server/debug.log";

        public static void DebugLog(string message)
        {
            try
            {
                var line = $"[{DateTime.Now:HH:mm:ss}] {message}\n";
                File.AppendAllText(_debugLogPath, line);
                Log?.LogInfo(message);
            }
            catch { }
        }

        /// <summary>
        /// Find and disable all TVEGlobalVolume components.
        /// Harmony patches don't work for Unity MonoBehaviour Update methods when called via native code.
        /// </summary>
        private static void TryDisableTVEComponents()
        {
            try
            {
                // Find TVEGlobalVolume type
                Type tveType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        tveType = asm.GetType("TheVegetationEngine.TVEGlobalVolume");
                        if (tveType != null) break;
                    }
                    catch { }
                }

                if (tveType == null)
                {
                    // Type not loaded yet, try again next frame
                    return;
                }

                // Use FindObjectsOfType to get all instances
                var findMethod = typeof(UnityEngine.Object).GetMethod("FindObjectsOfType",
                    new Type[] { typeof(Type) });
                if (findMethod == null)
                {
                    Log.LogWarning("[TVE] FindObjectsOfType method not found");
                    return;
                }

                var components = findMethod.Invoke(null, new object[] { tveType }) as UnityEngine.Object[];
                if (components == null || components.Length == 0)
                {
                    // No components found yet, try again next frame
                    return;
                }

                Log.LogInfo($"[TVE] Found {components.Length} TVEGlobalVolume component(s) - disabling them");

                int disabled = 0;
                foreach (var component in components)
                {
                    if (component == null) continue;

                    try
                    {
                        // Cast to MonoBehaviour and disable
                        var mono = component as MonoBehaviour;
                        if (mono != null && mono.enabled)
                        {
                            mono.enabled = false;
                            disabled++;
                            Log.LogInfo($"[TVE] Disabled TVEGlobalVolume on {mono.gameObject?.name ?? "unknown"}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning($"[TVE] Error disabling component: {ex.Message}");
                    }
                }

                if (disabled > 0)
                {
                    Log.LogInfo($"[TVE] Successfully disabled {disabled} TVEGlobalVolume component(s)");
                    _tveDisabled = true;
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"[TVE] Error finding TVEGlobalVolume: {ex.Message}");
            }
        }

        private IEnumerator AutoLoadCoroutine()
        {
            DebugLog("[AutoLoad] Coroutine started, waiting 10 seconds for game to initialize...");

            // Wait for game to fully initialize - use realtime to work even when timeScale=0
            yield return new WaitForSecondsRealtime(10f);

            DebugLog("[AutoLoad] Initial wait complete, checking for FlowManager...");

            // Wait for FlowManager to exist
            Type flowManagerType = null;
            object flowManager = null;
            int attempts = 0;

            while (flowManager == null && attempts < 60)
            {
                if (flowManagerType == null)
                {
                    flowManagerType = Networking.AccessTools.TypeByName("FlowManager");
                }

                if (flowManagerType != null)
                {
                    var instanceField = flowManagerType.GetField("instance",
                        BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                    flowManager = instanceField?.GetValue(null);
                }

                if (flowManager == null)
                {
                    if (attempts % 10 == 0)
                    {
                        DebugLog($"[AutoLoad] Waiting for FlowManager... attempt {attempts}");
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                    attempts++;
                }
            }

            if (flowManager == null)
            {
                DebugLog("[AutoLoad] ERROR: FlowManager not found after 30 seconds!");
                yield break;
            }

            DebugLog("[AutoLoad] FlowManager found! Now triggering auto-load...");

            // Trigger the auto-load
            AutoLoadManager.TryAutoLoad();
        }

        // Auto-load state (kept for backward compatibility)
        private bool _autoLoadTriggered = false;

        private void PatchSteamPlatform()
        {
            try
            {
                // Find SteamPlatform type
                Type steamPlatformType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        steamPlatformType = asm.GetType("SteamPlatform");
                        if (steamPlatformType != null) break;

                        foreach (var t in asm.GetTypes())
                        {
                            if (t.Name == "SteamPlatform")
                            {
                                steamPlatformType = t;
                                break;
                            }
                        }
                        if (steamPlatformType != null) break;
                    }
                    catch { }
                }

                if (steamPlatformType == null)
                {
                    Log.LogWarning("[Plugin] SteamPlatform type not found!");
                    return;
                }

                // Find constructor
                ConstructorInfo ctor = null;
                foreach (var c in steamPlatformType.GetConstructors())
                {
                    var parms = c.GetParameters();
                    if (parms.Length == 1 && parms[0].ParameterType == typeof(uint))
                    {
                        ctor = c;
                        break;
                    }
                }

                if (ctor == null)
                {
                    Log.LogWarning("[Plugin] SteamPlatform(uint) constructor not found!");
                    return;
                }

                Log.LogInfo("[Plugin] Patching SteamPlatform constructor manually...");

                // Get our prefix method
                var prefixMethod = typeof(Plugin).GetMethod(nameof(SteamPlatformPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic);

                var finalizerMethod = typeof(Plugin).GetMethod(nameof(SteamPlatformFinalizer),
                    BindingFlags.Static | BindingFlags.NonPublic);

                // Apply patch
                _harmony.Patch(ctor,
                    prefix: prefixMethod != null ? new HarmonyMethod(prefixMethod) : null,
                    finalizer: finalizerMethod != null ? new HarmonyMethod(finalizerMethod) : null);

                Log.LogInfo("[Plugin] SteamPlatform constructor patched!");
            }
            catch (Exception ex)
            {
                Log.LogError($"[Plugin] Failed to patch SteamPlatform: {ex}");
            }
        }

        private static bool SteamPlatformPrefix()
        {
            Log.LogInfo("[SteamBypass] SteamPlatform Prefix called!");

            bool enableDirect = EnableDirectConnect?.Value ?? false;
            bool autoStart = AutoStartServer?.Value ?? false;

            Log.LogInfo($"[SteamBypass] EnableDirectConnect={enableDirect}, AutoStartServer={autoStart}");

            if (enableDirect && autoStart)
            {
                Log.LogInfo("[SteamBypass] SKIPPING SteamPlatform constructor!");
                Patches.SteamBypassPatches.SteamInitFailed = true;
                return false; // Skip constructor
            }
            return true;
        }

        private static Exception SteamPlatformFinalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Log.LogWarning($"[SteamBypass] Finalizer caught: {__exception.Message}");
                Patches.SteamBypassPatches.SteamInitFailed = true;
                if (EnableDirectConnect.Value && AutoStartServer.Value)
                {
                    return null; // Suppress
                }
            }
            return __exception;
        }

        private void LoadConfig()
        {
            EnableDirectConnect = Config.Bind(
                "General",
                "EnableDirectConnect",
                true,
                "Enable direct IP connections (bypasses Steam lobbies)"
            );

            ServerPort = Config.Bind(
                "Server",
                "Port",
                6968,
                "Port for the dedicated server to listen on"
            );

            MaxPlayers = Config.Bind(
                "Server",
                "MaxPlayers",
                16,
                "Maximum number of players allowed on the server"
            );

            ServerPassword = Config.Bind(
                "Server",
                "Password",
                "",
                "Server password (leave empty for no password)"
            );

            HeadlessMode = Config.Bind(
                "Server",
                "HeadlessMode",
                false,
                "Run in headless mode (no graphics, for dedicated servers)"
            );

            AutoStartServer = Config.Bind(
                "Server",
                "AutoStartServer",
                false,
                "Automatically start the server when the game loads"
            );

            AutoLoadSave = Config.Bind(
                "Server",
                "AutoLoadSave",
                "",
                "Path to a save file to auto-load on startup (e.g., /saves/world1/save.dat). Leave empty to use AutoLoadSlot instead."
            );

            AutoLoadSlot = Config.Bind(
                "Server",
                "AutoLoadSlot",
                -1,
                "Save slot number to auto-load on startup (-1 = disabled, 0+ = slot number)"
            );

            ConnectAddress = Config.Bind(
                "Client",
                "ConnectAddress",
                "51.81.155.59:6968",
                "Default server address to connect to (ip:port). Used by F8 hotkey."
            );

            PublicAddress = Config.Bind(
                "Server",
                "PublicAddress",
                "certifriedmultitool.com:6968",
                "Public address shown in logs and Discord webhooks. Set to your domain or public IP."
            );
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            DirectConnectManager.Cleanup();
        }

        // Frame counter for periodic debug logging
        private int _frameCount = 0;
        private float _lastRealtime = 0;
        private bool _autoLoadStarted = false;
        private int _onGuiCount = 0;

        private void LateUpdate()
        {
            CheckAndTriggerAutoLoad("LateUpdate");
        }

        private void FixedUpdate()
        {
            CheckAndTriggerAutoLoad("FixedUpdate");
        }

        private void OnGUI()
        {
            _onGuiCount++;

            CheckAndTriggerAutoLoad("OnGUI");

            // Log periodically
            if (_onGuiCount % 500 == 0)
            {
                DebugLog($"[OnGUI] Called {_onGuiCount} times, Realtime: {Time.realtimeSinceStartup:F1}s");
            }
        }

        private void CheckAndTriggerAutoLoad(string source)
        {
            // Check if thread triggered auto-load
            if (_threadTriggeredAutoLoad && !_autoLoadStarted)
            {
                _autoLoadStarted = true;
                DebugLog($"[{source}] Thread triggered auto-load - executing on main thread!");
                AutoLoadManager.TryAutoLoad();
            }
        }

        private static bool _tveDisabled = false;

        private void Update()
        {
            _frameCount++;

            // Log first update call to confirm lifecycle is working
            if (!_firstUpdateLogged)
            {
                _firstUpdateLogged = true;
                DebugLog($"[Update] FIRST Update() call! _threadTriggeredAutoLoad={_threadTriggeredAutoLoad}, _autoLoadStarted={_autoLoadStarted}");
            }

            // Disable TVEGlobalVolume components in headless mode (Harmony patches don't work for MonoBehaviour Update)
            if (HeadlessMode.Value && !_tveDisabled)
            {
                TryDisableTVEComponents();
            }

            // Check for scheduled server start (must happen on main thread)
            Networking.AutoLoadManager.Update();

            // Check if thread triggered auto-load
            if (_threadTriggeredAutoLoad && !_autoLoadStarted)
            {
                _autoLoadStarted = true;
                DebugLog("[Update] Thread triggered auto-load - executing on main thread!");
                AutoLoadManager.TryAutoLoad();
            }

            // Log every 300 frames (about 5 seconds at 60fps)
            if (_frameCount % 300 == 0)
            {
                var currentRealtime = Time.realtimeSinceStartup;
                DebugLog($"[Update] Frame {_frameCount}, Realtime: {currentRealtime:F1}s, TimeScale: {Time.timeScale}");
                _lastRealtime = currentRealtime;
            }

            // Process console input for headless mode
            if (HeadlessMode.Value)
            {
                ConsoleCommands.ProcessHeadlessInput();
            }

            // Keyboard shortcuts (works in-game, no console needed)
            // F8 = Connect to server (client mode)
            if (UnityEngine.Input.GetKeyDown(KeyCode.F8))
            {
                Log.LogInfo("[Hotkey] F8 pressed - Connecting to server...");
                var address = ConnectAddress.Value;
                if (string.IsNullOrEmpty(address))
                {
                    Log.LogWarning("[Hotkey] No ConnectAddress configured! Set it in config file.");
                }
                else if (DirectConnectManager.Connect(address))
                {
                    Log.LogInfo($"[Hotkey] Connecting to {address}...");
                }
                else
                {
                    Log.LogWarning("[Hotkey] Failed to connect - check address or already connected");
                }
            }

            // F9 = Start as Host (server + local player)
            if (UnityEngine.Input.GetKeyDown(KeyCode.F9))
            {
                Log.LogInfo("[Hotkey] F9 pressed - Starting as Host...");
                if (DirectConnectManager.StartHost())
                {
                    var addr = DirectConnectManager.GetServerAddress();
                    Log.LogInfo($"[Hotkey] Host started! Players can connect to: {addr}");
                }
                else
                {
                    Log.LogWarning("[Hotkey] Failed to start host - already running or NetworkManager not ready");
                }
            }

            // F10 = Show server status
            if (UnityEngine.Input.GetKeyDown(KeyCode.F10))
            {
                Log.LogInfo("=== Server Status ===");
                Log.LogInfo($"Direct Connect Active: {DirectConnectManager.IsDirectConnectActive}");
                Log.LogInfo($"Is Server: {DirectConnectManager.IsServer}");
                Log.LogInfo($"Is Client: {DirectConnectManager.IsClient}");
                Log.LogInfo($"Is Host: {DirectConnectManager.IsHost}");
                if (DirectConnectManager.IsServer)
                {
                    Log.LogInfo($"Server Address: {DirectConnectManager.GetServerAddress()}");
                    Log.LogInfo($"Players: {DirectConnectManager.GetPlayerCount()}/{MaxPlayers.Value}");
                }
            }

            // F11 = Stop server/disconnect
            if (UnityEngine.Input.GetKeyDown(KeyCode.F11))
            {
                Log.LogInfo("[Hotkey] F11 pressed - Stopping server...");
                DirectConnectManager.Stop();
                Log.LogInfo("[Hotkey] Server stopped");
            }
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.community.techtonicadedicatedserver";
        public const string PLUGIN_NAME = "Techtonica Dedicated Server";
        public const string PLUGIN_VERSION = "0.1.0";
    }
}
