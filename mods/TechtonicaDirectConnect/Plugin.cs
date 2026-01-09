using System;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using kcp2k;
using UnityEngine;
using UnityEngine.SceneManagement;
using Mirror.FizzySteam;

namespace TechtonicaDirectConnect
{
    [BepInPlugin(PluginInfo.PLUGIN_GUID, PluginInfo.PLUGIN_NAME, PluginInfo.PLUGIN_VERSION)]
    [BepInProcess("Techtonica.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log { get; private set; }
        public static ConfigEntry<int> DefaultPort { get; private set; }
        public static ConfigEntry<string> LastServerAddress { get; private set; }
        public static ConfigEntry<KeyCode> ConnectHotkey { get; private set; }

        private static Plugin _instance;
        public static Plugin Instance => _instance;
        private Harmony _harmony;
        private bool _showConnectUI = false;
        private string _serverAddress = "";
        private string _serverPort = "6968";
        private string _statusMessage = "";
        private bool _isConnecting = false;
        private GUIStyle _windowStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _textFieldStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _statusStyle;
        private Rect _windowRect = new Rect(Screen.width / 2 - 200, Screen.height / 2 - 100, 400, 200);

        private static KcpTransport _kcpTransport;
        private static Transport _originalTransport;
        private static bool _isDirectConnectActive;

        // Windows API for keyboard input (bypasses Rewired)
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private const int VK_F11 = 0x7A;
        private const int VK_ESCAPE = 0x1B;

        private bool _f11WasPressed = false;
        private bool _escWasPressed = false;
        private float _debugTimer = 0f;
        private bool _updateRunning = false;
        private int _lastToggleFrame = -1; // Prevent double-toggle in same frame

        // Pending connection info (for joining from main menu)
        private string _pendingServerAddress = "";
        private int _pendingServerPort = 6968;

        // Loading monitor for dedicated server connections
        private static float _loadingStuckTimer = 0f;
        private static bool _loadingMonitorActive = false;
        private static string _lastLoadingState = "";
        private static bool _finishLoadingCalled = false;
        private static int _lastLoggedSecond = -1;

        private void Awake()
        {
            _instance = this;
            Log = Logger;

            // Make this object persist and be hidden
            this.gameObject.hideFlags = HideFlags.HideAndDontSave;

            // Config
            DefaultPort = Config.Bind("General", "DefaultPort", 6968, "Default server port");
            LastServerAddress = Config.Bind("General", "LastServerAddress", "", "Last connected server address");
            ConnectHotkey = Config.Bind("General", "ConnectHotkey", KeyCode.F11, "Hotkey to open connect dialog");

            // Load last server
            _serverAddress = LastServerAddress.Value;
            _serverPort = DefaultPort.Value.ToString();

            // Apply Harmony patches
            _harmony = new Harmony(PluginInfo.PLUGIN_GUID);
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            // Apply null safety patches for networked player objects
            NullSafetyPatches.ApplyPatches(_harmony);

            // Apply inventory sync patches for headless server compatibility
            InventorySyncPatches.ApplyPatches(_harmony);

            Log.LogInfo($"[{PluginInfo.PLUGIN_NAME}] v{PluginInfo.PLUGIN_VERSION} loaded!");
            Log.LogInfo($"[{PluginInfo.PLUGIN_NAME}] Press F11 to open connect dialog");
        }

        // Heartbeat counter for Update
        private static int _updateHeartbeat = 0;

        // Update runs directly on the plugin (like ConsoleCommands mod)
        private void Update()
        {
            _updateHeartbeat++;

            // Log once to confirm Update is running
            if (!_updateRunning)
            {
                _updateRunning = true;
                Log.LogInfo("[DirectConnect] Update() is running!");
            }

            // Heartbeat log every 5 seconds (300 frames at 60fps)
            if (_updateHeartbeat % 300 == 0 && NetworkClient.active)
            {
                Log.LogInfo($"[DirectConnect] HEARTBEAT: frame={_updateHeartbeat}, monitorActive={_loadingMonitorActive}, timer={_loadingStuckTimer:F1}s, finishCalled={_finishLoadingCalled}");
            }

            // Try Unity's Input first (works for some keys even with Rewired)
            bool f11Unity = Input.GetKeyDown(KeyCode.F11);
            bool escUnity = Input.GetKeyDown(KeyCode.Escape);

            // Also try Windows API as backup
            bool f11Win = (GetAsyncKeyState(VK_F11) & 0x8000) != 0;
            bool escWin = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;

            // Debug: log every 5 seconds
            _debugTimer += Time.deltaTime;
            if (_debugTimer > 5f)
            {
                _debugTimer = 0f;
                Log.LogInfo($"[DirectConnect] F11: Unity={f11Unity}, Win={f11Win}, UI={_showConnectUI}");
            }

            // F11 toggle - try both methods (but skip if OnGUI already handled this frame)
            bool f11Triggered = f11Unity || (f11Win && !_f11WasPressed);
            if (f11Triggered && Time.frameCount != _lastToggleFrame)
            {
                _lastToggleFrame = Time.frameCount;
                _showConnectUI = !_showConnectUI;
                if (_showConnectUI)
                {
                    _statusMessage = "";
                }
                Log.LogInfo($"[DirectConnect] UI toggled via Update: {_showConnectUI}");
            }
            _f11WasPressed = f11Win;

            // ESC to close
            bool escTriggered = escUnity || (escWin && !_escWasPressed);
            if (_showConnectUI && escTriggered)
            {
                _showConnectUI = false;
                Log.LogInfo("[DirectConnect] UI closed via ESC");
            }
            _escWasPressed = escWin;

            // Loading monitor - detect stuck loading and force completion
            CheckLoadingMonitor();
        }

        // Debug logging for loading monitor
        private static bool _loadingMonitorDebugLogged = false;

        // Counter for throttled logging
        private static int _monitorCheckCount = 0;

        /// <summary>
        /// Monitors loading state and forces completion if stuck on "Generating Machines"
        /// This handles the case where NetworkMessageRelay.instance is null on dedicated servers
        /// </summary>
        private void CheckLoadingMonitor()
        {
            _monitorCheckCount++;

            // Only monitor when connected as client
            if (!NetworkClient.active)
            {
                if (_monitorCheckCount % 300 == 0 && _loadingMonitorActive)
                    Log.LogWarning("[DirectConnect] Monitor: NetworkClient not active!");
                return;
            }

            if (_finishLoadingCalled)
            {
                if (_monitorCheckCount % 300 == 0)
                    Log.LogInfo("[DirectConnect] Monitor: finish already called, skipping");
                return;
            }

            try
            {
                // Check if LoadingUI exists and what state it's in
                var loadingUIType = AccessTools.TypeByName("LoadingUI");
                if (loadingUIType == null)
                {
                    if (!_loadingMonitorDebugLogged)
                    {
                        Log.LogWarning("[DirectConnect] LoadingUI type not found!");
                        _loadingMonitorDebugLogged = true;
                    }
                    return;
                }

                // Try to find instance - could be field or property with various names
                object loadingUI = null;

                // Try static field "instance"
                var instanceField = AccessTools.Field(loadingUIType, "instance");
                if (instanceField != null)
                {
                    loadingUI = instanceField.GetValue(null);
                }

                // Try static property "Instance"
                if (loadingUI == null)
                {
                    var instanceProp = AccessTools.Property(loadingUIType, "Instance");
                    if (instanceProp != null)
                    {
                        loadingUI = instanceProp.GetValue(null);
                    }
                }

                // Try FindObjectOfType as fallback
                if (loadingUI == null)
                {
                    loadingUI = UnityEngine.Object.FindObjectOfType(loadingUIType);
                }

                if (loadingUI == null)
                {
                    if (_loadingMonitorActive && _monitorCheckCount % 60 == 0)
                    {
                        Log.LogWarning("[DirectConnect] Monitor: LoadingUI instance became null!");
                    }
                    else if (!_loadingMonitorDebugLogged)
                    {
                        // List all fields for debugging
                        var fields = loadingUIType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                        Log.LogWarning($"[DirectConnect] LoadingUI.instance is null. Available fields: {string.Join(", ", fields.Select(f => f.Name))}");
                        _loadingMonitorDebugLogged = true;
                    }
                    return;
                }

                // Check if loading screen is active by checking the gameObject
                var loadingUIComponent = loadingUI as Component;
                bool goActive = loadingUIComponent != null && loadingUIComponent.gameObject.activeInHierarchy;

                // Check the _loaded field - if false, loading is still in progress
                bool loadedField = false;
                var loadedFieldInfo = loadingUIType.GetField("_loaded", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (loadedFieldInfo != null)
                {
                    try { loadedField = (bool)loadedFieldInfo.GetValue(loadingUI); } catch { }
                }

                // Loading is "active" if gameObject is active AND _loaded is false (still loading)
                bool isActive = goActive && !loadedField;

                // Log state occasionally for debugging
                if (_monitorCheckCount % 60 == 0 && _loadingMonitorActive)
                {
                    Log.LogInfo($"[DirectConnect] Monitor check: goActive={goActive}, _loaded={loadedField}, isActive={isActive}, timer={_loadingStuckTimer:F1}s");
                }

                if (!isActive)
                {
                    if (_loadingMonitorActive)
                    {
                        Log.LogWarning($"[DirectConnect] Monitor: Loading done or inactive! goActive={goActive}, _loaded={loadedField}");
                    }
                    _loadingMonitorActive = false;
                    _loadingStuckTimer = 0f;
                    return;
                }

                // Get current loading state - try multiple field names
                string currentState = "";
                string[] stateFieldNames = { "currentState", "loadingState", "state", "currentLoadingState", "loadState" };
                foreach (var fieldName in stateFieldNames)
                {
                    var stateField = loadingUIType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (stateField != null)
                    {
                        currentState = stateField.GetValue(loadingUI)?.ToString() ?? "";
                        if (!string.IsNullOrEmpty(currentState)) break;
                    }
                }

                // Log the state periodically for debugging
                if (!_loadingMonitorActive && !_loadingMonitorDebugLogged)
                {
                    Log.LogInfo($"[DirectConnect] LoadingUI found! Active={isActive}, State='{currentState}'");

                    // List all fields for debugging
                    var allFields = loadingUIType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    Log.LogInfo($"[DirectConnect] LoadingUI fields: {string.Join(", ", allFields.Select(f => f.Name))}");
                    _loadingMonitorDebugLogged = true;
                }

                // Start monitoring when we're in a loading state (any state while loading screen is active)
                // Changed: Don't wait for specific state, just monitor while loading is active
                if (isActive)
                {
                    if (!_loadingMonitorActive)
                    {
                        _loadingMonitorActive = true;
                        _loadingStuckTimer = 0f;
                        _lastLoadingState = currentState;
                        Log.LogInfo($"[DirectConnect] Loading monitor started at state: '{currentState}'");
                    }

                    // ALWAYS increment timer when isActive (use unscaledDeltaTime because game is paused during loading!)
                    _loadingStuckTimer += Time.unscaledDeltaTime;

                    // Log progress every second
                    int currentSecond = (int)_loadingStuckTimer;
                    if (currentSecond > 0 && currentSecond != _lastLoggedSecond)
                    {
                        _lastLoggedSecond = currentSecond;
                        Log.LogInfo($"[DirectConnect] Loading monitor: {_loadingStuckTimer:F2}s at '{currentState}' (deltaTime={Time.deltaTime:F4})");
                    }

                    // If stuck for more than 3 seconds, force finish loading
                    if (_loadingStuckTimer > 3f && !_finishLoadingCalled)
                        {
                            _finishLoadingCalled = true;
                            Log.LogWarning($"[DirectConnect] Loading stuck for {_loadingStuckTimer:F1}s at '{currentState}' - forcing completion!");

                            // Call OnFinishLoading directly
                            var onFinishMethod = AccessTools.Method(loadingUIType, "OnFinishLoading");
                            if (onFinishMethod != null)
                            {
                                try
                                {
                                    onFinishMethod.Invoke(loadingUI, null);
                                    Log.LogInfo("[DirectConnect] Successfully called LoadingUI.OnFinishLoading()!");

                                    // Also restore timeScale since loading may have paused the game
                                    if (Time.timeScale == 0f)
                                    {
                                        Time.timeScale = 1f;
                                        Log.LogInfo("[DirectConnect] Restored Time.timeScale to 1");
                                    }

                                    // Also set confirmedLoad = true to bypass "click to continue"
                                    var confirmedLoadField = loadingUIType.GetField("confirmedLoad", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (confirmedLoadField != null)
                                    {
                                        confirmedLoadField.SetValue(loadingUI, true);
                                        Log.LogInfo("[DirectConnect] Set confirmedLoad = true");
                                    }

                                    // Ensure _loaded is set to true (reuse loadedFieldInfo from earlier check)
                                    if (loadedFieldInfo != null)
                                    {
                                        loadedFieldInfo.SetValue(loadingUI, true);
                                        Log.LogInfo("[DirectConnect] Set _loaded = true");
                                    }

                                    // Just hide visually but keep active so loading completes
                                    var cgField = loadingUIType.GetField("cg", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (cgField != null)
                                    {
                                        var cg = cgField.GetValue(loadingUI);
                                        if (cg != null)
                                        {
                                            var cgType = cg.GetType();
                                            cgType.GetProperty("alpha")?.SetValue(cg, 0f);  // Hide it
                                            cgType.GetProperty("blocksRaycasts")?.SetValue(cg, false);  // Allow clicks through
                                            Log.LogInfo("[DirectConnect] Set CanvasGroup alpha=0, hidden (but gameObject still active)");
                                        }
                                    }

                                    // Create a dummy NetworkMessageRelay instance if needed to prevent NRE
                                    EnsureNetworkMessageRelayExists();
                                }
                                catch (Exception ex)
                                {
                                    Log.LogError($"[DirectConnect] Error calling OnFinishLoading: {ex.Message}");
                                }
                            }
                            else
                            {
                                // Try other methods to finish loading
                                var methods = loadingUIType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                Log.LogInfo($"[DirectConnect] OnFinishLoading not found. Available methods: {string.Join(", ", methods.Select(m => m.Name))}");
                            }
                        }
                    }
                }
            catch (Exception ex)
            {
                // Always log errors now - they're critical for debugging
                Log.LogError($"[DirectConnect] Loading monitor error: {ex.Message}");
            }
        }

        /// <summary>
        /// Ensures NetworkMessageRelay instance exists by finding a scene relay or creating a dummy.
        /// Prefers scene relays with NetworkIdentity for proper network communication.
        /// </summary>
        private void EnsureNetworkMessageRelayExists()
        {
            try
            {
                var relayType = AccessTools.TypeByName("NetworkMessageRelay");
                if (relayType == null)
                {
                    Log.LogWarning("[DirectConnect] NetworkMessageRelay type not found");
                    return;
                }

                // Check if instance already exists
                var instanceField = relayType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (instanceField == null)
                {
                    Log.LogWarning("[DirectConnect] Could not find NetworkMessageRelay.instance field");
                    return;
                }

                var existingInstance = instanceField.GetValue(null);
                if (existingInstance != null)
                {
                    // Check if it has NetworkIdentity
                    var existingMB = existingInstance as MonoBehaviour;
                    var hasNetId = existingMB?.GetComponent<NetworkIdentity>() != null;
                    Log.LogInfo($"[DirectConnect] NetworkMessageRelay.instance already exists (hasNetworkIdentity={hasNetId})");
                    return;
                }

                // Try to find an existing NetworkMessageRelay in the scene (preferred - has proper NetworkIdentity)
                var sceneRelays = UnityEngine.Object.FindObjectsOfType(relayType);
                Log.LogInfo($"[DirectConnect] Found {sceneRelays.Length} NetworkMessageRelay objects in scene");

                foreach (var relay in sceneRelays)
                {
                    var mb = relay as MonoBehaviour;
                    if (mb != null)
                    {
                        var netId = mb.GetComponent<NetworkIdentity>();
                        Log.LogInfo($"[DirectConnect] Found relay: {mb.name}, hasNetworkIdentity={netId != null}, netId={netId?.netId ?? 0}");

                        if (netId != null)
                        {
                            // Use this scene relay - it has proper networking
                            instanceField.SetValue(null, relay);
                            Log.LogInfo($"[DirectConnect] Using scene NetworkMessageRelay with netId {netId.netId}");
                            return;
                        }
                    }
                }

                // No networked scene relay found - create a dummy (won't be able to send commands properly)
                Log.LogWarning("[DirectConnect] No networked scene relay found - creating dummy (commands may not work!)");
                var dummyGO = new GameObject("DirectConnect_DummyNetworkMessageRelay");
                DontDestroyOnLoad(dummyGO);
                var dummyRelay = dummyGO.AddComponent(relayType);
                instanceField.SetValue(null, dummyRelay);
                Log.LogInfo("[DirectConnect] Created dummy NetworkMessageRelay instance");
            }
            catch (Exception ex)
            {
                Log.LogError($"[DirectConnect] Failed to create NetworkMessageRelay: {ex.Message}");
            }
        }

        // OnGUI runs directly on the plugin
        private void OnGUI()
        {
            // Check for F11 via Event.current (only toggle once per frame)
            var e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.F11)
            {
                // Prevent multiple toggles in the same frame (OnGUI is called multiple times)
                if (Time.frameCount != _lastToggleFrame)
                {
                    _lastToggleFrame = Time.frameCount;
                    _showConnectUI = !_showConnectUI;
                    if (_showConnectUI) _statusMessage = "";
                    Log.LogInfo($"[DirectConnect] UI toggled via Event.current: {_showConnectUI}");
                }
                e.Use();
            }

            if (!_showConnectUI) return;

            InitStyles();

            _windowRect = GUI.Window(12345, _windowRect, DrawConnectWindow, "Direct Connect", _windowStyle);
        }

        private void InitStyles()
        {
            if (_windowStyle != null) return;

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = MakeTexture(2, 2, new Color(0.1f, 0.1f, 0.15f, 0.95f));
            _windowStyle.fontSize = 16;
            _windowStyle.fontStyle = FontStyle.Bold;
            _windowStyle.normal.textColor = new Color(0.65f, 0.55f, 0.98f);
            _windowStyle.padding = new RectOffset(15, 15, 25, 15);

            _labelStyle = new GUIStyle(GUI.skin.label);
            _labelStyle.fontSize = 14;
            _labelStyle.normal.textColor = Color.white;

            _textFieldStyle = new GUIStyle(GUI.skin.textField);
            _textFieldStyle.fontSize = 14;
            _textFieldStyle.normal.background = MakeTexture(2, 2, new Color(0.15f, 0.15f, 0.2f, 1f));
            _textFieldStyle.normal.textColor = Color.white;
            _textFieldStyle.padding = new RectOffset(10, 10, 8, 8);

            _buttonStyle = new GUIStyle(GUI.skin.button);
            _buttonStyle.fontSize = 14;
            _buttonStyle.fontStyle = FontStyle.Bold;
            _buttonStyle.normal.background = MakeTexture(2, 2, new Color(0.49f, 0.23f, 0.93f, 1f));
            _buttonStyle.hover.background = MakeTexture(2, 2, new Color(0.59f, 0.33f, 1f, 1f));
            _buttonStyle.normal.textColor = Color.white;
            _buttonStyle.hover.textColor = Color.white;
            _buttonStyle.padding = new RectOffset(15, 15, 10, 10);

            _statusStyle = new GUIStyle(GUI.skin.label);
            _statusStyle.fontSize = 12;
            _statusStyle.alignment = TextAnchor.MiddleCenter;
            _statusStyle.wordWrap = true;
        }

        private Texture2D MakeTexture(int width, int height, Color color)
        {
            Color[] pixels = new Color[width * height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            Texture2D tex = new Texture2D(width, height);
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        private void DrawConnectWindow(int windowID)
        {
            GUILayout.Space(10);

            // Server Address
            GUILayout.BeginHorizontal();
            GUILayout.Label("Server:", _labelStyle, GUILayout.Width(60));
            _serverAddress = GUILayout.TextField(_serverAddress, _textFieldStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(10);

            // Port
            GUILayout.BeginHorizontal();
            GUILayout.Label("Port:", _labelStyle, GUILayout.Width(60));
            _serverPort = GUILayout.TextField(_serverPort, _textFieldStyle, GUILayout.Width(80));
            GUILayout.EndHorizontal();

            GUILayout.Space(15);

            // Connection status
            bool isConnected = NetworkClient.active;
            if (isConnected)
            {
                _statusStyle.normal.textColor = new Color(0.2f, 0.83f, 0.6f);
                GUILayout.Label($"Connected to {NetworkManager.singleton?.networkAddress}", _statusStyle);

                GUILayout.Space(10);

                if (GUILayout.Button("Disconnect", _buttonStyle))
                {
                    Disconnect();
                }
            }
            else
            {
                // Status message
                if (!string.IsNullOrEmpty(_statusMessage))
                {
                    _statusStyle.normal.textColor = _statusMessage.Contains("Error") || _statusMessage.Contains("Failed")
                        ? new Color(0.97f, 0.44f, 0.44f)
                        : new Color(0.65f, 0.55f, 0.98f);
                    GUILayout.Label(_statusMessage, _statusStyle);
                    GUILayout.Space(5);
                }

                // Connect button
                GUI.enabled = !_isConnecting && !string.IsNullOrWhiteSpace(_serverAddress);
                if (GUILayout.Button(_isConnecting ? "Connecting..." : "Connect", _buttonStyle))
                {
                    Connect();
                }
                GUI.enabled = true;
            }

            GUILayout.Space(10);

            // Close button
            var closeStyle = new GUIStyle(_buttonStyle);
            closeStyle.normal.background = MakeTexture(2, 2, new Color(0.3f, 0.3f, 0.35f, 1f));
            if (GUILayout.Button("Close", closeStyle))
            {
                _showConnectUI = false;
            }

            GUI.DragWindow(new Rect(0, 0, 10000, 30));
        }

        private void Connect()
        {
            if (string.IsNullOrWhiteSpace(_serverAddress))
            {
                _statusMessage = "Please enter a server address";
                return;
            }

            if (!int.TryParse(_serverPort, out int port) || port < 1 || port > 65535)
            {
                _statusMessage = "Invalid port number";
                return;
            }

            try
            {
                // Check if we're in a valid game scene (not main menu)
                var currentScene = SceneManager.GetActiveScene().name;
                Log.LogInfo($"[DirectConnect] Current scene: {currentScene}");

                // Log all loaded scenes
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var scene = SceneManager.GetSceneAt(i);
                    Log.LogInfo($"[DirectConnect] Loaded scene {i}: {scene.name} (active: {scene == SceneManager.GetActiveScene()})");
                }

                // Check if we're in the game world - look for Player Scene or Voxeland Scene
                bool inGameWorld = false;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var sceneName = SceneManager.GetSceneAt(i).name;
                    if (sceneName.Contains("Player") || sceneName.Contains("Voxeland"))
                    {
                        inGameWorld = true;
                        break;
                    }
                }

                if (!inGameWorld)
                {
                    // Not in game world - trigger FlowManager.JoinGameAsClient to load scenes
                    Log.LogInfo("[DirectConnect] Not in game world - triggering JoinGameAsClient to load game scenes...");
                    _statusMessage = "Loading game...";

                    // Store connection info for after scene loads
                    _pendingServerAddress = _serverAddress;
                    _pendingServerPort = port;

                    // IMPORTANT: Enable direct connect transport BEFORE loading scenes
                    // This prevents FizzyFacepunch from trying to use Steam networking
                    if (!EnableDirectConnect(port))
                    {
                        _statusMessage = "Failed to initialize connection";
                        return;
                    }

                    // CRITICAL: Set the network address BEFORE calling JoinGameAsClient
                    // The game will try to connect as part of the join flow
                    var networkManager = NetworkManager.singleton;
                    if (networkManager != null)
                    {
                        networkManager.networkAddress = _serverAddress;
                        Log.LogInfo($"[DirectConnect] Set network address to: {_serverAddress}");
                    }

                    // Call FlowManager.JoinGameAsClient() directly - it's a static method
                    try
                    {
                        // Create callback action for when scene finishes loading
                        Action callback = () => {
                            Log.LogInfo("[DirectConnect] JoinGameAsClient callback - scene loaded!");
                        };

                        // Call the static method directly - this will load scene AND connect
                        FlowManager.JoinGameAsClient(callback);
                        Log.LogInfo("[DirectConnect] Called FlowManager.JoinGameAsClient successfully");
                        return;
                    }
                    catch (Exception ex)
                    {
                        Log.LogError($"[DirectConnect] Failed to call JoinGameAsClient: {ex}");
                        _statusMessage = "Failed to load game - try loading a save first";
                        return;
                    }
                }

                // Already in game world - connect directly
                DoConnect(_serverAddress, port);
            }
            catch (Exception ex)
            {
                _statusMessage = $"Error: {ex.Message}";
                _isConnecting = false;
                Log.LogError($"[DirectConnect] Connection error: {ex}");
            }
        }

        private System.Collections.IEnumerator CheckConnection()
        {
            float timeout = 15f;
            float elapsed = 0f;

            while (elapsed < timeout)
            {
                if (NetworkClient.isConnected)
                {
                    Log.LogInfo("[DirectConnect] Connected! Sending ready signal...");
                    _statusMessage = "Connected! Joining game...";

                    // Tell the server we're ready to receive spawned objects
                    if (!NetworkClient.ready)
                    {
                        try
                        {
                            NetworkClient.Ready();
                            Log.LogInfo("[DirectConnect] Sent Ready signal");
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning($"[DirectConnect] Ready failed: {ex.Message}");
                        }
                    }

                    // Wait a moment for server to process
                    yield return new WaitForSeconds(0.5f);

                    // Request player spawning if available
                    try
                    {
                        if (NetworkClient.ready && NetworkClient.localPlayer == null)
                        {
                            // Try to add player - this triggers server-side player spawning
                            NetworkClient.AddPlayer();
                            Log.LogInfo("[DirectConnect] Requested player spawn");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.LogWarning($"[DirectConnect] AddPlayer failed: {ex.Message}");
                    }

                    _statusMessage = "Connected!";
                    _isConnecting = false;
                    yield return new WaitForSeconds(1f);
                    _showConnectUI = false;
                    yield break;
                }

                if (!NetworkClient.active && !_isConnecting)
                {
                    _statusMessage = "Connection failed";
                    yield break;
                }

                elapsed += 0.5f;
                yield return new WaitForSeconds(0.5f);
            }

            if (!NetworkClient.isConnected)
            {
                _statusMessage = "Connection timed out";
                _isConnecting = false;
                NetworkManager.singleton?.StopClient();
            }
        }

        /// <summary>
        /// Coroutine to connect after JoinGameAsClient callback fires
        /// </summary>
        private System.Collections.IEnumerator ConnectAfterSceneLoad()
        {
            Log.LogInfo("[DirectConnect] ConnectAfterSceneLoad - waiting for game world...");
            _statusMessage = "Loading game world...";

            // Wait for game scene to be fully loaded
            float maxWait = 30f;
            float elapsed = 0f;

            while (elapsed < maxWait)
            {
                // Check if we're in the game world now
                bool inGameWorld = false;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var sceneName = SceneManager.GetSceneAt(i).name;
                    if (sceneName.Contains("Player") || sceneName.Contains("Voxeland"))
                    {
                        inGameWorld = true;
                        break;
                    }
                }

                if (inGameWorld)
                {
                    Log.LogInfo("[DirectConnect] Game world loaded! Starting connection...");
                    yield return new WaitForSeconds(2f); // Give game a moment to fully initialize
                    DoConnect(_pendingServerAddress, _pendingServerPort);
                    yield break;
                }

                elapsed += 0.5f;
                yield return new WaitForSeconds(0.5f);
            }

            _statusMessage = "Timeout waiting for game to load";
            Log.LogError("[DirectConnect] Timeout waiting for game world to load");
        }

        /// <summary>
        /// Coroutine to wait for game world and then connect (polling version)
        /// </summary>
        private System.Collections.IEnumerator WaitForGameWorldAndConnect(int port)
        {
            Log.LogInfo("[DirectConnect] WaitForGameWorldAndConnect - polling for game world...");
            _statusMessage = "Loading game world...";

            _pendingServerAddress = _serverAddress;
            _pendingServerPort = port;

            // Wait for game scene to be fully loaded
            float maxWait = 60f;
            float elapsed = 0f;

            while (elapsed < maxWait)
            {
                // Check if we're in the game world now
                bool inGameWorld = false;
                for (int i = 0; i < SceneManager.sceneCount; i++)
                {
                    var sceneName = SceneManager.GetSceneAt(i).name;
                    Log.LogInfo($"[DirectConnect] Checking scene: {sceneName}");
                    if (sceneName.Contains("Player") || sceneName.Contains("Voxeland"))
                    {
                        inGameWorld = true;
                        break;
                    }
                }

                if (inGameWorld)
                {
                    Log.LogInfo("[DirectConnect] Game world loaded! Starting connection...");
                    yield return new WaitForSeconds(3f); // Give game more time to fully initialize
                    DoConnect(_pendingServerAddress, _pendingServerPort);
                    yield break;
                }

                elapsed += 1f;
                _statusMessage = $"Loading game world... ({elapsed:0}s)";
                yield return new WaitForSeconds(1f);
            }

            _statusMessage = "Timeout waiting for game to load";
            Log.LogError("[DirectConnect] Timeout waiting for game world to load");
        }

        /// <summary>
        /// Actually performs the connection (shared by both Connect and post-scene-load)
        /// </summary>
        private void DoConnect(string address, int port)
        {
            try
            {
                _isConnecting = true;
                _statusMessage = "Connecting...";

                // Save last server
                LastServerAddress.Value = address;

                // Enable direct connect transport
                if (!EnableDirectConnect(port))
                {
                    _statusMessage = "Failed to initialize connection";
                    _isConnecting = false;
                    return;
                }

                var networkManager = NetworkManager.singleton;
                if (networkManager == null)
                {
                    _statusMessage = "Error: NetworkManager not found";
                    _isConnecting = false;
                    return;
                }

                // Resolve DNS if address is a hostname (not an IP)
                string resolvedAddress = address;
                if (!IPAddress.TryParse(address, out _))
                {
                    try
                    {
                        _statusMessage = $"Resolving {address}...";
                        Log.LogInfo($"[DirectConnect] Resolving hostname: {address}");
                        var addresses = Dns.GetHostAddresses(address);
                        foreach (var addr in addresses)
                        {
                            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                resolvedAddress = addr.ToString();
                                Log.LogInfo($"[DirectConnect] Resolved {address} -> {resolvedAddress}");
                                break;
                            }
                        }
                    }
                    catch (Exception dnsEx)
                    {
                        Log.LogWarning($"[DirectConnect] DNS resolution failed: {dnsEx.Message}, trying direct connection...");
                        // Continue with original address - let transport try
                    }
                }

                // Set address
                networkManager.networkAddress = resolvedAddress;

                // Start client
                networkManager.StartClient();
                Log.LogInfo($"[DirectConnect] Connecting to {resolvedAddress}:{port}..." + (resolvedAddress != address ? $" (resolved from {address})" : ""));

                // Auto-close on success after delay
                StartCoroutine(CheckConnection());
            }
            catch (Exception ex)
            {
                _statusMessage = $"Error: {ex.Message}";
                _isConnecting = false;
                Log.LogError($"[DirectConnect] Connection error: {ex}");
            }
        }

        private void Disconnect()
        {
            try
            {
                NetworkManager.singleton?.StopClient();
                DisableDirectConnect();
                NetworkRelayLinkingPatches.Reset(); // Reset linking state for next connection
                _statusMessage = "Disconnected";
                Log.LogInfo("[DirectConnect] Disconnected");
            }
            catch (Exception ex)
            {
                Log.LogError($"[DirectConnect] Disconnect error: {ex}");
            }
        }

        /// <summary>
        /// Public method to show the connect UI (called from main menu patch)
        /// </summary>
        public void ShowConnectUI()
        {
            _showConnectUI = true;
            _statusMessage = "";
            Log.LogInfo("[DirectConnect] Opening connect UI from main menu");
        }

        private static bool EnableDirectConnect(int port)
        {
            try
            {
                var networkManager = NetworkManager.singleton;
                if (networkManager == null)
                {
                    Log.LogError("[DirectConnect] NetworkManager not found!");
                    return false;
                }

                // Get transport field via reflection
                var transportField = typeof(NetworkManager).GetField("transport",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (transportField == null)
                {
                    Log.LogError("[DirectConnect] Could not find transport field!");
                    return false;
                }

                // Store original transport
                _originalTransport = transportField.GetValue(networkManager) as Transport;

                // Create KCP transport if needed
                if (_kcpTransport == null)
                {
                    var transportGO = new GameObject("DirectConnect_KcpTransport");
                    DontDestroyOnLoad(transportGO);
                    _kcpTransport = transportGO.AddComponent<KcpTransport>();
                }

                // Configure KCP - MUST match server settings
                _kcpTransport.Port = (ushort)port;
                _kcpTransport.NoDelay = true;
                _kcpTransport.Interval = 10;
                _kcpTransport.Timeout = 10000;
                _kcpTransport.DualMode = false; // IPv4 only - MUST match server

                // Swap transport
                transportField.SetValue(networkManager, _kcpTransport);

                // Set active transport
                var activeTransportField = typeof(Transport).GetField("activeTransport",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                activeTransportField?.SetValue(null, _kcpTransport);

                _isDirectConnectActive = true;
                Log.LogInfo($"[DirectConnect] Switched to KCP transport on port {port}");

                return true;
            }
            catch (Exception ex)
            {
                Log.LogError($"[DirectConnect] Failed to enable: {ex}");
                return false;
            }
        }

        private static void DisableDirectConnect()
        {
            if (_originalTransport != null && NetworkManager.singleton != null)
            {
                try
                {
                    var transportField = typeof(NetworkManager).GetField("transport",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    transportField?.SetValue(NetworkManager.singleton, _originalTransport);

                    var activeTransportField = typeof(Transport).GetField("activeTransport",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    activeTransportField?.SetValue(null, _originalTransport);

                    _isDirectConnectActive = false;
                    Log.LogInfo("[DirectConnect] Restored original transport");
                }
                catch (Exception ex)
                {
                    Log.LogError($"[DirectConnect] Failed to restore transport: {ex}");
                }
            }
        }

        private void OnDestroy()
        {
            _harmony?.UnpatchSelf();
            if (_kcpTransport != null)
            {
                Destroy(_kcpTransport.gameObject);
            }
        }
    }

    public static class PluginInfo
    {
        public const string PLUGIN_GUID = "com.certifried.techtonicadirectconnect";
        public const string PLUGIN_NAME = "Techtonica Direct Connect";
        public const string PLUGIN_VERSION = "1.0.71";
    }

    /// <summary>
    /// Patches to link client's scene NetworkMessageRelay to server's dynamic relay netId.
    /// This is CRITICAL for Commands to work - Mirror routes Commands by netId.
    /// Server creates dynamic relay (netId 1), client has scene relay (different netId).
    /// We intercept spawn messages and link them so Commands route properly.
    /// </summary>
    [HarmonyPatch]
    public static class NetworkRelayLinkingPatches
    {
        private static bool _relayLinked = false;
        private static bool _loggedOnce = false;

        /// <summary>
        /// Reset linking state when disconnecting
        /// </summary>
        public static void Reset()
        {
            _relayLinked = false;
            _loggedOnce = false;
        }

        /// <summary>
        /// Patch NetworkClient.OnSpawn to intercept spawn messages.
        /// When we see a spawn with no assetId/sceneId (the server's dynamic relay),
        /// we link our scene relay to that netId so Commands route properly.
        /// Returns false to skip original method when we handle the spawn ourselves.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NetworkClient), "OnSpawn")]
        public static bool OnSpawn_Prefix(SpawnMessage message)
        {
            // Check if this spawn will fail (no assetId, no sceneId)
            // This is likely the server's dynamic NetworkMessageRelay
            if (message.assetId == Guid.Empty && message.sceneId == 0)
            {
                if (!_loggedOnce)
                {
                    Plugin.Log.LogInfo($"[DirectConnect] Intercepted spawn for netId {message.netId} with no assetId/sceneId - will try to link scene relay");
                    _loggedOnce = true;
                }

                // Only try to link once per session
                if (!_relayLinked)
                {
                    // Try to link our scene NetworkMessageRelay to this netId
                    if (LinkSceneRelayToNetId(message.netId))
                    {
                        _relayLinked = true;
                        // Skip original method - we handled it
                        return false;
                    }
                }
                else
                {
                    // Already linked, skip this spawn to avoid error
                    return false;
                }
            }

            // Let original method run for other spawns
            return true;
        }

        /// <summary>
        /// Links the client's scene NetworkMessageRelay to the server's netId.
        /// This makes Commands from the client use the correct netId that the server expects.
        /// If no scene relay exists, creates one with NetworkIdentity and links it.
        /// </summary>
        private static bool LinkSceneRelayToNetId(uint targetNetId)
        {
            try
            {
                var relayType = AccessTools.TypeByName("NetworkMessageRelay");
                if (relayType == null)
                {
                    Plugin.Log.LogWarning("[DirectConnect] NetworkMessageRelay type not found");
                    return false;
                }

                // Find scene NetworkMessageRelay with NetworkIdentity
                var sceneRelays = UnityEngine.Object.FindObjectsOfType(relayType);
                Plugin.Log.LogInfo($"[DirectConnect] Found {sceneRelays.Length} NetworkMessageRelay objects in scene");

                NetworkIdentity identity = null;
                object relay = null;

                foreach (var existingRelay in sceneRelays)
                {
                    var mb = existingRelay as MonoBehaviour;
                    if (mb == null) continue;

                    identity = mb.GetComponent<NetworkIdentity>();
                    if (identity != null)
                    {
                        relay = existingRelay;
                        Plugin.Log.LogInfo($"[DirectConnect] Found scene relay '{mb.name}' with identity");
                        break;
                    }
                    else
                    {
                        Plugin.Log.LogInfo($"[DirectConnect] Relay '{mb.name}' has no NetworkIdentity");
                    }
                }

                // If no scene relay with NetworkIdentity found, CREATE one
                if (relay == null || identity == null)
                {
                    Plugin.Log.LogInfo($"[DirectConnect] No scene relay found - creating networked relay for netId {targetNetId}");

                    var relayGO = new GameObject("DirectConnect_NetworkMessageRelay");
                    GameObject.DontDestroyOnLoad(relayGO);

                    // Add NetworkIdentity FIRST
                    identity = relayGO.AddComponent<NetworkIdentity>();

                    // Add the relay component
                    relay = relayGO.AddComponent(relayType);

                    // CRITICAL: Manually set the netIdentity backing field on the NetworkBehaviour
                    // When adding components at runtime, the caching doesn't happen automatically
                    var netIdentityField = typeof(NetworkBehaviour).GetField("<netIdentity>k__BackingField",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                    if (netIdentityField != null)
                    {
                        netIdentityField.SetValue(relay, identity);
                        Plugin.Log.LogInfo($"[DirectConnect] Set netIdentity backing field on relay");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[DirectConnect] Could not find netIdentity backing field!");
                    }

                    // CRITICAL: Set up the NetworkBehaviours array on the NetworkIdentity
                    // This is normally done in Awake() by InitializeNetworkBehaviours()
                    // Without this, Mirror can't find component [0] for RPCs
                    var networkBehavioursProperty = typeof(NetworkIdentity).GetProperty("NetworkBehaviours",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (networkBehavioursProperty != null)
                    {
                        var setter = networkBehavioursProperty.GetSetMethod(true);
                        if (setter != null)
                        {
                            var behaviours = new NetworkBehaviour[] { relay as NetworkBehaviour };
                            setter.Invoke(identity, new object[] { behaviours });
                            Plugin.Log.LogInfo($"[DirectConnect] Set NetworkBehaviours array on identity");
                        }
                        else
                        {
                            Plugin.Log.LogWarning("[DirectConnect] Could not get NetworkBehaviours setter!");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[DirectConnect] Could not find NetworkBehaviours property!");
                    }

                    // CRITICAL: Set ComponentIndex on the relay
                    // This tells Mirror which index this behaviour is in the array
                    var componentIndexProp = typeof(NetworkBehaviour).GetProperty("ComponentIndex",
                        BindingFlags.Public | BindingFlags.Instance);
                    if (componentIndexProp != null)
                    {
                        var setter = componentIndexProp.GetSetMethod(true);
                        if (setter != null)
                        {
                            setter.Invoke(relay, new object[] { (byte)0 });
                            Plugin.Log.LogInfo($"[DirectConnect] Set ComponentIndex = 0 on relay");
                        }
                        else
                        {
                            Plugin.Log.LogWarning("[DirectConnect] Could not get ComponentIndex setter!");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[DirectConnect] Could not find ComponentIndex property!");
                    }

                    Plugin.Log.LogInfo($"[DirectConnect] Created NetworkMessageRelay with NetworkIdentity");
                }

                // Link it to the server's netId
                Plugin.Log.LogInfo($"[DirectConnect] Linking relay to netId {targetNetId}");

                // Set the netId using reflection (it has internal setter)
                bool netIdSet = false;
                var netIdField = typeof(NetworkIdentity).GetField("_netId", BindingFlags.NonPublic | BindingFlags.Instance);
                if (netIdField == null)
                {
                    netIdField = typeof(NetworkIdentity).GetField("netId", BindingFlags.NonPublic | BindingFlags.Instance);
                }

                if (netIdField != null)
                {
                    netIdField.SetValue(identity, targetNetId);
                    Plugin.Log.LogInfo($"[DirectConnect] Set identity._netId = {targetNetId} via field");
                    netIdSet = true;
                }
                else
                {
                    // Try property with reflection
                    var netIdProp = typeof(NetworkIdentity).GetProperty("netId", BindingFlags.Public | BindingFlags.Instance);
                    if (netIdProp != null)
                    {
                        var setter = netIdProp.GetSetMethod(true);
                        if (setter != null)
                        {
                            setter.Invoke(identity, new object[] { targetNetId });
                            Plugin.Log.LogInfo($"[DirectConnect] Set identity.netId = {targetNetId} via property setter");
                            netIdSet = true;
                        }
                    }
                }

                if (!netIdSet)
                {
                    Plugin.Log.LogError($"[DirectConnect] Could not set netId on identity!");
                    return false;
                }

                // Add to spawned dictionary so Mirror can find it for Commands
                if (!NetworkClient.spawned.ContainsKey(targetNetId))
                {
                    NetworkClient.spawned[targetNetId] = identity;
                    Plugin.Log.LogInfo($"[DirectConnect] Added identity to NetworkClient.spawned[{targetNetId}]");
                }

                // Set the static instance field on NetworkMessageRelay
                var instanceField = relayType.GetField("instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (instanceField != null)
                {
                    instanceField.SetValue(null, relay);
                    Plugin.Log.LogInfo($"[DirectConnect] Set NetworkMessageRelay.instance");
                }

                // Mark as client-side object
                var isClientField = typeof(NetworkIdentity).GetField("isClient", BindingFlags.NonPublic | BindingFlags.Instance);
                if (isClientField != null)
                {
                    isClientField.SetValue(identity, true);
                }

                // Also try to set hasAuthority for client-owned commands
                var hasAuthorityField = typeof(NetworkIdentity).GetField("_hasAuthority", BindingFlags.NonPublic | BindingFlags.Instance);
                if (hasAuthorityField != null)
                {
                    hasAuthorityField.SetValue(identity, true);
                    Plugin.Log.LogInfo($"[DirectConnect] Set hasAuthority = true");
                }

                // CRITICAL: Set connectionToServer so commands can be sent to server
                // Without this, SendCommandInternal fails with null reference
                // connectionToServer is a PROPERTY with internal set, not a field
                var connectionToServerProp = typeof(NetworkIdentity).GetProperty("connectionToServer",
                    BindingFlags.Public | BindingFlags.Instance);
                if (connectionToServerProp != null && NetworkClient.connection != null)
                {
                    var setter = connectionToServerProp.GetSetMethod(true); // true = get non-public setter
                    if (setter != null)
                    {
                        setter.Invoke(identity, new object[] { NetworkClient.connection });
                        Plugin.Log.LogInfo($"[DirectConnect] Set connectionToServer = NetworkClient.connection (via property)");
                    }
                    else
                    {
                        // Try direct property set (might work if accessible)
                        try
                        {
                            connectionToServerProp.SetValue(identity, NetworkClient.connection);
                            Plugin.Log.LogInfo($"[DirectConnect] Set connectionToServer = NetworkClient.connection (direct)");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[DirectConnect] Could not set connectionToServer: {ex.Message}");
                        }
                    }
                }
                else
                {
                    Plugin.Log.LogWarning($"[DirectConnect] Could not set connectionToServer: prop={connectionToServerProp != null}, connection={NetworkClient.connection != null}");
                }

                Plugin.Log.LogInfo($"[DirectConnect] Successfully linked NetworkMessageRelay to server's netId {targetNetId}!");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Failed to link scene relay: {ex}");
                return false;
            }
        }
    }

    /// <summary>
    /// Patches to suppress FizzyFacepunch (Steam transport) errors.
    /// These occur because Steam isn't properly initialized for direct connect.
    /// </summary>
    [HarmonyPatch]
    public static class FizzyFacepunchPatches
    {
        /// <summary>
        /// Patch FizzyFacepunch.ClientEarlyUpdate to prevent NullReferenceException
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mirror.FizzySteam.FizzyFacepunch), "ClientEarlyUpdate")]
        public static bool ClientEarlyUpdate_Prefix()
        {
            // Skip if we're using direct connect (KCP transport)
            return false; // Always skip - we don't use Steam transport
        }

        /// <summary>
        /// Patch FizzyFacepunch.ClientLateUpdate to prevent NullReferenceException
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mirror.FizzySteam.FizzyFacepunch), "ClientLateUpdate")]
        public static bool ClientLateUpdate_Prefix()
        {
            // Skip if we're using direct connect (KCP transport)
            return false; // Always skip - we don't use Steam transport
        }

        /// <summary>
        /// Patch FizzyFacepunch.ServerEarlyUpdate to prevent NullReferenceException
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mirror.FizzySteam.FizzyFacepunch), "ServerEarlyUpdate")]
        public static bool ServerEarlyUpdate_Prefix()
        {
            return false; // Always skip
        }

        /// <summary>
        /// Patch FizzyFacepunch.ServerLateUpdate to prevent NullReferenceException
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(Mirror.FizzySteam.FizzyFacepunch), "ServerLateUpdate")]
        public static bool ServerLateUpdate_Prefix()
        {
            return false; // Always skip
        }
    }

    /// <summary>
    /// Patches to enable the hidden "Join Multiplayer" button in the main menu.
    /// The game has this button but always hides it with flag2 = false.
    /// </summary>
    [HarmonyPatch]
    public static class MainMenuPatches
    {
        /// <summary>
        /// Patch RefreshHiddenButtonState to show the Join Multiplayer button (index 3)
        /// Original code: flag2 = false; menuSelections[3].gameObject.SetActive(flag2);
        /// We want to show menuSelections[3] and hook its click to show our connect UI
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MainMenuUI), "RefreshHiddenButtonState")]
        public static void RefreshHiddenButtonState_Postfix(MainMenuUI __instance)
        {
            try
            {
                // Get menuSelections array via reflection
                var menuSelectionsField = AccessTools.Field(typeof(MainMenuUI), "menuSelections");
                var menuSelections = menuSelectionsField?.GetValue(__instance) as MainMenuItem[];

                if (menuSelections != null && menuSelections.Length > 3)
                {
                    // Show the "Join Multiplayer" button (index 3)
                    menuSelections[3].gameObject.SetActive(true);
                    Plugin.Log.LogInfo("[DirectConnect] Enabled 'Join Multiplayer' button in main menu");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Failed to patch main menu: {ex}");
            }
        }

        /// <summary>
        /// Intercept JoinMultiplayerAsClient to show our custom connect dialog instead
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MainMenuUI), "JoinMultiplayerAsClient")]
        public static bool JoinMultiplayerAsClient_Prefix()
        {
            try
            {
                // Show our custom connect UI instead of the default behavior
                Plugin.Log.LogInfo("[DirectConnect] 'Join Multiplayer' clicked - showing connect dialog");
                Plugin.Instance.ShowConnectUI();
                return false; // Skip original method
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error in JoinMultiplayerAsClient: {ex}");
                return true; // Let original method run on error
            }
        }
    }

    /// <summary>
    /// Patches to prevent null reference exceptions when connecting to dedicated servers.
    /// These errors occur because the game expects local player references that don't exist
    /// in the multiplayer context until fully joined.
    /// </summary>
    public static class NullSafetyPatches
    {
        private static bool _patchesApplied = false;

        public static void ApplyPatches(Harmony harmony)
        {
            if (_patchesApplied) return;

            try
            {
                // NOTE: We do NOT patch NetworkedPlayer.OnStartClient or OnStartLocalPlayer
                // Those methods are REQUIRED for the client to request save data from the server
                // The server mod patches them for headless mode, but client needs them to work

                // Patch ThirdPersonDisplayAnimator.Update and UpdateSillyStuff
                var animatorType = AccessTools.TypeByName("ThirdPersonDisplayAnimator");
                if (animatorType != null)
                {
                    var updateMethod = AccessTools.Method(animatorType, "Update");
                    if (updateMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(Skip_Prefix));
                        harmony.Patch(updateMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched ThirdPersonDisplayAnimator.Update to skip");
                    }

                    var sillyMethod = AccessTools.Method(animatorType, "UpdateSillyStuff");
                    if (sillyMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(Skip_Prefix));
                        harmony.Patch(sillyMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched ThirdPersonDisplayAnimator.UpdateSillyStuff to skip");
                    }
                }
                else
                {
                    Plugin.Log.LogWarning("[DirectConnect] ThirdPersonDisplayAnimator type not found");
                }

                // Patch NetworkMessageRelay.SendNetworkAction - crashes when network isn't connected
                var networkRelayType = AccessTools.TypeByName("NetworkMessageRelay");
                if (networkRelayType != null)
                {
                    var sendMethod = AccessTools.Method(networkRelayType, "SendNetworkAction");
                    if (sendMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(SendNetworkAction_Prefix));
                        var finalizer = new HarmonyMethod(typeof(NullSafetyPatches), nameof(SuppressException_Finalizer));
                        harmony.Patch(sendMethod, prefix: prefix, finalizer: finalizer);
                        Plugin.Log.LogInfo("[DirectConnect] Patched NetworkMessageRelay.SendNetworkAction with prefix and finalizer");
                    }

                    // Patch RequestCurrentSimTick - server might not have NetworkMessageRelay on dedicated servers
                    var requestMethod = AccessTools.Method(networkRelayType, "RequestCurrentSimTick");
                    if (requestMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(RequestCurrentSimTick_Prefix));
                        var finalizer = new HarmonyMethod(typeof(NullSafetyPatches), nameof(RequestCurrentSimTick_Finalizer));
                        harmony.Patch(requestMethod, prefix: prefix, finalizer: finalizer);
                        Plugin.Log.LogInfo("[DirectConnect] Patched NetworkMessageRelay.RequestCurrentSimTick with prefix and finalizer");
                    }

                    // Patch UserCode_ProcessCurrentSimTick - handles server's tick sync response
                    // FactorySimManager.instance might be null on client
                    var processMethod = AccessTools.Method(networkRelayType, "UserCode_ProcessCurrentSimTick");
                    if (processMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(ProcessCurrentSimTick_Prefix));
                        harmony.Patch(processMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched NetworkMessageRelay.UserCode_ProcessCurrentSimTick with prefix");
                    }
                }

                // Patch NetworkedPlayer.OnStopClient to track disconnects
                var networkedPlayerType = AccessTools.TypeByName("NetworkedPlayer");
                if (networkedPlayerType != null)
                {
                    var onStopClientMethod = AccessTools.Method(networkedPlayerType, "OnStopClient");
                    if (onStopClientMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(OnStopClient_Prefix));
                        harmony.Patch(onStopClientMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched NetworkedPlayer.OnStopClient for disconnect tracking");
                    }
                }

                // Patch NetworkManager.OnClientDisconnect for more info
                var networkManagerType = typeof(Mirror.NetworkManager);
                var onClientDisconnectMethod = AccessTools.Method(networkManagerType, "OnClientDisconnect");
                if (onClientDisconnectMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(OnClientDisconnect_Prefix));
                    harmony.Patch(onClientDisconnectMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[DirectConnect] Patched NetworkManager.OnClientDisconnect for disconnect tracking");
                }

                // AGGRESSIVE LOGGING: Wrap each patch in try-catch so one failure doesn't stop others

                // Patch NetworkClient.OnSpawn to log ALL spawns
                try
                {
                    var onSpawnMethod = typeof(Mirror.NetworkClient).GetMethod("OnObjectSpawn", BindingFlags.NonPublic | BindingFlags.Static);
                    if (onSpawnMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(OnObjectSpawn_Prefix));
                        harmony.Patch(onSpawnMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched NetworkClient.OnObjectSpawn for spawn tracking");
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[DirectConnect] Failed to patch OnObjectSpawn: {ex.Message}"); }

                // Patch NetworkClient.OnObjectDestroy
                try
                {
                    var onDestroyMethod = typeof(Mirror.NetworkClient).GetMethod("OnObjectDestroy", BindingFlags.NonPublic | BindingFlags.Static);
                    if (onDestroyMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(OnObjectDestroy_Prefix));
                        harmony.Patch(onDestroyMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched NetworkClient.OnObjectDestroy for destroy tracking");
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[DirectConnect] Failed to patch OnObjectDestroy: {ex.Message}"); }

                // Patch NetworkIdentity.HandleRemoteCall (all RPCs)
                try
                {
                    var handleRemoteCallMethod = typeof(Mirror.NetworkIdentity).GetMethod("HandleRemoteCall", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (handleRemoteCallMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(HandleRemoteCall_Prefix));
                        harmony.Patch(handleRemoteCallMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched NetworkIdentity.HandleRemoteCall for RPC tracking");
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[DirectConnect] Failed to patch HandleRemoteCall: {ex.Message}"); }

                // Patch NetworkClient.Connect
                try
                {
                    var connectMethod = typeof(Mirror.NetworkClient).GetMethod("Connect", new Type[] { typeof(string) });
                    if (connectMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(NetworkClient_Connect_Prefix));
                        harmony.Patch(connectMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched NetworkClient.Connect");
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[DirectConnect] Failed to patch NetworkClient.Connect: {ex.Message}"); }

                // Patch KCP transport events
                try
                {
                    var kcpTransportType = typeof(kcp2k.KcpTransport);
                    var onClientConnectedMethod = kcpTransportType.GetMethod("OnClientConnected", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (onClientConnectedMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(KCP_OnClientConnected_Prefix));
                        harmony.Patch(onClientConnectedMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched KcpTransport.OnClientConnected");
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[DirectConnect] Failed to patch KcpTransport.OnClientConnected: {ex.Message}"); }

                try
                {
                    var kcpTransportType = typeof(kcp2k.KcpTransport);
                    var onClientDisconnectedMethod = kcpTransportType.GetMethod("OnClientDisconnected", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (onClientDisconnectedMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(KCP_OnClientDisconnected_Prefix));
                        harmony.Patch(onClientDisconnectedMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched KcpTransport.OnClientDisconnected");
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[DirectConnect] Failed to patch KcpTransport.OnClientDisconnected: {ex.Message}"); }

                // Patch KcpClient.Disconnect to log reason
                try
                {
                    var kcpClientType = typeof(kcp2k.KcpClient);
                    var kcpClientDisconnectMethod = kcpClientType.GetMethod("Disconnect", BindingFlags.Public | BindingFlags.Instance);
                    if (kcpClientDisconnectMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(KcpClient_Disconnect_Prefix));
                        harmony.Patch(kcpClientDisconnectMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched KcpClient.Disconnect");
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[DirectConnect] Failed to patch KcpClient.Disconnect: {ex.Message}"); }

                // Patch KcpConnection.Disconnect to catch the reason
                try
                {
                    var kcpConnectionType = typeof(kcp2k.KcpConnection);
                    var kcpConnectionDisconnectMethod = kcpConnectionType.GetMethod("Disconnect", BindingFlags.Public | BindingFlags.Instance);
                    if (kcpConnectionDisconnectMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(NullSafetyPatches), nameof(KcpConnection_Disconnect_Prefix));
                        harmony.Patch(kcpConnectionDisconnectMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[DirectConnect] Patched KcpConnection.Disconnect");
                    }
                }
                catch (Exception ex) { Plugin.Log.LogWarning($"[DirectConnect] Failed to patch KcpConnection.Disconnect: {ex.Message}"); }

                _patchesApplied = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Failed to apply null safety patches: {ex}");
            }
        }

        /// <summary>
        /// Prefix that skips the original method entirely
        /// </summary>
        public static bool Skip_Prefix()
        {
            return false;
        }

        /// <summary>
        /// Prefix for SendNetworkAction - fixes connectionToServer and netId before command is sent
        /// </summary>
        public static void SendNetworkAction_Prefix(object __instance, object action)
        {
            try
            {
                var behaviour = __instance as NetworkBehaviour;
                var identity = behaviour?.netIdentity;

                // ALWAYS log when this is called to confirm it triggers
                Plugin.Log.LogInfo($"[DirectConnect] SendNetworkAction called! Action: {action?.GetType()?.Name ?? "null"}");

                if (identity == null)
                {
                    Plugin.Log.LogWarning($"[DirectConnect] SendNetworkAction: netIdentity is NULL!");
                    return;
                }

                Plugin.Log.LogInfo($"[DirectConnect] SendNetworkAction: netId={identity.netId}, connToServer={identity.connectionToServer != null}");

                // Fix connectionToServer if needed
                if (identity.connectionToServer == null && NetworkClient.connection != null)
                {
                    Plugin.Log.LogInfo($"[DirectConnect] SendNetworkAction: Fixing connectionToServer...");
                    TrySetConnectionToServer(identity);
                }

                // CRITICAL: Fix netId if it doesn't match server's relay (netId=2)
                // The server's NetworkMessageRelay (MachineMessageRelay) has netId=2
                // spawned[1] = Player Cheats, spawned[2] = MachineMessageRelay
                if (identity.netId != 2)
                {
                    Plugin.Log.LogInfo($"[DirectConnect] SendNetworkAction: Fixing netId from {identity.netId} to 2...");
                    var netIdField = typeof(NetworkIdentity).GetField("_netId", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (netIdField != null)
                    {
                        netIdField.SetValue(identity, (uint)2);
                        Plugin.Log.LogInfo($"[DirectConnect] Set netId = 2");
                    }
                    else
                    {
                        var netIdProp = typeof(NetworkIdentity).GetProperty("netId", BindingFlags.Public | BindingFlags.Instance);
                        if (netIdProp != null)
                        {
                            var setter = netIdProp.GetSetMethod(true);
                            if (setter != null)
                            {
                                setter.Invoke(identity, new object[] { (uint)2 });
                                Plugin.Log.LogInfo($"[DirectConnect] Set netId = 2 via property");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DirectConnect] SendNetworkAction_Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Finalizer that suppresses exceptions (for methods that should not crash the game)
        /// </summary>
        public static Exception SuppressException_Finalizer(Exception __exception)
        {
            // Suppress null reference exceptions - they happen when network isn't ready
            if (__exception is NullReferenceException)
            {
                return null; // Suppress the exception
            }
            return __exception; // Let other exceptions through
        }

        private static bool _requestSimTickHandled = false;

        /// <summary>
        /// Prefix for RequestCurrentSimTick - handles the case where the server doesn't have
        /// NetworkMessageRelay (dedicated servers run on Main Menu scene without Player Scene)
        /// </summary>
        public static bool RequestCurrentSimTick_Prefix(object __instance)
        {
            // Add detailed debug logging to understand SendCommandInternal failures
            try
            {
                var behaviour = __instance as NetworkBehaviour;
                var identity = behaviour?.netIdentity;

                Plugin.Log.LogInfo($"[DirectConnect] RequestCurrentSimTick DEBUG:");
                Plugin.Log.LogInfo($"[DirectConnect]   __instance != null: {__instance != null}");
                Plugin.Log.LogInfo($"[DirectConnect]   As NetworkBehaviour: {behaviour != null}");
                Plugin.Log.LogInfo($"[DirectConnect]   netIdentity: {identity != null}");

                if (identity != null)
                {
                    Plugin.Log.LogInfo($"[DirectConnect]   netId: {identity.netId}");
                    Plugin.Log.LogInfo($"[DirectConnect]   isClient: {identity.isClient}");
                    Plugin.Log.LogInfo($"[DirectConnect]   isServer: {identity.isServer}");
                    Plugin.Log.LogInfo($"[DirectConnect]   hasAuthority: {identity.hasAuthority}");
                    Plugin.Log.LogInfo($"[DirectConnect]   connectionToServer: {identity.connectionToServer != null}");
                    if (identity.connectionToServer != null)
                    {
                        Plugin.Log.LogInfo($"[DirectConnect]   connectionToServer.isReady: {identity.connectionToServer.isReady}");
                    }
                }

                Plugin.Log.LogInfo($"[DirectConnect]   NetworkClient.active: {NetworkClient.active}");
                Plugin.Log.LogInfo($"[DirectConnect]   NetworkClient.isConnected: {NetworkClient.isConnected}");
                Plugin.Log.LogInfo($"[DirectConnect]   NetworkClient.connection: {NetworkClient.connection != null}");
                if (NetworkClient.connection != null)
                {
                    Plugin.Log.LogInfo($"[DirectConnect]   NetworkClient.connection.isReady: {NetworkClient.connection.isReady}");
                }

                // FIX: If connectionToServer is null but we have a valid NetworkClient.connection, set it!
                if (identity != null && identity.connectionToServer == null && NetworkClient.connection != null)
                {
                    Plugin.Log.LogInfo($"[DirectConnect] FIXING: connectionToServer is null, setting to NetworkClient.connection...");
                    TrySetConnectionToServer(identity);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DirectConnect] RequestCurrentSimTick debug logging error: {ex.Message}");
            }

            if (__instance == null)
            {
                Plugin.Log.LogWarning("[DirectConnect] RequestCurrentSimTick called on null instance - dedicated server mode?");
                // Call OnFinishLoading directly since server can't respond
                TryCallOnFinishLoading();
                return false; // Skip original method
            }

            // NOTE: Server now proactively pushes tick, so even if this command fails,
            // the client will receive tick via ProcessCurrentSimTick
            Plugin.Log.LogInfo("[DirectConnect] RequestCurrentSimTick proceeding to original method...");
            return true; // Run original method
        }

        /// <summary>
        /// Finalizer for RequestCurrentSimTick - if it throws, complete loading anyway
        /// </summary>
        public static Exception RequestCurrentSimTick_Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Plugin.Log.LogWarning($"[DirectConnect] RequestCurrentSimTick exception: {__exception.Message}");
                // Server probably doesn't have NetworkMessageRelay - complete loading directly
                TryCallOnFinishLoading();
                return null; // Suppress the exception
            }
            return __exception;
        }

        /// <summary>
        /// Prefix for ProcessCurrentSimTick - handles server's tick sync response.
        /// The original UserCode_ProcessCurrentSimTick calls:
        ///   FactorySimManager.instance.HandleServerTimeSync(tick);
        /// But FactorySimManager.instance might be null on client.
        /// </summary>
        private static bool _saveLoaded = false;

        public static bool ProcessCurrentSimTick_Prefix(int tick)
        {
            try
            {
                Plugin.Log.LogInfo($"[DirectConnect] ProcessCurrentSimTick received tick={tick}");

                // Check if save has loaded by looking at SaveState.instance
                var saveStateType = AccessTools.TypeByName("SaveState");
                var saveStateInstance = saveStateType?.GetProperty("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

                if (saveStateInstance == null)
                {
                    Plugin.Log.LogWarning($"[DirectConnect] ProcessCurrentSimTick: SaveState.instance is null - game not ready, skipping tick sync");
                    return false;
                }

                // Check if MachineManager has been initialized (has valid machineDefinitions)
                var machineManagerType = AccessTools.TypeByName("MachineManager");
                var mmInstanceField = machineManagerType?.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                var mmInstance = mmInstanceField?.GetValue(null);

                if (mmInstance == null)
                {
                    Plugin.Log.LogWarning($"[DirectConnect] ProcessCurrentSimTick: MachineManager.instance is null - game not ready, skipping tick sync");
                    return false;
                }

                // Try to call HandleServerTimeSync if FactorySimManager.instance exists
                var factorySimType = AccessTools.TypeByName("FactorySimManager");
                if (factorySimType != null)
                {
                    var instanceProp = factorySimType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                    if (instanceProp != null)
                    {
                        var fsmInstance = instanceProp.GetValue(null);
                        if (fsmInstance != null)
                        {
                            // Additional check: make sure _needInit is false
                            var needInitField = factorySimType.GetField("_needInit", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (needInitField != null)
                            {
                                bool needInit = (bool)needInitField.GetValue(fsmInstance);
                                if (needInit)
                                {
                                    Plugin.Log.LogWarning($"[DirectConnect] ProcessCurrentSimTick: FactorySimManager still needs init - skipping tick sync");
                                    return false;
                                }
                            }

                            var handleMethod = factorySimType.GetMethod("HandleServerTimeSync",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (handleMethod != null)
                            {
                                handleMethod.Invoke(fsmInstance, new object[] { tick });
                                Plugin.Log.LogInfo($"[DirectConnect] Successfully synced tick to {tick}");
                                _saveLoaded = true;
                                return false; // Skip original - we handled it
                            }
                        }
                        else
                        {
                            Plugin.Log.LogWarning($"[DirectConnect] FactorySimManager.instance is null - can't sync tick");
                        }
                    }
                }

                // Also set MachineManager.instance.curTick directly (already have mmInstance from check above)
                if (mmInstance != null)
                {
                    var curTickField = machineManagerType.GetField("curTick", BindingFlags.Public | BindingFlags.Instance);
                    if (curTickField != null)
                    {
                        curTickField.SetValue(mmInstance, tick);
                        Plugin.Log.LogInfo($"[DirectConnect] Set MachineManager.curTick = {tick}");
                    }
                }

                return false; // Skip original to prevent crash
            }
            catch (Exception ex)
            {
                // Log full exception details including inner exception
                var innerMsg = ex.InnerException?.Message ?? "no inner";
                var innerStack = ex.InnerException?.StackTrace ?? "";
                Plugin.Log.LogError($"[DirectConnect] ProcessCurrentSimTick error: {ex.Message} | Inner: {innerMsg}");
                if (!string.IsNullOrEmpty(innerStack))
                {
                    Plugin.Log.LogError($"[DirectConnect] Inner stack: {innerStack}");
                }
                return false; // Skip original to prevent crash
            }
        }

        /// <summary>
        /// Track NetworkedPlayer.OnStopClient to understand disconnect causes.
        /// </summary>
        public static void OnStopClient_Prefix(object __instance)
        {
            try
            {
                var netId = __instance.GetType().GetProperty("netId")?.GetValue(__instance);
                var isLocalPlayer = __instance.GetType().GetProperty("isLocalPlayer")?.GetValue(__instance);

                Plugin.Log.LogWarning($"[DirectConnect] DISCONNECT TRACKING: NetworkedPlayer.OnStopClient called! netId={netId}, isLocalPlayer={isLocalPlayer}");

                // Log stack trace to see what called this
                var stackTrace = new System.Diagnostics.StackTrace(true);
                var frames = stackTrace.GetFrames();
                if (frames != null)
                {
                    var traceStr = "";
                    for (int i = 0; i < Math.Min(frames.Length, 15); i++)
                    {
                        var frame = frames[i];
                        var method = frame.GetMethod();
                        if (method != null)
                        {
                            traceStr += $"{method.DeclaringType?.Name}.{method.Name} -> ";
                        }
                    }
                    Plugin.Log.LogWarning($"[DirectConnect] DISCONNECT STACK: {traceStr}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] OnStopClient_Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Track NetworkManager.OnClientDisconnect for disconnect causes.
        /// </summary>
        public static void OnClientDisconnect_Prefix()
        {
            Plugin.Log.LogWarning("[DirectConnect] DISCONNECT TRACKING: NetworkManager.OnClientDisconnect called!");

            // Log stack trace
            try
            {
                var stackTrace = new System.Diagnostics.StackTrace(true);
                var frames = stackTrace.GetFrames();
                if (frames != null)
                {
                    var traceStr = "";
                    for (int i = 0; i < Math.Min(frames.Length, 15); i++)
                    {
                        var frame = frames[i];
                        var method = frame.GetMethod();
                        if (method != null)
                        {
                            traceStr += $"{method.DeclaringType?.Name}.{method.Name} -> ";
                        }
                    }
                    Plugin.Log.LogWarning($"[DirectConnect] DISCONNECT MANAGER STACK: {traceStr}");
                }
            }
            catch { }
        }

        // =========== AGGRESSIVE LOGGING HANDLERS ===========

        public static void OnObjectSpawn_Prefix(Mirror.SpawnMessage msg)
        {
            Plugin.Log.LogInfo($"[NETWORK] SPAWN: netId={msg.netId}, isLocalPlayer={msg.isLocalPlayer}, isOwner={msg.isOwner}, sceneId={msg.sceneId:X16}, assetId={msg.assetId}");
        }

        public static void OnObjectDestroy_Prefix(Mirror.ObjectDestroyMessage message)
        {
            Plugin.Log.LogWarning($"[NETWORK] DESTROY: netId={message.netId}");
        }

        public static void HandleRemoteCall_Prefix(object __instance, int componentIndex, int functionHash, object invokeType, object reader, object senderConnection)
        {
            var identity = __instance as NetworkIdentity;
            Plugin.Log.LogInfo($"[NETWORK] RPC: netId={identity?.netId}, componentIndex={componentIndex}, functionHash={functionHash}, type={invokeType}");
        }

        public static void NetworkClient_Connect_Prefix(string address)
        {
            Plugin.Log.LogInfo($"[NETWORK] CONNECT: Connecting to {address}");
        }

        public static void KCP_OnClientConnected_Prefix()
        {
            Plugin.Log.LogInfo("[NETWORK] KCP: Client connected at transport level!");
        }

        public static void KCP_OnClientDisconnected_Prefix()
        {
            Plugin.Log.LogError("[NETWORK] KCP: Client DISCONNECTED at transport level!");

            // Log stack trace to see what triggered the transport disconnect
            try
            {
                var stackTrace = new System.Diagnostics.StackTrace(true);
                var frames = stackTrace.GetFrames();
                if (frames != null)
                {
                    var traceStr = "";
                    for (int i = 0; i < Math.Min(frames.Length, 20); i++)
                    {
                        var frame = frames[i];
                        var method = frame.GetMethod();
                        if (method != null)
                        {
                            traceStr += $"{method.DeclaringType?.Name}.{method.Name} -> ";
                        }
                    }
                    Plugin.Log.LogError($"[NETWORK] KCP DISCONNECT STACK: {traceStr}");
                }
            }
            catch { }
        }

        public static void KcpClient_Disconnect_Prefix(object __instance)
        {
            Plugin.Log.LogError($"[NETWORK] KcpClient.Disconnect called!");

            // Log stack trace to understand why
            try
            {
                var stackTrace = new System.Diagnostics.StackTrace(true);
                var frames = stackTrace.GetFrames();
                if (frames != null)
                {
                    var traceStr = "";
                    for (int i = 0; i < Math.Min(frames.Length, 25); i++)
                    {
                        var frame = frames[i];
                        var method = frame.GetMethod();
                        if (method != null)
                        {
                            traceStr += $"{method.DeclaringType?.Name}.{method.Name} -> ";
                        }
                    }
                    Plugin.Log.LogError($"[NETWORK] KcpClient.Disconnect STACK: {traceStr}");
                }
            }
            catch { }
        }

        public static void KcpConnection_Disconnect_Prefix(object __instance)
        {
            Plugin.Log.LogError($"[NETWORK] KcpConnection.Disconnect called!");

            // Try to get the state/reason from the connection
            try
            {
                var connType = __instance.GetType();
                var stateField = connType.GetField("state", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var state = stateField?.GetValue(__instance);
                Plugin.Log.LogError($"[NETWORK] KcpConnection state before disconnect: {state}");

                // Log stack trace
                var stackTrace = new System.Diagnostics.StackTrace(true);
                var frames = stackTrace.GetFrames();
                if (frames != null)
                {
                    var traceStr = "";
                    for (int i = 0; i < Math.Min(frames.Length, 30); i++)
                    {
                        var frame = frames[i];
                        var method = frame.GetMethod();
                        if (method != null)
                        {
                            traceStr += $"{method.DeclaringType?.Name}.{method.Name} -> ";
                        }
                    }
                    Plugin.Log.LogError($"[NETWORK] KcpConnection.Disconnect STACK: {traceStr}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[NETWORK] KcpConnection logging error: {ex.Message}");
            }
        }

        /// <summary>
        /// Sets connectionToServer on a NetworkIdentity using reflection.
        /// This is needed for client commands to work on server-spawned objects.
        /// </summary>
        private static void TrySetConnectionToServer(NetworkIdentity identity)
        {
            if (identity == null || NetworkClient.connection == null) return;

            try
            {
                // connectionToServer is a property with internal setter
                var connectionToServerProp = typeof(NetworkIdentity).GetProperty("connectionToServer",
                    BindingFlags.Public | BindingFlags.Instance);

                if (connectionToServerProp != null)
                {
                    var setter = connectionToServerProp.GetSetMethod(true);
                    if (setter != null)
                    {
                        setter.Invoke(identity, new object[] { NetworkClient.connection });
                        Plugin.Log.LogInfo($"[DirectConnect] Set connectionToServer via setter");
                    }
                    else
                    {
                        // Try the backing field directly
                        var backingField = typeof(NetworkIdentity).GetField("<connectionToServer>k__BackingField",
                            BindingFlags.NonPublic | BindingFlags.Instance);
                        if (backingField != null)
                        {
                            backingField.SetValue(identity, NetworkClient.connection);
                            Plugin.Log.LogInfo($"[DirectConnect] Set connectionToServer via backing field");
                        }
                        else
                        {
                            Plugin.Log.LogWarning($"[DirectConnect] Could not find connectionToServer setter or backing field");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[DirectConnect] TrySetConnectionToServer error: {ex.Message}");
            }
        }

        /// <summary>
        /// Calls LoadingUI.OnFinishLoading() to complete the loading screen
        /// </summary>
        private static void TryCallOnFinishLoading()
        {
            if (_requestSimTickHandled) return;
            _requestSimTickHandled = true;

            try
            {
                Plugin.Log.LogInfo("[DirectConnect] Calling LoadingUI.OnFinishLoading() directly (dedicated server mode)");

                var loadingUIType = AccessTools.TypeByName("LoadingUI");
                if (loadingUIType != null)
                {
                    // LoadingUI.instance is a FIELD, not a property
                    var instanceField = AccessTools.Field(loadingUIType, "instance");
                    var loadingUI = instanceField?.GetValue(null);

                    if (loadingUI != null)
                    {
                        var onFinishMethod = AccessTools.Method(loadingUIType, "OnFinishLoading");
                        if (onFinishMethod != null)
                        {
                            onFinishMethod.Invoke(loadingUI, null);
                            Plugin.Log.LogInfo("[DirectConnect] Successfully called LoadingUI.OnFinishLoading()");
                        }
                        else
                        {
                            Plugin.Log.LogError("[DirectConnect] LoadingUI.OnFinishLoading method not found");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[DirectConnect] LoadingUI.instance is null - may need to wait");
                    }
                }
                else
                {
                    Plugin.Log.LogError("[DirectConnect] LoadingUI type not found");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error calling OnFinishLoading: {ex}");
            }
        }
    }
}
