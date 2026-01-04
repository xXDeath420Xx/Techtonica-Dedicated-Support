using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Collections.Generic;
using Mirror;
using UnityEngine;
using UnityEngine.SceneManagement;
using kcp2k;

namespace TechtonicaDedicatedServer.Networking
{
    /// <summary>
    /// Manages direct IP connections by swapping the transport layer from FizzyFacepunch to KCP.
    /// Based on decompilation of TechNetworkManager, NetworkConnector, and SteamLobbyConnector.
    /// </summary>
    public static class DirectConnectManager
    {
        private static KcpTransport _kcpTransport;
        private static Transport _originalTransport;
        private static bool _isInitialized;
        private static bool _isDirectConnectActive;
        private static DirectConnectLobbyConnector _directConnectLobby;
        private static Dictionary<int, PlayerInfo> _connectedPlayers = new Dictionary<int, PlayerInfo>();
        private static string _eventLogPath = "/home/death/techtonica-server/events.log";

        public static bool IsDirectConnectActive => _isDirectConnectActive;
        public static bool IsServer => NetworkServer.active;
        public static bool IsClient => NetworkClient.active;
        public static bool IsHost => NetworkServer.active && NetworkClient.active;

        public static event Action OnServerStarted;
        public static event Action OnServerStopped;
        public static event Action OnClientConnected;
        public static event Action OnClientDisconnected;

        public class PlayerInfo
        {
            public int ConnectionId { get; set; }
            public string Name { get; set; }
            public DateTime ConnectedAt { get; set; }
            public string Address { get; set; }
        }

        public static IReadOnlyDictionary<int, PlayerInfo> ConnectedPlayers => _connectedPlayers;

        public static void Initialize()
        {
            if (_isInitialized) return;

            Plugin.Log.LogInfo("[DirectConnect] Initializing...");
            _isInitialized = true;
            Plugin.Log.LogInfo("[DirectConnect] Initialized successfully");
        }

        /// <summary>
        /// Writes an event to the event log file for the admin panel to read.
        /// </summary>
        public static void WriteEvent(string eventType, string message, Dictionary<string, string> data = null)
        {
            try
            {
                var timestamp = DateTime.UtcNow.ToString("O");
                var dataStr = "";
                if (data != null)
                {
                    var parts = new List<string>();
                    foreach (var kvp in data)
                    {
                        parts.Add($"\"{kvp.Key}\":\"{kvp.Value}\"");
                    }
                    dataStr = string.Join(",", parts);
                }

                var logLine = $"{{\"timestamp\":\"{timestamp}\",\"type\":\"{eventType}\",\"message\":\"{message}\"{(dataStr.Length > 0 ? "," + dataStr : "")}}}\n";
                File.AppendAllText(_eventLogPath, logLine);
                Plugin.Log.LogInfo($"[Events] {eventType}: {message}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Events] Failed to write event: {ex.Message}");
            }
        }

        public static void Cleanup()
        {
            if (_kcpTransport != null)
            {
                GameObject.Destroy(_kcpTransport.gameObject);
                _kcpTransport = null;
            }

            _isInitialized = false;
            _isDirectConnectActive = false;
        }

        /// <summary>
        /// Creates or retrieves the KCP transport for direct connections.
        /// </summary>
        public static KcpTransport GetOrCreateKcpTransport()
        {
            if (_kcpTransport != null) return _kcpTransport;

            int port = Plugin.ServerPort.Value;

            // Create a new GameObject for our transport
            var transportGO = new GameObject("DirectConnect_KcpTransport");
            GameObject.DontDestroyOnLoad(transportGO);

            _kcpTransport = transportGO.AddComponent<KcpTransport>();

            // Configure KCP for game traffic
            _kcpTransport.Port = (ushort)port;
            _kcpTransport.NoDelay = true;
            _kcpTransport.Interval = 10;
            _kcpTransport.Timeout = 10000;
            _kcpTransport.DualMode = false; // Force IPv4 only - fixes Wine socket issues

            Plugin.Log.LogInfo($"[DirectConnect] Created KCP transport on port {_kcpTransport.Port}");

            return _kcpTransport;
        }

        /// <summary>
        /// Creates the GameLogic and GameState instances required for player registration.
        /// GameState.instance.allPlayers is needed for NetworkMessageRelay.GetPlayer() to work.
        /// Without this, player commands fail with "Spawned object not found" errors.
        /// </summary>
        private static void CreateGameLogicInstance()
        {
            try
            {
                // Check if GameLogic.instance already exists
                var gameLogicType = AccessTools.TypeByName("GameLogic");
                if (gameLogicType == null)
                {
                    Plugin.Log.LogError("[DirectConnect] GameLogic type not found!");
                    return;
                }

                var instanceField = gameLogicType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceField == null)
                {
                    Plugin.Log.LogError("[DirectConnect] GameLogic.instance field not found!");
                    return;
                }

                var existingInstance = instanceField.GetValue(null);
                if (existingInstance != null)
                {
                    Plugin.Log.LogInfo("[DirectConnect] GameLogic.instance already exists");
                    return;
                }

                // Create a new GameObject for GameLogic
                Plugin.Log.LogInfo("[DirectConnect] Creating GameLogic instance for dedicated server...");
                var gameLogicGO = new GameObject("DedicatedServer_GameLogic");
                GameObject.DontDestroyOnLoad(gameLogicGO);

                // Add GameLogic component - this triggers OnEnable which sets GameLogic.instance
                var gameLogic = gameLogicGO.AddComponent(gameLogicType);

                // Verify it was set
                existingInstance = instanceField.GetValue(null);
                if (existingInstance != null)
                {
                    Plugin.Log.LogInfo("[DirectConnect] GameLogic.instance created successfully!");

                    // Check GameState
                    var gameStateType = AccessTools.TypeByName("GameState");
                    if (gameStateType != null)
                    {
                        var gameStateInstanceProp = gameStateType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                        if (gameStateInstanceProp != null)
                        {
                            var gameStateInstance = gameStateInstanceProp.GetValue(null);
                            if (gameStateInstance != null)
                            {
                                Plugin.Log.LogInfo("[DirectConnect] GameState.instance is available!");

                                // Check allPlayers dictionary
                                var allPlayersField = gameStateType.GetField("allPlayers", BindingFlags.Public | BindingFlags.Instance);
                                if (allPlayersField != null)
                                {
                                    var allPlayers = allPlayersField.GetValue(gameStateInstance);
                                    if (allPlayers != null)
                                    {
                                        Plugin.Log.LogInfo("[DirectConnect] GameState.instance.allPlayers dictionary exists!");
                                    }
                                }

                                // Check SaveState
                                var saveStateType = AccessTools.TypeByName("SaveState");
                                if (saveStateType != null)
                                {
                                    var saveStateInstanceProp = saveStateType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                                    if (saveStateInstanceProp != null)
                                    {
                                        var saveStateInstance = saveStateInstanceProp.GetValue(null);
                                        if (saveStateInstance != null)
                                        {
                                            Plugin.Log.LogInfo("[DirectConnect] SaveState.instance is available!");
                                        }
                                        else
                                        {
                                            Plugin.Log.LogWarning("[DirectConnect] SaveState.instance is NULL - creating...");
                                            // SaveState is created as part of GameState, it should exist
                                            // If it's null, we may need to initialize it
                                            var saveStateField = gameStateType.GetField("saveState", BindingFlags.Public | BindingFlags.Instance);
                                            if (saveStateField != null)
                                            {
                                                var saveState = saveStateField.GetValue(gameStateInstance);
                                                if (saveState != null)
                                                {
                                                    Plugin.Log.LogInfo("[DirectConnect] GameState.saveState exists (SaveState.instance should work now)");
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                            else
                            {
                                Plugin.Log.LogWarning("[DirectConnect] GameState.instance is NULL even though GameLogic.instance exists!");
                            }
                        }
                    }
                }
                else
                {
                    Plugin.Log.LogError("[DirectConnect] Failed to create GameLogic.instance!");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error creating GameLogic instance: {ex.Message}");
                Plugin.Log.LogError($"[DirectConnect] Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Initializes the FakeNetworkAuthenticator to handle client authentication.
        /// The authenticator's OnStartServer() must be called to register the AuthRequestMessage handler.
        /// Without this, clients send auth requests but server can't respond, causing immediate disconnects.
        /// </summary>
        private static void InitializeAuthenticator(NetworkManager networkManager)
        {
            try
            {
                Plugin.Log.LogInfo("[DirectConnect] Initializing authenticator...");

                // Check if NetworkManager has an authenticator
                var authField = networkManager.GetType().GetField("authenticator",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (authField == null)
                {
                    // Try the property instead
                    var authProp = networkManager.GetType().GetProperty("authenticator",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (authProp != null)
                    {
                        var auth = authProp.GetValue(networkManager);
                        if (auth != null)
                        {
                            Plugin.Log.LogInfo($"[DirectConnect] Found authenticator via property: {auth.GetType().Name}");
                            CallAuthenticatorOnStartServer(auth);
                            return;
                        }
                    }
                }
                else
                {
                    var auth = authField.GetValue(networkManager);
                    if (auth != null)
                    {
                        Plugin.Log.LogInfo($"[DirectConnect] Found authenticator via field: {auth.GetType().Name}");
                        CallAuthenticatorOnStartServer(auth);
                        return;
                    }
                }

                // No authenticator found on NetworkManager - create FakeNetworkAuthenticator
                Plugin.Log.LogInfo("[DirectConnect] No authenticator on NetworkManager, creating FakeNetworkAuthenticator...");

                var fakeAuthType = AccessTools.TypeByName("FakeNetworkAuthenticator");
                if (fakeAuthType != null)
                {
                    // Create a GameObject for the authenticator
                    var authGO = new GameObject("DedicatedServer_Authenticator");
                    GameObject.DontDestroyOnLoad(authGO);

                    // Add the FakeNetworkAuthenticator component
                    var authenticator = authGO.AddComponent(fakeAuthType);
                    Plugin.Log.LogInfo($"[DirectConnect] Created FakeNetworkAuthenticator: {authenticator}");

                    // Set it on the NetworkManager
                    if (authField != null)
                    {
                        authField.SetValue(networkManager, authenticator);
                    }

                    // Call OnStartServer to register the handler
                    CallAuthenticatorOnStartServer(authenticator);
                }
                else
                {
                    // FakeNetworkAuthenticator not found - manually register the handler
                    Plugin.Log.LogWarning("[DirectConnect] FakeNetworkAuthenticator type not found, registering handler manually...");
                    RegisterAuthRequestHandler();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error initializing authenticator: {ex.Message}");
                Plugin.Log.LogError($"[DirectConnect] Stack: {ex.StackTrace}");
                // Try manual registration as fallback
                RegisterAuthRequestHandler();
            }
        }

        private static void CallAuthenticatorOnStartServer(object authenticator)
        {
            try
            {
                var onStartServerMethod = authenticator.GetType().GetMethod("OnStartServer",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (onStartServerMethod != null)
                {
                    onStartServerMethod.Invoke(authenticator, null);
                    Plugin.Log.LogInfo("[DirectConnect] Called authenticator.OnStartServer() - auth handler registered");
                }
                else
                {
                    Plugin.Log.LogWarning("[DirectConnect] OnStartServer method not found on authenticator");
                }

                // Also register our own handler as backup
                RegisterDirectAuthHandler(authenticator);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error calling OnStartServer: {ex.Message}");
            }
        }

        // Store authenticator reference for our handler
        private static object _authenticatorInstance;

        private static bool _debugHandlerRegistered = false;

        /// <summary>
        /// Registers our own auth request handler as a backup.
        /// This handles the case where the standard handler registration doesn't work.
        /// </summary>
        private static void RegisterDirectAuthHandler(object authenticator)
        {
            try
            {
                _authenticatorInstance = authenticator;

                // Only register the handler once
                if (_debugHandlerRegistered)
                {
                    Plugin.Log.LogInfo("[DirectConnect] Debug handler already registered, skipping");
                    return;
                }

                // Add connection event handler for manual auth
                NetworkServer.OnConnectedEvent += OnClientConnectedDebug;
                _debugHandlerRegistered = true;
                Plugin.Log.LogInfo("[DirectConnect] Added OnConnectedEvent debug handler");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error registering direct auth handler: {ex.Message}");
            }
        }

        private static void OnClientConnectedDebug(NetworkConnection conn)
        {
            Plugin.Log.LogInfo($"[DirectConnect] DEBUG: Client connected event fired! connId={conn.connectionId}, address={conn.address}, isAuthenticated={conn.isAuthenticated}");

            // DON'T call ServerAccept immediately - need to wait for auth message exchange
            // The client needs to receive AuthResponseMessage before we can accept
            try
            {
                if (Plugin.Instance != null)
                {
                    Plugin.Log.LogInfo($"[DirectConnect] Starting delayed auth coroutine for {conn.connectionId}...");
                    Plugin.Instance.StartCoroutine(DelayedAuthAccept(conn));
                }
                else
                {
                    Plugin.Log.LogError("[DirectConnect] Plugin.Instance is null, can't start coroutine!");
                    // Fallback: do auth immediately
                    DoManualAuth(conn);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error starting auth coroutine: {ex.Message}");
                // Fallback: do auth immediately
                DoManualAuth(conn);
            }
        }

        // Queue for delayed auth - process in Update loop
        private static readonly Queue<(NetworkConnection conn, float time)> _pendingAuthQueue = new Queue<(NetworkConnection, float)>();
        private static bool _authUpdateRegistered = false;

        private static void DoManualAuth(NetworkConnection conn)
        {
            Plugin.Log.LogInfo($"[DirectConnect] DoManualAuth for connection {conn.connectionId}...");

            try
            {
                // First, send AuthResponseMessage to the client
                var authResponseType = AccessTools.TypeByName("FakeNetworkAuthenticator+AuthResponseMessage");
                Plugin.Log.LogInfo($"[DirectConnect] AuthResponseMessage type: {authResponseType?.FullName ?? "NULL"}");

                if (authResponseType != null)
                {
                    var responseMsg = Activator.CreateInstance(authResponseType);
                    Plugin.Log.LogInfo($"[DirectConnect] Created AuthResponseMessage instance");

                    // Try different Send method signatures
                    var sendMethods = typeof(NetworkConnection).GetMethods(BindingFlags.Public | BindingFlags.Instance)
                        .Where(m => m.Name == "Send")
                        .ToList();
                    Plugin.Log.LogInfo($"[DirectConnect] Found {sendMethods.Count} Send methods");

                    var sendMethod = sendMethods.FirstOrDefault(m => m.IsGenericMethod && m.GetParameters().Length == 2);
                    if (sendMethod == null)
                    {
                        sendMethod = sendMethods.FirstOrDefault(m => m.IsGenericMethod && m.GetParameters().Length == 1);
                    }

                    if (sendMethod != null)
                    {
                        Plugin.Log.LogInfo($"[DirectConnect] Using Send method with {sendMethod.GetParameters().Length} params");
                        var genericSend = sendMethod.MakeGenericMethod(authResponseType);
                        if (sendMethod.GetParameters().Length == 2)
                        {
                            genericSend.Invoke(conn, new object[] { responseMsg, 0 }); // 0 = Reliable channel
                        }
                        else
                        {
                            genericSend.Invoke(conn, new object[] { responseMsg });
                        }
                        Plugin.Log.LogInfo($"[DirectConnect] Sent AuthResponseMessage to connection {conn.connectionId}");
                    }
                    else
                    {
                        Plugin.Log.LogError($"[DirectConnect] No suitable Send method found!");
                    }
                }
                else
                {
                    Plugin.Log.LogError($"[DirectConnect] AuthResponseMessage type not found!");
                }

                // Call ServerAccept immediately after sending auth response
                // The delay was causing Mirror to timeout the unauthenticated connection
                if (_authenticatorInstance != null)
                {
                    var serverAcceptMethod = _authenticatorInstance.GetType().GetMethod("ServerAccept",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (serverAcceptMethod != null)
                    {
                        Plugin.Log.LogInfo($"[DirectConnect] Calling ServerAccept for connection {conn.connectionId}");
                        serverAcceptMethod.Invoke(_authenticatorInstance, new object[] { conn });
                        Plugin.Log.LogInfo($"[DirectConnect] ServerAccept completed for connection {conn.connectionId}");
                    }
                    else
                    {
                        Plugin.Log.LogError($"[DirectConnect] ServerAccept method not found!");
                    }
                }
                else
                {
                    Plugin.Log.LogError($"[DirectConnect] _authenticatorInstance is null!");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error in DoManualAuth: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Process pending auth queue - should be called from an Update loop
        /// </summary>
        public static void ProcessPendingAuth()
        {
            if (_pendingAuthQueue.Count == 0) return;

            float now = Time.time;
            lock (_pendingAuthQueue)
            {
                while (_pendingAuthQueue.Count > 0)
                {
                    var (conn, time) = _pendingAuthQueue.Peek();
                    if (now < time)
                    {
                        // Not ready yet
                        break;
                    }

                    _pendingAuthQueue.Dequeue();

                    if (conn == null)
                    {
                        Plugin.Log.LogWarning($"[DirectConnect] Pending auth connection is null");
                        continue;
                    }

                    if (conn.isAuthenticated)
                    {
                        Plugin.Log.LogInfo($"[DirectConnect] Connection {conn.connectionId} already authenticated");
                        continue;
                    }

                    try
                    {
                        if (_authenticatorInstance != null)
                        {
                            var serverAcceptMethod = _authenticatorInstance.GetType().GetMethod("ServerAccept",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                            if (serverAcceptMethod != null)
                            {
                                Plugin.Log.LogInfo($"[DirectConnect] Processing delayed ServerAccept for connection {conn.connectionId}");
                                serverAcceptMethod.Invoke(_authenticatorInstance, new object[] { conn });
                                Plugin.Log.LogInfo($"[DirectConnect] ServerAccept completed for connection {conn.connectionId}");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[DirectConnect] Error in delayed ServerAccept: {ex.Message}");
                    }
                }
            }
        }

        private static System.Collections.IEnumerator DelayedAuthAccept(NetworkConnection conn)
        {
            Plugin.Log.LogInfo($"[DirectConnect] DelayedAuthAccept started for {conn.connectionId}");

            // Wait a very short time for client to be ready
            yield return new UnityEngine.WaitForSeconds(0.1f);

            Plugin.Log.LogInfo($"[DirectConnect] DelayedAuthAccept: after delay for {conn.connectionId}");

            if (conn == null)
            {
                Plugin.Log.LogWarning($"[DirectConnect] Connection is null after delay");
                yield break;
            }

            if (conn.isAuthenticated)
            {
                Plugin.Log.LogInfo($"[DirectConnect] Connection {conn.connectionId} already authenticated!");
                yield break;
            }

            DoManualAuth(conn);
        }

        private static void OnAuthRequestMessageDirect(NetworkConnectionToClient conn, object msg)
        {
            Plugin.Log.LogInfo($"[DirectConnect] DIRECT AUTH: Received auth request from {conn.connectionId}");
        }

        /// <summary>
        /// Manually register a handler for AuthRequestMessage if no authenticator is available.
        /// This mimics FakeNetworkAuthenticator.OnAuthRequestMessage behavior.
        /// </summary>
        private static void RegisterAuthRequestHandler()
        {
            try
            {
                Plugin.Log.LogInfo("[DirectConnect] Registering AuthRequestMessage handler manually...");

                // Get the AuthRequestMessage type
                var authRequestType = AccessTools.TypeByName("FakeNetworkAuthenticator+AuthRequestMessage");
                var authResponseType = AccessTools.TypeByName("FakeNetworkAuthenticator+AuthResponseMessage");

                if (authRequestType == null || authResponseType == null)
                {
                    Plugin.Log.LogError("[DirectConnect] Could not find AuthRequestMessage or AuthResponseMessage types");
                    return;
                }

                // Find NetworkServer.RegisterHandler method and call it
                // Since we can't easily create a generic delegate, create FakeNetworkAuthenticator instance
                var fakeAuthType = AccessTools.TypeByName("FakeNetworkAuthenticator");
                if (fakeAuthType != null)
                {
                    var authGO = new GameObject("DedicatedServer_FallbackAuthenticator");
                    GameObject.DontDestroyOnLoad(authGO);
                    var authenticator = authGO.AddComponent(fakeAuthType);

                    // Call OnStartServer on this authenticator
                    var onStartServerMethod = fakeAuthType.GetMethod("OnStartServer",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (onStartServerMethod != null)
                    {
                        onStartServerMethod.Invoke(authenticator, null);
                        Plugin.Log.LogInfo("[DirectConnect] Fallback FakeNetworkAuthenticator.OnStartServer() called");
                    }
                }
                else
                {
                    Plugin.Log.LogError("[DirectConnect] FakeNetworkAuthenticator type not found!");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error registering auth handler: {ex.Message}");
                Plugin.Log.LogError($"[DirectConnect] Stack: {ex.StackTrace}");
            }
        }

        /// <summary>
        /// Creates a NetworkMessageRelay for the server.
        /// NOTE: We do NOT spawn this to clients - they will use their own scene relay.
        /// The server's relay handles command processing locally.
        /// </summary>
        private static void CreateDummyNetworkMessageRelay(Type relayType, FieldInfo instanceField)
        {
            if (relayType == null || instanceField == null)
            {
                Plugin.Log.LogError("[DirectConnect] Cannot create relay - type or field is null");
                return;
            }

            // First, search for any existing NetworkMessageRelay in the scene
            var existingRelays = UnityEngine.Object.FindObjectsOfType(relayType);
            Plugin.Log.LogInfo($"[DirectConnect] Found {existingRelays.Length} existing NetworkMessageRelay objects");

            foreach (var existing in existingRelays)
            {
                var mb = existing as MonoBehaviour;
                if (mb != null)
                {
                    var netId = mb.GetComponent<NetworkIdentity>();
                    Plugin.Log.LogInfo($"[DirectConnect] Existing relay: {mb.name}, hasNetId={netId != null}, sceneId={netId?.sceneId ?? 0}");

                    // If we find a scene relay with NetworkIdentity, use it
                    if (netId != null && netId.sceneId != 0)
                    {
                        instanceField.SetValue(null, existing);
                        Plugin.Log.LogInfo($"[DirectConnect] Using existing scene NetworkMessageRelay (sceneId={netId.sceneId})");
                        return;
                    }
                }
            }

            // Check if relay is a NetworkBehaviour (it likely is)
            if (typeof(NetworkBehaviour).IsAssignableFrom(relayType))
            {
                Plugin.Log.LogInfo("[DirectConnect] NetworkMessageRelay is a NetworkBehaviour - creating server relay");

                // Create a GameObject for the relay with NetworkIdentity
                var relayGO = new GameObject("DedicatedServer_NetworkMessageRelay");
                GameObject.DontDestroyOnLoad(relayGO);

                // Add NetworkIdentity first (required for NetworkBehaviour)
                var networkIdentity = relayGO.AddComponent<NetworkIdentity>();

                // Add the NetworkMessageRelay component
                var relay = relayGO.AddComponent(relayType);

                // Set the static instance field
                instanceField.SetValue(null, relay);

                // IMPORTANT: We DO spawn this to clients now.
                // The client mod intercepts the spawn message and links their scene relay
                // to this netId instead of trying to instantiate a new object.
                // This allows Commands to route properly between client and server.
                if (NetworkServer.active)
                {
                    NetworkServer.Spawn(relayGO);
                    Plugin.Log.LogInfo($"[DirectConnect] Spawned NetworkMessageRelay to clients (netId={networkIdentity.netId})");
                }
                else
                {
                    Plugin.Log.LogWarning("[DirectConnect] NetworkServer not active, relay not spawned");
                }

                // Verify it was set
                var checkRelay = instanceField.GetValue(null);
                if (checkRelay != null)
                {
                    Plugin.Log.LogInfo("[DirectConnect] NetworkMessageRelay.instance is now set!");
                }
                else
                {
                    Plugin.Log.LogWarning("[DirectConnect] NetworkMessageRelay.instance is still null after creation!");
                }
            }
            else
            {
                Plugin.Log.LogWarning($"[DirectConnect] NetworkMessageRelay is not a NetworkBehaviour: {relayType.BaseType?.Name}");

                // Try to create it anyway
                var relayGO = new GameObject("DedicatedServer_NetworkMessageRelay");
                GameObject.DontDestroyOnLoad(relayGO);
                var relay = relayGO.AddComponent(relayType);
                instanceField.SetValue(null, relay);
                Plugin.Log.LogInfo("[DirectConnect] Created non-NetworkBehaviour dummy relay");
            }
        }

        /// <summary>
        /// Switches the active transport from FizzyFacepunch to KCP.
        /// Must be called BEFORE starting server/client.
        /// </summary>
        public static bool EnableDirectConnect()
        {
            Plugin.Log.LogInfo($"[DirectConnect] EnableDirectConnect called, config value = {Plugin.EnableDirectConnect.Value}");

            if (!Plugin.EnableDirectConnect.Value)
            {
                Plugin.Log.LogWarning("[DirectConnect] Direct connect is disabled in config");
                return false;
            }

            try
            {
                Plugin.Log.LogInfo("[DirectConnect] Looking for NetworkManager.singleton...");
                var networkManager = NetworkManager.singleton;
                if (networkManager == null)
                {
                    Plugin.Log.LogError("[DirectConnect] NetworkManager.singleton is NULL!");
                    return false;
                }
                Plugin.Log.LogInfo($"[DirectConnect] Found NetworkManager: {networkManager.name}");

                // Get the transport field via reflection (it's internal in some Mirror versions)
                var transportField = typeof(NetworkManager).GetField("transport",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                if (transportField == null)
                {
                    Plugin.Log.LogError("[DirectConnect] Could not find transport field!");
                    return false;
                }

                // Store the original transport
                _originalTransport = transportField.GetValue(networkManager) as Transport;

                // Properly shutdown the original transport to release any resources
                if (_originalTransport != null)
                {
                    Plugin.Log.LogInfo($"[DirectConnect] Shutting down original transport: {_originalTransport.GetType().Name}");
                    try
                    {
                        // Try to call Shutdown() if it exists
                        var shutdownMethod = _originalTransport.GetType().GetMethod("Shutdown",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        if (shutdownMethod != null)
                        {
                            shutdownMethod.Invoke(_originalTransport, null);
                            Plugin.Log.LogInfo("[DirectConnect] Original transport Shutdown() called");
                        }

                        // Disable the transport component
                        if (_originalTransport is MonoBehaviour mb)
                        {
                            mb.enabled = false;
                            Plugin.Log.LogInfo("[DirectConnect] Original transport disabled");
                        }
                    }
                    catch (Exception shutdownEx)
                    {
                        Plugin.Log.LogWarning($"[DirectConnect] Error shutting down original transport: {shutdownEx.Message}");
                    }
                }

                // Get or create our KCP transport
                var kcpTransport = GetOrCreateKcpTransport();

                // Swap the transport on NetworkManager using reflection
                transportField.SetValue(networkManager, kcpTransport);

                // Also try to set Transport.active or Transport.activeTransport
                // In newer Mirror versions, this may be a property
                var activeTransportField = typeof(Transport).GetField("activeTransport",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var activeProperty = typeof(Transport).GetProperty("active",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                Plugin.Log.LogInfo($"[DirectConnect] activeTransport field: {activeTransportField != null}, active property: {activeProperty != null}");

                if (activeTransportField != null)
                {
                    activeTransportField.SetValue(null, kcpTransport);
                    Plugin.Log.LogInfo("[DirectConnect] Set Transport.activeTransport field");
                }
                else if (activeProperty != null && activeProperty.CanWrite)
                {
                    activeProperty.SetValue(null, kcpTransport);
                    Plugin.Log.LogInfo("[DirectConnect] Set Transport.active property");
                }
                else
                {
                    // Try direct assignment - in some Mirror versions, Transport.active is just a static field
                    // Actually, the transport should be set automatically by NetworkManager when starting
                    Plugin.Log.LogWarning("[DirectConnect] Could not find activeTransport field or property - transport will be set by NetworkManager");
                }

                _isDirectConnectActive = true;
                Plugin.Log.LogInfo("[DirectConnect] Switched to KCP transport");

                // Verify Transport.activeTransport after setting
                Plugin.Log.LogInfo($"[DirectConnect] Transport.activeTransport after set: {Transport.activeTransport?.GetType().Name ?? "NULL"}");

                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Failed to switch transport: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Restores the original FizzyFacepunch transport.
        /// </summary>
        public static void DisableDirectConnect()
        {
            if (_originalTransport != null && NetworkManager.singleton != null)
            {
                try
                {
                    // Use reflection to set the transport (it's internal in some Mirror versions)
                    var transportField = typeof(NetworkManager).GetField("transport",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    if (transportField != null)
                    {
                        transportField.SetValue(NetworkManager.singleton, _originalTransport);
                    }

                    // Also try to set Transport.activeTransport if it exists
                    var activeTransportField = typeof(Transport).GetField("activeTransport",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (activeTransportField != null)
                    {
                        activeTransportField.SetValue(null, _originalTransport);
                    }

                    _isDirectConnectActive = false;
                    Plugin.Log.LogInfo("[DirectConnect] Restored original transport");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"[DirectConnect] Failed to restore transport: {ex}");
                }
            }
        }

        /// <summary>
        /// Starts a dedicated server (no local client).
        /// Based on NetworkConnector.ConnectAsHost() but without starting a host.
        /// </summary>
        public static bool StartServer(int port = -1)
        {
            Plugin.Log.LogInfo("[DirectConnect] StartServer called...");

            if (IsServer)
            {
                Plugin.Log.LogWarning("[DirectConnect] Server is already running");
                return false;
            }

            try
            {
                if (port > 0)
                {
                    Plugin.ServerPort.Value = port;
                }

                Plugin.Log.LogInfo("[DirectConnect] About to enable direct connect...");

                // Enable direct connect transport
                if (!EnableDirectConnect())
                {
                    Plugin.Log.LogError("[DirectConnect] EnableDirectConnect returned false!");
                    return false;
                }

                Plugin.Log.LogInfo("[DirectConnect] Direct connect enabled successfully");

                // Update port
                if (_kcpTransport != null)
                {
                    _kcpTransport.Port = (ushort)Plugin.ServerPort.Value;
                }

                var networkManager = NetworkManager.singleton;
                if (networkManager == null)
                {
                    Plugin.Log.LogError("[DirectConnect] NetworkManager not found!");
                    return false;
                }

                networkManager.maxConnections = Plugin.MaxPlayers.Value;

                // CRITICAL: Set the scene name so Mirror tells clients to load the correct scene
                // If we're stuck on Main Menu, tell clients to load Player Scene instead
                var currentScene = SceneManager.GetActiveScene().name;
                var networkScene = currentScene;

                if (currentScene == "Main Menu" || currentScene == "Loading")
                {
                    networkScene = "Player Scene";
                    Plugin.Log.LogInfo($"[DirectConnect] Server is on '{currentScene}', will use 'Player Scene' for clients");
                }

                Plugin.Log.LogInfo($"[DirectConnect] Current scene: {currentScene}, Network scene: {networkScene}");

                // Set networkSceneName via reflection (may be internal in some Mirror versions)
                var sceneNameField = typeof(NetworkManager).GetField("networkSceneName",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sceneNameField != null)
                {
                    sceneNameField.SetValue(networkManager, networkScene);
                    Plugin.Log.LogInfo($"[DirectConnect] Set networkSceneName to: {networkScene}");
                }
                else
                {
                    // Try the property instead
                    var sceneNameProp = typeof(NetworkManager).GetProperty("networkSceneName",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (sceneNameProp != null && sceneNameProp.CanWrite)
                    {
                        sceneNameProp.SetValue(networkManager, networkScene);
                        Plugin.Log.LogInfo($"[DirectConnect] Set networkSceneName property to: {networkScene}");
                    }
                }

                // Register callbacks for client connect/disconnect
                NetworkServer.OnConnectedEvent += HandleClientConnect;
                NetworkServer.OnDisconnectedEvent += HandleClientDisconnect;

                // Start server only (not host)
                Plugin.Log.LogInfo("[DirectConnect] About to call networkManager.StartServer()...");
                try
                {
                    networkManager.StartServer();
                    Plugin.Log.LogInfo("[DirectConnect] networkManager.StartServer() completed");
                }
                catch (Exception startEx)
                {
                    Plugin.Log.LogError($"[DirectConnect] networkManager.StartServer() threw: {startEx.GetType().Name}: {startEx.Message}");
                    throw;
                }

                // CRITICAL: Initialize FakeNetworkAuthenticator to handle client authentication
                // Without this, clients send AuthRequestMessage but server can't respond
                InitializeAuthenticator(networkManager);

                // CRITICAL: Create GameLogic/GameState before spawning objects
                // This provides GameState.instance.allPlayers which is required for NetworkMessageRelay.GetPlayer()
                // Without this, player commands fail with "Spawned object not found" errors
                CreateGameLogicInstance();

                // CRITICAL: Spawn scene network objects
                // When using StartServer() instead of StartHost(), scene objects with NetworkIdentity
                // (like NetworkMessageRelay) need to be explicitly spawned
                if (NetworkServer.active)
                {
                    Plugin.Log.LogInfo("[DirectConnect] Spawning scene network objects...");
                    NetworkServer.SpawnObjects();

                    // Check for critical network objects and create dummy if needed
                    var networkMessageRelayType = AccessTools.TypeByName("NetworkMessageRelay");
                    if (networkMessageRelayType != null)
                    {
                        var instanceField = networkMessageRelayType.GetField("instance",
                            BindingFlags.Public | BindingFlags.Static);
                        var relay = instanceField?.GetValue(null);
                        if (relay != null)
                        {
                            Plugin.Log.LogInfo("[DirectConnect] NetworkMessageRelay.instance found!");
                        }
                        else
                        {
                            // Create a dummy NetworkMessageRelay for the server
                            // This is needed to handle network actions like item pickups
                            Plugin.Log.LogInfo("[DirectConnect] NetworkMessageRelay.instance is NULL - creating dummy relay...");
                            try
                            {
                                CreateDummyNetworkMessageRelay(networkMessageRelayType, instanceField);
                            }
                            catch (Exception relayEx)
                            {
                                Plugin.Log.LogError($"[DirectConnect] Failed to create dummy relay: {relayEx.Message}");
                            }
                        }
                    }

                    Plugin.Log.LogInfo("[DirectConnect] Scene objects spawned");
                }

                Plugin.Log.LogInfo($"[DirectConnect] Server started on port {Plugin.ServerPort.Value}");
                Plugin.Log.LogInfo($"[DirectConnect] Max players: {Plugin.MaxPlayers.Value}");
                Plugin.Log.LogInfo($"[DirectConnect] Address: {GetServerAddress()}");

                // Write event for admin panel
                WriteEvent("server_start", "Server started", new Dictionary<string, string>
                {
                    { "port", Plugin.ServerPort.Value.ToString() },
                    { "maxPlayers", Plugin.MaxPlayers.Value.ToString() },
                    { "address", GetServerAddress() }
                });

                OnServerStarted?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Failed to start server!");
                Plugin.Log.LogError($"[DirectConnect] Exception type: {ex.GetType().Name}");
                Plugin.Log.LogError($"[DirectConnect] Message: {ex.Message}");
                Plugin.Log.LogError($"[DirectConnect] Stack: {ex.StackTrace}");
                return false;
            }
        }

        /// <summary>
        /// Starts as host (server + local client).
        /// Based on NetworkConnector.ConnectAsHost() without Steam lobby.
        /// </summary>
        public static bool StartHost(int port = -1)
        {
            if (IsServer)
            {
                Plugin.Log.LogWarning("[DirectConnect] Server is already running");
                return false;
            }

            try
            {
                if (port > 0)
                {
                    Plugin.ServerPort.Value = port;
                }

                // Enable direct connect transport
                if (!EnableDirectConnect())
                {
                    return false;
                }

                // Update port
                if (_kcpTransport != null)
                {
                    _kcpTransport.Port = (ushort)Plugin.ServerPort.Value;
                }

                var networkManager = NetworkManager.singleton;
                if (networkManager == null)
                {
                    Plugin.Log.LogError("[DirectConnect] NetworkManager not found!");
                    return false;
                }

                networkManager.maxConnections = Plugin.MaxPlayers.Value;

                // This mirrors NetworkConnector.ConnectAsHost() but skips StartLobby()
                // From decompilation: NetworkManager.singleton.StartHost();
                networkManager.StartHost();

                Plugin.Log.LogInfo($"[DirectConnect] Host started on port {Plugin.ServerPort.Value}");
                Plugin.Log.LogInfo($"[DirectConnect] Address: {GetServerAddress()}");

                OnServerStarted?.Invoke();
                OnClientConnected?.Invoke();
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Failed to start host: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Connects to a server at the specified address.
        /// Based on NetworkConnector.ConnectAsClient() with direct IP.
        /// </summary>
        public static bool Connect(string address, int port = -1)
        {
            if (IsClient)
            {
                Plugin.Log.LogWarning("[DirectConnect] Already connected");
                return false;
            }

            try
            {
                // Enable direct connect transport
                if (!EnableDirectConnect())
                {
                    return false;
                }

                var networkManager = NetworkManager.singleton;
                if (networkManager == null)
                {
                    Plugin.Log.LogError("[DirectConnect] NetworkManager not found!");
                    return false;
                }

                // Parse address
                string host = address;
                int targetPort = port > 0 ? port : Plugin.ServerPort.Value;

                // Check if address includes port
                if (address.Contains(":"))
                {
                    var parts = address.Split(':');
                    host = parts[0];
                    if (parts.Length > 1 && int.TryParse(parts[1], out int parsedPort))
                    {
                        targetPort = parsedPort;
                    }
                }

                // Resolve DNS if hostname is not an IP address
                if (!System.Net.IPAddress.TryParse(host, out _))
                {
                    try
                    {
                        Plugin.Log.LogInfo($"[DirectConnect] Resolving hostname: {host}");
                        var addresses = Dns.GetHostAddresses(host);
                        foreach (var addr in addresses)
                        {
                            if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            {
                                var resolvedIp = addr.ToString();
                                Plugin.Log.LogInfo($"[DirectConnect] Resolved {host} -> {resolvedIp}");
                                host = resolvedIp;
                                break;
                            }
                        }
                    }
                    catch (Exception dnsEx)
                    {
                        Plugin.Log.LogError($"[DirectConnect] DNS resolution failed for {host}: {dnsEx.Message}");
                        // Continue anyway - let the transport try to resolve it
                    }
                }

                // Update KCP port
                if (_kcpTransport != null)
                {
                    _kcpTransport.Port = (ushort)targetPort;
                }

                // Set network address (from decompilation: NetworkManager.singleton.networkAddress = text)
                networkManager.networkAddress = host;

                // This mirrors NetworkConnector.ConnectAsClient()
                // From decompilation: NetworkManager.singleton.StartClient();
                networkManager.StartClient();

                Plugin.Log.LogInfo($"[DirectConnect] Connecting to {host}:{targetPort}...");
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Failed to connect: {ex}");
                return false;
            }
        }

        /// <summary>
        /// Stops the server or disconnects from the current server.
        /// Based on NetworkConnector.Disconnect().
        /// </summary>
        public static void Stop()
        {
            try
            {
                // Unregister event handlers
                NetworkServer.OnConnectedEvent -= HandleClientConnect;
                NetworkServer.OnDisconnectedEvent -= HandleClientDisconnect;

                var networkManager = NetworkManager.singleton;
                if (networkManager == null) return;

                // From decompilation of NetworkConnector.Disconnect():
                if (IsHost)
                {
                    networkManager.StopHost();
                    Plugin.Log.LogInfo("[DirectConnect] Host stopped");
                    WriteEvent("server_stop", "Host stopped");
                    _connectedPlayers.Clear();
                    OnServerStopped?.Invoke();
                }
                else if (IsServer)
                {
                    networkManager.StopServer();
                    Plugin.Log.LogInfo("[DirectConnect] Server stopped");
                    WriteEvent("server_stop", "Server stopped");
                    _connectedPlayers.Clear();
                    OnServerStopped?.Invoke();
                }
                else if (IsClient)
                {
                    networkManager.StopClient();
                    Plugin.Log.LogInfo("[DirectConnect] Disconnected");
                    OnClientDisconnected?.Invoke();
                }

                // Restore original transport
                DisableDirectConnect();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error stopping: {ex}");
            }
        }

        /// <summary>
        /// Gets the server's network address for players to connect.
        /// Uses PublicAddress config if set, otherwise falls back to local detection.
        /// </summary>
        public static string GetServerAddress()
        {
            if (!IsServer) return null;

            // Use configured public address if set
            if (!string.IsNullOrEmpty(Plugin.PublicAddress?.Value))
            {
                return Plugin.PublicAddress.Value;
            }

            try
            {
                var hostName = Dns.GetHostName();
                var addresses = Dns.GetHostAddresses(hostName);

                foreach (var addr in addresses)
                {
                    if (addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        // Skip loopback
                        if (!addr.ToString().StartsWith("127."))
                        {
                            return $"{addr}:{Plugin.ServerPort.Value}";
                        }
                    }
                }

                return $"localhost:{Plugin.ServerPort.Value}";
            }
            catch
            {
                return $"localhost:{Plugin.ServerPort.Value}";
            }
        }

        /// <summary>
        /// Gets the number of connected players.
        /// </summary>
        public static int GetPlayerCount()
        {
            if (!IsServer) return 0;
            return NetworkServer.connections.Count;
        }

        /// <summary>
        /// Called when a client connects to the server.
        /// Sends the game scene to the client so they can load it.
        /// </summary>
        private static void HandleClientConnect(NetworkConnection conn)
        {
            try
            {
                var address = conn.address ?? "unknown";
                Plugin.Log.LogInfo($"[DirectConnect] Client connected: {conn.connectionId} from {address}");

                // Track player
                var playerInfo = new PlayerInfo
                {
                    ConnectionId = conn.connectionId,
                    Name = $"Player_{conn.connectionId}", // Will be updated when we get their actual name
                    ConnectedAt = DateTime.UtcNow,
                    Address = address
                };
                _connectedPlayers[conn.connectionId] = playerInfo;

                // Write event for admin panel
                WriteEvent("player_connect", $"Player connected from {address}", new Dictionary<string, string>
                {
                    { "connectionId", conn.connectionId.ToString() },
                    { "address", address },
                    { "playerCount", GetPlayerCount().ToString() }
                });

                // Get current scene - but if we're on Main Menu, send "Player Scene" instead
                // so clients load into the game properly
                var currentScene = SceneManager.GetActiveScene().name;
                var sceneToSend = currentScene;

                // IMPORTANT: If server is stuck on Main Menu, tell clients to load Player Scene
                // This allows clients to enter the game world even when the server is headless
                if (currentScene == "Main Menu" || currentScene == "Loading")
                {
                    sceneToSend = "Player Scene";
                    Plugin.Log.LogInfo($"[DirectConnect] Server is on '{currentScene}', telling client to load 'Player Scene' instead");
                }

                Plugin.Log.LogInfo($"[DirectConnect] Sending scene '{sceneToSend}' to client {conn.connectionId}");

                // Send scene message to client
                // Mirror's SceneMessage tells the client to load a scene
                var sceneMsg = new SceneMessage
                {
                    sceneName = sceneToSend,
                    sceneOperation = SceneOperation.Normal,
                    customHandling = false
                };

                conn.Send(sceneMsg);
                Plugin.Log.LogInfo($"[DirectConnect] Sent SceneMessage to client {conn.connectionId}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error handling client connect: {ex}");
            }
        }

        /// <summary>
        /// Called when a client disconnects from the server.
        /// </summary>
        private static void HandleClientDisconnect(NetworkConnection conn)
        {
            try
            {
                var playerName = "Unknown";
                var connectedFor = "";

                if (_connectedPlayers.TryGetValue(conn.connectionId, out var playerInfo))
                {
                    playerName = playerInfo.Name;
                    var duration = DateTime.UtcNow - playerInfo.ConnectedAt;
                    connectedFor = $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
                    _connectedPlayers.Remove(conn.connectionId);
                }

                Plugin.Log.LogInfo($"[DirectConnect] Client disconnected: {conn.connectionId} ({playerName})");

                // Write event for admin panel
                WriteEvent("player_disconnect", $"{playerName} disconnected", new Dictionary<string, string>
                {
                    { "connectionId", conn.connectionId.ToString() },
                    { "playerName", playerName },
                    { "connectedFor", connectedFor },
                    { "playerCount", GetPlayerCount().ToString() }
                });

                OnClientDisconnected?.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[DirectConnect] Error handling client disconnect: {ex}");
            }
        }

        /// <summary>
        /// Updates a player's name (called when we learn their actual in-game name).
        /// </summary>
        public static void UpdatePlayerName(int connectionId, string name)
        {
            if (_connectedPlayers.TryGetValue(connectionId, out var playerInfo))
            {
                var oldName = playerInfo.Name;
                playerInfo.Name = name;
                Plugin.Log.LogInfo($"[DirectConnect] Player {connectionId} identified as: {name}");

                WriteEvent("player_identified", $"{name} joined the game", new Dictionary<string, string>
                {
                    { "connectionId", connectionId.ToString() },
                    { "name", name },
                    { "playerCount", GetPlayerCount().ToString() }
                });
            }
        }
    }
}
