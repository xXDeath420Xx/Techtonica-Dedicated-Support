using System;
using System.IO;
using System.Net;
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

            // Create a new GameObject for our transport
            var transportGO = new GameObject("DirectConnect_KcpTransport");
            GameObject.DontDestroyOnLoad(transportGO);

            _kcpTransport = transportGO.AddComponent<KcpTransport>();

            // Configure KCP for game traffic
            _kcpTransport.Port = (ushort)Plugin.ServerPort.Value;
            _kcpTransport.NoDelay = true;
            _kcpTransport.Interval = 10;
            _kcpTransport.Timeout = 10000;

            Plugin.Log.LogInfo($"[DirectConnect] Created KCP transport on port {_kcpTransport.Port}");

            return _kcpTransport;
        }

        /// <summary>
        /// Switches the active transport from FizzyFacepunch to KCP.
        /// Must be called BEFORE starting server/client.
        /// </summary>
        public static bool EnableDirectConnect()
        {
            if (!Plugin.EnableDirectConnect.Value)
            {
                Plugin.Log.LogWarning("[DirectConnect] Direct connect is disabled in config");
                return false;
            }

            try
            {
                var networkManager = NetworkManager.singleton;
                if (networkManager == null)
                {
                    Plugin.Log.LogError("[DirectConnect] NetworkManager not found!");
                    return false;
                }

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

                // Get or create our KCP transport
                var kcpTransport = GetOrCreateKcpTransport();

                // Swap the transport on NetworkManager using reflection
                transportField.SetValue(networkManager, kcpTransport);

                // Also try to set Transport.activeTransport if it exists
                var activeTransportField = typeof(Transport).GetField("activeTransport",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (activeTransportField != null)
                {
                    activeTransportField.SetValue(null, kcpTransport);
                }

                _isDirectConnectActive = true;
                Plugin.Log.LogInfo("[DirectConnect] Switched to KCP transport");

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

                // CRITICAL: Set the current scene name so Mirror tells clients to load it
                // Without this, clients stay in menu and can't spawn scene objects
                var currentScene = SceneManager.GetActiveScene().name;
                Plugin.Log.LogInfo($"[DirectConnect] Current scene: {currentScene}");

                // Set networkSceneName via reflection (may be internal in some Mirror versions)
                var sceneNameField = typeof(NetworkManager).GetField("networkSceneName",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (sceneNameField != null)
                {
                    sceneNameField.SetValue(networkManager, currentScene);
                    Plugin.Log.LogInfo($"[DirectConnect] Set networkSceneName to: {currentScene}");
                }
                else
                {
                    // Try the property instead
                    var sceneNameProp = typeof(NetworkManager).GetProperty("networkSceneName",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (sceneNameProp != null && sceneNameProp.CanWrite)
                    {
                        sceneNameProp.SetValue(networkManager, currentScene);
                        Plugin.Log.LogInfo($"[DirectConnect] Set networkSceneName property to: {currentScene}");
                    }
                }

                // Register callbacks for client connect/disconnect
                NetworkServer.OnConnectedEvent += HandleClientConnect;
                NetworkServer.OnDisconnectedEvent += HandleClientDisconnect;

                // Start server only (not host)
                networkManager.StartServer();

                // CRITICAL: Spawn scene network objects
                // When using StartServer() instead of StartHost(), scene objects with NetworkIdentity
                // (like NetworkMessageRelay) need to be explicitly spawned
                if (NetworkServer.active)
                {
                    Plugin.Log.LogInfo("[DirectConnect] Spawning scene network objects...");
                    NetworkServer.SpawnObjects();

                    // Check for critical network objects
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
                            Plugin.Log.LogWarning("[DirectConnect] NetworkMessageRelay.instance is NULL - clients may have issues");
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
                Plugin.Log.LogError($"[DirectConnect] Failed to start server: {ex}");
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
        /// Sends the current scene to the client so they can load it.
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

                // Get current scene
                var currentScene = SceneManager.GetActiveScene().name;
                Plugin.Log.LogInfo($"[DirectConnect] Sending scene '{currentScene}' to client {conn.connectionId}");

                // Send scene message to client
                // Mirror's SceneMessage tells the client to load a scene
                var sceneMsg = new SceneMessage
                {
                    sceneName = currentScene,
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
