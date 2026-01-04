using HarmonyLib;
using Mirror;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace TechtonicaDedicatedServer.Networking
{
    /// <summary>
    /// AGGRESSIVE connection handler that forces save data to be sent to clients.
    /// This completely bypasses Harmony patches which don't work under Wine/Mono.
    /// Uses Update() polling to guarantee execution.
    /// </summary>
    public class ServerConnectionHandler : MonoBehaviour
    {
        private static ServerConnectionHandler _instance;
        private static bool _initialized;

        // Track which connections have received save data
        private HashSet<int> _connectionsWithSaveData = new HashSet<int>();
        private HashSet<int> _connectionsSendingData = new HashSet<int>();
        private HashSet<int> _connectionsSpawningPlayer = new HashSet<int>();
        private Dictionary<int, float> _connectionFirstSeen = new Dictionary<int, float>();

        // Player tracking for admin panel / Discord webhooks
        private HashSet<int> _loggedConnections = new HashSet<int>();
        private HashSet<int> _previousConnections = new HashSet<int>();
        private Dictionary<int, DateTime> _connectionStartTimes = new Dictionary<int, DateTime>();

        // Cached types and methods
        private static Type _networkedPlayerType;
        private MethodInfo _targetLoadMethod;
        private MethodInfo _handleSetStrataMethod;

        // Player prefab for spawning
        private static GameObject _playerPrefab;

        // Timing
        private float _lastCheck;
        private int _updateCount;
        private bool _loggedFirstUpdate;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            Plugin.Log.LogInfo("[ServerConnectionHandler] Initialize() starting...");

            // Create GameObject and mark it to persist across scene loads
            var go = new GameObject("TechtonicaServerConnectionHandler");
            go.hideFlags = HideFlags.HideAndDontSave;  // Make it invisible to scene management
            Plugin.Log.LogInfo($"[ServerConnectionHandler] GameObject created: {go != null}");

            DontDestroyOnLoad(go);
            Plugin.Log.LogInfo("[ServerConnectionHandler] DontDestroyOnLoad called");

            var comp = go.AddComponent<ServerConnectionHandler>();
            Plugin.Log.LogInfo($"[ServerConnectionHandler] AddComponent returned: {comp != null}");

            _instance = comp;
            Plugin.Log.LogInfo($"[ServerConnectionHandler] _instance set to: {_instance != null}");

            // Keep a strong reference to prevent GC
            _persistentObject = go;

            Plugin.Log.LogInfo("[ServerConnectionHandler] Initialized - AGGRESSIVE MODE");
        }

        // Strong reference to prevent garbage collection
        private static GameObject _persistentObject;

        private static float _lastCallbackCheck;
        private static int _callbackCount;

        private static bool _firstCallbackLogged;

        /// <summary>
        /// Called from OnBeforeRender callback since Update/InvokeRepeating don't work under Wine
        /// </summary>
        public static void CheckFromCallback()
        {
            _callbackCount++;

            // Log first call
            if (!_firstCallbackLogged)
            {
                _firstCallbackLogged = true;
                Plugin.Log.LogInfo($"[ServerConnectionHandler] CheckFromCallback FIRST CALL! instance={_instance != null}, _initialized={_initialized}, hashCode={typeof(ServerConnectionHandler).GetHashCode()}, headless={Plugin.HeadlessMode.Value}, server={NetworkServer.active}");
            }

            // Extra check - try to re-initialize if somehow not done
            if (!_initialized)
            {
                Plugin.Log.LogWarning("[ServerConnectionHandler] Not initialized in callback! Attempting re-init...");
                Initialize();
            }

            if (_instance == null) return;
            if (!Plugin.HeadlessMode.Value) return;
            if (!NetworkServer.active) return;

            // Only check every ~0.25 seconds (assuming ~60fps, check every 15 frames)
            if (_callbackCount % 15 != 0) return;

            var time = Time.realtimeSinceStartup;
            if (time - _lastCallbackCheck < 0.25f) return;
            _lastCallbackCheck = time;

            // Log every 100 checks (about every 25 seconds at 60fps/15frame interval)
            if (_callbackCount % 1500 == 0)
            {
                Plugin.Log.LogInfo($"[ServerConnectionHandler] Callback heartbeat #{_callbackCount}");
            }

            try
            {
                _instance.CheckAndSendSaveData();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ServerConnectionHandler] CheckFromCallback error: {ex.Message}");
            }
        }

        private void Awake()
        {
            Plugin.Log.LogInfo($"[ServerConnectionHandler] Awake on {gameObject.name}, this={GetHashCode()}");
            CacheTypes();
        }

        private void OnDestroy()
        {
            Plugin.Log.LogWarning($"[ServerConnectionHandler] OnDestroy called! this={GetHashCode()}, _instance={_instance != null}");
            if (_instance == this)
            {
                Plugin.Log.LogError("[ServerConnectionHandler] Main instance is being destroyed!");
            }
        }

        private void OnEnable()
        {
            Plugin.Log.LogInfo($"[ServerConnectionHandler] OnEnable on {gameObject.name}");
        }

        private void OnDisable()
        {
            Plugin.Log.LogWarning($"[ServerConnectionHandler] OnDisable called!");
        }

        private void CacheTypes()
        {
            _networkedPlayerType = AccessTools.TypeByName("NetworkedPlayer");
            if (_networkedPlayerType != null)
            {
                // Try multiple method name patterns - CORRECT NAME IS LoadInitialSaveDataFromServer
                string[] methodNames = {
                    "LoadInitialSaveDataFromServer",  // This is the actual [TargetRpc] method
                    "UserCode_LoadInitialSaveDataFromServer",
                    "TargetLoadInitialSaveDataFromServer",
                    "TargetRpcLoadInitialSaveDataFromServer"
                };

                foreach (var name in methodNames)
                {
                    _targetLoadMethod = _networkedPlayerType.GetMethod(name,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (_targetLoadMethod != null) break;
                }

                if (_targetLoadMethod == null)
                {
                    // List ALL methods that might be for sending save data
                    Plugin.Log.LogInfo("[ServerConnectionHandler] Listing all methods on NetworkedPlayer:");
                    foreach (var m in _networkedPlayerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                    {
                        if (m.Name.Contains("Target") || m.Name.Contains("Load") || m.Name.Contains("Save"))
                        {
                            var parms = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                            Plugin.Log.LogInfo($"  {m.Name}({parms}) [{(m.IsStatic ? "Static" : "Instance")}]");

                            // If it's the one we need - LoadInitialSaveDataFromServer
                            if (m.Name.Contains("LoadInitial") && m.Name.Contains("Save"))
                            {
                                _targetLoadMethod = m;
                                Plugin.Log.LogInfo($"  >>> FOUND IT! Using this method!");
                            }
                        }
                    }
                }

                Plugin.Log.LogInfo($"[ServerConnectionHandler] NetworkedPlayer type found, TargetLoadMethod: {_targetLoadMethod?.Name ?? "NULL"}");

                // Also cache HandleSetStrata method for sending strata to clients
                // First, list ALL strata-related methods to understand what's available
                Plugin.Log.LogInfo("[ServerConnectionHandler] Listing ALL strata-related methods:");
                foreach (var m in _networkedPlayerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name.ToLower().Contains("strata"))
                    {
                        var parms = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                        Plugin.Log.LogInfo($"  STRATA METHOD: {m.Name}({parms})");
                    }
                }

                // Try to find the TargetRpc version that takes connection + byte
                _handleSetStrataMethod = _networkedPlayerType.GetMethod("HandleSetStrata",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(NetworkConnectionToClient), typeof(byte) },
                    null);

                if (_handleSetStrataMethod != null)
                {
                    Plugin.Log.LogInfo($"[ServerConnectionHandler] Found TargetRpc HandleSetStrata(NetworkConnectionToClient, byte)");
                }
                else
                {
                    // Try with NetworkConnection
                    _handleSetStrataMethod = _networkedPlayerType.GetMethod("HandleSetStrata",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                        null,
                        new Type[] { typeof(NetworkConnection), typeof(byte) },
                        null);

                    if (_handleSetStrataMethod != null)
                    {
                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Found TargetRpc HandleSetStrata(NetworkConnection, byte)");
                    }
                }

                // Fallback: try any HandleSetStrata
                if (_handleSetStrataMethod == null)
                {
                    string[] strataMethodNames = {
                        "HandleSetStrata",
                        "UserCode_HandleSetStrata",
                        "TargetHandleSetStrata",
                        "TargetRpcHandleSetStrata"
                    };

                    foreach (var name in strataMethodNames)
                    {
                        _handleSetStrataMethod = _networkedPlayerType.GetMethod(name,
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (_handleSetStrataMethod != null)
                        {
                            var parms = string.Join(", ", Array.ConvertAll(_handleSetStrataMethod.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                            Plugin.Log.LogInfo($"[ServerConnectionHandler] Found strata method: {_handleSetStrataMethod.Name}({parms})");
                            break;
                        }
                    }
                }

                if (_handleSetStrataMethod == null)
                {
                    // Search for any method containing "Strata"
                    foreach (var m in _networkedPlayerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name.Contains("Strata"))
                        {
                            var parms = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                            Plugin.Log.LogInfo($"[ServerConnectionHandler] Strata method candidate: {m.Name}({parms})");

                            if (m.Name.Contains("HandleSetStrata") || m.Name.Contains("SetStrata"))
                            {
                                _handleSetStrataMethod = m;
                                Plugin.Log.LogInfo($"[ServerConnectionHandler] >>> Using strata method: {m.Name}");
                            }
                        }
                    }
                }

                Plugin.Log.LogInfo($"[ServerConnectionHandler] HandleSetStrataMethod: {_handleSetStrataMethod?.Name ?? "NULL"}");
            }
            else
            {
                Plugin.Log.LogError("[ServerConnectionHandler] NetworkedPlayer type NOT FOUND!");
            }

            // Also start InvokeRepeating as a backup for Update
            InvokeRepeating(nameof(CheckConnectionsInvoke), 1f, 0.5f);
            Plugin.Log.LogInfo("[ServerConnectionHandler] Started InvokeRepeating for connection checks");

            // Register for authentication events to properly handle client auth
            RegisterAuthenticationHandler();
        }

        private void RegisterAuthenticationHandler()
        {
            try
            {
                // Try to register for NetworkServer events
                // OnServerAuthenticated is called when a client successfully authenticates
                var authenticatedEvent = typeof(NetworkServer).GetField("OnServerAuthenticated",
                    BindingFlags.Public | BindingFlags.Static);
                if (authenticatedEvent != null)
                {
                    Plugin.Log.LogInfo("[ServerConnectionHandler] Found NetworkServer.OnServerAuthenticated");
                }

                // Also check if there's an authenticator we need to configure
                var nmType = AccessTools.TypeByName("Mirror.NetworkManager");
                if (nmType != null)
                {
                    var managers = UnityEngine.Object.FindObjectsOfType<NetworkManager>();
                    foreach (var manager in managers)
                    {
                        // Check if there's an authenticator
                        var authField = nmType.GetField("authenticator", BindingFlags.Public | BindingFlags.Instance);
                        if (authField != null)
                        {
                            var auth = authField.GetValue(manager);
                            if (auth != null)
                            {
                                Plugin.Log.LogInfo($"[ServerConnectionHandler] Found authenticator: {auth.GetType().Name}");

                                // Try to register for OnServerAuthenticated event on the authenticator
                                var authType = auth.GetType();
                                var onAuthEvent = authType.GetEvent("OnServerAuthenticated");
                                if (onAuthEvent != null)
                                {
                                    Plugin.Log.LogInfo("[ServerConnectionHandler] Authenticator has OnServerAuthenticated event");
                                }
                            }
                            else
                            {
                                Plugin.Log.LogInfo("[ServerConnectionHandler] No authenticator configured on NetworkManager");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ServerConnectionHandler] RegisterAuthenticationHandler error: {ex.Message}");
            }
        }

        private int _invokeCount;
        private static bool _autoLoadTriggeredFromHandler;

        private void CheckConnectionsInvoke()
        {
            _invokeCount++;

            // Log first call unconditionally
            if (_invokeCount == 1)
            {
                Plugin.Log.LogInfo("[ServerConnectionHandler] CheckConnectionsInvoke() FIRST CALL!");
            }

            // Log periodically
            if (_invokeCount % 20 == 0)
            {
                Plugin.Log.LogInfo($"[ServerConnectionHandler] InvokeRepeating #{_invokeCount}, HeadlessMode={Plugin.HeadlessMode.Value}, NetworkServer.active={NetworkServer.active}");
            }

            // CRITICAL: Trigger auto-load from here since Plugin.Update() isn't being called
            // This InvokeRepeating IS working, so we use it as a backup trigger
            if (!_autoLoadTriggeredFromHandler && _invokeCount > 30 && Plugin.HeadlessMode.Value && !NetworkServer.active)
            {
                _autoLoadTriggeredFromHandler = true;
                Plugin.Log.LogInfo("[ServerConnectionHandler] Triggering AutoLoadManager.TryAutoLoad() from InvokeRepeating!");
                try
                {
                    AutoLoadManager.TryAutoLoad();
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[ServerConnectionHandler] AutoLoad error: {ex}");
                }
            }

            // Also call AutoLoadManager.Update() since Plugin.Update() and OnBeforeRender aren't reliable
            try
            {
                AutoLoadManager.Update();
            }
            catch { }

            // CRITICAL: Manually tick the transport since Unity's Update/LateUpdate aren't working under Wine
            // Without this, the KCP transport never processes incoming packets
            try
            {
                TickTransport();
            }
            catch { }

            if (!Plugin.HeadlessMode.Value) return;
            if (!NetworkServer.active) return;

            try
            {
                CheckAndSendSaveData();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ServerConnectionHandler] CheckConnectionsInvoke error: {ex.Message}");
            }
        }

        private void Update()
        {
            _updateCount++;

            if (!_loggedFirstUpdate)
            {
                _loggedFirstUpdate = true;
                Plugin.Log.LogInfo("[ServerConnectionHandler] Update() running!");
            }

            if (!Plugin.HeadlessMode.Value) return;

            var time = Time.realtimeSinceStartup;
            if (time - _lastCheck < 0.25f) return;
            _lastCheck = time;

            // Log every 100 checks to confirm we're running
            if (_updateCount % 400 == 0)
            {
                Plugin.Log.LogInfo($"[ServerConnectionHandler] Heartbeat: {_updateCount} updates, {_connectionsWithSaveData.Count} clients served");
            }

            if (!NetworkServer.active) return;

            try
            {
                CheckAndSendSaveData();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ServerConnectionHandler] Update error: {ex}");
            }
        }

        /// <summary>
        /// Check all connections and send save data to any that need it
        /// </summary>
        private void CheckAndSendSaveData()
        {
            // Track player connections/disconnections for admin panel
            TrackPlayerConnections();

            var connections = NetworkServer.connections;
            if (connections == null || connections.Count == 0)
            {
                return; // No connections, nothing to check
            }

            Plugin.Log.LogInfo($"[ServerConnectionHandler] Checking {connections.Count} connections...");

            foreach (var kvp in connections)
            {
                var conn = kvp.Value;
                if (conn == null)
                {
                    Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {kvp.Key} is null");
                    continue;
                }

                int connId = conn.connectionId;
                Plugin.Log.LogInfo($"[ServerConnectionHandler] Checking connection {connId}...");

                // Skip if already handled or currently sending
                if (_connectionsWithSaveData.Contains(connId))
                {
                    Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {connId} already has save data");
                    continue;
                }
                if (_connectionsSendingData.Contains(connId))
                {
                    Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {connId} is currently receiving data");
                    continue;
                }

                // CRITICAL: Wait for client to be fully authenticated before doing ANYTHING
                // Sending RPCs before authentication causes Mirror to close the connection
                if (!conn.isAuthenticated)
                {
                    if (!_connectionFirstSeen.ContainsKey(connId))
                    {
                        _connectionFirstSeen[connId] = Time.realtimeSinceStartup;
                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {connId} not authenticated yet, waiting...");
                    }
                    else if (Time.realtimeSinceStartup - _connectionFirstSeen[connId] > 5f)
                    {
                        // Wait 5 seconds before force authenticating (was 2s, too aggressive)
                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {connId} still not authenticated after 5s, force authenticating...");
                        conn.isAuthenticated = true;
                    }
                    continue; // Don't process unauthenticated connections AT ALL
                }

                // Note: We don't wait for conn.isReady - it may never become ready in headless mode
                // Just log if not ready and continue processing
                if (!conn.isReady)
                {
                    Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {connId} authenticated but not ready (continuing anyway)");
                }

                // Check if this connection has a player
                if (conn.identity == null)
                {

                    // Try to spawn a player for this connection
                    if (!_connectionsSpawningPlayer.Contains(connId))
                    {
                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {connId} has no identity, attempting to spawn player...");
                        _connectionsSpawningPlayer.Add(connId);
                        TrySpawnPlayerForConnection(conn);
                    }
                    continue;
                }

                Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {connId} has identity: {conn.identity.gameObject.name}");

                // Find NetworkedPlayer component
                var networkedPlayer = FindNetworkedPlayer(conn.identity.gameObject);
                if (networkedPlayer == null)
                {
                    Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {connId} has no NetworkedPlayer component");
                    continue;
                }

                // FOUND A NEW PLAYER - SEND SAVE DATA IMMEDIATELY
                Plugin.Log.LogInfo($"[ServerConnectionHandler] !!! NEW PLAYER DETECTED on connection {connId} !!!");
                _connectionsSendingData.Add(connId);

                SendSaveDataDirect(networkedPlayer, conn, connId);
            }
        }

        /// <summary>
        /// Send save data directly to a player - NO COROUTINES, completely synchronous
        /// </summary>
        private void SendSaveDataDirect(object networkedPlayer, NetworkConnectionToClient conn, int connId)
        {
            try
            {
                Plugin.Log.LogInfo($"[ServerConnectionHandler] Sending save data to connection {connId}...");

                // Get cached save data
                var cachedData = AutoLoadManager.GetCachedSaveString();
                if (string.IsNullOrEmpty(cachedData))
                {
                    Plugin.Log.LogError("[ServerConnectionHandler] NO CACHED SAVE DATA!");
                    _connectionsSendingData.Remove(connId);
                    return;
                }

                Plugin.Log.LogInfo($"[ServerConnectionHandler] Have {cachedData.Length} chars of save data");

                // Re-cache the target method if needed
                if (_targetLoadMethod == null || _handleSetStrataMethod == null)
                {
                    CacheTypes();
                }

                // CRITICAL: Send strata FIRST before save data!
                // Clients time out waiting for strata if we don't send it
                byte strata = ExtractStrataFromSaveData(cachedData);
                try
                {
                    // Try multiple approaches to set/send strata
                    bool strataSent = false;

                    // Approach 1: Set the SyncVar directly (NetworkmyStrata)
                    var syncVarSetter = _networkedPlayerType.GetMethod("set_NetworkmyStrata",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (syncVarSetter != null)
                    {
                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Setting SyncVar NetworkmyStrata = {strata}");
                        syncVarSetter.Invoke(networkedPlayer, new object[] { strata });
                        strataSent = true;
                    }

                    // Approach 2: Also try the HandleSetStrata RPC
                    if (_handleSetStrataMethod != null)
                    {
                        var parameters = _handleSetStrataMethod.GetParameters();
                        var paramStr = string.Join(", ", Array.ConvertAll(parameters, p => $"{p.ParameterType.Name} {p.Name}"));
                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Also calling {_handleSetStrataMethod.Name}({paramStr}) with strata={strata}");
                        _handleSetStrataMethod.Invoke(networkedPlayer, new object[] { strata });
                        strataSent = true;
                    }

                    // Approach 3: Try UpdateStrata which might be a ClientRpc
                    var updateStrataMethod = _networkedPlayerType.GetMethod("UpdateStrata",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (updateStrataMethod != null)
                    {
                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Also calling UpdateStrata({strata})");
                        updateStrataMethod.Invoke(networkedPlayer, new object[] { strata });
                        strataSent = true;
                    }

                    if (strataSent)
                    {
                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Strata={strata} sent to connection {connId}");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[ServerConnectionHandler] No strata method worked!");
                    }
                }
                catch (Exception strataEx)
                {
                    Plugin.Log.LogError($"[ServerConnectionHandler] Error sending strata: {strataEx}");
                    // Continue anyway - try to send save data
                }

                if (_targetLoadMethod == null)
                {
                    Plugin.Log.LogError("[ServerConnectionHandler] LoadInitialSaveDataFromServer method NOT FOUND!");
                    ListAvailableMethods(networkedPlayer);
                    _connectionsSendingData.Remove(connId);
                    return;
                }

                // Send in chunks
                int maxPacketSize = 30000;
                int numChunks = (cachedData.Length + maxPacketSize - 1) / maxPacketSize;

                Plugin.Log.LogInfo($"[ServerConnectionHandler] Sending {numChunks} chunks to client");

                for (int i = 0; i < numChunks; i++)
                {
                    int start = i * maxPacketSize;
                    int length = Math.Min(maxPacketSize, cachedData.Length - start);
                    string chunk = cachedData.Substring(start, length);

                    try
                    {
                        // Call: LoadInitialSaveDataFromServer(NetworkConnection connection, string partialData, int index, int numTotal)
                        _targetLoadMethod.Invoke(networkedPlayer, new object[] { conn, chunk, i, numChunks });

                        if (i == 0 || i == numChunks - 1 || i % 5 == 0)
                        {
                            Plugin.Log.LogInfo($"[ServerConnectionHandler] Sent chunk {i + 1}/{numChunks} ({chunk.Length} chars)");
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[ServerConnectionHandler] Error sending chunk {i}: {ex}");
                        _connectionsSendingData.Remove(connId);
                        return;
                    }
                }

                Plugin.Log.LogInfo($"[ServerConnectionHandler] Successfully sent all {numChunks} chunks to connection {connId}!");
                _connectionsWithSaveData.Add(connId);
                _connectionsSendingData.Remove(connId);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ServerConnectionHandler] SendSaveDataDirect error: {ex}");
                _connectionsSendingData.Remove(connId);
            }
        }

        private void ListAvailableMethods(object networkedPlayer)
        {
            var type = networkedPlayer.GetType();
            Plugin.Log.LogInfo($"[ServerConnectionHandler] Available methods on {type.Name}:");
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (m.Name.Contains("Target") || m.Name.Contains("Save") || m.Name.Contains("Load"))
                {
                    var parms = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => p.ParameterType.Name));
                    Plugin.Log.LogInfo($"  {m.Name}({parms})");
                }
            }
        }

        private object FindNetworkedPlayer(GameObject go)
        {
            if (go == null) return null;

            foreach (var component in go.GetComponents<MonoBehaviour>())
            {
                if (component == null) continue;
                if (component.GetType().Name == "NetworkedPlayer")
                {
                    return component;
                }
            }

            foreach (Transform child in go.transform)
            {
                var result = FindNetworkedPlayer(child.gameObject);
                if (result != null) return result;
            }

            return null;
        }

        private static int _tickCount;

        /// <summary>
        /// Manually tick the transport and NetworkServer to process packets.
        /// Mirror requires Update/LateUpdate calls which don't work under Wine.
        /// </summary>
        private static void TickTransport()
        {
            _tickCount++;

            // Log first few ticks and periodically to debug transport
            if (_tickCount <= 3 || (_tickCount % 500 == 0))
            {
                Plugin.Log.LogInfo($"[ServerConnectionHandler] TickTransport #{_tickCount}, NetworkServer.active={NetworkServer.active}, Transport.activeTransport={Mirror.Transport.activeTransport?.GetType().Name ?? "NULL"}");
            }

            // Only tick when server is active
            if (!NetworkServer.active) return;

            var transport = Mirror.Transport.activeTransport;
            if (transport == null)
            {
                if (_tickCount <= 3)
                {
                    Plugin.Log.LogWarning("[ServerConnectionHandler] TickTransport: Transport.activeTransport is NULL!");
                }
                return;
            }

            // Log first tick with detailed info
            if (_tickCount == 1)
            {
                Plugin.Log.LogInfo($"[ServerConnectionHandler] TickTransport FIRST CALL! Transport: {transport.GetType().Name}");

                // List all methods on the transport
                var methods = transport.GetType().GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var m in methods)
                {
                    if (m.Name.Contains("Update") || m.Name.Contains("Tick") || m.Name.Contains("Send") || m.Name.Contains("Flush"))
                    {
                        Plugin.Log.LogInfo($"  Transport method: {m.Name}");
                    }
                }
            }

            // Log periodically
            if (_tickCount % 1000 == 0)
            {
                Plugin.Log.LogInfo($"[ServerConnectionHandler] TickTransport #{_tickCount}");
            }

            // Call transport update methods via reflection (they may be internal/protected)
            try
            {
                // Try ServerEarlyUpdate - handles receiving
                var earlyUpdate = transport.GetType().GetMethod("ServerEarlyUpdate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                earlyUpdate?.Invoke(transport, null);

                // Try ServerLateUpdate - handles sending
                var lateUpdate = transport.GetType().GetMethod("ServerLateUpdate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                lateUpdate?.Invoke(transport, null);

                // Also try the generic OnUpdate if it exists
                var onUpdate = transport.GetType().GetMethod("OnUpdate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                onUpdate?.Invoke(transport, null);
            }
            catch (Exception ex)
            {
                if (_tickCount <= 5)
                {
                    Plugin.Log.LogError($"[ServerConnectionHandler] TickTransport error: {ex.Message}");
                }
            }

            // Also tick NetworkServer's update methods
            try
            {
                // NetworkServer.NetworkEarlyUpdate()
                var nsEarlyUpdate = typeof(NetworkServer).GetMethod("NetworkEarlyUpdate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                nsEarlyUpdate?.Invoke(null, null);

                // NetworkServer.NetworkLateUpdate()
                var nsLateUpdate = typeof(NetworkServer).GetMethod("NetworkLateUpdate",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                nsLateUpdate?.Invoke(null, null);

                if (_tickCount == 1 && nsEarlyUpdate != null)
                {
                    Plugin.Log.LogInfo("[ServerConnectionHandler] Found and calling NetworkServer.NetworkEarlyUpdate/NetworkLateUpdate");
                }
            }
            catch (Exception ex)
            {
                if (_tickCount <= 5)
                {
                    Plugin.Log.LogError($"[ServerConnectionHandler] NetworkServer tick error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Extract strata value from save data JSON metadata (line 1)
        /// Format: {"strata":0, ...} or similar
        /// </summary>
        private byte ExtractStrataFromSaveData(string cachedData)
        {
            try
            {
                // Save file format: Line 1 is JSON metadata, Line 2+ is base64 messagepack
                int newlineIndex = cachedData.IndexOf('\n');
                if (newlineIndex <= 0)
                {
                    Plugin.Log.LogWarning("[ServerConnectionHandler] No newline in save data, using default strata=0");
                    return 0;
                }

                string jsonMetadata = cachedData.Substring(0, newlineIndex);
                Plugin.Log.LogInfo($"[ServerConnectionHandler] JSON metadata: {jsonMetadata.Substring(0, Math.Min(200, jsonMetadata.Length))}...");

                // Simple regex-free JSON parsing for "strata":X
                // Look for "strata": followed by a number
                int strataIndex = jsonMetadata.IndexOf("\"strata\"");
                if (strataIndex < 0)
                {
                    // Try without quotes
                    strataIndex = jsonMetadata.IndexOf("strata");
                }

                if (strataIndex >= 0)
                {
                    // Find the colon after "strata"
                    int colonIndex = jsonMetadata.IndexOf(':', strataIndex);
                    if (colonIndex >= 0)
                    {
                        // Extract the number after the colon
                        int numStart = colonIndex + 1;
                        while (numStart < jsonMetadata.Length && (jsonMetadata[numStart] == ' ' || jsonMetadata[numStart] == '\t'))
                        {
                            numStart++;
                        }

                        int numEnd = numStart;
                        while (numEnd < jsonMetadata.Length && char.IsDigit(jsonMetadata[numEnd]))
                        {
                            numEnd++;
                        }

                        if (numEnd > numStart)
                        {
                            string strataStr = jsonMetadata.Substring(numStart, numEnd - numStart);
                            if (byte.TryParse(strataStr, out byte strata))
                            {
                                Plugin.Log.LogInfo($"[ServerConnectionHandler] Extracted strata={strata} from save metadata");
                                return strata;
                            }
                        }
                    }
                }

                Plugin.Log.LogWarning("[ServerConnectionHandler] Could not find strata in metadata, using default=0");
                return 0;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ServerConnectionHandler] Error extracting strata: {ex.Message}");
                return 0; // Default to surface level
            }
        }

        /// <summary>
        /// Called externally when a player is spawned
        /// </summary>
        public static void OnPlayerSpawned(object networkedPlayer, NetworkConnectionToClient conn)
        {
            if (_instance == null) return;

            Plugin.Log.LogInfo($"[ServerConnectionHandler] OnPlayerSpawned called for connection {conn.connectionId}");

            // Force immediate send
            _instance.SendSaveDataDirect(networkedPlayer, conn, conn.connectionId);
        }

        /// <summary>
        /// Try to spawn a player object for a connection that has no identity
        /// </summary>
        private void TrySpawnPlayerForConnection(NetworkConnectionToClient conn)
        {
            try
            {
                Plugin.Log.LogInfo($"[ServerConnectionHandler] TrySpawnPlayerForConnection for {conn.connectionId}");

                // First check if connection is authenticated - use proper Mirror authentication
                if (!conn.isAuthenticated)
                {
                    Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {conn.connectionId} is not authenticated, calling ServerAccept...");

                    // Try to find and call the authenticator's ServerAccept method
                    // This properly completes the authentication handshake
                    try
                    {
                        var managers = UnityEngine.Object.FindObjectsOfType<NetworkManager>();
                        bool accepted = false;
                        foreach (var manager in managers)
                        {
                            var authField = manager.GetType().GetField("authenticator",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                            if (authField != null)
                            {
                                var auth = authField.GetValue(manager);
                                if (auth != null)
                                {
                                    // Call ServerAccept on the authenticator
                                    var acceptMethod = auth.GetType().GetMethod("ServerAccept",
                                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                                    if (acceptMethod != null)
                                    {
                                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Calling {auth.GetType().Name}.ServerAccept for {conn.connectionId}");
                                        acceptMethod.Invoke(auth, new object[] { conn });
                                        accepted = true;
                                        break;
                                    }
                                }
                            }
                        }

                        // If no authenticator found, try to set authenticated directly and send auth response
                        if (!accepted)
                        {
                            Plugin.Log.LogInfo($"[ServerConnectionHandler] No authenticator found, manually authenticating...");
                            conn.isAuthenticated = true;

                            // Send ReadyMessage to client to notify authentication complete
                            // This is what Mirror does internally when authentication succeeds
                        }
                    }
                    catch (Exception authEx)
                    {
                        Plugin.Log.LogError($"[ServerConnectionHandler] Authentication error: {authEx.Message}");
                        conn.isAuthenticated = true;
                    }
                }

                // Check if connection is ready
                if (!conn.isReady)
                {
                    Plugin.Log.LogInfo($"[ServerConnectionHandler] Connection {conn.connectionId} is not ready yet, marking ready...");
                    // Mark the connection as ready
                    NetworkServer.SetClientReady(conn);
                }

                // Try to find the player prefab
                if (_playerPrefab == null)
                {
                    _playerPrefab = FindPlayerPrefab();
                }

                if (_playerPrefab == null)
                {
                    Plugin.Log.LogError("[ServerConnectionHandler] Could not find player prefab!");
                    return;
                }

                Plugin.Log.LogInfo($"[ServerConnectionHandler] Found player prefab: {_playerPrefab.name}");

                // Instantiate the player
                var playerInstance = UnityEngine.Object.Instantiate(_playerPrefab);
                playerInstance.name = $"NetworkedPlayer_Client{conn.connectionId}";

                Plugin.Log.LogInfo($"[ServerConnectionHandler] Instantiated player: {playerInstance.name}");

                // Add to connection
                NetworkServer.AddPlayerForConnection(conn, playerInstance);

                Plugin.Log.LogInfo($"[ServerConnectionHandler] Added player for connection {conn.connectionId}!");

                // Remove from spawning set since we're done
                _connectionsSpawningPlayer.Remove(conn.connectionId);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ServerConnectionHandler] TrySpawnPlayerForConnection error: {ex}");
                _connectionsSpawningPlayer.Remove(conn.connectionId);
            }
        }

        /// <summary>
        /// Find the player prefab from various sources
        /// </summary>
        private static GameObject FindPlayerPrefab()
        {
            // Try NetworkManager.singleton.playerPrefab
            try
            {
                var nmType = AccessTools.TypeByName("Mirror.NetworkManager");
                if (nmType != null)
                {
                    var singletonProp = nmType.GetProperty("singleton", BindingFlags.Public | BindingFlags.Static);
                    if (singletonProp != null)
                    {
                        var nm = singletonProp.GetValue(null);
                        if (nm != null)
                        {
                            var prefabField = nmType.GetField("playerPrefab", BindingFlags.Public | BindingFlags.Instance);
                            if (prefabField != null)
                            {
                                var prefab = prefabField.GetValue(nm) as GameObject;
                                if (prefab != null)
                                {
                                    Plugin.Log.LogInfo($"[ServerConnectionHandler] Found player prefab from NetworkManager: {prefab.name}");
                                    return prefab;
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ServerConnectionHandler] Error getting prefab from NetworkManager: {ex.Message}");
            }

            // Try to find NetworkManager in scene
            try
            {
                var managers = UnityEngine.Object.FindObjectsOfType<NetworkManager>();
                foreach (var manager in managers)
                {
                    if (manager.playerPrefab != null)
                    {
                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Found player prefab from scene NetworkManager: {manager.playerPrefab.name}");
                        return manager.playerPrefab;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ServerConnectionHandler] Error finding NetworkManager in scene: {ex.Message}");
            }

            // Try to find existing NetworkedPlayer in scene and use its prefab source
            try
            {
                var existingPlayers = UnityEngine.Object.FindObjectsOfType<NetworkIdentity>();
                foreach (var identity in existingPlayers)
                {
                    var np = identity.GetComponent(_networkedPlayerType);
                    if (np != null)
                    {
                        Plugin.Log.LogInfo($"[ServerConnectionHandler] Found existing NetworkedPlayer: {identity.gameObject.name}");
                        // This is an instance, not a prefab - but we can check if it's spawned
                        // and try to find its asset ID
                        if (identity.assetId != default)
                        {
                            Plugin.Log.LogInfo($"[ServerConnectionHandler] NetworkedPlayer asset ID: {identity.assetId}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ServerConnectionHandler] Error finding existing players: {ex.Message}");
            }

            // Try finding by name in Resources
            try
            {
                var prefabs = Resources.FindObjectsOfTypeAll<GameObject>();
                foreach (var go in prefabs)
                {
                    if (go.name.Contains("NetworkedPlayer") || go.name.Contains("Player"))
                    {
                        var identity = go.GetComponent<NetworkIdentity>();
                        if (identity != null)
                        {
                            Plugin.Log.LogInfo($"[ServerConnectionHandler] Found potential player prefab: {go.name}");
                            return go;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ServerConnectionHandler] Error searching Resources: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Track player connections and disconnections for admin panel/Discord webhooks.
        /// Called from the main thread via CheckAndSendSaveData.
        /// </summary>
        private void TrackPlayerConnections()
        {
            if (!NetworkServer.active) return;

            var connections = NetworkServer.connections;
            var currentConnections = new HashSet<int>();

            // Build set of current connections and detect new ones
            if (connections != null)
            {
                foreach (var kvp in connections)
                {
                    var conn = kvp.Value;
                    if (conn == null) continue;

                    int connId = conn.connectionId;
                    currentConnections.Add(connId);

                    // New connection that we haven't logged yet
                    if (!_loggedConnections.Contains(connId))
                    {
                        _loggedConnections.Add(connId);
                        _connectionStartTimes[connId] = DateTime.UtcNow;

                        string address = GetConnectionAddress(conn);
                        LogPlayerConnect(connId, address, currentConnections.Count);
                    }
                }
            }

            // Detect disconnections by comparing with previous frame
            foreach (int connId in _previousConnections)
            {
                if (!currentConnections.Contains(connId))
                {
                    // Player disconnected
                    string connectedFor = "unknown";
                    if (_connectionStartTimes.TryGetValue(connId, out DateTime startTime))
                    {
                        TimeSpan duration = DateTime.UtcNow - startTime;
                        connectedFor = FormatDuration(duration);
                        _connectionStartTimes.Remove(connId);
                    }

                    LogPlayerDisconnect(connId, connectedFor, currentConnections.Count);
                    _loggedConnections.Remove(connId);
                }
            }

            // Update previous connections for next check
            _previousConnections = currentConnections;
        }

        private string GetConnectionAddress(NetworkConnectionToClient conn)
        {
            try
            {
                // Try to get remote endpoint address
                var addressProp = conn.GetType().GetProperty("address", BindingFlags.Public | BindingFlags.Instance);
                if (addressProp != null)
                {
                    var addr = addressProp.GetValue(conn);
                    if (addr != null) return addr.ToString();
                }

                // Try remoteEndPoint field
                var endpointField = conn.GetType().GetField("remoteEndPoint", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (endpointField != null)
                {
                    var endpoint = endpointField.GetValue(conn);
                    if (endpoint != null) return endpoint.ToString();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ServerConnectionHandler] GetConnectionAddress error: {ex.Message}");
            }
            return "unknown";
        }

        private string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 1)
                return $"{(int)duration.TotalHours}h {duration.Minutes}m {duration.Seconds}s";
            else if (duration.TotalMinutes >= 1)
                return $"{duration.Minutes}m {duration.Seconds}s";
            else
                return $"{duration.Seconds}s";
        }

        private static readonly string _eventLogPath = "/home/death/techtonica-server/events.log";

        private void LogPlayerConnect(int connectionId, string address, int playerCount)
        {
            Plugin.Log.LogInfo($"[ServerConnectionHandler] Player connected: connId={connectionId}, address={address}, playerCount={playerCount}");

            try
            {
                string json = $"{{\"timestamp\":\"{DateTime.UtcNow:O}\",\"type\":\"player_connect\",\"message\":\"Player connected\",\"connectionId\":\"{connectionId}\",\"address\":\"{address}\",\"playerCount\":\"{playerCount}\"}}";
                System.IO.File.AppendAllText(_eventLogPath, json + "\n");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ServerConnectionHandler] Failed to write player_connect event: {ex.Message}");
            }
        }

        private void LogPlayerDisconnect(int connectionId, string connectedFor, int playerCount)
        {
            Plugin.Log.LogInfo($"[ServerConnectionHandler] Player disconnected: connId={connectionId}, connectedFor={connectedFor}, playerCount={playerCount}");

            try
            {
                string json = $"{{\"timestamp\":\"{DateTime.UtcNow:O}\",\"type\":\"player_disconnect\",\"message\":\"Player disconnected\",\"connectionId\":\"{connectionId}\",\"connectedFor\":\"{connectedFor}\",\"playerCount\":\"{playerCount}\"}}";
                System.IO.File.AppendAllText(_eventLogPath, json + "\n");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[ServerConnectionHandler] Failed to write player_disconnect event: {ex.Message}");
            }
        }
    }
}
