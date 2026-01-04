using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEngine;

namespace TechtonicaDedicatedServer.Networking
{
    /// <summary>
    /// Manages auto-loading of save files for dedicated server mode.
    /// This allows the server to automatically load a world and start hosting.
    /// </summary>
    public static class AutoLoadManager
    {
        private static bool _hasAttemptedAutoLoad;
        private static bool _isAutoLoading;

        // Server start scheduling (must happen on main thread)
        private static bool _serverStartPending;
        private static float _serverStartScheduledTime;

        // Cached save data for sending to clients (used when PrepSave fails in headless mode)
        private static string _cachedSaveString;
        private static byte[] _cachedSaveBytes;
        private static string _lastLoadedSavePath;

        public static bool IsAutoLoading => _isAutoLoading;

        private static int _serverStartAttempts = 0;

        private static int _updateCallCount;
        private static float _lastUpdateLogTime;

        // Scene loading state
        private static bool _sceneLoadingInitiated;
        private static float _sceneLoadStartTime;
        private static bool _forceLoadedScenes;

        // Ghost host auto-navigation state
        private static bool _mainMenuHandled;
        private static bool _loadingPromptHandled;
        private static float _lastAutoClickTime;

        // Loading scene timeout monitoring
        private static float _loadingSceneEnteredTime;
        private static bool _loadingSceneMonitorActive;
        private static bool _forcedLoadingCompletion;
        private const float LOADING_TIMEOUT_SECONDS = 5f;

        // Main Menu stuck detection
        private static float _mainMenuStuckStartTime;
        private static bool _mainMenuStuckMonitorActive;
        private static bool _forcedMainMenuBypass;
        private const float MAIN_MENU_TIMEOUT_SECONDS = 10f;

        /// <summary>
        /// Called from Plugin.Update() to handle scheduled server start on main thread.
        /// </summary>
        public static void Update()
        {
            _updateCallCount++;

            // Log periodically to confirm Update is being called
            if (Time.realtimeSinceStartup - _lastUpdateLogTime > 10f)
            {
                _lastUpdateLogTime = Time.realtimeSinceStartup;
                Plugin.Log.LogInfo($"[AutoLoad] Update() #{_updateCallCount}, pending={_serverStartPending}, scheduled={_serverStartScheduledTime:F1}, now={Time.realtimeSinceStartup:F1}");
            }

            // CRITICAL: Ensure Time.timeScale is not 0 - the game freezes it during loading
            // which prevents coroutines from running. We need to keep it at 1 for headless mode.
            if (_isAutoLoading && Time.timeScale < 0.5f)
            {
                Time.timeScale = 1f;
            }

            // GHOST HOST MODE: Auto-navigate through menus and loading screens
            if (Plugin.AutoStartServer.Value)
            {
                TryAutoNavigate();
            }

            if (_serverStartPending && Time.realtimeSinceStartup >= _serverStartScheduledTime)
            {
                _serverStartPending = false;
                _serverStartAttempts++;
                Plugin.DebugLog($"[AutoLoad] Server start attempt #{_serverStartAttempts}...");

                try
                {
                    // Check current scene
                    string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                    Plugin.DebugLog($"[AutoLoad] Current scene: {currentScene}");

                    // GHOST HOST MODE: Wait for game world to be loaded
                    // Server needs to be in the game world to properly host
                    bool isMainMenu = currentScene == "Main Menu";
                    bool isPlayerScene = currentScene == "Player Scene" || currentScene.Contains("Player");
                    bool isLoadingScene = currentScene == "Loading";

                    if (isMainMenu)
                    {
                        Plugin.DebugLog("[AutoLoad] GHOST HOST MODE: Still on Main Menu - waiting for game to load...");
                        Plugin.DebugLog("[AutoLoad] Rescheduling server start for 5 more seconds...");
                        _serverStartScheduledTime = Time.realtimeSinceStartup + 5f;
                        _serverStartPending = true;
                        _serverStartAttempts--; // Don't count this as a real attempt
                        return; // Wait longer
                    }

                    // Check if game systems are initialized
                    bool gameSystemsReady = CheckGameSystemsReady();
                    if (!gameSystemsReady)
                    {
                        if (_serverStartAttempts < 20)
                        {
                            Plugin.Log.LogInfo($"[AutoLoad] Game systems not ready yet (attempt #{_serverStartAttempts}). Waiting 3 more seconds...");
                            _serverStartScheduledTime = Time.realtimeSinceStartup + 3f;
                            _serverStartPending = true;
                            return;
                        }
                        else
                        {
                            Plugin.Log.LogWarning("[AutoLoad] Game systems still not ready after 20 attempts, starting HOST anyway...");
                        }
                    }

                    if (isPlayerScene || gameSystemsReady)
                    {
                        Plugin.DebugLog("[AutoLoad] GHOST HOST MODE: Game world loaded! Starting as HOST...");
                    }

                    // Log loaded scenes for debugging
                    int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
                    Plugin.DebugLog($"[AutoLoad] Loaded scenes ({sceneCount}):");
                    for (int i = 0; i < sceneCount; i++)
                    {
                        var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                        Plugin.DebugLog($"[AutoLoad]   - {scene.name} (loaded={scene.isLoaded})");
                    }

                    // GHOST HOST MODE: Start as HOST so we have a player in the world
                    Plugin.DebugLog($"[AutoLoad] Starting HOST on scene '{currentScene}' (attempt #{_serverStartAttempts})");

                    if (DirectConnectManager.StartHost())
                    {
                        var addr = DirectConnectManager.GetServerAddress();
                        Plugin.DebugLog($"[AutoLoad] HOST started successfully on {addr}");
                        Plugin.DebugLog("[AutoLoad] Ghost host is now in the game world. Clients can connect!");
                    }
                    else
                    {
                        Plugin.DebugLog("[AutoLoad] ERROR: Failed to start HOST!");
                        // Retry after a delay
                        if (_serverStartAttempts < 30)
                        {
                            Plugin.DebugLog("[AutoLoad] Will retry in 5 seconds...");
                            _serverStartScheduledTime = Time.realtimeSinceStartup + 5f;
                            _serverStartPending = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.DebugLog($"[AutoLoad] Server start error: {ex.Message}");
                }

                _isAutoLoading = false;
            }
        }

        /// <summary>
        /// Check if key game systems are initialized and ready.
        /// </summary>
        private static bool CheckGameSystemsReady()
        {
            try
            {
                // Check for Player Scene loaded
                bool playerSceneLoaded = false;
                int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
                for (int i = 0; i < sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    if (scene.name == "Player Scene" && scene.isLoaded)
                    {
                        playerSceneLoaded = true;
                        break;
                    }
                }

                if (!playerSceneLoaded)
                {
                    Plugin.Log.LogInfo("[AutoLoad] Player Scene not fully loaded yet");
                    return false;
                }

                // Check for GameState.instance
                var gameStateType = AccessTools.TypeByName("GameState");
                if (gameStateType != null)
                {
                    var instanceField = gameStateType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    var gameState = instanceField?.GetValue(null);
                    if (gameState == null)
                    {
                        Plugin.Log.LogInfo("[AutoLoad] GameState.instance is null");
                        return false;
                    }
                }

                // Check for FactorySimManager.instance
                var factorySimType = AccessTools.TypeByName("FactorySimManager");
                if (factorySimType != null)
                {
                    var instanceField = factorySimType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    var factorySim = instanceField?.GetValue(null);
                    if (factorySim == null)
                    {
                        Plugin.Log.LogInfo("[AutoLoad] FactorySimManager.instance is null");
                        return false;
                    }
                }

                // Check for Player.instance
                var playerType = AccessTools.TypeByName("Player");
                if (playerType != null)
                {
                    var instanceField = playerType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    var player = instanceField?.GetValue(null);
                    if (player == null)
                    {
                        Plugin.Log.LogInfo("[AutoLoad] Player.instance is null");
                        return false;
                    }
                }

                Plugin.Log.LogInfo("[AutoLoad] All game systems ready!");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[AutoLoad] CheckGameSystemsReady error: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// GHOST HOST MODE: Auto-navigate through menus and loading screens.
        /// This simulates user input to get past prompts without human interaction.
        /// </summary>
        private static int _autoNavLogCount = 0;
        private static float _lastAutoNavLog = -100f; // Start negative so first log triggers immediately

        private static void TryAutoNavigate()
        {
            _autoNavLogCount++;

            // Log first call to verify method is running
            if (_autoNavLogCount == 1)
            {
                Plugin.Log.LogInfo("[AutoNav] FIRST CALL to TryAutoNavigate()!");
            }

            // Log state every 2 seconds
            if (Time.realtimeSinceStartup - _lastAutoNavLog >= 2f)
            {
                _lastAutoNavLog = Time.realtimeSinceStartup;
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                Plugin.Log.LogInfo($"[AutoNav] #{_autoNavLogCount}: scene={scene}, handled={_mainMenuHandled}, bypass={_forcedMainMenuBypass}, stuckMon={_mainMenuStuckMonitorActive}, lastClick={_lastAutoClickTime:F1}");
            }

            // Rate limit auto-clicks
            float timeSinceLastClick = Time.realtimeSinceStartup - _lastAutoClickTime;
            if (timeSinceLastClick < 1f)
            {
                return;
            }

            try
            {
                string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

                // Handle Main Menu - auto-click Continue/Load Game
                if (currentScene == "Main Menu" && !_mainMenuHandled)
                {
                    TryAutoClickMainMenu();
                }

                // DETECT STUCK ON MAIN MENU: If we've tried to load but scene hasn't changed
                if (currentScene == "Main Menu" && _mainMenuHandled && !_forcedMainMenuBypass)
                {
                    if (!_mainMenuStuckMonitorActive)
                    {
                        _mainMenuStuckMonitorActive = true;
                        _mainMenuStuckStartTime = Time.realtimeSinceStartup;
                        Plugin.Log.LogInfo("[AutoNavigate] Started Main Menu stuck monitor...");
                    }
                    else
                    {
                        float timeStuck = Time.realtimeSinceStartup - _mainMenuStuckStartTime;
                        // Log every 2 seconds
                        if (((int)timeStuck) % 2 == 0 && ((int)(timeStuck * 10) % 20 < 10))
                        {
                            Plugin.Log.LogInfo($"[AutoNavigate] Main Menu stuck for {timeStuck:F1}s (timeout at {MAIN_MENU_TIMEOUT_SECONDS}s)");
                        }
                        if (timeStuck >= MAIN_MENU_TIMEOUT_SECONDS)
                        {
                            Plugin.Log.LogWarning($"[AutoNavigate] STUCK on Main Menu for {timeStuck:F1}s - forcing scene bypass!");
                            ForceSceneTransition();
                        }
                    }
                }
                else if (currentScene != "Main Menu")
                {
                    // We left Main Menu, stop monitoring
                    _mainMenuStuckMonitorActive = false;
                }

                // Handle Loading Screen - bypass "press any key" prompt
                if (!_loadingPromptHandled)
                {
                    TryBypassLoadingPrompt();
                }
            }
            catch (Exception ex)
            {
                // Don't spam logs
                if (_updateCallCount % 300 == 0)
                {
                    Plugin.DebugLog($"[AutoNavigate] Error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Force scene transition when async loading is broken.
        /// Since Unity scene loading doesn't work in Wine headless mode,
        /// we just start the HOST directly from Main Menu.
        /// </summary>
        private static void ForceSceneTransition()
        {
            try
            {
                _forcedMainMenuBypass = true;
                Plugin.Log.LogWarning("[AutoNavigate] Scene loading broken - starting HOST from Main Menu!");

                // Scene loading doesn't work in Wine, so just start the server
                // Clients will connect and receive save data even without full game state
                ForceStartHost();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AutoNavigate] ForceSceneTransition error: {ex.Message}");
            }
        }

        /// <summary>
        /// Force start the HOST even without proper scene loading.
        /// </summary>
        private static void ForceStartHost()
        {
            try
            {
                Plugin.Log.LogInfo("[AutoNavigate] Force-starting HOST from Main Menu...");

                // CRITICAL: Set up KCP transport before starting the host
                // FizzyFacepunch (Steam) won't work without Steam
                SetupKCPTransport();

                var networkManager = Mirror.NetworkManager.singleton;
                if (networkManager == null)
                {
                    Plugin.Log.LogError("[AutoNavigate] Could not find NetworkManager.singleton!");
                    return;
                }

                Plugin.Log.LogInfo($"[AutoNavigate] Found NetworkManager: {networkManager.GetType().Name}");

                // Set the network address
                networkManager.networkAddress = "0.0.0.0";

                // Get the port from our config
                ushort port = (ushort)Plugin.ServerPort.Value;

                // Set the transport port
                var transport = Mirror.Transport.activeTransport;
                if (transport != null)
                {
                    Plugin.Log.LogInfo($"[AutoNavigate] Active transport: {transport.GetType().Name}");

                    var portField = transport.GetType().GetField("Port", BindingFlags.Public | BindingFlags.Instance);
                    var portProp = transport.GetType().GetProperty("Port", BindingFlags.Public | BindingFlags.Instance);

                    if (portField != null)
                        portField.SetValue(transport, port);
                    else if (portProp != null)
                        portProp.SetValue(transport, port);

                    Plugin.Log.LogInfo($"[AutoNavigate] Set transport port to {port}");
                }

                // Start as host
                networkManager.StartHost();
                Plugin.Log.LogInfo("[AutoNavigate] StartHost() called!");

                // Check if server started
                if (Mirror.NetworkServer.active)
                {
                    Plugin.Log.LogInfo($"[AutoNavigate] HOST STARTED on port {port}!");

                    // CRITICAL: Spawn NetworkMessageRelay for game commands to work
                    SpawnNetworkMessageRelay();

                    // CRITICAL: Create stub game systems for tick sync
                    CreateStubGameSystems();
                }
                else
                {
                    Plugin.Log.LogWarning("[AutoNavigate] StartHost called but NetworkServer.active is false");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AutoNavigate] ForceStartHost error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Set up KCP transport for the server.
        /// </summary>
        private static void SetupKCPTransport()
        {
            try
            {
                var networkManager = Mirror.NetworkManager.singleton;
                if (networkManager == null)
                {
                    Plugin.Log.LogError("[AutoNavigate] Cannot setup KCP - NetworkManager.singleton is null");
                    return;
                }

                // Find KCP transport on the NetworkManager GameObject
                var kcpTransport = networkManager.GetComponent<kcp2k.KcpTransport>();
                if (kcpTransport == null)
                {
                    // Try to find it as a child or add it
                    var go = networkManager.gameObject;
                    kcpTransport = go.GetComponentInChildren<kcp2k.KcpTransport>();

                    if (kcpTransport == null)
                    {
                        // Add KCP transport
                        Plugin.Log.LogInfo("[AutoNavigate] Adding KcpTransport to NetworkManager");
                        kcpTransport = go.AddComponent<kcp2k.KcpTransport>();
                    }
                }

                if (kcpTransport != null)
                {
                    Plugin.Log.LogInfo("[AutoNavigate] Found/created KcpTransport, setting as active");

                    // Set port
                    kcpTransport.Port = (ushort)Plugin.ServerPort.Value;

                    // CRITICAL: Set DualMode = false to fix Wine socket issues
                    // This forces IPv4 only which works properly in Wine
                    kcpTransport.DualMode = false;
                    kcpTransport.NoDelay = true;
                    kcpTransport.Interval = 10;
                    kcpTransport.Timeout = 10000;

                    Plugin.Log.LogInfo($"[AutoNavigate] KCP configured: Port={kcpTransport.Port}, DualMode={kcpTransport.DualMode}");

                    // Set as the active transport
                    Mirror.Transport.activeTransport = kcpTransport;

                    // Also set on NetworkManager
                    var transportField = typeof(Mirror.NetworkManager).GetField("transport", BindingFlags.Public | BindingFlags.Instance);
                    if (transportField != null)
                    {
                        transportField.SetValue(networkManager, kcpTransport);
                    }

                    Plugin.Log.LogInfo($"[AutoNavigate] KCP transport configured on port {kcpTransport.Port}");
                }
                else
                {
                    Plugin.Log.LogWarning("[AutoNavigate] Could not find or create KcpTransport!");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AutoNavigate] SetupKCPTransport error: {ex.Message}");
            }
        }

        // Cached NetworkMessageRelay instance
        private static object _serverNetworkRelay;

        /// <summary>
        /// Spawn a NetworkMessageRelay on the server for handling game commands.
        /// This is critical for client-server communication.
        /// </summary>
        private static void SpawnNetworkMessageRelay()
        {
            try
            {
                Plugin.Log.LogInfo("[AutoNavigate] Creating NetworkMessageRelay for server...");

                // Find the NetworkMessageRelay type
                var relayType = HarmonyLib.AccessTools.TypeByName("NetworkMessageRelay");
                if (relayType == null)
                {
                    Plugin.Log.LogError("[AutoNavigate] NetworkMessageRelay type not found!");
                    return;
                }

                // Check if there's already an instance
                var instanceField = relayType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceField != null)
                {
                    var existing = instanceField.GetValue(null);
                    if (existing != null)
                    {
                        Plugin.Log.LogInfo("[AutoNavigate] NetworkMessageRelay.instance already exists");
                        _serverNetworkRelay = existing;
                        return;
                    }
                }

                // Create a new GameObject with NetworkMessageRelay
                var go = new GameObject("ServerNetworkMessageRelay");
                UnityEngine.Object.DontDestroyOnLoad(go);

                // Add NetworkIdentity first (required for NetworkBehaviour)
                var networkIdentity = go.AddComponent<Mirror.NetworkIdentity>();

                // Add NetworkMessageRelay component
                var relay = go.AddComponent(relayType);

                // Set the static instance
                if (instanceField != null)
                {
                    instanceField.SetValue(null, relay);
                    Plugin.Log.LogInfo("[AutoNavigate] Set NetworkMessageRelay.instance");
                }

                // Spawn on the network so clients can see it
                if (Mirror.NetworkServer.active)
                {
                    Mirror.NetworkServer.Spawn(go);
                    Plugin.Log.LogInfo("[AutoNavigate] NetworkMessageRelay spawned on network!");
                }

                _serverNetworkRelay = relay;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AutoNavigate] SpawnNetworkMessageRelay error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        // Cached stub game systems
        private static object _stubFactorySimManager;
        private static object _stubMachineManager;

        /// <summary>
        /// Create stub game systems (FactorySimManager, MachineManager) for headless mode.
        /// These are minimal implementations that allow tick sync and basic operations.
        /// </summary>
        private static void CreateStubGameSystems()
        {
            try
            {
                Plugin.Log.LogInfo("[AutoNavigate] Creating stub game systems for headless mode...");

                // Create stub MachineManager first (it's a simple class, not MonoBehaviour)
                var machineManagerType = HarmonyLib.AccessTools.TypeByName("MachineManager");
                if (machineManagerType != null)
                {
                    var instanceField = machineManagerType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    if (instanceField != null)
                    {
                        var existing = instanceField.GetValue(null);
                        if (existing == null)
                        {
                            // Create new MachineManager
                            var stubMM = Activator.CreateInstance(machineManagerType);
                            instanceField.SetValue(null, stubMM);
                            _stubMachineManager = stubMM;

                            // Set curTick to match save data
                            var curTickField = machineManagerType.GetField("curTick", BindingFlags.Public | BindingFlags.Instance);
                            if (curTickField != null)
                            {
                                curTickField.SetValue(stubMM, 724189); // Default tick from save
                                Plugin.Log.LogInfo("[AutoNavigate] Created stub MachineManager with curTick=724189");
                            }
                        }
                        else
                        {
                            Plugin.Log.LogInfo("[AutoNavigate] MachineManager.instance already exists");
                            _stubMachineManager = existing;
                        }
                    }
                }
                else
                {
                    Plugin.Log.LogWarning("[AutoNavigate] MachineManager type not found");
                }

                // FactorySimManager is a MonoBehaviour - create a GameObject for it
                var factorySimType = HarmonyLib.AccessTools.TypeByName("FactorySimManager");
                if (factorySimType != null)
                {
                    // Check static _instance field
                    var instanceField = factorySimType.GetField("_instance", BindingFlags.NonPublic | BindingFlags.Static);
                    if (instanceField != null)
                    {
                        var existing = instanceField.GetValue(null);
                        if (existing == null)
                        {
                            // Create GameObject with FactorySimManager
                            var go = new GameObject("StubFactorySimManager");
                            UnityEngine.Object.DontDestroyOnLoad(go);

                            var stubFSM = go.AddComponent(factorySimType);
                            _stubFactorySimManager = stubFSM;

                            // The OnEnable method should set _instance = this
                            Plugin.Log.LogInfo("[AutoNavigate] Created stub FactorySimManager");

                            // Set lastSyncTick
                            var lastSyncField = factorySimType.GetField("_lastSyncTick", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (lastSyncField != null)
                            {
                                lastSyncField.SetValue(stubFSM, 724189);
                                Plugin.Log.LogInfo("[AutoNavigate] Set FactorySimManager.lastSyncTick=724189");
                            }
                        }
                        else
                        {
                            Plugin.Log.LogInfo("[AutoNavigate] FactorySimManager._instance already exists");
                            _stubFactorySimManager = existing;
                        }
                    }
                }
                else
                {
                    Plugin.Log.LogWarning("[AutoNavigate] FactorySimManager type not found");
                }

                Plugin.Log.LogInfo("[AutoNavigate] Stub game systems created");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AutoNavigate] CreateStubGameSystems error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Auto-click on Main Menu to load the game.
        /// </summary>
        private static void TryAutoClickMainMenu()
        {
            try
            {
                // GHOST HOST MODE: If we have a save path configured, load it directly
                // This is more reliable than trying to click menu buttons
                if (!string.IsNullOrEmpty(Plugin.AutoLoadSave.Value) || Plugin.AutoLoadSlot.Value >= 0)
                {
                    Plugin.DebugLog("[AutoNavigate] Have save configured, loading directly via FlowManager...");
                    TryAutoLoadDirect();
                    _mainMenuHandled = true;
                    _lastAutoClickTime = Time.realtimeSinceStartup;
                    return;
                }

                // Fall back to menu button clicking if no save configured
                // Find MainMenuUI
                var mainMenuUIType = AccessTools.TypeByName("MainMenuUI");
                if (mainMenuUIType == null) return;

                var instanceField = mainMenuUIType.GetField("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                var mainMenuUI = instanceField?.GetValue(null);
                if (mainMenuUI == null) return;

                Plugin.DebugLog("[AutoNavigate] Found MainMenuUI, looking for Continue/Load button...");

                // Try to find and click the Continue button first (if there's a save)
                var continueMethod = mainMenuUIType.GetMethod("ContinueClicked", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (continueMethod != null)
                {
                    // Check if continue button is active/interactable
                    var continueButtonField = mainMenuUIType.GetField("continueButton", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                    var continueButton = continueButtonField?.GetValue(mainMenuUI);

                    if (continueButton != null)
                    {
                        // Check if button is interactable
                        var interactableProp = continueButton.GetType().GetProperty("interactable");
                        bool isInteractable = interactableProp != null && (bool)interactableProp.GetValue(continueButton);

                        if (isInteractable)
                        {
                            Plugin.DebugLog("[AutoNavigate] Clicking Continue button...");
                            continueMethod.Invoke(mainMenuUI, null);
                            _mainMenuHandled = true;
                            _lastAutoClickTime = Time.realtimeSinceStartup;
                            return;
                        }
                    }
                }

                // If no continue, try Load Game button
                var loadGameMethod = mainMenuUIType.GetMethod("LoadGameClicked", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                if (loadGameMethod != null)
                {
                    Plugin.DebugLog("[AutoNavigate] Clicking Load Game button...");
                    loadGameMethod.Invoke(mainMenuUI, null);
                    _mainMenuHandled = true;
                    _lastAutoClickTime = Time.realtimeSinceStartup;
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.DebugLog($"[AutoNavigate] MainMenu error: {ex.Message}");
            }
        }

        /// <summary>
        /// Bypass the "press any key to continue" loading screen prompt.
        /// </summary>
        private static void TryBypassLoadingPrompt()
        {
            try
            {
                string currentScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;

                // Start monitoring when we enter Loading scene
                if (currentScene == "Loading" && !_loadingSceneMonitorActive)
                {
                    _loadingSceneMonitorActive = true;
                    _loadingSceneEnteredTime = Time.realtimeSinceStartup;
                    Plugin.DebugLog("[AutoNavigate] Entered Loading scene, starting monitor...");
                }

                // If we've left the Loading scene, stop monitoring
                if (currentScene != "Loading" && _loadingSceneMonitorActive)
                {
                    _loadingSceneMonitorActive = false;
                    _forcedLoadingCompletion = false;
                    Plugin.DebugLog($"[AutoNavigate] Left Loading scene, now on: {currentScene}");
                    return;
                }

                if (!_loadingSceneMonitorActive) return;

                // Find LoadingUI
                var loadingUIType = AccessTools.TypeByName("LoadingUI");
                if (loadingUIType == null) return;

                var instanceField = loadingUIType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                var loadingUI = instanceField?.GetValue(null);
                if (loadingUI == null) return;

                // Check current state
                var loadedField = loadingUIType.GetField("_loaded", BindingFlags.NonPublic | BindingFlags.Instance);
                var confirmedField = loadingUIType.GetField("confirmedLoad", BindingFlags.Public | BindingFlags.Instance);

                if (loadedField == null || confirmedField == null) return;

                bool isLoaded = (bool)loadedField.GetValue(loadingUI);
                bool isConfirmed = (bool)confirmedField.GetValue(loadingUI);

                // If loaded but not confirmed, bypass prompt immediately
                if (isLoaded && !isConfirmed)
                {
                    Plugin.DebugLog("[AutoNavigate] Loading complete, bypassing 'press any key' prompt...");
                    ForceLoadingCompletion(loadingUIType, loadingUI, loadedField, confirmedField);
                    return;
                }

                // TIMEOUT-BASED FORCE COMPLETION: If stuck on Loading scene too long, force it
                float timeOnLoadingScene = Time.realtimeSinceStartup - _loadingSceneEnteredTime;
                if (timeOnLoadingScene >= LOADING_TIMEOUT_SECONDS && !_forcedLoadingCompletion)
                {
                    Plugin.DebugLog($"[AutoNavigate] TIMEOUT! Stuck on Loading for {timeOnLoadingScene:F1}s - forcing completion!");
                    ForceLoadingCompletion(loadingUIType, loadingUI, loadedField, confirmedField);
                }
            }
            catch (Exception ex)
            {
                // Only log periodically
                if (_updateCallCount % 300 == 0)
                {
                    Plugin.DebugLog($"[AutoNavigate] Loading prompt error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Force loading to complete by setting all necessary flags.
        /// This is called either when loading is done or after timeout.
        /// </summary>
        private static void ForceLoadingCompletion(Type loadingUIType, object loadingUI, FieldInfo loadedField, FieldInfo confirmedField)
        {
            try
            {
                _forcedLoadingCompletion = true;

                // Set _loaded = true
                loadedField.SetValue(loadingUI, true);
                Plugin.Log.LogInfo("[AutoNavigate] Set _loaded = true");

                // Set confirmedLoad = true
                confirmedField.SetValue(loadingUI, true);
                Plugin.Log.LogInfo("[AutoNavigate] Set confirmedLoad = true");

                // Call OnFinishLoading to complete the transition
                var onFinishMethod = loadingUIType.GetMethod("OnFinishLoading", BindingFlags.Public | BindingFlags.Instance);
                if (onFinishMethod != null)
                {
                    onFinishMethod.Invoke(loadingUI, null);
                    Plugin.Log.LogInfo("[AutoNavigate] Called LoadingUI.OnFinishLoading()");
                }

                // Also try to call DJManager.instance.OnFinishInit() which is done in the Update
                var djManagerType = AccessTools.TypeByName("DJManager");
                if (djManagerType != null)
                {
                    var djInstance = djManagerType.GetField("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    if (djInstance != null)
                    {
                        var onFinishInit = djManagerType.GetMethod("OnFinishInit", BindingFlags.Public | BindingFlags.Instance);
                        onFinishInit?.Invoke(djInstance, null);
                        Plugin.Log.LogInfo("[AutoNavigate] Called DJManager.OnFinishInit()");
                    }
                }

                _loadingPromptHandled = true;
                _lastAutoClickTime = Time.realtimeSinceStartup;
                Plugin.Log.LogInfo("[AutoNavigate] Loading completion forced successfully!");

                // Try to force-load the required game scenes if they haven't loaded
                TryForceLoadGameScenes();
            }
            catch (Exception ex)
            {
                Plugin.DebugLog($"[AutoNavigate] ForceLoadingCompletion error: {ex.Message}");
            }
        }

        /// <summary>
        /// Try to force-load the game scenes that the FlowManager coroutine failed to load.
        /// </summary>
        private static void TryForceLoadGameScenes()
        {
            try
            {
                Plugin.Log.LogInfo("[AutoNavigate] Attempting to force-load game scenes...");

                // Check which scenes are loaded
                int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
                bool hasPlayerScene = false;
                bool hasVoxelandScene = false;
                bool hasUIScene = false;

                for (int i = 0; i < sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    Plugin.Log.LogInfo($"[AutoNavigate] Scene {i}: {scene.name} (loaded={scene.isLoaded})");

                    if (scene.name == "Player Scene" && scene.isLoaded) hasPlayerScene = true;
                    if (scene.name == "Voxeland Scene" && scene.isLoaded) hasVoxelandScene = true;
                    if (scene.name == "UI" && scene.isLoaded) hasUIScene = true;
                }

                // Try to load missing scenes additively using SYNCHRONOUS loading
                // Async loading doesn't work properly in Wine/headless mode
                if (!hasPlayerScene)
                {
                    Plugin.Log.LogInfo("[AutoNavigate] Loading Player Scene (SYNC)...");
                    try
                    {
                        UnityEngine.SceneManagement.SceneManager.LoadScene("Player Scene", UnityEngine.SceneManagement.LoadSceneMode.Additive);
                        Plugin.Log.LogInfo("[AutoNavigate] Player Scene loaded!");
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[AutoNavigate] Player Scene load failed: {ex.Message}");
                    }
                }

                if (!hasVoxelandScene)
                {
                    Plugin.Log.LogInfo("[AutoNavigate] Loading Voxeland Scene (SYNC)...");
                    try
                    {
                        UnityEngine.SceneManagement.SceneManager.LoadScene("Voxeland Scene", UnityEngine.SceneManagement.LoadSceneMode.Additive);
                        Plugin.Log.LogInfo("[AutoNavigate] Voxeland Scene loaded!");
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[AutoNavigate] Voxeland Scene load failed: {ex.Message}");
                    }
                }

                if (!hasUIScene)
                {
                    Plugin.Log.LogInfo("[AutoNavigate] Loading UI scene (SYNC)...");
                    try
                    {
                        UnityEngine.SceneManagement.SceneManager.LoadScene("UI", UnityEngine.SceneManagement.LoadSceneMode.Additive);
                        Plugin.Log.LogInfo("[AutoNavigate] UI Scene loaded!");
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[AutoNavigate] UI Scene load failed: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AutoNavigate] TryForceLoadGameScenes error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Attempt to force scene loading if FlowManager's coroutine failed.
        /// This tries to help Unity's async operations complete.
        /// </summary>
        private static void TryForceSceneLoad()
        {
            if (_forceLoadedScenes) return;
            _forceLoadedScenes = true;

            Plugin.DebugLog("[AutoLoad] Attempting to help scene loading complete...");

            try
            {
                // Keep time scale at 1
                Time.timeScale = 1f;

                // Check if there are pending async operations
                int sceneCount = UnityEngine.SceneManagement.SceneManager.sceneCount;
                Plugin.DebugLog($"[AutoLoad] Current scene count: {sceneCount}");
                for (int i = 0; i < sceneCount; i++)
                {
                    var scene = UnityEngine.SceneManagement.SceneManager.GetSceneAt(i);
                    Plugin.DebugLog($"[AutoLoad]   - {scene.name} (isLoaded={scene.isLoaded}, isValid={scene.IsValid()})");

                    // Try to set any valid game scene as active
                    if (scene.isLoaded && scene.name != "Main Menu" && scene.name != "Loading")
                    {
                        Plugin.DebugLog($"[AutoLoad] Setting {scene.name} as active scene...");
                        UnityEngine.SceneManagement.SceneManager.SetActiveScene(scene);
                    }
                }

                // Try to force LoadingUI to skip the "press any key" prompt
                TryBypassLoadingPrompts();

                // Try to initialize GameState and MachineManager
                TryInitializeGameSystems();
            }
            catch (Exception ex)
            {
                Plugin.DebugLog($"[AutoLoad] Force scene load error: {ex}");
            }
        }

        /// <summary>
        /// Try to bypass loading prompts that require user input
        /// </summary>
        private static void TryBypassLoadingPrompts()
        {
            Plugin.DebugLog("[AutoLoad] Attempting to bypass loading prompts...");

            try
            {
                // Find InputHandler and simulate input
                var inputHandlerType = AccessTools.TypeByName("InputHandler");
                if (inputHandlerType != null)
                {
                    var instanceField = inputHandlerType.GetField("instance",
                        BindingFlags.Public | BindingFlags.Static);
                    var inputHandler = instanceField?.GetValue(null);

                    if (inputHandler != null)
                    {
                        // Try to set anyInputPressed
                        var anyInputField = inputHandlerType.GetField("anyInputPressed",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (anyInputField != null)
                        {
                            anyInputField.SetValue(inputHandler, true);
                            Plugin.DebugLog("[AutoLoad] Set InputHandler.anyInputPressed = true");
                        }

                        // Also try AnyInputPressed property
                        var anyInputProp = inputHandlerType.GetProperty("AnyInputPressed",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (anyInputProp != null)
                        {
                            Plugin.DebugLog($"[AutoLoad] InputHandler.AnyInputPressed = {anyInputProp.GetValue(inputHandler)}");
                        }
                    }
                    else
                    {
                        Plugin.DebugLog("[AutoLoad] InputHandler.instance is NULL");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.DebugLog($"[AutoLoad] Bypass loading prompts error: {ex.Message}");
            }
        }

        /// <summary>
        /// Try to initialize game systems after scene load
        /// </summary>
        private static void TryInitializeGameSystems()
        {
            Plugin.DebugLog("[AutoLoad] Attempting to initialize game systems...");

            try
            {
                // Find GameState
                var gameStateType = AccessTools.TypeByName("GameState");
                if (gameStateType != null)
                {
                    var instanceField = gameStateType.GetField("instance",
                        BindingFlags.Public | BindingFlags.Static);
                    var gameState = instanceField?.GetValue(null);
                    Plugin.DebugLog($"[AutoLoad] GameState.instance: {(gameState != null ? "exists" : "NULL")}");
                }

                // Find MachineManager
                var machineManagerType = AccessTools.TypeByName("MachineManager");
                if (machineManagerType != null)
                {
                    var instanceField = machineManagerType.GetField("instance",
                        BindingFlags.Public | BindingFlags.Static);
                    var machineManager = instanceField?.GetValue(null);
                    Plugin.DebugLog($"[AutoLoad] MachineManager.instance: {(machineManager != null ? "exists" : "NULL")}");
                }

                // Find LoadingUI and force it to complete
                var loadingUIType = AccessTools.TypeByName("LoadingUI");
                if (loadingUIType != null)
                {
                    var instanceField = loadingUIType.GetField("instance",
                        BindingFlags.Public | BindingFlags.Static);
                    var loadingUI = instanceField?.GetValue(null);

                    if (loadingUI != null)
                    {
                        Plugin.DebugLog("[AutoLoad] Found LoadingUI.instance, forcing completion...");

                        // Set _loaded = true
                        var loadedField = loadingUIType.GetField("_loaded",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (loadedField != null)
                        {
                            loadedField.SetValue(loadingUI, true);
                            Plugin.DebugLog("[AutoLoad] Set LoadingUI._loaded = true");
                        }

                        // Set confirmedLoad = true
                        var confirmedField = loadingUIType.GetField("confirmedLoad",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (confirmedField != null)
                        {
                            confirmedField.SetValue(loadingUI, true);
                            Plugin.DebugLog("[AutoLoad] Set LoadingUI.confirmedLoad = true");
                        }

                        // Call OnFinishLoading
                        var onFinishMethod = loadingUIType.GetMethod("OnFinishLoading",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (onFinishMethod != null)
                        {
                            onFinishMethod.Invoke(loadingUI, null);
                            Plugin.DebugLog("[AutoLoad] Called LoadingUI.OnFinishLoading()");
                        }

                        // Call DismissLoadingScreen
                        var dismissMethod = loadingUIType.GetMethod("DismissLoadingScreen",
                            BindingFlags.Public | BindingFlags.Instance);
                        if (dismissMethod != null)
                        {
                            dismissMethod.Invoke(loadingUI, null);
                            Plugin.DebugLog("[AutoLoad] Called LoadingUI.DismissLoadingScreen()");
                        }
                    }
                    else
                    {
                        Plugin.DebugLog("[AutoLoad] LoadingUI.instance is NULL");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.DebugLog($"[AutoLoad] Game systems init error: {ex}");
            }
        }

        /// <summary>
        /// Gets cached save data as a string. Used by SendSaveString patch when PrepSave fails.
        /// </summary>
        public static string GetCachedSaveString()
        {
            if (!string.IsNullOrEmpty(_cachedSaveString))
            {
                return _cachedSaveString;
            }

            // Try to read from file if we have the path
            // The save file is already in the correct format: JSON metadata + newline + base64 messagepack
            // We should read it as text, NOT base64 encode it again
            if (!string.IsNullOrEmpty(_lastLoadedSavePath) && File.Exists(_lastLoadedSavePath))
            {
                try
                {
                    _cachedSaveString = File.ReadAllText(_lastLoadedSavePath);
                    Plugin.DebugLog($"[AutoLoad] Cached save data from file: {_cachedSaveString.Length} chars");
                    return _cachedSaveString;
                }
                catch (Exception ex)
                {
                    Plugin.DebugLog($"[AutoLoad] Failed to read cached save: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>
        /// Cache the save file data right after loading
        /// </summary>
        private static void CacheSaveData(string savePath)
        {
            try
            {
                _lastLoadedSavePath = savePath;

                if (File.Exists(savePath))
                {
                    // Read as text - the file is already in the correct format:
                    // Line 1: JSON metadata
                    // Line 2+: Base64-encoded LZ4-compressed MessagePack
                    _cachedSaveString = File.ReadAllText(savePath);
                    Plugin.DebugLog($"[AutoLoad] Cached save file: {savePath} ({_cachedSaveString.Length} chars)");
                }
                else
                {
                    Plugin.DebugLog($"[AutoLoad] Cannot cache save - file not found: {savePath}");
                }
            }
            catch (Exception ex)
            {
                Plugin.DebugLog($"[AutoLoad] Error caching save data: {ex.Message}");
            }
        }

        /// <summary>
        /// Attempts to auto-load a save file if configured.
        /// Called from the main menu patch.
        /// </summary>
        public static void TryAutoLoad()
        {
            Plugin.DebugLog("[AutoLoad] TryAutoLoad called!");

            if (_hasAttemptedAutoLoad)
            {
                Plugin.DebugLog("[AutoLoad] Already attempted, skipping");
                return;
            }
            _hasAttemptedAutoLoad = true;

            // Check if auto-load is configured
            bool hasAutoLoadPath = !string.IsNullOrEmpty(Plugin.AutoLoadSave.Value);
            bool hasAutoLoadSlot = Plugin.AutoLoadSlot.Value >= 0;

            Plugin.DebugLog($"[AutoLoad] hasAutoLoadPath={hasAutoLoadPath} ({Plugin.AutoLoadSave.Value}), hasAutoLoadSlot={hasAutoLoadSlot} ({Plugin.AutoLoadSlot.Value})");

            if (!hasAutoLoadPath && !hasAutoLoadSlot)
            {
                Plugin.DebugLog("[AutoLoad] No auto-load configured");
                return;
            }

            if (!Plugin.AutoStartServer.Value)
            {
                Plugin.DebugLog("[AutoLoad] AutoLoadSave/Slot specified but AutoStartServer is false. Skipping auto-load.");
                return;
            }

            Plugin.DebugLog("[AutoLoad] Starting auto-load process...");
            _isAutoLoading = true;

            // Try direct load first (for main thread context), then coroutine
            Plugin.DebugLog("[AutoLoad] Attempting direct (non-coroutine) auto-load...");
            TryAutoLoadDirect();
        }

        /// <summary>
        /// Direct (non-coroutine) version of auto-load for when called from non-main thread.
        /// </summary>
        private static void TryAutoLoadDirect()
        {
            Plugin.DebugLog("[AutoLoad-Direct] Starting direct auto-load...");

            // Find FlowManager type
            var flowManagerType = AccessTools.TypeByName("FlowManager");
            if (flowManagerType == null)
            {
                Plugin.DebugLog("[AutoLoad-Direct] ERROR: FlowManager type not found!");
                return;
            }

            Plugin.DebugLog($"[AutoLoad-Direct] Found FlowManager type: {flowManagerType.FullName}");

            // Check if FlowManager.instance exists
            var instanceField = flowManagerType.GetField("instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            var flowManager = instanceField?.GetValue(null);

            Plugin.DebugLog($"[AutoLoad-Direct] FlowManager.instance: {(flowManager != null ? "exists" : "NULL")}");

            if (flowManager == null)
            {
                Plugin.DebugLog("[AutoLoad-Direct] FlowManager not ready yet - we may need to wait");
                // FlowManager isn't available yet, we can't load directly
                return;
            }

            // Load save file
            object saveState = null;
            string savePath = null;

            if (!string.IsNullOrEmpty(Plugin.AutoLoadSave.Value))
            {
                savePath = Plugin.AutoLoadSave.Value;
                saveState = LoadSaveFromPath(savePath);
            }
            else if (Plugin.AutoLoadSlot.Value >= 0)
            {
                saveState = LoadSaveFromSlot(Plugin.AutoLoadSlot.Value);
            }

            if (saveState == null)
            {
                Plugin.DebugLog("[AutoLoad-Direct] ERROR: Failed to load save file!");
                return;
            }

            Plugin.DebugLog($"[AutoLoad-Direct] Save loaded successfully! Type: {saveState.GetType().Name}");

            // CRITICAL: Cache the save data immediately for sending to clients
            // This is needed because PrepSave() fails in headless mode
            if (!string.IsNullOrEmpty(savePath))
            {
                CacheSaveData(savePath);
            }

            // Call FlowManager.LoadSaveGame(saveState)
            var loadSaveGameMethod = flowManagerType.GetMethod("LoadSaveGame",
                BindingFlags.Public | BindingFlags.Static);

            if (loadSaveGameMethod == null)
            {
                Plugin.DebugLog("[AutoLoad-Direct] ERROR: FlowManager.LoadSaveGame method not found!");
                return;
            }

            Plugin.DebugLog("[AutoLoad-Direct] Calling FlowManager.LoadSaveGame...");

            try
            {
                // LoadSaveGame(SaveState loadingState, Action callback = null, bool asClient = false)
                loadSaveGameMethod.Invoke(null, new object[] { saveState, null, false });
                Plugin.DebugLog("[AutoLoad-Direct] LoadSaveGame called successfully!");

                // CRITICAL: The game sets Time.timeScale = 0 during loading which can freeze coroutines
                // We need to reset it to allow the loading coroutine to progress
                if (Time.timeScale < 0.01f)
                {
                    Plugin.DebugLog("[AutoLoad-Direct] Time.timeScale was 0, resetting to 1 to allow loading...");
                    Time.timeScale = 1f;
                }

                // CRITICAL: Set SaveState.saveOpStatus to SaveSucceeded
                // This is required for SendSaveString coroutine to send save data to clients
                // Without this, clients get stuck on "Getting Save File From Host"
                try
                {
                    var saveStateType = AccessTools.TypeByName("SaveState");
                    if (saveStateType != null)
                    {
                        var saveOpStatusField = saveStateType.GetField("saveOpStatus",
                            BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                        if (saveOpStatusField != null)
                        {
                            // Get the SaveOpStatus enum type and find SaveSucceeded value
                            var statusType = saveOpStatusField.FieldType;
                            var succeededValue = Enum.Parse(statusType, "SaveSucceeded");
                            saveOpStatusField.SetValue(null, succeededValue);
                            Plugin.DebugLog("[AutoLoad-Direct] Set SaveState.saveOpStatus = SaveSucceeded");
                        }
                        else
                        {
                            Plugin.DebugLog("[AutoLoad-Direct] WARNING: Could not find saveOpStatus field");
                        }
                    }
                }
                catch (Exception statusEx)
                {
                    Plugin.DebugLog($"[AutoLoad-Direct] WARNING: Failed to set saveOpStatus: {statusEx.Message}");
                }

                // Start the server after a short delay (no scene loading needed in headless mode)
                Plugin.DebugLog("[AutoLoad-Direct] Scheduling server start in 5 seconds...");
                _serverStartScheduledTime = Time.realtimeSinceStartup + 5f;
                _serverStartPending = true;
            }
            catch (Exception ex)
            {
                Plugin.DebugLog($"[AutoLoad-Direct] LoadSaveGame error: {ex}");
            }
        }

        private static IEnumerator AutoLoadCoroutine()
        {
            Plugin.DebugLog("[AutoLoad] AutoLoadCoroutine started!");

            // Wait for the game to fully initialize (use realtime for menu)
            yield return new WaitForSecondsRealtime(2f);

            Plugin.DebugLog("[AutoLoad] Looking for FlowManager type...");

            // Wait for FlowManager to be ready
            var flowManagerType = AccessTools.TypeByName("FlowManager");
            if (flowManagerType == null)
            {
                Plugin.DebugLog("[AutoLoad] ERROR: FlowManager type not found!");
                _isAutoLoading = false;
                yield break;
            }

            Plugin.DebugLog($"[AutoLoad] Found FlowManager type: {flowManagerType.FullName}");

            var instanceField = flowManagerType.GetField("instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            if (instanceField == null)
            {
                Plugin.DebugLog("[AutoLoad] ERROR: FlowManager.instance field not found!");
                _isAutoLoading = false;
                yield break;
            }

            Plugin.DebugLog("[AutoLoad] Waiting for FlowManager.instance...");

            object flowManager = null;
            int waitAttempts = 0;
            while (flowManager == null && waitAttempts < 30)
            {
                flowManager = instanceField.GetValue(null);
                if (flowManager == null)
                {
                    if (waitAttempts % 5 == 0)
                    {
                        Plugin.DebugLog($"[AutoLoad] Still waiting for FlowManager.instance... attempt {waitAttempts}");
                    }
                    yield return new WaitForSecondsRealtime(0.5f);
                    waitAttempts++;
                }
            }

            if (flowManager == null)
            {
                Plugin.DebugLog("[AutoLoad] ERROR: FlowManager.instance is null after waiting!");
                _isAutoLoading = false;
                yield break;
            }

            Plugin.DebugLog("[AutoLoad] FlowManager ready, loading save...");

            // Try to load save
            object saveState = null;

            Plugin.DebugLog($"[AutoLoad] Attempting to load save. Path='{Plugin.AutoLoadSave.Value}', Slot={Plugin.AutoLoadSlot.Value}");

            if (!string.IsNullOrEmpty(Plugin.AutoLoadSave.Value))
            {
                Plugin.DebugLog($"[AutoLoad] Loading from path: {Plugin.AutoLoadSave.Value}");
                saveState = LoadSaveFromPath(Plugin.AutoLoadSave.Value);
            }
            else if (Plugin.AutoLoadSlot.Value >= 0)
            {
                Plugin.DebugLog($"[AutoLoad] Loading from slot: {Plugin.AutoLoadSlot.Value}");
                saveState = LoadSaveFromSlot(Plugin.AutoLoadSlot.Value);
            }

            if (saveState == null)
            {
                Plugin.DebugLog("[AutoLoad] ERROR: Failed to load save file!");
                _isAutoLoading = false;
                yield break;
            }

            Plugin.DebugLog($"[AutoLoad] Save loaded successfully! Type: {saveState.GetType().Name}");

            // Call FlowManager.LoadSaveGame(saveState)
            var loadSaveGameMethod = flowManagerType.GetMethod("LoadSaveGame",
                BindingFlags.Public | BindingFlags.Static);

            if (loadSaveGameMethod == null)
            {
                Plugin.Log.LogError("[AutoLoad] FlowManager.LoadSaveGame method not found!");
                _isAutoLoading = false;
                yield break;
            }

            // Create callback to start server after load
            Action onLoadComplete = () =>
            {
                Plugin.Log.LogInfo("[AutoLoad] Game loaded, starting server...");
                Plugin.Instance.StartCoroutine(StartServerAfterDelay());
            };

            try
            {
                // LoadSaveGame(SaveState loadingState, Action callback = null, bool asClient = false)
                loadSaveGameMethod.Invoke(null, new object[] { saveState, onLoadComplete, false });
                Plugin.Log.LogInfo("[AutoLoad] LoadSaveGame called successfully");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AutoLoad] Failed to call LoadSaveGame: {ex}");
                _isAutoLoading = false;
            }
        }

        private static IEnumerator StartServerAfterDelay()
        {
            // Wait for the world to fully load
            yield return new WaitForSecondsRealtime(5f);

            Plugin.Log.LogInfo("[AutoLoad] Starting dedicated server...");

            if (DirectConnectManager.StartServer())
            {
                var addr = DirectConnectManager.GetServerAddress();
                Plugin.Log.LogInfo($"[AutoLoad] Server started successfully on {addr}");
            }
            else
            {
                Plugin.Log.LogError("[AutoLoad] Failed to start server!");
            }

            _isAutoLoading = false;
        }

        private static object LoadSaveFromPath(string path)
        {
            try
            {
                Plugin.DebugLog($"[AutoLoad] Loading save from path: {path}");

                // Check if file exists
                if (!File.Exists(path))
                {
                    Plugin.DebugLog($"[AutoLoad] ERROR: Save file does not exist at path: {path}");
                    return null;
                }

                Plugin.DebugLog($"[AutoLoad] Save file exists, size: {new FileInfo(path).Length} bytes");

                var saveStateType = AccessTools.TypeByName("SaveState");
                if (saveStateType == null)
                {
                    Plugin.DebugLog("[AutoLoad] ERROR: SaveState type not found!");
                    return null;
                }

                Plugin.DebugLog($"[AutoLoad] Found SaveState type: {saveStateType.FullName}");

                // List all available methods for debugging
                var methods = saveStateType.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                Plugin.DebugLog($"[AutoLoad] SaveState has {methods.Length} methods. Looking for load methods...");

                foreach (var m in methods)
                {
                    if (m.Name.Contains("Load"))
                    {
                        var parms = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                        Plugin.DebugLog($"[AutoLoad]   Found: {m.Name}({parms})");
                    }
                }

                // Try LoadFileDataFromAbsolutePath FIRST since we're using absolute paths
                var loadAbsMethod = saveStateType.GetMethod("LoadFileDataFromAbsolutePath",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);

                if (loadAbsMethod != null)
                {
                    Plugin.DebugLog("[AutoLoad] Using LoadFileDataFromAbsolutePath method");
                    var result = loadAbsMethod.Invoke(null, new object[] { path });
                    Plugin.DebugLog($"[AutoLoad] LoadFileDataFromAbsolutePath returned: {result?.GetType().Name ?? "null"}");
                    if (result != null) return result;
                    Plugin.DebugLog("[AutoLoad] LoadFileDataFromAbsolutePath returned null, trying other methods...");
                }

                // Try with Wine Z: drive path (absolute paths become Z:\path\to\file)
                string winePath = "Z:" + path.Replace("/", "\\");
                Plugin.DebugLog($"[AutoLoad] Trying with Wine path: {winePath}");

                // Try LoadFileData(string saveLocation) with Wine path
                var loadMethod = saveStateType.GetMethod("LoadFileData",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { typeof(string) },
                    null);

                if (loadMethod != null)
                {
                    Plugin.DebugLog("[AutoLoad] Using LoadFileData(string) with Wine path");
                    var result = loadMethod.Invoke(null, new object[] { winePath });
                    Plugin.DebugLog($"[AutoLoad] LoadFileData(winePath) returned: {result?.GetType().Name ?? "null"}");
                    if (result != null) return result;

                    // Also try original Unix path
                    Plugin.DebugLog("[AutoLoad] Trying LoadFileData with original Unix path");
                    result = loadMethod.Invoke(null, new object[] { path });
                    Plugin.DebugLog($"[AutoLoad] LoadFileData(unixPath) returned: {result?.GetType().Name ?? "null"}");
                    if (result != null) return result;
                }

                Plugin.DebugLog("[AutoLoad] ERROR: No suitable load method found!");
                return null;
            }
            catch (Exception ex)
            {
                Plugin.DebugLog($"[AutoLoad] ERROR loading save from path: {ex}");
                return null;
            }
        }

        private static object LoadSaveFromSlot(int slot)
        {
            try
            {
                Plugin.Log.LogInfo($"[AutoLoad] Loading save from slot: {slot}");

                var saveStateType = AccessTools.TypeByName("SaveState");
                if (saveStateType == null)
                {
                    Plugin.Log.LogError("[AutoLoad] SaveState type not found!");
                    return null;
                }

                // Get saves in slot
                var getSavesMethod = saveStateType.GetMethod("GetSavesInSlot",
                    BindingFlags.Public | BindingFlags.Static);

                if (getSavesMethod == null)
                {
                    Plugin.Log.LogError("[AutoLoad] GetSavesInSlot method not found!");
                    return null;
                }

                var saves = getSavesMethod.Invoke(null, new object[] { slot }) as System.Collections.IList;
                if (saves == null || saves.Count == 0)
                {
                    Plugin.Log.LogError($"[AutoLoad] No saves found in slot {slot}!");
                    return null;
                }

                // Get the most recent save (first in list)
                var saveMetadata = saves[0];
                Plugin.Log.LogInfo($"[AutoLoad] Found save in slot {slot}");

                // Load the save using metadata
                var loadMethod = saveStateType.GetMethod("LoadFileData",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new Type[] { saveMetadata.GetType(), typeof(string) },
                    null);

                if (loadMethod != null)
                {
                    return loadMethod.Invoke(null, new object[] { saveMetadata, null });
                }

                Plugin.Log.LogError("[AutoLoad] LoadFileData(metadata) method not found!");
                return null;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AutoLoad] Error loading save from slot: {ex}");
                return null;
            }
        }
    }

    /// <summary>
    /// Harmony's AccessTools-like helper for finding types.
    /// </summary>
    public static class AccessTools
    {
        public static Type TypeByName(string name)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = asm.GetType(name);
                    if (type != null) return type;

                    foreach (var t in asm.GetTypes())
                    {
                        if (t.Name == name) return t;
                    }
                }
                catch
                {
                    // Ignore assemblies that can't be searched
                }
            }
            return null;
        }
    }
}
