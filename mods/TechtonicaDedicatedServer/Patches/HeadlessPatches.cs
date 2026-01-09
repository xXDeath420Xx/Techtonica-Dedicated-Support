using HarmonyLib;
using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using UnityEngine;

namespace TechtonicaDedicatedServer.Patches
{
    /// <summary>
    /// Patches to enable headless (no graphics) mode for dedicated servers.
    /// These patches null out or bypass graphics-dependent code.
    /// </summary>
    public static class HeadlessPatches
    {
        private static bool _patchesApplied;

        public static void ApplyPatches(Harmony harmony)
        {
            if (_patchesApplied) return;
            if (!Plugin.HeadlessMode.Value) return;

            try
            {
                Plugin.Log.LogInfo("[HeadlessPatches] Applying headless mode patches...");

                // Disable cursor locking (causes issues in headless)
                PatchCursorLock(harmony);

                // Disable audio if running headless
                PatchAudio(harmony);

                // Disable camera rendering
                PatchCamera(harmony);

                // Disable UI updates
                PatchUI(harmony);

                // Patch NetworkedPlayer and ThirdPersonDisplayAnimator to prevent null refs in dedicated server mode
                PatchNetworkedPlayer(harmony);
                PatchPlayerCrafting(harmony);
                PatchThirdPersonDisplayAnimator(harmony);

                // CRITICAL: Patch NetworkTransformBase to prevent position RPC spam from ghost player
                // This stops the server's ghost player from flooding clients with position updates
                PatchNetworkTransformForGhostPlayer(harmony);

                // CRITICAL: Patch TheVegetationEngine to prevent graphics errors in headless mode
                PatchTVEGlobalVolume(harmony);

                // GHOST HOST MODE: Patch InputHandler to simulate input for loading prompts
                PatchInputHandler(harmony);

                // CRITICAL: Patch TechNetworkManager.Awake to skip FizzyFacepunch (Steam) transport creation
                // This frees up port 6968 for our KCP transport
                PatchTechNetworkManager(harmony);

                // CRITICAL: Patch TechNetworkManager.OnServerAddPlayer to handle headless mode
                PatchTechNetworkManagerOnServerAddPlayer(harmony);

                // CRITICAL: Patch SaveState.PrepSave to handle null references in headless mode
                PatchSaveState(harmony);

                // CRITICAL: Patch FlowManager's scene loading to work in headless mode
                PatchFlowManagerSceneLoading(harmony);

                // CRITICAL: Patch AddressablesManager to skip async loading waits in headless mode
                PatchAddressablesManager(harmony);

                // CRITICAL: Patch NetworkMessageRelay to handle network actions in headless mode
                PatchNetworkMessageRelay(harmony);

                // CRITICAL: Patch FactorySimManager.SimUpdateAll to handle null Player.instance
                PatchFactorySimManager(harmony);

                // DISABLED: Background timer causes Wine critical section deadlock
                // StartSimulationTickTimer();
                Plugin.Log.LogInfo("[HeadlessPatches] Background timer DISABLED - using Unity callbacks instead");

                // CRITICAL: Patch IPEndPointNonAlloc to handle Wine's incorrect address families
                PatchIPEndPointNonAlloc(harmony);

                _patchesApplied = true;
                Plugin.Log.LogInfo("[HeadlessPatches] Headless patches applied");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Failed to apply patches: {ex}");
            }
        }

        #region Simulation Tick Timer

        /// <summary>
        /// Background timer that drives simulation ticks since Unity's Update loop isn't running in headless mode.
        /// </summary>
        private static Timer _simulationTimer;
        private static bool _timerRunning = false;
        private static DateTime _lastTickTime = DateTime.UtcNow;
        private static int _timerCallCount = 0;

        /// <summary>
        /// Starts a background timer that runs the simulation tick every ~15ms (64 ticks/sec).
        /// This is necessary because Unity's Update() loop doesn't run properly in headless Wine mode.
        /// </summary>
        private static void StartSimulationTickTimer()
        {
            if (_timerRunning) return;

            try
            {
                Plugin.Log.LogInfo("[HeadlessPatches] Starting background simulation tick timer (64 Hz)...");

                // Create a timer that fires every 15ms (~64 Hz)
                _simulationTimer = new Timer(SimulationTimerCallback, null, 1000, 15);
                _timerRunning = true;

                Plugin.Log.LogInfo("[HeadlessPatches] Simulation tick timer started!");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Failed to start simulation timer: {ex}");
            }
        }

        /// <summary>
        /// Timer callback that runs the simulation tick.
        /// </summary>
        private static void SimulationTimerCallback(object state)
        {
            _timerCallCount++;

            try
            {
                // Calculate elapsed time since last tick
                var now = DateTime.UtcNow;
                float dt = (float)(now - _lastTickTime).TotalSeconds;
                _lastTickTime = now;

                // Cap dt to prevent huge jumps
                if (dt > 0.5f) dt = 0.5f;
                if (dt < 0.001f) dt = 0.015f; // Default to 15ms if too small

                // Log every 1000 calls (~15 seconds)
                if (_timerCallCount % 1000 == 1)
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches] SimTimer #{_timerCallCount}: dt={dt:F4}s, NetworkServer.active={Mirror.NetworkServer.active}");
                }

                // CRITICAL: Check if server needs to be started (since Unity's Update doesn't run)
                try
                {
                    Networking.AutoLoadManager.CheckServerStartFromTimer();
                }
                catch (Exception serverEx)
                {
                    if (_timerCallCount % 1000 == 1)
                    {
                        Plugin.Log.LogWarning($"[HeadlessPatches] Server start check error: {serverEx.Message}");
                    }
                }

                // CRITICAL: Tick the network transport to process incoming connections and messages
                // Since Unity's Update loop isn't running, Mirror won't receive any network events
                try
                {
                    TickNetworkTransport();
                }
                catch (Exception netEx)
                {
                    if (_timerCallCount % 1000 == 1)
                    {
                        Plugin.Log.LogWarning($"[HeadlessPatches] Network tick error: {netEx.Message}");
                    }
                }

                // Get FactorySimManager.instance
                var factorySimType = AccessTools.TypeByName("FactorySimManager");
                if (factorySimType == null) return;

                var fsInstanceProp = factorySimType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                var fsInstance = fsInstanceProp?.GetValue(null);
                if (fsInstance == null) return;

                // Run the simulation tick
                RunSimulationTick(fsInstance, dt);
            }
            catch (Exception ex)
            {
                // Log errors occasionally to avoid spam
                if (_timerCallCount % 1000 == 1)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] SimTimer error: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Tick the network transport to process incoming connections and messages.
        /// This is needed because Unity's Update loop doesn't run in headless mode.
        /// </summary>
        private static int _netTickLogCount = 0;
        private static int _lastConnectionCount = 0;
        private static void TickNetworkTransport()
        {
            if (!Mirror.NetworkServer.active) return;

            // Get the active transport
            var transport = Mirror.Transport.activeTransport;
            if (transport == null) return;

            // Log connection count changes
            int currentConnections = Mirror.NetworkServer.connections.Count;
            if (currentConnections != _lastConnectionCount)
            {
                Plugin.Log.LogInfo($"[HeadlessPatches] Connection count changed: {_lastConnectionCount} -> {currentConnections}");
                _lastConnectionCount = currentConnections;
            }

            // Call ServerEarlyUpdate to receive data
            try
            {
                transport.ServerEarlyUpdate();
            }
            catch (Exception ex)
            {
                _netTickLogCount++;
                if (_netTickLogCount % 1000 == 1)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] ServerEarlyUpdate error: {ex.Message}");
                }
            }

            // Call ServerLateUpdate to send data
            try
            {
                transport.ServerLateUpdate();
            }
            catch (Exception ex)
            {
                _netTickLogCount++;
                if (_netTickLogCount % 1000 == 1)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] ServerLateUpdate error: {ex.Message}");
                }
            }
        }

        #endregion

        /// <summary>
        /// Helper to verify a Harmony patch was applied correctly
        /// </summary>
        private static void VerifyPatch(MethodInfo method, string name)
        {
            try
            {
                var patchInfo = Harmony.GetPatchInfo(method);
                if (patchInfo?.Prefixes != null && patchInfo.Prefixes.Count > 0)
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches] {name} patched! Prefixes: {patchInfo.Prefixes.Count}");
                    foreach (var p in patchInfo.Prefixes)
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches]   Prefix: {p.owner} -> {p.PatchMethod.Name}");
                    }
                }
                else
                {
                    Plugin.Log.LogError($"[HeadlessPatches] {name} patch FAILED - no prefixes registered!");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Error verifying {name} patch: {ex.Message}");
            }
        }

        private static void PatchSaveState(Harmony harmony)
        {
            try
            {
                // Patch NetworkedPlayer.SendSaveString - this is the coroutine that calls SaveAsString
                // By patching this, we can intercept before it ever tries to call SaveAsString
                var networkedPlayerType = AccessTools.TypeByName("NetworkedPlayer");
                if (networkedPlayerType != null)
                {
                    // List all methods to find SendSaveString
                    var allMethods = networkedPlayerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                    foreach (var m in allMethods)
                    {
                        if (m.Name.Contains("SendSave") || m.Name.Contains("RequestInitial"))
                        {
                            var parms = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                            Plugin.Log.LogInfo($"[HeadlessPatches] NetworkedPlayer method: {m.ReturnType.Name} {m.Name}({parms}) [Static={m.IsStatic}]");
                        }
                    }

                    // APPROACH 1: Patch UserCode_RequestInitialSaveData using TRANSPILER
                    // Transpilers modify IL directly and should work better under Wine/Mono
                    var userCodeMethod = networkedPlayerType.GetMethod("UserCode_RequestInitialSaveData",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (userCodeMethod != null)
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] Found UserCode_RequestInitialSaveData: {userCodeMethod.DeclaringType.Name}.{userCodeMethod.Name}");
                        // Use BOTH prefix AND transpiler
                        var userCodePrefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(UserCode_RequestInitialSaveData_Prefix));
                        userCodePrefix.priority = Priority.First; // Run first
                        var userCodeTranspiler = new HarmonyMethod(typeof(HeadlessPatches), nameof(UserCode_RequestInitialSaveData_Transpiler));
                        harmony.Patch(userCodeMethod, prefix: userCodePrefix, transpiler: userCodeTranspiler);
                        VerifyPatch(userCodeMethod, "UserCode_RequestInitialSaveData");
                    }

                    // APPROACH 2: Patch the STATIC InvokeUserCode_RequestInitialSaveData - this is what Mirror calls
                    // Use BOTH prefix AND finalizer to ensure we catch it
                    var invokeMethod = networkedPlayerType.GetMethod("InvokeUserCode_RequestInitialSaveData",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (invokeMethod != null)
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] Found InvokeUserCode: {invokeMethod.DeclaringType.Name}.{invokeMethod.Name}");
                        var invokePrefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(InvokeUserCode_RequestInitialSaveData_Prefix));
                        var invokeFinalizer = new HarmonyMethod(typeof(HeadlessPatches), nameof(InvokeUserCode_Finalizer));
                        harmony.Patch(invokeMethod, prefix: invokePrefix, finalizer: invokeFinalizer);
                        VerifyPatch(invokeMethod, "InvokeUserCode_RequestInitialSaveData");
                    }

                    // APPROACH 2B: Also patch Mirror's RemoteCallHelper.Invoke if we can find it
                    try
                    {
                        var remoteCallHelperType = AccessTools.TypeByName("Mirror.RemoteCalls.RemoteCallHelper");
                        if (remoteCallHelperType != null)
                        {
                            var invokeDelegate = remoteCallHelperType.GetMethod("InvokeHandlerDelegate",
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                            if (invokeDelegate != null)
                            {
                                Plugin.Log.LogInfo($"[HeadlessPatches] Found Mirror RemoteCallHelper.InvokeHandlerDelegate");
                                var mirrorPrefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(Mirror_InvokeHandlerDelegate_Prefix));
                                harmony.Patch(invokeDelegate, prefix: mirrorPrefix);
                                VerifyPatch(invokeDelegate, "Mirror.InvokeHandlerDelegate");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"[HeadlessPatches] Mirror patch failed: {ex.Message}");
                    }

                    // APPROACH 3: Patch RequestInitialSaveData (the Command wrapper)
                    var requestMethod = networkedPlayerType.GetMethod("RequestInitialSaveData",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (requestMethod != null)
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] Found RequestInitialSaveData: {requestMethod.DeclaringType.Name}.{requestMethod.Name}");
                        var requestPrefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(RequestInitialSaveData_Prefix));
                        harmony.Patch(requestMethod, prefix: requestPrefix);
                        VerifyPatch(requestMethod, "RequestInitialSaveData");
                    }

                    // APPROACH 4: Patch SendSaveString coroutine's MoveNext
                    var sendSaveStringType = networkedPlayerType.GetNestedType("<SendSaveString>d__55", BindingFlags.NonPublic);
                    if (sendSaveStringType == null)
                    {
                        // Try other possible names
                        foreach (var nestedType in networkedPlayerType.GetNestedTypes(BindingFlags.NonPublic))
                        {
                            if (nestedType.Name.Contains("SendSaveString"))
                            {
                                sendSaveStringType = nestedType;
                                Plugin.Log.LogInfo($"[HeadlessPatches] Found coroutine type: {nestedType.Name}");
                                break;
                            }
                        }
                    }
                    if (sendSaveStringType != null)
                    {
                        var moveNextMethod = sendSaveStringType.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (moveNextMethod != null)
                        {
                            Plugin.Log.LogInfo($"[HeadlessPatches] Found SendSaveString.MoveNext");
                            var moveNextPrefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(SendSaveString_MoveNext_Prefix));
                            harmony.Patch(moveNextMethod, prefix: moveNextPrefix);
                            VerifyPatch(moveNextMethod, "SendSaveString.MoveNext");
                        }
                    }
                }

                var saveStateType = AccessTools.TypeByName("SaveState");
                if (saveStateType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] SaveState type not found");
                    return;
                }

                // Log all SaveAsString methods to understand what we're dealing with
                var saveMethods = saveStateType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (var m in saveMethods)
                {
                    if (m.Name.Contains("SaveAsString") || m.Name.Contains("PrepSave"))
                    {
                        var parms = string.Join(", ", Array.ConvertAll(m.GetParameters(), p => $"{p.ParameterType.Name} {p.Name}"));
                        Plugin.Log.LogInfo($"[HeadlessPatches] SaveState method: {m.ReturnType.Name} {m.Name}({parms}) [Static={m.IsStatic}]");
                    }
                }

                // Patch SaveAsString with a PREFIX that completely bypasses the method in headless mode
                var saveAsStringMethod = AccessTools.Method(saveStateType, "SaveAsString");
                if (saveAsStringMethod != null)
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches] Patching: {saveAsStringMethod.DeclaringType.Name}.{saveAsStringMethod.Name}, Static={saveAsStringMethod.IsStatic}, ReturnType={saveAsStringMethod.ReturnType.Name}");

                    var prefixMethod = AccessTools.Method(typeof(HeadlessPatches), nameof(SaveAsString_Prefix));
                    Plugin.Log.LogInfo($"[HeadlessPatches] Prefix method found: {prefixMethod?.Name ?? "NULL"}, DeclaringType: {prefixMethod?.DeclaringType.Name ?? "NULL"}");

                    var prefix = new HarmonyMethod(prefixMethod);
                    Plugin.Log.LogInfo($"[HeadlessPatches] HarmonyMethod created, method: {prefix.method?.Name ?? "NULL"}");

                    harmony.Patch(saveAsStringMethod, prefix: prefix);

                    // Verify the patch was applied
                    var patches = Harmony.GetPatchInfo(saveAsStringMethod);
                    Plugin.Log.LogInfo($"[HeadlessPatches] Patches on SaveAsString - Prefixes: {patches?.Prefixes?.Count ?? 0}, Postfixes: {patches?.Postfixes?.Count ?? 0}");
                    if (patches?.Prefixes != null)
                    {
                        foreach (var p in patches.Prefixes)
                        {
                            Plugin.Log.LogInfo($"[HeadlessPatches]   Prefix: {p.owner} -> {p.PatchMethod.Name}");
                        }
                    }
                }

                // ALSO patch PrepSave directly
                var prepSaveMethod = AccessTools.Method(saveStateType, "PrepSave");
                if (prepSaveMethod != null)
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches] Patching PrepSave: Static={prepSaveMethod.IsStatic}");
                    var prepSavePrefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(PrepSave_Prefix));
                    harmony.Patch(prepSaveMethod, prefix: prepSavePrefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] SaveState.PrepSave patched to skip in headless mode");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] SaveState patch failed: {ex}");
            }
        }

        private static void PatchCursorLock(Harmony harmony)
        {
            try
            {
                // Prevent cursor lock operations
                var lockStateProperty = typeof(Cursor).GetProperty("lockState");
                if (lockStateProperty != null)
                {
                    var setter = lockStateProperty.GetSetMethod();
                    if (setter != null)
                    {
                        var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(CursorLock_Prefix));
                        harmony.Patch(setter, prefix: prefix);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] Cursor patch failed: {ex.Message}");
            }
        }

        private static void PatchAudio(Harmony harmony)
        {
            try
            {
                // Disable AudioListener if present - use reflection to avoid assembly reference issues
                var audioListenerType = AccessTools.TypeByName("UnityEngine.AudioListener");
                if (audioListenerType == null) return;

                var awakeMethod = audioListenerType.GetMethod("Awake",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (awakeMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(DisableComponent_Prefix));
                    harmony.Patch(awakeMethod, prefix: prefix);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] Audio patch failed: {ex.Message}");
            }
        }

        private static void PatchCamera(Harmony harmony)
        {
            try
            {
                // We could disable camera rendering, but this might break
                // game logic that depends on camera positions
                // For now, just log that we're in headless mode
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] Camera patch failed: {ex.Message}");
            }
        }

        private static void PatchUI(Harmony harmony)
        {
            try
            {
                // Find and patch UI manager if it exists
                var uiManagerType = AccessTools.TypeByName("UIManager") ??
                                   AccessTools.TypeByName("UIController") ??
                                   AccessTools.TypeByName("HUDManager");

                if (uiManagerType != null)
                {
                    var updateMethod = AccessTools.Method(uiManagerType, "Update");
                    if (updateMethod != null)
                    {
                        var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(SkipMethod_Prefix));
                        harmony.Patch(updateMethod, prefix: prefix);
                        Plugin.Log.LogInfo("[HeadlessPatches] UI updates disabled");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] UI patch failed: {ex.Message}");
            }
        }

        private static void PatchNetworkedPlayer(Harmony harmony)
        {
            try
            {
                var networkedPlayerType = AccessTools.TypeByName("NetworkedPlayer");
                if (networkedPlayerType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] NetworkedPlayer type not found");
                    return;
                }

                var updateMethod = AccessTools.Method(networkedPlayerType, "Update");
                if (updateMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkedPlayer_Update_Prefix));
                    harmony.Patch(updateMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] NetworkedPlayer.Update patched to skip");
                }

                // Patch OnStartServer with prefix (let it run) and finalizer (catch errors)
                var onStartServerMethod = AccessTools.Method(networkedPlayerType, "OnStartServer");
                if (onStartServerMethod != null)
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches] Found OnStartServer: {onStartServerMethod.DeclaringType.Name}.{onStartServerMethod.Name}");
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkedPlayer_OnStartServer_Prefix));
                    var finalizer = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkedPlayer_OnStartServer_Finalizer));
                    harmony.Patch(onStartServerMethod, prefix: prefix, finalizer: finalizer);
                    Plugin.Log.LogInfo("[HeadlessPatches] NetworkedPlayer.OnStartServer patched with error handling");
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] OnStartServer method not found on NetworkedPlayer!");
                    // Try to list all methods to see what's available
                    var allMethods = networkedPlayerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                    foreach (var m in allMethods)
                    {
                        if (m.Name.Contains("Start") || m.Name.Contains("Server"))
                        {
                            Plugin.Log.LogInfo($"[HeadlessPatches]   Method: {m.DeclaringType.Name}.{m.Name}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] NetworkedPlayer patch failed: {ex.Message}");
            }
        }

        // Prefix to handle OnStartServer - allow it to run but catch errors
        private static bool NetworkedPlayer_OnStartServer_Prefix(object __instance)
        {
            try
            {
                Plugin.Log.LogInfo("[HeadlessPatches] NetworkedPlayer.OnStartServer called - letting it run with error handling");
                // Allow original to run - we'll patch PlayerCrafting.Init separately
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] NetworkedPlayer.OnStartServer error (ignored): {ex.Message}");
                return false;
            }
        }

        // Finalizer to catch any exceptions from OnStartServer
        public static Exception NetworkedPlayer_OnStartServer_Finalizer(Exception __exception, object __instance)
        {
            if (__exception != null)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] NetworkedPlayer.OnStartServer exception caught: {__exception.Message}");
                // Suppress the exception - let the server continue
            }

            // CRITICAL: Ensure player is registered in allPlayers even if OnStartServer had issues
            try
            {
                EnsurePlayerRegistered(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] Failed to ensure player registration: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Ensures a NetworkedPlayer is registered in GameState.instance.allPlayers.
        /// This is critical for NetworkMessageRelay.GetPlayer() to find the player when handling commands.
        /// </summary>
        private static void EnsurePlayerRegistered(object networkedPlayer)
        {
            if (networkedPlayer == null) return;

            try
            {
                // Get the NetworkID from the player
                var networkedPlayerType = networkedPlayer.GetType();
                var networkIdProp = networkedPlayerType.GetProperty("NetworkID", BindingFlags.Public | BindingFlags.Instance);
                if (networkIdProp == null)
                {
                    networkIdProp = networkedPlayerType.GetProperty("NetworkNetworkID", BindingFlags.Public | BindingFlags.Instance);
                }

                string networkId = null;
                if (networkIdProp != null)
                {
                    networkId = networkIdProp.GetValue(networkedPlayer) as string;
                }

                if (string.IsNullOrEmpty(networkId))
                {
                    // Try to get from connectionToClient.authenticationData
                    var connectionField = networkedPlayerType.GetProperty("connectionToClient", BindingFlags.Public | BindingFlags.Instance);
                    if (connectionField != null)
                    {
                        var connection = connectionField.GetValue(networkedPlayer);
                        if (connection != null)
                        {
                            var authDataProp = connection.GetType().GetProperty("authenticationData");
                            if (authDataProp != null)
                            {
                                networkId = authDataProp.GetValue(connection) as string;
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(networkId))
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] Could not determine player NetworkID for registration");
                    return;
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] Player NetworkID: {networkId}");

                // Get GameState.instance
                var gameStateType = AccessTools.TypeByName("GameState");
                if (gameStateType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] GameState type not found for player registration");
                    return;
                }

                var instanceProp = gameStateType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceProp == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] GameState.instance property not found");
                    return;
                }

                var gameStateInstance = instanceProp.GetValue(null);
                if (gameStateInstance == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] GameState.instance is NULL - cannot register player");
                    return;
                }

                // Get allPlayers dictionary
                var allPlayersField = gameStateType.GetField("allPlayers", BindingFlags.Public | BindingFlags.Instance);
                if (allPlayersField == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] GameState.allPlayers field not found");
                    return;
                }

                var allPlayers = allPlayersField.GetValue(gameStateInstance);
                if (allPlayers == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] GameState.allPlayers is NULL");
                    return;
                }

                // allPlayers is Dictionary<string, NetworkedPlayer>
                var dictType = allPlayers.GetType();
                var containsKeyMethod = dictType.GetMethod("ContainsKey");
                var addMethod = dictType.GetMethod("set_Item");

                bool alreadyRegistered = (bool)containsKeyMethod.Invoke(allPlayers, new object[] { networkId });
                if (!alreadyRegistered)
                {
                    addMethod.Invoke(allPlayers, new object[] { networkId, networkedPlayer });
                    Plugin.Log.LogInfo($"[HeadlessPatches] Registered player '{networkId}' in GameState.allPlayers");
                }
                else
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches] Player '{networkId}' already registered in GameState.allPlayers");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Error registering player: {ex.Message}");
            }
        }

        private static void PatchPlayerCrafting(Harmony harmony)
        {
            try
            {
                var playerCraftingType = AccessTools.TypeByName("PlayerCrafting");
                if (playerCraftingType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] PlayerCrafting type not found");
                    return;
                }

                // Patch Init method to handle headless mode
                var initMethod = AccessTools.Method(playerCraftingType, "Init");
                if (initMethod != null)
                {
                    var finalizer = new HarmonyMethod(typeof(HeadlessPatches), nameof(PlayerCrafting_Init_Finalizer));
                    harmony.Patch(initMethod, finalizer: finalizer);
                    Plugin.Log.LogInfo("[HeadlessPatches] PlayerCrafting.Init patched with finalizer");
                }

                // Also patch any methods that might throw when InventoryWrapper is null
                var craftMethods = playerCraftingType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var method in craftMethods)
                {
                    if (method.Name.Contains("Craft") || method.Name.Contains("Add") || method.Name.Contains("Remove"))
                    {
                        try
                        {
                            var finalizer = new HarmonyMethod(typeof(HeadlessPatches), nameof(PlayerCrafting_Method_Finalizer));
                            harmony.Patch(method, finalizer: finalizer);
                        }
                        catch { }
                    }
                }
                Plugin.Log.LogInfo("[HeadlessPatches] PlayerCrafting methods patched");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] PlayerCrafting patch failed: {ex.Message}");
            }
        }

        public static Exception PlayerCrafting_Init_Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] PlayerCrafting.Init exception caught (headless mode): {__exception.Message}");
                return null; // Suppress
            }
            return null;
        }

        public static Exception PlayerCrafting_Method_Finalizer(Exception __exception)
        {
            if (__exception != null)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] PlayerCrafting method exception: {__exception.Message}");
                return null; // Suppress
            }
            return null;
        }

        private static void PatchThirdPersonDisplayAnimator(Harmony harmony)
        {
            try
            {
                var animatorType = AccessTools.TypeByName("ThirdPersonDisplayAnimator");
                if (animatorType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] ThirdPersonDisplayAnimator type not found");
                    return;
                }

                // Patch Update method
                var updateMethod = AccessTools.Method(animatorType, "Update");
                if (updateMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(ThirdPersonDisplayAnimator_Update_Prefix));
                    harmony.Patch(updateMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] ThirdPersonDisplayAnimator.Update patched to skip");
                }

                // Also patch UpdateSillyStuff if it exists
                var sillyMethod = AccessTools.Method(animatorType, "UpdateSillyStuff");
                if (sillyMethod != null)
                {
                    var sillyPrefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(ThirdPersonDisplayAnimator_UpdateSillyStuff_Prefix));
                    harmony.Patch(sillyMethod, prefix: sillyPrefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] ThirdPersonDisplayAnimator.UpdateSillyStuff patched to skip");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] ThirdPersonDisplayAnimator patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch NetworkTransformBase to prevent position RPC spam from server's ghost player.
        /// On a dedicated server, the ghost host player isn't moving but NetworkTransformBase
        /// continuously sends position updates to all clients, flooding them with RPCs.
        /// This causes KCP dead link detection and disconnects.
        /// </summary>
        private static void PatchNetworkTransformForGhostPlayer(Harmony harmony)
        {
            try
            {
                // Find Mirror's NetworkTransformBase or NetworkTransformReliable
                var transformTypes = new[] {
                    "Mirror.NetworkTransformBase",
                    "Mirror.NetworkTransformReliable",
                    "Mirror.NetworkTransformUnreliable",
                    "NetworkTransformBase",
                    "NetworkTransformReliable"
                };

                Type transformType = null;
                foreach (var typeName in transformTypes)
                {
                    transformType = AccessTools.TypeByName(typeName);
                    if (transformType != null)
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] Found transform type: {typeName}");
                        break;
                    }
                }

                if (transformType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] NetworkTransformBase type not found - looking for any sync component");

                    // Try to find any component with sync in the name
                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                    foreach (var asm in assemblies)
                    {
                        try
                        {
                            foreach (var type in asm.GetTypes())
                            {
                                if (type.Name.Contains("NetworkTransform") ||
                                    (type.Name.Contains("Sync") && type.Namespace?.Contains("Mirror") == true))
                                {
                                    Plugin.Log.LogInfo($"[HeadlessPatches] Found candidate: {type.FullName}");
                                    transformType = type;
                                    break;
                                }
                            }
                        }
                        catch { }
                        if (transformType != null) break;
                    }
                }

                if (transformType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] No NetworkTransform type found");
                    return;
                }

                // Patch the Update or LateUpdate method to skip for server-only (non-client) players
                var updateMethod = AccessTools.Method(transformType, "Update") ??
                                   AccessTools.Method(transformType, "LateUpdate");

                if (updateMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkTransformBase_Update_Prefix));
                    harmony.Patch(updateMethod, prefix: prefix);
                    Plugin.Log.LogInfo($"[HeadlessPatches] {transformType.Name}.{updateMethod.Name} patched to skip for ghost player");
                }

                // Also try to patch OnSerialize to prevent sending data
                var onSerializeMethod = AccessTools.Method(transformType, "OnSerialize");
                if (onSerializeMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkTransformBase_OnSerialize_Prefix));
                    harmony.Patch(onSerializeMethod, prefix: prefix);
                    Plugin.Log.LogInfo($"[HeadlessPatches] {transformType.Name}.OnSerialize patched");
                }

                // Patch CmdClientToServerSync if it exists (client->server sync)
                var cmdSyncMethod = AccessTools.Method(transformType, "CmdClientToServerSync");
                if (cmdSyncMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkTransformBase_CmdSync_Prefix));
                    harmony.Patch(cmdSyncMethod, prefix: prefix);
                    Plugin.Log.LogInfo($"[HeadlessPatches] {transformType.Name}.CmdClientToServerSync patched");
                }

                // Try to patch RpcServerToClientSync (server->client sync) - this is the RPC causing spam
                var rpcSyncMethod = AccessTools.Method(transformType, "RpcServerToClientSync");
                if (rpcSyncMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkTransformBase_RpcSync_Prefix));
                    harmony.Patch(rpcSyncMethod, prefix: prefix);
                    Plugin.Log.LogInfo($"[HeadlessPatches] {transformType.Name}.RpcServerToClientSync patched");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] NetworkTransform patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix for NetworkTransformBase.Update - skip for server's ghost player.
        /// The ghost player isn't controlled by anyone, so syncing its position is pointless.
        /// </summary>
        public static bool NetworkTransformBase_Update_Prefix(object __instance)
        {
            if (!Plugin.HeadlessMode.Value) return true;

            try
            {
                // Get the NetworkIdentity from the component
                var componentType = __instance.GetType();
                var identityProp = componentType.GetProperty("netIdentity", BindingFlags.Public | BindingFlags.Instance);
                if (identityProp == null)
                {
                    identityProp = componentType.GetProperty("NetworkIdentity", BindingFlags.Public | BindingFlags.Instance);
                }

                var identity = identityProp?.GetValue(__instance) as Mirror.NetworkIdentity;
                if (identity == null) return true;

                // Check if this is a server-only object (not owned by a client)
                // On a dedicated server, the host's player is server-only
                if (identity.isServer && !identity.isClient)
                {
                    // This is the ghost host player - skip position sync
                    return false;
                }
            }
            catch { }

            return true;
        }

        /// <summary>
        /// Prefix for NetworkTransformBase.OnSerialize - reduce data sent for ghost player.
        /// </summary>
        public static bool NetworkTransformBase_OnSerialize_Prefix(object __instance)
        {
            if (!Plugin.HeadlessMode.Value) return true;

            try
            {
                var componentType = __instance.GetType();
                var identityProp = componentType.GetProperty("netIdentity", BindingFlags.Public | BindingFlags.Instance);
                var identity = identityProp?.GetValue(__instance) as Mirror.NetworkIdentity;

                if (identity != null && identity.isServer && !identity.isClient)
                {
                    // Ghost player - skip serialization
                    return false;
                }
            }
            catch { }

            return true;
        }

        /// <summary>
        /// Prefix for CmdClientToServerSync - allow normal client position updates.
        /// </summary>
        public static bool NetworkTransformBase_CmdSync_Prefix()
        {
            // Allow client->server sync to work normally
            return true;
        }

        /// <summary>
        /// Prefix for RpcServerToClientSync - skip sending position for ghost player.
        /// This is the main source of RPC spam - the server constantly sending position updates.
        /// </summary>
        public static bool NetworkTransformBase_RpcSync_Prefix(object __instance)
        {
            if (!Plugin.HeadlessMode.Value) return true;

            try
            {
                var componentType = __instance.GetType();
                var identityProp = componentType.GetProperty("netIdentity", BindingFlags.Public | BindingFlags.Instance);
                var identity = identityProp?.GetValue(__instance) as Mirror.NetworkIdentity;

                if (identity != null && identity.isServer && !identity.isClient)
                {
                    // Ghost player - don't send RPC
                    return false;
                }
            }
            catch { }

            return true;
        }

        /// <summary>
        /// Patch TechNetworkManager.Awake to destroy FizzyFacepunch (Steam transport) after creation.
        /// This frees up any ports it might bind and allows our KCP transport to work.
        /// </summary>
        private static void PatchTechNetworkManager(Harmony harmony)
        {
            try
            {
                var techNetworkManagerType = AccessTools.TypeByName("TechNetworkManager");
                if (techNetworkManagerType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] TechNetworkManager type not found");
                    return;
                }

                var awakeMethod = AccessTools.Method(techNetworkManagerType, "Awake");
                if (awakeMethod != null)
                {
                    // Use POSTFIX to destroy FizzyFacepunch after it's created
                    // This allows base.Awake() to run but removes the Steam transport
                    var postfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(TechNetworkManager_Awake_Postfix));
                    harmony.Patch(awakeMethod, postfix: postfix);
                    Plugin.Log.LogInfo("[HeadlessPatches] TechNetworkManager.Awake patched (postfix) to destroy FizzyFacepunch");
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] TechNetworkManager.Awake method not found");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] TechNetworkManager patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch FlowManager's scene loading to work in headless mode.
        /// Unity's async scene loading doesn't complete properly in Wine headless mode.
        /// We bypass the scene loading coroutine and start the server directly.
        /// </summary>
        private static void PatchFlowManagerSceneLoading(Harmony harmony)
        {
            try
            {
                var flowManagerType = AccessTools.TypeByName("FlowManager");
                if (flowManagerType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] FlowManager type not found");
                    return;
                }

                Plugin.Log.LogInfo("[HeadlessPatches] Found FlowManager type, patching scene loading...");

                // Patch the LoadScreenCoroutine's state machine (MoveNext method)
                // The coroutine is compiled into a nested class like <LoadScreenCoroutine>d__XX
                foreach (var nestedType in flowManagerType.GetNestedTypes(BindingFlags.NonPublic))
                {
                    if (nestedType.Name.Contains("LoadScreenCoroutine"))
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] Found coroutine type: {nestedType.Name}");

                        var moveNextMethod = nestedType.GetMethod("MoveNext",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                        if (moveNextMethod != null)
                        {
                            Plugin.Log.LogInfo("[HeadlessPatches] Found LoadScreenCoroutine.MoveNext");
                            var moveNextPostfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(LoadScreenCoroutine_MoveNext_Postfix));
                            harmony.Patch(moveNextMethod, postfix: moveNextPostfix);
                            Plugin.Log.LogInfo("[HeadlessPatches] LoadScreenCoroutine.MoveNext patched (postfix)");
                        }
                        break;
                    }
                }

                // Patch LoadingUI.confirmedLoad to always return true in headless mode
                var loadingUIType = AccessTools.TypeByName("LoadingUI");
                if (loadingUIType != null)
                {
                    // Try to patch the confirmedLoad field getter if it's a property
                    var confirmedLoadProp = loadingUIType.GetProperty("confirmedLoad",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    if (confirmedLoadProp != null && confirmedLoadProp.GetMethod != null)
                    {
                        var confirmedLoadPostfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(LoadingUI_ConfirmedLoad_Postfix));
                        harmony.Patch(confirmedLoadProp.GetMethod, postfix: confirmedLoadPostfix);
                        Plugin.Log.LogInfo("[HeadlessPatches] LoadingUI.confirmedLoad getter patched");
                    }

                    // Patch LoadingUI.OnFinishLoading to complete loading faster
                    var onFinishMethod = loadingUIType.GetMethod("OnFinishLoading",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (onFinishMethod != null)
                    {
                        var onFinishPostfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(LoadingUI_OnFinishLoading_Postfix));
                        harmony.Patch(onFinishMethod, postfix: onFinishPostfix);
                        Plugin.Log.LogInfo("[HeadlessPatches] LoadingUI.OnFinishLoading patched");
                    }
                }

                // Patch AsyncOperation to force completion in headless mode
                // This is a key fix - Unity's async operations don't complete in Wine
                PatchAsyncOperations(harmony);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] FlowManager scene loading patch failed: {ex}");
            }
        }

        /// <summary>
        /// Patch AsyncOperation to force progress completion in headless mode.
        /// Unity's async scene loading gets stuck at 0.9 progress in Wine.
        /// </summary>
        private static void PatchAsyncOperations(Harmony harmony)
        {
            try
            {
                // Patch SceneManager.LoadSceneAsync to use sync loading in headless mode
                var sceneManagerType = typeof(UnityEngine.SceneManagement.SceneManager);

                // Get the LoadSceneAsync(string, LoadSceneMode) overload specifically
                var loadSceneAsyncMethod = sceneManagerType.GetMethod("LoadSceneAsync",
                    new Type[] { typeof(string), typeof(UnityEngine.SceneManagement.LoadSceneMode) });

                if (loadSceneAsyncMethod != null)
                {
                    try
                    {
                        // Use PREFIX to do sync loading before async
                        var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(LoadSceneAsync_SyncPrefix));
                        var postfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(LoadSceneAsync_Postfix));
                        harmony.Patch(loadSceneAsyncMethod, prefix: prefix, postfix: postfix);
                        Plugin.Log.LogInfo($"[HeadlessPatches] Patched SceneManager.LoadSceneAsync(string, LoadSceneMode) with SYNC prefix");
                    }
                    catch (Exception patchEx)
                    {
                        Plugin.Log.LogWarning($"[HeadlessPatches] Failed to patch LoadSceneAsync with prefix: {patchEx.Message}");
                    }
                }

                // Get all other LoadSceneAsync overloads and patch with postfix only
                var loadSceneAsyncMethods = sceneManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "LoadSceneAsync");

                foreach (var method in loadSceneAsyncMethods)
                {
                    // Skip the one we already patched
                    if (method == loadSceneAsyncMethod) continue;

                    try
                    {
                        var postfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(LoadSceneAsync_Postfix));
                        harmony.Patch(method, postfix: postfix);
                        Plugin.Log.LogInfo($"[HeadlessPatches] Patched SceneManager.LoadSceneAsync ({method.GetParameters().Length} params) with postfix");
                    }
                    catch (Exception patchEx)
                    {
                        Plugin.Log.LogWarning($"[HeadlessPatches] Failed to patch LoadSceneAsync: {patchEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] AsyncOperation patch failed: {ex}");
            }
        }

        /// <summary>
        /// Postfix for LoadScreenCoroutine.MoveNext - force scene loading completion in headless mode
        /// </summary>
        public static void LoadScreenCoroutine_MoveNext_Postfix(object __instance, ref bool __result)
        {
            if (!Plugin.HeadlessMode.Value) return;

            try
            {
                // The coroutine state machine has a <>1__state field
                var stateType = __instance.GetType();
                var stateField = stateType.GetField("<>1__state", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (stateField != null)
                {
                    int state = (int)stateField.GetValue(__instance);

                    // Log state transitions (only first few times to avoid spam)
                    if (state >= 0 && state < 10)
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] LoadScreenCoroutine state: {state}");
                    }

                    // If coroutine is taking too long (state > 5), try to force completion
                    if (state > 5)
                    {
                        ForceSceneLoadingCompletion();
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] LoadScreenCoroutine_MoveNext_Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Force scene loading to complete in headless mode
        /// </summary>
        private static void ForceSceneLoadingCompletion()
        {
            try
            {
                // Set Time.timeScale to 1 to allow coroutines to progress
                Time.timeScale = 1f;

                // Try to set FlowManager.isTransitioning = false
                var flowManagerType = AccessTools.TypeByName("FlowManager");
                if (flowManagerType != null)
                {
                    var isTransitioningField = flowManagerType.GetField("isTransitioning",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                    if (isTransitioningField != null)
                    {
                        bool currentValue = (bool)isTransitioningField.GetValue(null);
                        if (currentValue)
                        {
                            Plugin.Log.LogInfo("[HeadlessPatches] Forcing FlowManager.isTransitioning = false");
                            isTransitioningField.SetValue(null, false);
                        }
                    }
                }

                // Try to complete LoadingUI
                var loadingUIType = AccessTools.TypeByName("LoadingUI");
                if (loadingUIType != null)
                {
                    var instanceField = loadingUIType.GetField("instance",
                        BindingFlags.Public | BindingFlags.Static);
                    var loadingUI = instanceField?.GetValue(null);

                    if (loadingUI != null)
                    {
                        // Set confirmedLoad = true
                        var confirmedLoadField = loadingUIType.GetField("confirmedLoad",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (confirmedLoadField != null)
                        {
                            confirmedLoadField.SetValue(loadingUI, true);
                            Plugin.Log.LogInfo("[HeadlessPatches] Set LoadingUI.confirmedLoad = true");
                        }

                        // Set _loaded = true
                        var loadedField = loadingUIType.GetField("_loaded",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (loadedField != null)
                        {
                            loadedField.SetValue(loadingUI, true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] ForceSceneLoadingCompletion error: {ex}");
            }
        }

        /// <summary>
        /// Postfix for LoadingUI.confirmedLoad - return true in headless mode to skip "press any key"
        /// </summary>
        public static void LoadingUI_ConfirmedLoad_Postfix(ref bool __result)
        {
            if (Plugin.HeadlessMode.Value)
            {
                __result = true;
            }
        }

        /// <summary>
        /// Postfix for LoadingUI.OnFinishLoading - force loading complete in headless mode
        /// </summary>
        public static void LoadingUI_OnFinishLoading_Postfix(object __instance)
        {
            if (!Plugin.HeadlessMode.Value) return;

            try
            {
                // Force confirmedLoad = true
                var loadingUIType = __instance.GetType();
                var confirmedLoadField = loadingUIType.GetField("confirmedLoad",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (confirmedLoadField != null)
                {
                    confirmedLoadField.SetValue(__instance, true);
                    Plugin.Log.LogInfo("[HeadlessPatches] LoadingUI.OnFinishLoading - set confirmedLoad = true");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] LoadingUI_OnFinishLoading_Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix for SceneManager.LoadSceneAsync - force async operation to complete in headless mode
        /// </summary>
        public static void LoadSceneAsync_Postfix(ref AsyncOperation __result)
        {
            if (!Plugin.HeadlessMode.Value) return;
            if (__result == null) return;

            try
            {
                // Force allowSceneActivation to true
                __result.allowSceneActivation = true;

                // Log the scene loading
                Plugin.Log.LogInfo($"[HeadlessPatches] LoadSceneAsync called - forcing allowSceneActivation = true");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] LoadSceneAsync_Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// PREFIX for SceneManager.LoadSceneAsync - replace async with sync loading in headless mode
        /// </summary>
        public static bool LoadSceneAsync_SyncPrefix(string sceneName, UnityEngine.SceneManagement.LoadSceneMode mode, ref AsyncOperation __result)
        {
            if (!Plugin.HeadlessMode.Value) return true; // Let original run

            try
            {
                Plugin.Log.LogInfo($"[HeadlessPatches] LoadSceneAsync intercepted - using SYNC load for scene: {sceneName}");

                // Use synchronous scene loading instead
                UnityEngine.SceneManagement.SceneManager.LoadScene(sceneName, mode);

                Plugin.Log.LogInfo($"[HeadlessPatches] Sync scene load complete: {sceneName}");

                // Create a completed AsyncOperation to return
                // We can't create a real AsyncOperation, but we need to return something
                // The caller will check isDone which we can't set
                // Best we can do is let the original run but the sync load already happened
                return true; // Let original create the AsyncOperation, but scene is already loaded
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Sync scene load FAILED for {sceneName}: {ex.Message}");
                return true; // Fall back to async
            }
        }

        /// <summary>
        /// Patch AddressablesManager.IsLoaded to always return true in headless mode.
        /// Unity Addressables async loading doesn't complete properly in Wine headless mode.
        /// This allows FlowManager's LoadScreenCoroutine to proceed.
        /// </summary>
        private static void PatchAddressablesManager(Harmony harmony)
        {
            try
            {
                var addressablesManagerType = AccessTools.TypeByName("AddressablesManager");
                if (addressablesManagerType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] AddressablesManager type not found");
                    return;
                }

                // Patch IsLoaded method to always return true
                var isLoadedMethod = addressablesManagerType.GetMethod("IsLoaded",
                    BindingFlags.Public | BindingFlags.Instance);

                if (isLoadedMethod != null)
                {
                    var postfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(AddressablesManager_IsLoaded_Postfix));
                    harmony.Patch(isLoadedMethod, postfix: postfix);
                    Plugin.Log.LogInfo("[HeadlessPatches] AddressablesManager.IsLoaded patched");
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] AddressablesManager.IsLoaded method not found");
                }

                // Also patch the Loaded property on AddressableLoadHandle
                var loadHandleType = addressablesManagerType.GetNestedType("AddressableLoadHandle",
                    BindingFlags.Public | BindingFlags.NonPublic);
                if (loadHandleType != null)
                {
                    var loadedGetter = loadHandleType.GetProperty("Loaded", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                    if (loadedGetter != null)
                    {
                        var loadedPostfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(AddressableLoadHandle_Loaded_Postfix));
                        harmony.Patch(loadedGetter, postfix: loadedPostfix);
                        Plugin.Log.LogInfo("[HeadlessPatches] AddressableLoadHandle.Loaded patched");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] AddressablesManager patch failed: {ex}");
            }
        }

        /// <summary>
        /// Postfix for AddressablesManager.IsLoaded - always return true
        /// </summary>
        public static void AddressablesManager_IsLoaded_Postfix(ref bool __result)
        {
            if (!Plugin.HeadlessMode.Value) return;
            __result = true;
        }

        /// <summary>
        /// Postfix for AddressableLoadHandle.Loaded - always return true
        /// </summary>
        public static void AddressableLoadHandle_Loaded_Postfix(ref bool __result)
        {
            if (!Plugin.HeadlessMode.Value) return;
            __result = true;
        }

        /// <summary>
        /// GHOST HOST MODE: Patch InputHandler to always report key presses.
        /// This allows bypassing "press any key to continue" prompts during loading.
        /// </summary>
        private static void PatchInputHandler(Harmony harmony)
        {
            try
            {
                var inputHandlerType = AccessTools.TypeByName("InputHandler");
                if (inputHandlerType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] InputHandler type not found");
                    return;
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] Found InputHandler type, patching AnyInputPressed...");

                // Find AnyInputPressed property getter
                var anyInputPressedProp = inputHandlerType.GetProperty("AnyInputPressed",
                    BindingFlags.Public | BindingFlags.Instance);

                if (anyInputPressedProp != null && anyInputPressedProp.GetMethod != null)
                {
                    var postfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(AnyInputPressed_Postfix));
                    harmony.Patch(anyInputPressedProp.GetMethod, postfix: postfix);
                    Plugin.Log.LogInfo("[HeadlessPatches] Patched InputHandler.AnyInputPressed to return true");
                }
                else
                {
                    // Try anyInputPressed field
                    Plugin.Log.LogInfo("[HeadlessPatches] AnyInputPressed property not found, trying field...");
                }

                // Also patch any GetKeyDown or GetButtonDown methods if they exist
                foreach (var method in inputHandlerType.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
                {
                    if (method.Name == "GetKeyDown" || method.Name == "GetAnyKey" || method.Name == "WasAnyInputPressed")
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] Found {method.Name}, patching...");
                        try
                        {
                            var postfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(ReturnTrue_Postfix));
                            harmony.Patch(method, postfix: postfix);
                            Plugin.Log.LogInfo($"[HeadlessPatches] Patched {method.Name} to return true");
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[HeadlessPatches] Failed to patch {method.Name}: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] Failed to patch InputHandler: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix to make AnyInputPressed always return true in headless mode.
        /// </summary>
        public static void AnyInputPressed_Postfix(ref bool __result)
        {
            __result = true;
        }

        /// <summary>
        /// Generic postfix to make any bool method return true.
        /// </summary>
        public static void ReturnTrue_Postfix(ref bool __result)
        {
            __result = true;
        }

        private static void PatchTVEGlobalVolume(Harmony harmony)
        {
            try
            {
                // TheVegetationEngine.TVEGlobalVolume causes graphics errors in headless mode
                // which accumulate and eventually cause pthread_mutex_lock crashes
                var tveGlobalVolumeType = AccessTools.TypeByName("TheVegetationEngine.TVEGlobalVolume");
                if (tveGlobalVolumeType == null)
                {
                    // Try without namespace
                    tveGlobalVolumeType = AccessTools.TypeByName("TVEGlobalVolume");
                }

                if (tveGlobalVolumeType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] TVEGlobalVolume type not found - searching assemblies...");

                    // Search all loaded assemblies
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            var types = asm.GetTypes().Where(t => t.Name.Contains("TVE") || t.Name.Contains("Vegetation"));
                            foreach (var t in types)
                            {
                                Plugin.Log.LogInfo($"[HeadlessPatches] Found vegetation type: {t.FullName}");
                                if (t.Name == "TVEGlobalVolume")
                                {
                                    tveGlobalVolumeType = t;
                                    break;
                                }
                            }
                        }
                        catch { }
                        if (tveGlobalVolumeType != null) break;
                    }

                    if (tveGlobalVolumeType == null) return;
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] Found TVEGlobalVolume: {tveGlobalVolumeType.FullName}");

                // List ALL methods on this type for debugging
                var allMethods = tveGlobalVolumeType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
                Plugin.Log.LogInfo($"[HeadlessPatches] TVEGlobalVolume has {allMethods.Length} declared methods:");
                foreach (var m in allMethods)
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches]   - {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                }

                // Patch ALL lifecycle methods using the NoArg prefix (simpler binding)
                string[] methodsToSkip = { "Update", "LateUpdate", "OnEnable", "OnDisable", "Awake", "Start", "ExecuteRenderBuffers" };
                foreach (var methodName in methodsToSkip)
                {
                    var method = tveGlobalVolumeType.GetMethod(methodName,
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                    if (method != null)
                    {
                        try
                        {
                            // Use NoArg prefix for simpler binding
                            var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(TVEGlobalVolume_NoArg_Prefix));
                            harmony.Patch(method, prefix: prefix);
                            Plugin.Log.LogInfo($"[HeadlessPatches] TVEGlobalVolume.{methodName} patched to skip");

                            // Verify patch was applied
                            var patchInfo = Harmony.GetPatchInfo(method);
                            if (patchInfo?.Prefixes != null && patchInfo.Prefixes.Count > 0)
                            {
                                Plugin.Log.LogInfo($"[HeadlessPatches]   Verified: {patchInfo.Prefixes.Count} prefix(es) on {methodName}");
                            }
                            else
                            {
                                Plugin.Log.LogWarning($"[HeadlessPatches]   WARNING: No prefixes found on {methodName} after patching!");
                            }
                        }
                        catch (Exception patchEx)
                        {
                            Plugin.Log.LogError($"[HeadlessPatches] Failed to patch {methodName}: {patchEx.Message}");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] TVEGlobalVolume.{methodName} not found");
                    }
                }

                // Also try to patch any render-related methods
                foreach (var method in allMethods)
                {
                    if (method.Name.Contains("Render") || method.Name.Contains("Buffer") || method.Name.Contains("Execute"))
                    {
                        if (!methodsToSkip.Contains(method.Name))
                        {
                            try
                            {
                                var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(TVEGlobalVolume_NoArg_Prefix));
                                harmony.Patch(method, prefix: prefix);
                                Plugin.Log.LogInfo($"[HeadlessPatches] Also patched TVEGlobalVolume.{method.Name}");
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] TVEGlobalVolume patch failed: {ex}");
            }
        }

        // Patch methods

        /// <summary>
        /// TRANSPILER for UserCode_RequestInitialSaveData - injects our handler at the start of the method.
        /// This modifies IL directly and should work better under Wine/Mono.
        /// </summary>
        public static IEnumerable<CodeInstruction> UserCode_RequestInitialSaveData_Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase original)
        {
            Plugin.Log.LogInfo("[HeadlessPatches] UserCode_RequestInitialSaveData_Transpiler CALLED!");

            // Get our handler method
            var handlerMethod = typeof(HeadlessPatches).GetMethod(nameof(HandleSaveDataRequest),
                BindingFlags.Public | BindingFlags.Static);

            if (handlerMethod == null)
            {
                Plugin.Log.LogError("[HeadlessPatches] HandleSaveDataRequest method not found!");
                foreach (var inst in instructions) yield return inst;
                yield break;
            }

            Plugin.Log.LogInfo("[HeadlessPatches] Injecting call to HandleSaveDataRequest at start of method");

            // Create a label for the original code
            var skipLabel = generator.DefineLabel();

            // Inject: if (HandleSaveDataRequest(__instance, sender)) return;
            yield return new CodeInstruction(OpCodes.Ldarg_0); // this
            yield return new CodeInstruction(OpCodes.Ldarg_1); // sender
            yield return new CodeInstruction(OpCodes.Call, handlerMethod);
            yield return new CodeInstruction(OpCodes.Brfalse, skipLabel); // If false, continue to original
            yield return new CodeInstruction(OpCodes.Ret); // If true, return immediately

            // Mark the first original instruction with the skip label
            bool first = true;
            foreach (var inst in instructions)
            {
                if (first)
                {
                    inst.labels.Add(skipLabel);
                    first = false;
                }
                yield return inst;
            }

            Plugin.Log.LogInfo("[HeadlessPatches] Transpiler completed successfully");
        }

        /// <summary>
        /// Handler method called by the transpiler. Returns true to skip original, false to run it.
        /// </summary>
        public static bool HandleSaveDataRequest(object networkedPlayer, object sender)
        {
            try
            {
                // ALWAYS LOG - this proves the transpiler worked
                UnityEngine.Debug.Log("[HeadlessPatches] !!! HandleSaveDataRequest CALLED !!!");
                Plugin.Log.LogInfo("[HeadlessPatches] !!! HandleSaveDataRequest CALLED via Transpiler !!!");

                if (Plugin.HeadlessMode?.Value != true)
                {
                    Plugin.Log.LogInfo("[HeadlessPatches] Not headless, running original");
                    return false; // Run original
                }

                Plugin.Log.LogInfo("[HeadlessPatches] Headless mode - sending cached save via transpiler");

                var cachedData = Networking.AutoLoadManager.GetCachedSaveString();
                if (string.IsNullOrEmpty(cachedData))
                {
                    Plugin.Log.LogError("[HeadlessPatches] No cached save data!");
                    return false; // Run original as fallback
                }

                var playerType = networkedPlayer.GetType();

                // Find LoadInitialSaveDataFromServer - this is the actual [TargetRpc] method
                var targetMethod = playerType.GetMethod("LoadInitialSaveDataFromServer",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (targetMethod == null)
                {
                    Plugin.Log.LogError("[HeadlessPatches] LoadInitialSaveDataFromServer not found!");
                    // List all methods
                    foreach (var m in playerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name.Contains("Load") || m.Name.Contains("Target") || m.Name.Contains("Save"))
                        {
                            Plugin.Log.LogInfo($"[HeadlessPatches] Found: {m.Name}");
                        }
                    }
                    return false;
                }

                int maxPacketSize = 30000;
                int numChunks = (cachedData.Length + maxPacketSize - 1) / maxPacketSize;

                Plugin.Log.LogInfo($"[HeadlessPatches] Sending {numChunks} chunks ({cachedData.Length} total chars)");

                for (int i = 0; i < numChunks; i++)
                {
                    int start = i * maxPacketSize;
                    int length = Math.Min(maxPacketSize, cachedData.Length - start);
                    string chunk = cachedData.Substring(start, length);

                    Plugin.Log.LogInfo($"[HeadlessPatches] Sending chunk {i + 1}/{numChunks}");
                    targetMethod.Invoke(networkedPlayer, new object[] { sender, chunk, i, numChunks });
                }

                Plugin.Log.LogInfo("[HeadlessPatches] Successfully sent cached save data!");
                return true; // Skip original
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] HandleSaveDataRequest error: {ex}");
                return false; // Run original as fallback
            }
        }

        /// <summary>
        /// Prefix for SendSaveString coroutine's MoveNext - this is where the actual code runs.
        /// We intercept here to completely skip the coroutine and send cached data instead.
        /// </summary>
        public static bool SendSaveString_MoveNext_Prefix(object __instance, ref bool __result)
        {
            Plugin.Log.LogInfo($"[HeadlessPatches] >>> SendSaveString_MoveNext_Prefix CALLED! HeadlessMode={Plugin.HeadlessMode.Value}");

            if (!Plugin.HeadlessMode.Value)
            {
                return true; // Run original in non-headless mode
            }

            try
            {
                // Get the state machine's fields to access the NetworkedPlayer instance and connection
                var stateType = __instance.GetType();
                Plugin.Log.LogInfo($"[HeadlessPatches] State machine type: {stateType.Name}");

                // Get <>4__this field (the NetworkedPlayer instance)
                var thisField = stateType.GetField("<>4__this", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var senderField = stateType.GetField("sender", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (thisField == null || senderField == null)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] Could not find state machine fields. Fields: {string.Join(", ", stateType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Select(f => f.Name))}");
                    return true;
                }

                var networkedPlayer = thisField.GetValue(__instance);
                var sender = senderField.GetValue(__instance);

                Plugin.Log.LogInfo($"[HeadlessPatches] Got NetworkedPlayer and sender connection");

                // Get cached save data
                var cachedData = Networking.AutoLoadManager.GetCachedSaveString();
                if (string.IsNullOrEmpty(cachedData))
                {
                    Plugin.Log.LogError("[HeadlessPatches] No cached save data available in MoveNext!");
                    return true;
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] Sending cached save data ({cachedData.Length} chars) via LoadInitialSaveDataFromServer");

                // Find LoadInitialSaveDataFromServer method - the actual [TargetRpc]
                var networkedPlayerType = networkedPlayer.GetType();
                var targetMethod = networkedPlayerType.GetMethod("LoadInitialSaveDataFromServer",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (targetMethod != null)
                {
                    int maxPacketSize = 32000;
                    int numChunks = (cachedData.Length + maxPacketSize - 1) / maxPacketSize;

                    Plugin.Log.LogInfo($"[HeadlessPatches] Sending {numChunks} chunks");

                    for (int i = 0; i < numChunks; i++)
                    {
                        int start = i * maxPacketSize;
                        int length = Math.Min(maxPacketSize, cachedData.Length - start);
                        string chunk = cachedData.Substring(start, length);

                        Plugin.Log.LogInfo($"[HeadlessPatches] Sending chunk {i + 1}/{numChunks}");
                        targetMethod.Invoke(networkedPlayer, new object[] { sender, chunk, i, numChunks });
                    }

                    Plugin.Log.LogInfo("[HeadlessPatches] Successfully sent cached save data!");

                    // Mark coroutine as complete
                    __result = false; // false = MoveNext finished (no more iterations)
                    return false; // Skip original MoveNext
                }
                else
                {
                    Plugin.Log.LogError("[HeadlessPatches] LoadInitialSaveDataFromServer not found!");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Error in SendSaveString_MoveNext_Prefix: {ex}");
                return true;
            }
        }

        /// <summary>
        /// Prefix for NetworkedPlayer.UserCode_RequestInitialSaveData (INSTANCE method)
        /// This runs when a client requests save data.
        /// Signature: void UserCode_RequestInitialSaveData(NetworkConnectionToClient sender)
        /// </summary>
        public static bool UserCode_RequestInitialSaveData_Prefix(object __instance, object sender)
        {
            // Wrap EVERYTHING in try-catch to ensure we see any errors
            try
            {
                // IMMEDIATELY log to verify this is called - use multiple methods
                System.Console.WriteLine("!!! HARMONY PREFIX CALLED: UserCode_RequestInitialSaveData !!!");
                UnityEngine.Debug.Log("!!! UNITY DEBUG: UserCode_RequestInitialSaveData_Prefix CALLED !!!");
                Plugin.Log.LogInfo("!!! [HeadlessPatches] UserCode_RequestInitialSaveData_Prefix CALLED !!!");
            }
            catch (System.Exception logEx)
            {
                // Try basic console output if logging fails
                System.Console.WriteLine($"Prefix logging failed: {logEx.Message}");
            }

            try
            {
                if (Plugin.HeadlessMode?.Value != true)
                {
                    Plugin.Log.LogInfo("[HeadlessPatches] Not headless, running original");
                    return true;
                }

                Plugin.Log.LogInfo("[HeadlessPatches] Headless mode - sending cached save data");

                var cachedData = Networking.AutoLoadManager.GetCachedSaveString();
                if (string.IsNullOrEmpty(cachedData))
                {
                    Plugin.Log.LogError("[HeadlessPatches] No cached save data!");
                    return true;
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] Cached data: {cachedData.Length} chars");

                // Get the NetworkedPlayer type and find LoadInitialSaveDataFromServer (the TargetRpc method)
                var playerType = __instance.GetType();

                // Find the RPC method - it might be named LoadInitialSaveDataFromServer
                var targetMethod = playerType.GetMethod("LoadInitialSaveDataFromServer",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (targetMethod == null)
                {
                    Plugin.Log.LogError("[HeadlessPatches] LoadInitialSaveDataFromServer not found! Listing methods...");
                    foreach (var m in playerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        if (m.Name.Contains("Save") || m.Name.Contains("Load") || m.Name.Contains("Initial"))
                        {
                            Plugin.Log.LogInfo($"[HeadlessPatches] Found method: {m.Name}");
                        }
                    }
                    return true;
                }

                // Send in chunks - use smaller chunks to be safe
                int maxPacketSize = 30000;
                int numChunks = (cachedData.Length + maxPacketSize - 1) / maxPacketSize;
                Plugin.Log.LogInfo($"[HeadlessPatches] Sending {numChunks} chunks via {targetMethod.Name}");

                for (int i = 0; i < numChunks; i++)
                {
                    int start = i * maxPacketSize;
                    int length = Math.Min(maxPacketSize, cachedData.Length - start);
                    string chunk = cachedData.Substring(start, length);

                    Plugin.Log.LogInfo($"[HeadlessPatches] Sending chunk {i+1}/{numChunks} ({chunk.Length} chars)");

                    // Parameters: NetworkConnection connection, string partialData, int index, int numTotal
                    targetMethod.Invoke(__instance, new object[] { sender, chunk, i, numChunks });
                }

                Plugin.Log.LogInfo("[HeadlessPatches] Successfully sent cached save data!");
                return false; // Skip original
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] UserCode prefix error: {ex}");
                Console.WriteLine($"[HeadlessPatches] UserCode prefix EXCEPTION: {ex}");
                return true;
            }
        }

        /// <summary>
        /// Prefix for NetworkedPlayer.InvokeUserCode_RequestInitialSaveData (STATIC method)
        /// This is the entry point that Mirror's network stack calls.
        /// Signature: static void InvokeUserCode_RequestInitialSaveData(NetworkBehaviour obj, NetworkReader reader, NetworkConnectionToClient senderConnection)
        /// </summary>
        public static bool InvokeUserCode_RequestInitialSaveData_Prefix(object obj, object reader, object senderConnection)
        {
            try
            {
                UnityEngine.Debug.Log("[HeadlessPatches] >>> InvokeRequestInitialSaveData_Prefix CALLED!");
                UnityEngine.Debug.Log($"[HeadlessPatches] obj={obj?.GetType().Name}, senderConnection={senderConnection?.GetType().Name}");

                if (Plugin.HeadlessMode?.Value != true)
                {
                    UnityEngine.Debug.Log("[HeadlessPatches] Not in headless mode, running original");
                    return true; // Run original in non-headless mode
                }
                // obj is the NetworkedPlayer instance
                var networkedPlayerType = obj.GetType();
                Plugin.Log.LogInfo($"[HeadlessPatches] NetworkedPlayer type: {networkedPlayerType.FullName}");

                // Step 1: Call LoadOrCreateSaveFile() - this sets up player save data
                var loadOrCreateMethod = networkedPlayerType.GetMethod("LoadOrCreateSaveFile",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (loadOrCreateMethod != null)
                {
                    var playerSaveInfo = loadOrCreateMethod.Invoke(obj, null);
                    Plugin.Log.LogInfo($"[HeadlessPatches] LoadOrCreateSaveFile returned: {playerSaveInfo?.GetType().Name}");

                    // Get strata from playerSaveInfo
                    var strataField = playerSaveInfo?.GetType().GetField("strata");
                    if (strataField != null)
                    {
                        byte strata = (byte)strataField.GetValue(playerSaveInfo);
                        Plugin.Log.LogInfo($"[HeadlessPatches] Player strata: {strata}");

                        // Step 2: Call HandleSetStrata(strata)
                        var handleSetStrataMethod = networkedPlayerType.GetMethod("HandleSetStrata",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (handleSetStrataMethod != null)
                        {
                            handleSetStrataMethod.Invoke(obj, new object[] { strata });
                            Plugin.Log.LogInfo("[HeadlessPatches] HandleSetStrata called");
                        }
                    }
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] LoadOrCreateSaveFile method not found");
                }

                // Step 3: Get cached save data and send it via LoadInitialSaveDataFromServer
                var cachedData = Networking.AutoLoadManager.GetCachedSaveString();
                if (string.IsNullOrEmpty(cachedData))
                {
                    Plugin.Log.LogError("[HeadlessPatches] No cached save data available!");
                    return true; // Try original as fallback
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] Sending cached save data ({cachedData.Length} chars) to client");

                // Find LoadInitialSaveDataFromServer - this is the [TargetRpc] that sends to client
                var targetMethod = networkedPlayerType.GetMethod("LoadInitialSaveDataFromServer",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (targetMethod != null)
                {
                    // Get max packet size (use a safe default)
                    int maxPacketSize = 32000;
                    int numChunks = (cachedData.Length + maxPacketSize - 1) / maxPacketSize;

                    Plugin.Log.LogInfo($"[HeadlessPatches] Sending save data in {numChunks} chunks via LoadInitialSaveDataFromServer");

                    for (int i = 0; i < numChunks; i++)
                    {
                        int start = i * maxPacketSize;
                        int length = Math.Min(maxPacketSize, cachedData.Length - start);
                        string chunk = cachedData.Substring(start, length);

                        Plugin.Log.LogInfo($"[HeadlessPatches] Sending chunk {i + 1}/{numChunks} ({chunk.Length} chars)");
                        // LoadInitialSaveDataFromServer(NetworkConnection connection, string partialData, int index, int numTotal)
                        targetMethod.Invoke(obj, new object[] { senderConnection, chunk, i, numChunks });
                    }

                    Plugin.Log.LogInfo("[HeadlessPatches] Successfully sent cached save data to client!");
                    return false; // Skip original method
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] LoadInitialSaveDataFromServer method not found, listing available methods...");
                    var methods = networkedPlayerType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    foreach (var m in methods)
                    {
                        if (m.Name.Contains("Load") || m.Name.Contains("Save") || m.Name.Contains("Target"))
                        {
                            Plugin.Log.LogInfo($"[HeadlessPatches]   Available: {m.Name}");
                        }
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Error in InvokeRequestInitialSaveData_Prefix: {ex}");
                return true; // Try original as fallback
            }
        }

        /// <summary>
        /// FINALIZER for InvokeUserCode - this ALWAYS runs, even if prefix doesn't
        /// </summary>
        public static void InvokeUserCode_Finalizer(Exception __exception)
        {
            UnityEngine.Debug.Log($"[HeadlessPatches] !!! FINALIZER CALLED !!! Exception: {__exception?.Message ?? "none"}");
            Plugin.Log.LogInfo($"[HeadlessPatches] !!! InvokeUserCode_Finalizer CALLED !!! Exception: {__exception?.Message ?? "none"}");
        }

        /// <summary>
        /// Patch Mirror's RemoteCallHelper.InvokeHandlerDelegate - this is the actual entry point
        /// NOTE: The parameter is named cmdHash, NOT functionHash
        /// </summary>
        public static void Mirror_InvokeHandlerDelegate_Prefix(int cmdHash, object invokeType, object reader, object invokingType, object senderConnection)
        {
            UnityEngine.Debug.Log($"[HeadlessPatches] !!! MIRROR InvokeHandlerDelegate called! hash={cmdHash}");
            Plugin.Log.LogInfo($"[HeadlessPatches] !!! Mirror.InvokeHandlerDelegate CALLED! hash={cmdHash}, behaviour={invokingType?.GetType().Name}");
        }

        /// <summary>
        /// Prefix for NetworkedPlayer.UserCode_RequestInitialSaveData (fallback)
        /// This intercepts the save data request and sends cached data directly
        /// </summary>
        public static bool RequestInitialSaveData_Prefix(object __instance, object sender)
        {
            Plugin.Log.LogInfo($"[HeadlessPatches] >>> RequestInitialSaveData_Prefix CALLED! HeadlessMode={Plugin.HeadlessMode.Value}");

            if (!Plugin.HeadlessMode.Value)
            {
                return true; // Run original in non-headless mode
            }

            try
            {
                var networkedPlayerType = __instance.GetType();

                // Step 1: Call LoadOrCreateSaveFile() - this sets up player save data
                var loadOrCreateMethod = networkedPlayerType.GetMethod("LoadOrCreateSaveFile",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (loadOrCreateMethod != null)
                {
                    var playerSaveInfo = loadOrCreateMethod.Invoke(__instance, null);
                    Plugin.Log.LogInfo($"[HeadlessPatches] LoadOrCreateSaveFile returned: {playerSaveInfo?.GetType().Name}");

                    // Get strata from playerSaveInfo
                    var strataField = playerSaveInfo?.GetType().GetField("strata");
                    if (strataField != null)
                    {
                        byte strata = (byte)strataField.GetValue(playerSaveInfo);
                        Plugin.Log.LogInfo($"[HeadlessPatches] Player strata: {strata}");

                        // Step 2: Call HandleSetStrata(strata)
                        var handleSetStrataMethod = networkedPlayerType.GetMethod("HandleSetStrata",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (handleSetStrataMethod != null)
                        {
                            handleSetStrataMethod.Invoke(__instance, new object[] { strata });
                            Plugin.Log.LogInfo("[HeadlessPatches] HandleSetStrata called");
                        }
                    }
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] LoadOrCreateSaveFile method not found");
                }

                // Step 3: Get cached save data and send it via LoadInitialSaveDataFromServer
                var cachedData = Networking.AutoLoadManager.GetCachedSaveString();
                if (string.IsNullOrEmpty(cachedData))
                {
                    Plugin.Log.LogError("[HeadlessPatches] No cached save data available!");
                    return true; // Try original as fallback
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] Sending cached save data ({cachedData.Length} chars)");

                // Find LoadInitialSaveDataFromServer method
                var loadInitialMethod = networkedPlayerType.GetMethod("LoadInitialSaveDataFromServer",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (loadInitialMethod != null)
                {
                    // Get max packet size (use a safe default)
                    int maxPacketSize = 32000;
                    int numChunks = (cachedData.Length + maxPacketSize - 1) / maxPacketSize;

                    Plugin.Log.LogInfo($"[HeadlessPatches] Sending save data in {numChunks} chunks");

                    for (int i = 0; i < numChunks; i++)
                    {
                        int start = i * maxPacketSize;
                        int length = Math.Min(maxPacketSize, cachedData.Length - start);
                        string chunk = cachedData.Substring(start, length);

                        Plugin.Log.LogInfo($"[HeadlessPatches] Sending chunk {i + 1}/{numChunks} ({chunk.Length} chars)");
                        loadInitialMethod.Invoke(__instance, new object[] { sender, chunk, i, numChunks });
                    }

                    Plugin.Log.LogInfo("[HeadlessPatches] Successfully sent cached save data to client!");
                    return false; // Skip original method
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] LoadInitialSaveDataFromServer method not found");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Error in RequestInitialSaveData_Prefix: {ex}");
                return true; // Try original as fallback
            }
        }

        /// <summary>
        /// Prefix for PrepSave - in headless mode, SKIP this method entirely
        /// to prevent NullReferenceException.
        /// </summary>
        public static bool PrepSave_Prefix()
        {
            try
            {
                // Always log to confirm prefix is called
                UnityEngine.Debug.Log("[HeadlessPatches] PrepSave_Prefix CALLED!");

                if (Plugin.HeadlessMode?.Value == true)
                {
                    UnityEngine.Debug.Log("[HeadlessPatches] Skipping PrepSave in headless mode");
                    return false; // Skip the method entirely
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[HeadlessPatches] PrepSave_Prefix exception: {ex}");
                return false; // Skip on error in headless mode
            }
            return true; // Run original
        }

        /// <summary>
        /// Prefix for SaveAsString - in headless mode, COMPLETELY BYPASS the original method
        /// and return cached save data directly. This prevents the NullReferenceException
        /// in PrepSave from ever occurring.
        /// </summary>
        /// <param name="__result">The result to return (output parameter)</param>
        /// <returns>False to skip the original method, True to run it</returns>
        public static bool SaveAsString_Prefix(ref string __result)
        {
            try
            {
                // ALWAYS LOG to confirm this prefix is being called
                UnityEngine.Debug.Log("[HeadlessPatches] SaveAsString_Prefix CALLED!");

                // Only bypass in headless mode
                if (Plugin.HeadlessMode?.Value != true)
                {
                    UnityEngine.Debug.Log("[HeadlessPatches] Not in headless mode, running original");
                    return true; // Run original method
                }

                UnityEngine.Debug.Log("[HeadlessPatches] SaveAsString in headless mode - using cached data");

                // Get cached save data
                var cachedData = Networking.AutoLoadManager.GetCachedSaveString();
                if (!string.IsNullOrEmpty(cachedData))
                {
                    UnityEngine.Debug.Log($"[HeadlessPatches] Returning cached save ({cachedData.Length} chars)");
                    __result = cachedData;
                    return false; // Skip original method entirely
                }
                else
                {
                    UnityEngine.Debug.LogError("[HeadlessPatches] No cached save data!");
                    return true; // Try original method as fallback
                }
            }
            catch (System.Exception ex)
            {
                UnityEngine.Debug.LogError($"[HeadlessPatches] SaveAsString_Prefix exception: {ex}");
                return true; // Try original on error
            }
        }

        public static bool CursorLock_Prefix()
        {
            // Skip cursor lock in headless mode
            return false;
        }

        public static bool DisableComponent_Prefix(MonoBehaviour __instance)
        {
            // Disable the component instead of running Awake
            if (__instance != null)
            {
                __instance.enabled = false;
            }
            return false;
        }

        public static bool SkipMethod_Prefix()
        {
            // Skip the method entirely
            return false;
        }

        /// <summary>
        /// Prefix for NetworkedPlayer.Update - skip entirely in dedicated server mode
        /// </summary>
        public static bool NetworkedPlayer_Update_Prefix()
        {
            // Skip this method entirely in dedicated server mode
            // NetworkedPlayer.Update has null references without a local player
            return false;
        }

        /// <summary>
        /// Prefix for ThirdPersonDisplayAnimator.Update - skip entirely in dedicated server mode
        /// </summary>
        public static bool ThirdPersonDisplayAnimator_Update_Prefix()
        {
            // Skip this method entirely in dedicated server mode
            // ThirdPersonDisplayAnimator.Update has null references without proper player setup
            return false;
        }

        /// <summary>
        /// Prefix for ThirdPersonDisplayAnimator.UpdateSillyStuff - skip entirely
        /// </summary>
        public static bool ThirdPersonDisplayAnimator_UpdateSillyStuff_Prefix()
        {
            // Skip this method entirely
            return false;
        }

        private static int _tveSkipCount = 0;

        /// <summary>
        /// Prefix for TVEGlobalVolume.Update/LateUpdate/OnEnable - skip entirely in headless mode
        /// TheVegetationEngine causes graphics errors that accumulate and crash the server
        /// </summary>
        public static bool TVEGlobalVolume_Update_Prefix(MonoBehaviour __instance)
        {
            // Log first few calls to verify this is working
            _tveSkipCount++;
            if (_tveSkipCount <= 5)
            {
                Console.WriteLine($"[HeadlessPatches] TVEGlobalVolume prefix called! Count={_tveSkipCount}");
                Plugin.Log.LogInfo($"[HeadlessPatches] TVEGlobalVolume prefix called! Count={_tveSkipCount}");
            }

            // Try to disable the component to prevent future calls
            if (__instance != null && __instance.enabled)
            {
                try
                {
                    __instance.enabled = false;
                    Console.WriteLine("[HeadlessPatches] Disabled TVEGlobalVolume component!");
                    Plugin.Log.LogInfo("[HeadlessPatches] Disabled TVEGlobalVolume component!");
                }
                catch { }
            }

            // Skip this method entirely in headless mode - prevents graphics errors
            return false;
        }

        /// <summary>
        /// Alternative prefix that takes no parameters (for static-like binding)
        /// </summary>
        public static bool TVEGlobalVolume_NoArg_Prefix()
        {
            _tveSkipCount++;
            if (_tveSkipCount <= 5)
            {
                Console.WriteLine($"[HeadlessPatches] TVE NoArg prefix called! Count={_tveSkipCount}");
            }
            return false;
        }

        /// <summary>
        /// Patches TechNetworkManager.OnServerAddPlayer to handle headless mode.
        /// The original throws NullReferenceException because game systems aren't initialized.
        /// </summary>
        private static void PatchTechNetworkManagerOnServerAddPlayer(Harmony harmony)
        {
            Plugin.Log.LogInfo("[HeadlessPatches] PatchTechNetworkManagerOnServerAddPlayer starting...");
            try
            {
                var techNetworkManagerType = AccessTools.TypeByName("TechNetworkManager");
                if (techNetworkManagerType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] TechNetworkManager type not found for OnServerAddPlayer patch");
                    return;
                }

                var onServerAddPlayerMethod = AccessTools.Method(techNetworkManagerType, "OnServerAddPlayer");
                if (onServerAddPlayerMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(TechNetworkManager_OnServerAddPlayer_Prefix));
                    harmony.Patch(onServerAddPlayerMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] TechNetworkManager.OnServerAddPlayer patched");
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] TechNetworkManager.OnServerAddPlayer method not found");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] TechNetworkManager.OnServerAddPlayer patch failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix for TechNetworkManager.OnServerAddPlayer - handles player spawning in headless mode.
        /// Original method signature: public override void OnServerAddPlayer(NetworkConnection conn)
        /// </summary>
        public static bool TechNetworkManager_OnServerAddPlayer_Prefix(object __instance, NetworkConnection conn)
        {
            // Log immediately at the very start
            Plugin.Log.LogInfo($"[HeadlessPatches] === TechNetworkManager.OnServerAddPlayer PREFIX CALLED === conn={conn?.connectionId}");

            try
            {
                // Cast instance to NetworkManager
                var networkManager = __instance as NetworkManager;
                if (networkManager == null)
                {
                    Plugin.Log.LogError("[HeadlessPatches] __instance is not a NetworkManager!");
                    return false;
                }

                // Get the player prefab from the NetworkManager
                var playerPrefab = networkManager.playerPrefab;
                if (playerPrefab == null)
                {
                    Plugin.Log.LogError("[HeadlessPatches] Player prefab is null!");
                    return false;
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] Found player prefab: {playerPrefab.name}");

                // Instantiate the player
                var player = GameObject.Instantiate(playerPrefab);
                player.name = $"NetworkedPlayer_Server_{conn.connectionId}";
                Plugin.Log.LogInfo($"[HeadlessPatches] Instantiated player: {player.name}");

                // Add player to connection
                NetworkServer.AddPlayerForConnection(conn, player);
                Plugin.Log.LogInfo($"[HeadlessPatches] Spawned player for connection {conn.connectionId}: {player.name}");

                // CRITICAL: Proactively send tick to new client
                // Client's RequestCurrentSimTick command fails due to Mirror internal state issues
                // So we push the tick immediately when they connect
                ProactivelySendTickToClient(conn as NetworkConnectionToClient);

                return false; // Skip original - we handled it
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] OnServerAddPlayer error: {ex.Message}");
                Plugin.Log.LogError($"[HeadlessPatches] Stack: {ex.StackTrace}");
                return false; // Skip original to prevent crash
            }
        }

        /// <summary>
        /// Proactively sends the current sim tick to a newly connected client.
        /// This bypasses the broken client->server RequestCurrentSimTick command flow.
        /// </summary>
        private static void ProactivelySendTickToClient(NetworkConnectionToClient conn)
        {
            if (conn == null)
            {
                Plugin.Log.LogWarning("[HeadlessPatches] ProactivelySendTickToClient: conn is null");
                return;
            }

            try
            {
                // Get the current tick from MachineManager if available
                int tickToSend = (int)_currentSimTick;

                var machineManagerType = AccessTools.TypeByName("MachineManager");
                if (machineManagerType != null)
                {
                    var instanceField = machineManagerType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    if (instanceField != null)
                    {
                        var mmInstance = instanceField.GetValue(null);
                        if (mmInstance != null)
                        {
                            var curTickField = machineManagerType.GetField("curTick", BindingFlags.Public | BindingFlags.Instance);
                            if (curTickField != null)
                            {
                                tickToSend = (int)curTickField.GetValue(mmInstance);
                            }
                        }
                    }
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] ProactivelySendTickToClient: Sending tick {tickToSend} to connection {conn.connectionId}");

                // Get NetworkMessageRelay.instance
                var relayType = AccessTools.TypeByName("NetworkMessageRelay");
                if (relayType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] ProactivelySendTickToClient: NetworkMessageRelay type not found");
                    return;
                }

                var instanceProp = relayType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                object relayInstance = null;
                if (instanceProp != null)
                {
                    relayInstance = instanceProp.GetValue(null);
                }
                else
                {
                    // Try as field
                    var instanceField = relayType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    if (instanceField != null)
                    {
                        relayInstance = instanceField.GetValue(null);
                    }
                }

                if (relayInstance == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] ProactivelySendTickToClient: NetworkMessageRelay.instance is null");
                    return;
                }

                // Get ProcessCurrentSimTick method
                var processMethod = relayType.GetMethod("ProcessCurrentSimTick",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (processMethod == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] ProactivelySendTickToClient: ProcessCurrentSimTick method not found");
                    return;
                }

                // Call ProcessCurrentSimTick(conn, tick) to send tick to client
                processMethod.Invoke(relayInstance, new object[] { conn, tickToSend });
                Plugin.Log.LogInfo($"[HeadlessPatches] ProactivelySendTickToClient: Successfully sent tick {tickToSend} to client {conn.connectionId}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] ProactivelySendTickToClient error: {ex.Message}");
            }
        }

        /// <summary>
        /// Postfix for TechNetworkManager.Awake - Destroy FizzyFacepunch after it's created.
        /// The original Awake creates FizzyFacepunch which may bind to ports.
        /// We destroy it immediately and let DirectConnectManager set up KCP transport instead.
        /// </summary>
        public static void TechNetworkManager_Awake_Postfix(NetworkManager __instance)
        {
            Plugin.Log.LogInfo("[HeadlessPatches] TechNetworkManager.Awake postfix - destroying FizzyFacepunch");

            try
            {
                // Get the current transport via reflection (transport is protected)
                var transportField = typeof(NetworkManager).GetField("transport",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                if (transportField == null)
                {
                    // Try as a property
                    var transportProp = typeof(NetworkManager).GetProperty("transport",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (transportProp != null)
                    {
                        var transport = transportProp.GetValue(__instance) as Transport;
                        ProcessTransport(__instance, transport, transportProp);
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[HeadlessPatches] Could not find transport field or property!");
                    }
                }
                else
                {
                    var transport = transportField.GetValue(__instance) as Transport;
                    ProcessTransport(__instance, transport, transportField);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] TechNetworkManager.Awake postfix error: {ex}");
            }
        }

        private static void ProcessTransport(NetworkManager manager, Transport transport, MemberInfo memberInfo)
        {
            if (transport != null)
            {
                Plugin.Log.LogInfo($"[HeadlessPatches] Found transport: {transport.GetType().Name}");

                // Check if it's FizzyFacepunch
                if (transport.GetType().Name.Contains("Fizzy") || transport.GetType().Name.Contains("Facepunch"))
                {
                    Plugin.Log.LogInfo("[HeadlessPatches] Destroying FizzyFacepunch transport...");

                    // Try to shut down the transport first
                    try
                    {
                        transport.Shutdown();
                        Plugin.Log.LogInfo("[HeadlessPatches] FizzyFacepunch.Shutdown() called");
                    }
                    catch (Exception shutdownEx)
                    {
                        Plugin.Log.LogWarning($"[HeadlessPatches] FizzyFacepunch.Shutdown() failed: {shutdownEx.Message}");
                    }

                    // Disable the component
                    var behaviour = transport as MonoBehaviour;
                    if (behaviour != null)
                    {
                        behaviour.enabled = false;
                    }

                    // Destroy the game object
                    UnityEngine.Object.Destroy(transport.gameObject);
                    Plugin.Log.LogInfo("[HeadlessPatches] FizzyFacepunch destroyed!");

                    // Clear the transport reference
                    if (memberInfo is FieldInfo field)
                    {
                        field.SetValue(manager, null);
                    }
                    else if (memberInfo is PropertyInfo prop && prop.CanWrite)
                    {
                        prop.SetValue(manager, null);
                    }
                }
                else
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches] Transport is not FizzyFacepunch: {transport.GetType().FullName}");
                }
            }
            else
            {
                Plugin.Log.LogInfo("[HeadlessPatches] No transport found on NetworkManager");
            }
        }

        /// <summary>
        /// Patches NetworkMessageRelay to handle network actions in headless mode.
        /// The relay needs to process SimTick requests and network actions for item pickup, crafting, etc.
        /// </summary>
        private static void PatchNetworkMessageRelay(Harmony harmony)
        {
            try
            {
                Plugin.Log.LogInfo("[HeadlessPatches] Looking for NetworkMessageRelay type...");

                var relayType = AccessTools.TypeByName("NetworkMessageRelay");
                if (relayType == null)
                {
                    // Try searching all assemblies
                    Plugin.Log.LogInfo("[HeadlessPatches] NetworkMessageRelay not found by name, searching assemblies...");
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            var types = asm.GetTypes().Where(t => t.Name == "NetworkMessageRelay");
                            foreach (var t in types)
                            {
                                relayType = t;
                                Plugin.Log.LogInfo($"[HeadlessPatches] Found NetworkMessageRelay in {asm.GetName().Name}");
                                break;
                            }
                        }
                        catch { }
                        if (relayType != null) break;
                    }
                }

                if (relayType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] NetworkMessageRelay type not found in any assembly");
                    return;
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] Found NetworkMessageRelay type: {relayType.FullName}");

                // List all methods to understand the relay
                var allMethods = relayType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                foreach (var m in allMethods)
                {
                    if (m.Name.Contains("SimTick") || m.Name.Contains("Action") || m.Name.Contains("Cmd") || m.Name.Contains("Rpc"))
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] NetworkMessageRelay method: {m.Name}");
                    }
                }

                // Patch SendNetworkAction to handle actions in headless mode
                var sendActionMethod = AccessTools.Method(relayType, "SendNetworkAction");
                if (sendActionMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkMessageRelay_SendNetworkAction_Prefix));
                    harmony.Patch(sendActionMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] NetworkMessageRelay.SendNetworkAction patched");
                }

                // Patch ACTUAL command handlers - there's no "CmdSendNetworkAction", each command has its own handler
                // Patch key commands: MOLECommand (mining), HitVoxelCommand (voxel mining), TakeAllCommand (pickup)
                var commandsToPatch = new[] {
                    "UserCode_MOLECommand",
                    "UserCode_HitVoxelCommand",
                    "UserCode_HitDestructableCommand",
                    "UserCode_TakeAllCommand",
                    "UserCode_CraftCommand",
                    "UserCode_InteractCommand",
                    "UserCode_ActivateMachineCommand",
                    "UserCode_ModifyMouseBufferCommand",
                    "UserCode_ExchangeMachineInvCommand"
                };

                var commandPrefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(GenericCommand_Prefix));
                foreach (var cmdName in commandsToPatch)
                {
                    var cmdMethod = AccessTools.Method(relayType, cmdName);
                    if (cmdMethod != null)
                    {
                        harmony.Patch(cmdMethod, prefix: commandPrefix);
                        Plugin.Log.LogInfo($"[HeadlessPatches] Patched {cmdName}");
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[HeadlessPatches] {cmdName} not found!");
                    }
                }

                // Patch UserCode_RequestCurrentSimTick - the SERVER-SIDE handler
                // RequestCurrentSimTick is the client-side stub that sends the command
                // UserCode_RequestCurrentSimTick is what processes it on the server
                var userCodeSimTickMethod = AccessTools.Method(relayType, "UserCode_RequestCurrentSimTick");
                if (userCodeSimTickMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkMessageRelay_RequestCurrentSimTick_Prefix));
                    harmony.Patch(userCodeSimTickMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] NetworkMessageRelay.UserCode_RequestCurrentSimTick patched");
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] UserCode_RequestCurrentSimTick not found!");
                }

                Plugin.Log.LogInfo("[HeadlessPatches] NetworkMessageRelay patches applied");

                // Start a coroutine to periodically check and fix the server's relay netId
                var plugin = Plugin.Instance;
                if (plugin != null)
                {
                    plugin.StartCoroutine(MonitorRelayNetId());
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] NetworkMessageRelay patch failed: {ex}");
            }
        }

        /// <summary>
        /// Patch FactorySimManager.SimUpdateAll to handle null Player.instance.
        /// On a headless server, Player.instance is null, which crashes SimUpdateAll.
        /// This patch processes the HostQueue manually when Player.instance is null.
        /// </summary>
        private static void PatchFactorySimManager(Harmony harmony)
        {
            try
            {
                var factorySimType = AccessTools.TypeByName("FactorySimManager");
                if (factorySimType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] FactorySimManager type not found");
                    return;
                }

                // Patch Update() to check if it's called
                var updateMethod = AccessTools.Method(factorySimType, "Update");
                if (updateMethod != null)
                {
                    var updatePrefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(FactorySimManager_Update_Prefix));
                    harmony.Patch(updateMethod, prefix: updatePrefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] FactorySimManager.Update patched");
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] FactorySimManager.Update not found");
                }

                var simUpdateAllMethod = AccessTools.Method(factorySimType, "SimUpdateAll");
                if (simUpdateAllMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(FactorySimManager_SimUpdateAll_Prefix));
                    harmony.Patch(simUpdateAllMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] FactorySimManager.SimUpdateAll patched");
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] FactorySimManager.SimUpdateAll not found");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] FactorySimManager patch failed: {ex}");
            }
        }

        /// <summary>
        /// Prefix for FactorySimManager.Update - checks if Update is being called
        /// Also forces initialization if _needInit is still true
        /// </summary>
        private static int _factoryUpdateCallCount = 0;
        private static bool _forceInitAttempted = false;

        public static void FactorySimManager_Update_Prefix(object __instance)
        {
            _factoryUpdateCallCount++;
            if (_factoryUpdateCallCount % 300 == 1)
            {
                Plugin.Log.LogInfo($"[HeadlessPatches] FactorySimManager.Update called #{_factoryUpdateCallCount}");
            }

            // Check if simulation is stuck in initialization state
            if (!_forceInitAttempted && _factoryUpdateCallCount > 1000)
            {
                try
                {
                    // Check _needInit field
                    var needInitField = __instance.GetType().GetField("_needInit", BindingFlags.NonPublic | BindingFlags.Instance);
                    if (needInitField != null)
                    {
                        bool needInit = (bool)needInitField.GetValue(__instance);
                        if (needInit)
                        {
                            Plugin.Log.LogWarning("[HeadlessPatches] FactorySimManager still needs init after 1000 updates! Forcing initialization...");

                            // Try to call PostLoadInit
                            var postLoadInitMethod = __instance.GetType().GetMethod("PostLoadInit", BindingFlags.Public | BindingFlags.Instance);
                            if (postLoadInitMethod != null)
                            {
                                try
                                {
                                    postLoadInitMethod.Invoke(__instance, null);
                                    Plugin.Log.LogInfo("[HeadlessPatches] Called PostLoadInit() successfully!");
                                }
                                catch (Exception ex)
                                {
                                    Plugin.Log.LogWarning($"[HeadlessPatches] PostLoadInit failed: {ex.InnerException?.Message ?? ex.Message}");
                                    // Force _needInit = false directly
                                    needInitField.SetValue(__instance, false);
                                    Plugin.Log.LogInfo("[HeadlessPatches] Forced _needInit = false directly");
                                }
                            }
                            else
                            {
                                // Just force _needInit = false
                                needInitField.SetValue(__instance, false);
                                Plugin.Log.LogInfo("[HeadlessPatches] Forced _needInit = false (no PostLoadInit method found)");
                            }

                            _forceInitAttempted = true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] Force init check error: {ex.Message}");
                    _forceInitAttempted = true;
                }
            }
        }

        /// <summary>
        /// Prefix for FactorySimManager.SimUpdateAll - handles null Player.instance
        /// When Player.instance is null (headless server), we run the full simulation manually
        /// </summary>
        private static int _simUpdateAllCallCount = 0;
        private static float _timeToProcess = 0f;
        private static int _targetTick = 0;
        private static bool _tickInitialized = false;
        private const float TICK_INTERVAL = 1f / 64f; // 64 ticks per second

        public static bool FactorySimManager_SimUpdateAll_Prefix(object __instance, float dt)
        {
            _simUpdateAllCallCount++;

            try
            {
                // Check if Player.instance.cheats.simSpeed is accessible (needed by original method)
                var playerType = AccessTools.TypeByName("Player");
                if (playerType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] SimUpdateAll: Player type not found!");
                    RunSimulationTick(__instance, dt);
                    return false;
                }

                var playerInstanceField = playerType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                var playerInstance = playerInstanceField?.GetValue(null);

                // Check if we can safely use Player.instance.cheats.simSpeed
                float simSpeed = 1.0f;
                bool canUseOriginal = false;

                if (playerInstance != null)
                {
                    try
                    {
                        // Try to get cheats.simSpeed
                        var cheatsField = playerType.GetField("cheats", BindingFlags.Public | BindingFlags.Instance);
                        var cheats = cheatsField?.GetValue(playerInstance);
                        if (cheats != null)
                        {
                            var simSpeedField = cheats.GetType().GetField("simSpeed", BindingFlags.Public | BindingFlags.Instance);
                            if (simSpeedField != null)
                            {
                                var simSpeedValue = simSpeedField.GetValue(cheats);
                                if (simSpeedValue is float speed && speed > 0)
                                {
                                    simSpeed = speed;
                                    canUseOriginal = true;
                                    if (_simUpdateAllCallCount % 1000 == 1)
                                    {
                                        Plugin.Log.LogInfo($"[HeadlessPatches] SimUpdateAll: Using original with simSpeed={simSpeed}");
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // cheats.simSpeed not accessible
                    }
                }

                if (canUseOriginal)
                {
                    // Let original run - Player.instance.cheats.simSpeed is accessible
                    return true;
                }

                // Can't use original - run simulation manually with simSpeed = 1.0
                if (_simUpdateAllCallCount % 1000 == 1)
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches] SimUpdateAll #{_simUpdateAllCallCount}: Running manual simulation (simSpeed=1.0)");
                }
                RunSimulationTick(__instance, dt);

                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] SimUpdateAll_Prefix error: {ex.Message}");
                RunSimulationTick(__instance, dt);
                return false;
            }
        }

        /// <summary>
        /// Run the full simulation tick manually when Player.instance is null
        /// This replicates SimUpdateAll logic with simSpeed = 1.0
        /// </summary>
        private static void RunSimulationTick(object factorySimInstance, float dt)
        {
            try
            {
                // Get MachineManager.instance
                var machineManagerType = AccessTools.TypeByName("MachineManager");
                if (machineManagerType == null)
                {
                    if (_simUpdateAllCallCount % 1000 == 1)
                        Plugin.Log.LogWarning("[HeadlessPatches] RunSimulationTick: MachineManager type not found!");
                    return;
                }

                var mmInstanceField = machineManagerType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                var mmInstance = mmInstanceField?.GetValue(null);
                if (mmInstance == null)
                {
                    // MachineManager.instance is null - try to set it from FactorySimManager.machineManagers
                    var factorySimType = AccessTools.TypeByName("FactorySimManager");
                    if (factorySimType != null)
                    {
                        var fsInstanceProp = factorySimType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                        var fsInstance = fsInstanceProp?.GetValue(null);

                        if (fsInstance != null)
                        {
                            // Get machineManagers array
                            var mmArrayField = factorySimType.GetField("machineManagers", BindingFlags.Public | BindingFlags.Instance);
                            var mmArray = mmArrayField?.GetValue(fsInstance) as Array;

                            if (mmArray != null && mmArray.Length > 0)
                            {
                                // Get machineStateIndex (default to 0)
                                var stateIndexField = factorySimType.GetField("machineStateIndex", BindingFlags.Public | BindingFlags.Instance);
                                int stateIndex = 0;
                                if (stateIndexField != null)
                                {
                                    var indexVal = stateIndexField.GetValue(fsInstance);
                                    if (indexVal is int idx)
                                        stateIndex = idx;
                                }

                                // Get the machine manager from the array
                                var selectedMM = mmArray.GetValue(stateIndex);
                                if (selectedMM != null)
                                {
                                    // Set MachineManager.instance
                                    mmInstanceField.SetValue(null, selectedMM);
                                    mmInstance = selectedMM;
                                    Plugin.Log.LogInfo($"[HeadlessPatches] Set MachineManager.instance from machineManagers[{stateIndex}]");
                                }
                                else if (_simUpdateAllCallCount % 1000 == 1)
                                {
                                    Plugin.Log.LogWarning($"[HeadlessPatches] machineManagers[{stateIndex}] is null!");
                                }
                            }
                        }
                    }

                    // If still null, abort
                    if (mmInstance == null)
                    {
                        if (_simUpdateAllCallCount % 1000 == 1)
                            Plugin.Log.LogWarning("[HeadlessPatches] RunSimulationTick: MachineManager.instance is still null!");
                        return;
                    }
                }

                // Get curTick
                var curTickField = machineManagerType.GetField("curTick", BindingFlags.Public | BindingFlags.Instance);
                int curTick = (int)(curTickField?.GetValue(mmInstance) ?? 0);

                // Initialize target tick - use cached startTick from save data
                // Keep checking until we get a valid save tick (save data may not be parsed immediately)
                if (!_tickInitialized || (curTick < 100000 && _simUpdateAllCallCount % 100 == 0))
                {
                    // Get startTick from ServerConnectionHandler (extracted from save file JSON)
                    int saveTick = Networking.ServerConnectionHandler.CachedStartTick;

                    // Debug log every 500 calls
                    if (_simUpdateAllCallCount % 500 == 1)
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] Tick check: curTick={curTick}, saveTick={saveTick}");
                    }

                    // If we found a valid save tick and it's much higher than current, jump to it
                    if (saveTick > 100000 && curTick < saveTick)
                    {
                        int oldTick = curTick;
                        curTickField?.SetValue(mmInstance, saveTick);
                        _targetTick = saveTick;
                        curTick = saveTick;
                        Plugin.Log.LogInfo($"[HeadlessPatches] Jumped to save tick: {saveTick} (was at {oldTick})");
                    }

                    if (!_tickInitialized)
                    {
                        _targetTick = curTick;
                        _tickInitialized = true;
                        Plugin.Log.LogInfo($"[HeadlessPatches] Simulation initialized at tick {curTick}");
                    }
                }

                // Advance time with simSpeed = 1.0
                _timeToProcess += dt * 1.0f;

                // Calculate tick advancement (same as original: 64 ticks per second)
                if (_timeToProcess >= TICK_INTERVAL)
                {
                    int ticksToAdvance = (int)(_timeToProcess / TICK_INTERVAL);
                    _targetTick += ticksToAdvance;
                    _timeToProcess -= ticksToAdvance * TICK_INTERVAL;
                }

                // Process HostQueue before updating machines
                ProcessHostQueueManually(factorySimInstance, dt);

                // Calculate how many ticks to process
                int ticksToProcess = _targetTick - curTick;

                // Log every 100 calls
                if (_simUpdateAllCallCount % 100 == 1)
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches] SimTick #{_simUpdateAllCallCount}: curTick={curTick}, targetTick={_targetTick}, toProcess={ticksToProcess}");
                }

                // Update machines for each tick
                if (ticksToProcess > 0)
                {
                    var updateAllMethod = machineManagerType.GetMethod("UpdateAll", BindingFlags.Public | BindingFlags.Instance);
                    bool updateAllSucceeded = false;

                    if (updateAllMethod != null)
                    {
                        for (int i = 1; i <= ticksToProcess; i++)
                        {
                            bool isLastTick = (i == ticksToProcess);
                            try
                            {
                                // UpdateAll(bool isRefresh, bool isIncrement = true)
                                // For normal updates: isRefresh = true only on last tick
                                updateAllMethod.Invoke(mmInstance, new object[] { isLastTick, true });
                                updateAllSucceeded = true;
                            }
                            catch (Exception ex)
                            {
                                // UpdateAll failed - manually increment curTick so tick keeps advancing
                                curTickField?.SetValue(mmInstance, curTick + 1);
                                curTick++;

                                // Log occasionally
                                if (_simUpdateAllCallCount % 1000 == 1)
                                {
                                    Plugin.Log.LogWarning($"[HeadlessPatches] UpdateAll error (tick {curTick}): {ex.InnerException?.Message ?? ex.Message}");
                                }
                            }
                        }
                    }
                    else
                    {
                        // No UpdateAll method - just increment curTick directly
                        int newTick = curTick + ticksToProcess;
                        curTickField?.SetValue(mmInstance, newTick);
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] RunSimulationTick error: {ex.Message}");
            }
        }

        /// <summary>
        /// Manually process the HostQueue when Player.instance is null
        /// This mirrors the logic from SimUpdateAll but without the Player dependencies
        /// </summary>
        private static int _processQueueCallCount = 0;

        private static void ProcessHostQueueManually(object factorySimInstance, float dt)
        {
            _processQueueCallCount++;

            try
            {
                // Get MachineManager.instance.curTick
                var machineManagerType = AccessTools.TypeByName("MachineManager");
                if (machineManagerType == null)
                {
                    if (_processQueueCallCount % 100 == 1)
                        Plugin.Log.LogWarning("[HeadlessPatches] ProcessQueue: MachineManager type not found");
                    return;
                }

                var mmInstanceField = machineManagerType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                var mmInstance = mmInstanceField?.GetValue(null);
                if (mmInstance == null)
                {
                    if (_processQueueCallCount % 100 == 1)
                        Plugin.Log.LogWarning("[HeadlessPatches] ProcessQueue: MachineManager.instance is null");
                    return;
                }

                // Get curTick
                var curTickField = machineManagerType.GetField("curTick", BindingFlags.Public | BindingFlags.Instance);
                var curTick = (int)(curTickField?.GetValue(mmInstance) ?? 0);

                // Get NetworkMessageRelay.instance
                var relayType = AccessTools.TypeByName("NetworkMessageRelay");
                if (relayType == null)
                {
                    if (_processQueueCallCount % 100 == 1)
                        Plugin.Log.LogWarning("[HeadlessPatches] ProcessQueue: NetworkMessageRelay type not found");
                    return;
                }

                var relayInstanceField = relayType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                var relayInstance = relayInstanceField?.GetValue(null);
                if (relayInstance == null)
                {
                    if (_processQueueCallCount % 100 == 1)
                        Plugin.Log.LogWarning("[HeadlessPatches] ProcessQueue: NetworkMessageRelay.instance is null");
                    return;
                }

                // Get HostQueue
                var hostQueueProp = relayType.GetProperty("HostQueue", BindingFlags.Public | BindingFlags.Instance);
                if (hostQueueProp == null)
                {
                    if (_processQueueCallCount % 100 == 1)
                        Plugin.Log.LogWarning("[HeadlessPatches] ProcessQueue: HostQueue property not found");
                    return;
                }

                var hostQueue = hostQueueProp.GetValue(relayInstance) as System.Collections.ICollection;
                if (hostQueue == null || hostQueue.Count == 0) return;

                // Found items in queue - log this!
                Plugin.Log.LogInfo($"[HeadlessPatches] ProcessQueue: Found {hostQueue.Count} items in HostQueue, curTick={curTick}");

                // Get the queue as proper type to dequeue
                var enqueuedActionType = AccessTools.TypeByName("EnqueuedNetworkAction");
                var dequeueMethod = hostQueueProp.PropertyType.GetMethod("Dequeue");
                var countProp = hostQueueProp.PropertyType.GetProperty("Count");

                int processed = 0;
                while ((int)countProp.GetValue(hostQueueProp.GetValue(relayInstance)) > 0)
                {
                    var enqueuedAction = dequeueMethod.Invoke(hostQueueProp.GetValue(relayInstance), null);
                    if (enqueuedAction == null) break;

                    // Get action and sender from EnqueuedNetworkAction
                    var actionField = enqueuedActionType.GetField("action");
                    var senderField = enqueuedActionType.GetField("sender");

                    var action = actionField?.GetValue(enqueuedAction);
                    var sender = senderField?.GetValue(enqueuedAction);

                    if (action == null) continue;

                    // Set tick on action.GetInfo()
                    var getInfoMethod = action.GetType().GetMethod("GetInfo");
                    if (getInfoMethod != null)
                    {
                        var info = getInfoMethod.Invoke(action, null);
                        if (info != null)
                        {
                            var tickField = info.GetType().GetField("tick");
                            tickField?.SetValue(info, curTick);

                            var actionTypeField = info.GetType().GetField("actionType");
                            var actionTypeProp = action.GetType().GetProperty("actionType");
                            if (actionTypeField != null && actionTypeProp != null)
                            {
                                actionTypeField.SetValue(info, actionTypeProp.GetValue(action));
                            }
                        }
                    }

                    // Call ProcessOnHost(sender)
                    var processMethod = action.GetType().GetMethod("ProcessOnHost");
                    if (processMethod != null)
                    {
                        try
                        {
                            var success = processMethod.Invoke(action, new object[] { sender });
                            Plugin.Log.LogInfo($"[HeadlessPatches] ProcessOnHost({action.GetType().Name}) returned {success}");
                            processed++;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Log.LogWarning($"[HeadlessPatches] ProcessOnHost error: {ex.Message}");
                        }
                    }

                    // Skip ClearPendingAction since Player.instance is null
                }

                if (processed > 0)
                {
                    Plugin.Log.LogInfo($"[HeadlessPatches] Processed {processed} actions from HostQueue");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] ProcessHostQueueManually error: {ex.Message}");
            }
        }

        /// <summary>
        /// Monitors the server's NetworkMessageRelay and ensures it has netId=1
        /// so client commands (which we force to use netId=1) will be routed correctly.
        /// </summary>
        private static System.Collections.IEnumerator MonitorRelayNetId()
        {
            yield return new WaitForSeconds(5f);

            while (true)
            {
                try
                {
                    if (NetworkServer.active)
                    {
                        var relayType = AccessTools.TypeByName("NetworkMessageRelay");
                        if (relayType != null)
                        {
                            var relayInstanceField = relayType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                            var relayInstance = relayInstanceField?.GetValue(null) as MonoBehaviour;

                            if (relayInstance != null)
                            {
                                var identity = relayInstance.GetComponent<NetworkIdentity>();
                                if (identity != null)
                                {
                                    var currentNetId = identity.netId;
                                    Plugin.Log.LogInfo($"[HeadlessPatches] Server NetworkMessageRelay netId = {currentNetId}");

                                    // If netId is not 1, we need to add it to spawned dict with netId=1
                                    if (currentNetId != 1)
                                    {
                                        Plugin.Log.LogInfo($"[HeadlessPatches] Server relay has netId={currentNetId}, adding alias for netId=1");

                                        // Add this identity to spawned with netId=1 as alias
                                        if (!NetworkServer.spawned.ContainsKey(1))
                                        {
                                            NetworkServer.spawned[1] = identity;
                                            Plugin.Log.LogInfo($"[HeadlessPatches] Added server relay to NetworkServer.spawned[1]");
                                        }
                                    }

                                    // Log what's in the spawned dictionary
                                    Plugin.Log.LogInfo($"[HeadlessPatches] NetworkServer.spawned count: {NetworkServer.spawned.Count}");
                                    foreach (var kvp in NetworkServer.spawned.Take(10))
                                    {
                                        var name = kvp.Value?.gameObject?.name ?? "null";
                                        var typeName = kvp.Value?.GetType()?.Name ?? "null";
                                        Plugin.Log.LogInfo($"[HeadlessPatches]   spawned[{kvp.Key}] = {name} ({typeName})");
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] MonitorRelayNetId error: {ex.Message}");
                }

                yield return new WaitForSeconds(10f);
            }
        }

        // Stored sim tick from save data
        private static long _currentSimTick = 724189; // Default from save

        /// <summary>
        /// Sets the current sim tick from loaded save data
        /// </summary>
        public static void SetCurrentSimTick(long tick)
        {
            _currentSimTick = tick;
            Plugin.Log.LogInfo($"[HeadlessPatches] Set current sim tick to {tick}");
        }

        /// <summary>
        /// Prefix for NetworkMessageRelay.SendNetworkAction - logs and allows action processing
        /// </summary>
        public static bool NetworkMessageRelay_SendNetworkAction_Prefix(object action)
        {
            try
            {
                var actionType = action?.GetType().Name ?? "null";
                Plugin.Log.LogInfo($"[HeadlessPatches] SendNetworkAction called with: {actionType}");
                // Allow original to run - it will send to server
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] SendNetworkAction error: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Prefix for NetworkMessageRelay.CmdSendNetworkAction - server receives action from client
        /// </summary>
        public static bool NetworkMessageRelay_CmdSendNetworkAction_Prefix(object __instance, object action)
        {
            try
            {
                var actionType = action?.GetType().Name ?? "null";
                Plugin.Log.LogInfo($"[HeadlessPatches] CmdSendNetworkAction received: {actionType}");

                // Try to execute the action directly
                if (action != null)
                {
                    var executeMethod = action.GetType().GetMethod("Execute",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (executeMethod != null)
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] Executing action: {actionType}");
                        executeMethod.Invoke(action, null);
                        return false; // Skip original - we handled it
                    }
                }
                return true; // Run original if we couldn't handle
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] CmdSendNetworkAction error: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Generic prefix for all command handlers - logs when server receives a command
        /// </summary>
        /// <summary>
        /// Postfix for UserCode commands - directly process the action after it's enqueued
        /// since SimUpdateAll might not be running to drain the queue
        /// </summary>
        public static bool GenericCommand_Prefix(object __instance, object action, NetworkConnectionToClient sender, MethodBase __originalMethod)
        {
            try
            {
                var methodName = __originalMethod?.Name ?? "Unknown";
                var actionType = action?.GetType()?.Name ?? "null";
                Plugin.Log.LogInfo($"[HeadlessPatches] COMMAND RECEIVED: {methodName} with action {actionType}");

                // DEBUG: Check sender and authenticationData
                if (sender == null)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] sender is NULL!");
                    return true; // Let original run if we can't handle it
                }
                var authData = sender.authenticationData;
                Plugin.Log.LogInfo($"[HeadlessPatches] sender.connectionId={sender.connectionId}, authData={authData} (type={authData?.GetType()?.Name})");

                // DEBUG: Check GameState.instance and allPlayers
                var gameStateType = AccessTools.TypeByName("GameState");
                object gameStateInstance = null;
                object allPlayers = null;
                if (gameStateType != null)
                {
                    var gsInstField = gameStateType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    gameStateInstance = gsInstField?.GetValue(null);
                    if (gameStateInstance != null)
                    {
                        var allPlayersField = gameStateType.GetField("allPlayers", BindingFlags.Public | BindingFlags.Instance);
                        allPlayers = allPlayersField?.GetValue(gameStateInstance);

                        if (allPlayers != null)
                        {
                            var dictType = allPlayers.GetType();
                            var countProp = dictType.GetProperty("Count");
                            var keysProperty = dictType.GetProperty("Keys");
                            int count = (int)countProp.GetValue(allPlayers);
                            var keys = keysProperty.GetValue(allPlayers) as IEnumerable<string>;
                            Plugin.Log.LogInfo($"[HeadlessPatches] GameState.allPlayers has {count} entries: [{string.Join(", ", keys ?? new string[0])}]");

                            // Check if this sender's player is registered
                            string playerId = authData as string;
                            if (!string.IsNullOrEmpty(playerId))
                            {
                                var containsKeyMethod = dictType.GetMethod("ContainsKey");
                                bool hasPlayer = (bool)containsKeyMethod.Invoke(allPlayers, new object[] { playerId });
                                Plugin.Log.LogInfo($"[HeadlessPatches] Player '{playerId}' in allPlayers: {hasPlayer}");

                                if (!hasPlayer)
                                {
                                    // Player not registered - try to find their NetworkedPlayer and register
                                    Plugin.Log.LogWarning($"[HeadlessPatches] Player '{playerId}' NOT in allPlayers! Attempting manual registration...");
                                    TryRegisterPlayerInAllPlayers(sender, playerId, allPlayers);
                                }
                            }
                        }
                        else
                        {
                            Plugin.Log.LogWarning($"[HeadlessPatches] GameState.allPlayers is NULL");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[HeadlessPatches] GameState.instance is NULL");
                    }
                }

                // Directly process the action instead of waiting for SimUpdateAll
                // since SimUpdateAll doesn't run on headless server
                if (action != null)
                {
                    // Get curTick from MachineManager
                    int curTick = 724189; // default
                    var mmType = AccessTools.TypeByName("MachineManager");
                    if (mmType != null)
                    {
                        var mmInstField = mmType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                        var mmInstance = mmInstField?.GetValue(null);
                        if (mmInstance != null)
                        {
                            var curTickField = mmType.GetField("curTick", BindingFlags.Public | BindingFlags.Instance);
                            curTick = (int)(curTickField?.GetValue(mmInstance) ?? curTick);
                        }
                    }

                    // Get info from action
                    var getInfoMethod = action.GetType().GetMethod("GetInfo");
                    object info = null;
                    if (getInfoMethod != null)
                    {
                        info = getInfoMethod.Invoke(action, null);
                        if (info != null)
                        {
                            var tickField = info.GetType().GetField("tick");
                            tickField?.SetValue(info, curTick);
                        }
                    }

                    // HEADLESS MODE: Broadcast the action to all clients via NetworkMessageRelay RPC
                    bool broadcastSucceeded = BroadcastActionToClients(actionType, info, sender);

                    if (broadcastSucceeded)
                    {
                        Plugin.Log.LogInfo($"[HeadlessPatches] Broadcasted {actionType} to all clients");
                    }
                    else
                    {
                        Plugin.Log.LogWarning($"[HeadlessPatches] Failed to broadcast {actionType} - no handler");
                    }

                    // ALSO try to process the action on the server for state persistence
                    // This updates terrain, props, etc. so they persist in saves
                    try
                    {
                        var processOnHostMethod = action.GetType().GetMethod("ProcessOnHost", BindingFlags.Public | BindingFlags.Instance);
                        if (processOnHostMethod != null)
                        {
                            Plugin.Log.LogInfo($"[HeadlessPatches] Attempting ProcessOnHost for {actionType}...");
                            processOnHostMethod.Invoke(action, new object[] { sender });
                            Plugin.Log.LogInfo($"[HeadlessPatches] ProcessOnHost succeeded for {actionType}");
                        }
                    }
                    catch (Exception processEx)
                    {
                        // ProcessOnHost may fail on headless server - that's OK, broadcast already happened
                        // Log at debug level since this is expected for many actions
                        Plugin.Log.LogInfo($"[HeadlessPatches] ProcessOnHost for {actionType}: {processEx.InnerException?.Message ?? processEx.Message}");
                    }
                }

                // Skip the original method - we've already handled broadcast + attempted ProcessOnHost
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] GenericCommand_Prefix error: {ex.Message}");
                return true; // Let original run on error
            }
        }

        /// <summary>
        /// Broadcasts an action to all clients by calling the appropriate RPC on NetworkMessageRelay.
        /// This bypasses ProcessOnHost validation which fails on headless servers without game world data.
        /// </summary>
        private static bool BroadcastActionToClients(string actionType, object info, NetworkConnectionToClient sender)
        {
            if (info == null)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] Cannot broadcast {actionType} - info is null");
                return false;
            }

            try
            {
                // Get NetworkMessageRelay.instance
                var relayType = AccessTools.TypeByName("NetworkMessageRelay");
                if (relayType == null)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] relayType is null for {actionType}");
                    return false;
                }
                Plugin.Log.LogInfo($"[HeadlessPatches] Found relay type, info type={info.GetType().Name}");

                var instanceField = relayType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                Plugin.Log.LogInfo($"[HeadlessPatches] instanceField={instanceField != null}");
                object relayInstance = instanceField?.GetValue(null);
                Plugin.Log.LogInfo($"[HeadlessPatches] relayInstance={relayInstance != null}");
                if (relayInstance == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] NetworkMessageRelay.instance is null");
                    return false;
                }

                // Map action types to their broadcast RPC methods
                // The RPC method name is usually the action name without "Action" suffix
                Plugin.Log.LogInfo($"[HeadlessPatches] Mapping actionType={actionType}");
                string rpcMethodName = null;
                switch (actionType)
                {
                    case "MOLEAction":
                        rpcMethodName = "MOLEAction";
                        break;
                    case "TakeAllAction":
                        rpcMethodName = "TakeAllFromMachine";
                        break;
                    case "HitDestructibleAction":
                        rpcMethodName = "HitDestructable";
                        break;
                    case "ActivateMachineAction":
                        rpcMethodName = "ActivateMachine";
                        break;
                    case "DeactivateTechAction":
                        rpcMethodName = "DeactivateTech";
                        break;
                    case "ExchangeMachineAction":
                        rpcMethodName = "ExchangeMachineInventory";
                        break;
                    case "BuildAction":
                    case "SimpleBuildAction":
                        rpcMethodName = "RpcBuildSimpleMachine";
                        break;
                    case "ConveyorBuildAction":
                        rpcMethodName = "RpcBuildConveyor";
                        break;
                    case "EraseMachineAction":
                        rpcMethodName = "EraseMachine";
                        break;
                    case "RotateMachineAction":
                        rpcMethodName = "RotateMachine";
                        break;
                    case "SetFilterAction":
                    case "ChangeFilterAction":
                        rpcMethodName = "SetFilter";
                        break;
                    case "HarvestPlantAction":
                        rpcMethodName = "HarvestPlant";
                        break;
                    case "PingAction":
                        rpcMethodName = "Ping";
                        break;
                    case "ShootAction":
                        rpcMethodName = "Shoot";
                        break;
                    case "SwapVariantAction":
                        rpcMethodName = "SwapVariant";
                        break;
                    case "ReplaceAction":
                        rpcMethodName = "Replace";
                        break;
                    case "ActivatePowerAction":
                        rpcMethodName = "ActivatePower";
                        break;
                    case "TempConveyorAction":
                        rpcMethodName = "TempConveyor";
                        break;
                    case "CraftAction":
                        rpcMethodName = "Craft";
                        break;
                    default:
                        // Try to guess the method name by removing "Action" suffix
                        if (actionType.EndsWith("Action"))
                        {
                            rpcMethodName = actionType.Substring(0, actionType.Length - 6);
                        }
                        break;
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] After switch: rpcMethodName={rpcMethodName ?? "NULL"}");
                if (string.IsNullOrEmpty(rpcMethodName))
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] No RPC mapping for {actionType}");
                    return false;
                }

                // Find the RPC method
                Plugin.Log.LogInfo($"[HeadlessPatches] Looking for method {rpcMethodName} on relay");
                var rpcMethod = relayType.GetMethod(rpcMethodName, BindingFlags.Public | BindingFlags.Instance);
                if (rpcMethod == null)
                {
                    // Try with "Rpc" prefix
                    rpcMethod = relayType.GetMethod("Rpc" + rpcMethodName, BindingFlags.Public | BindingFlags.Instance);
                }
                if (rpcMethod == null)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] RPC method '{rpcMethodName}' not found on NetworkMessageRelay");
                    return false;
                }

                // Get the info object from the action using GetInfo()
                // Actions have an "info" field and a GetInfo() method that returns it
                // The RPC methods expect the *Info object (e.g., MOLEActionInfo), not the *Action object
                object infoObject = info;
                var getInfoMethod = info.GetType().GetMethod("GetInfo", BindingFlags.Public | BindingFlags.Instance);
                if (getInfoMethod != null)
                {
                    var extractedInfo = getInfoMethod.Invoke(info, null);
                    if (extractedInfo != null)
                    {
                        infoObject = extractedInfo;
                        Plugin.Log.LogInfo($"[HeadlessPatches] Extracted info: {infoObject.GetType().Name} from {info.GetType().Name}");
                    }
                }

                // Populate required fields that ProcessOnHost normally sets
                // Get player ID from sender's authenticationData
                string playerId = sender?.authenticationData?.ToString();
                if (!string.IsNullOrEmpty(playerId))
                {
                    // Set instigatingPlayerNetworkID for actions that need it (MOLEAction, etc.)
                    var instigatingField = infoObject.GetType().GetField("instigatingPlayerNetworkID");
                    if (instigatingField != null)
                    {
                        instigatingField.SetValue(infoObject, playerId);
                        Plugin.Log.LogInfo($"[HeadlessPatches] Set instigatingPlayerNetworkID={playerId}");
                    }

                    // Set playerNetworkID for other actions
                    var playerIdField = infoObject.GetType().GetField("playerNetworkID");
                    if (playerIdField != null)
                    {
                        playerIdField.SetValue(infoObject, playerId);
                        Plugin.Log.LogInfo($"[HeadlessPatches] Set playerNetworkID={playerId}");
                    }
                }

                // Invoke the RPC method with the info object
                Plugin.Log.LogInfo($"[HeadlessPatches] Calling {rpcMethodName}({infoObject.GetType().Name})");
                rpcMethod.Invoke(relayInstance, new object[] { infoObject });
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] BroadcastActionToClients error: {ex.Message}");
                if (ex.InnerException != null)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] Inner: {ex.InnerException.Message}");
                }
                return false;
            }
        }

        /// <summary>
        /// Attempts to find the NetworkedPlayer for a connection and register them in allPlayers.
        /// </summary>
        private static void TryRegisterPlayerInAllPlayers(NetworkConnectionToClient sender, string playerId, object allPlayers)
        {
            try
            {
                // Find the NetworkedPlayer associated with this connection
                // sender.identity gives us the NetworkIdentity of the player object
                var identity = sender.identity;
                if (identity == null)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] sender.identity is null - cannot register player");
                    return;
                }

                // Get the NetworkedPlayer component from the identity's gameObject
                var networkedPlayerType = AccessTools.TypeByName("NetworkedPlayer");
                if (networkedPlayerType == null)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] NetworkedPlayer type not found");
                    return;
                }

                var getComponentMethod = typeof(GameObject).GetMethod("GetComponent", new Type[0]).MakeGenericMethod(networkedPlayerType);
                var networkedPlayer = getComponentMethod.Invoke(identity.gameObject, null);

                if (networkedPlayer == null)
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] No NetworkedPlayer component on identity.gameObject");
                    return;
                }

                // Register in allPlayers
                var dictType = allPlayers.GetType();
                var addMethod = dictType.GetMethod("Add");
                addMethod.Invoke(allPlayers, new object[] { playerId, networkedPlayer });
                Plugin.Log.LogInfo($"[HeadlessPatches] Successfully registered player '{playerId}' in allPlayers!");

                // Also ensure the NetworkedPlayer has its NetworkID set
                var networkIdField = networkedPlayerType.GetField("NetworkNetworkID", BindingFlags.Public | BindingFlags.Instance);
                if (networkIdField != null)
                {
                    networkIdField.SetValue(networkedPlayer, playerId);
                    Plugin.Log.LogInfo($"[HeadlessPatches] Set NetworkNetworkID = '{playerId}'");
                }

                // Create serverInventory if missing
                var serverInvField = networkedPlayerType.GetField("serverInventory", BindingFlags.Public | BindingFlags.Instance);
                if (serverInvField != null && serverInvField.GetValue(networkedPlayer) == null)
                {
                    var serverInvType = AccessTools.TypeByName("ServerInventory");
                    if (serverInvType != null)
                    {
                        var serverInvCtor = serverInvType.GetConstructor(new Type[] { networkedPlayerType });
                        if (serverInvCtor != null)
                        {
                            var serverInv = serverInvCtor.Invoke(new object[] { networkedPlayer });
                            serverInvField.SetValue(networkedPlayer, serverInv);
                            Plugin.Log.LogInfo($"[HeadlessPatches] Created ServerInventory for player");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] TryRegisterPlayerInAllPlayers error: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix for NetworkMessageRelay.RequestCurrentSimTick - handles sim tick request and sends response.
        /// The original UserCode_RequestCurrentSimTick does:
        ///   GetPlayer(sender, out var _).SetPlayerFinishedLoading();
        ///   FactorySimManager.instance.lastSyncTick = MachineManager.instance.curTick;
        ///   ProcessCurrentSimTick(sender, MachineManager.instance.curTick);
        /// We need to call ProcessCurrentSimTick to respond to the client.
        /// </summary>
        public static bool NetworkMessageRelay_RequestCurrentSimTick_Prefix(
            object __instance,
            NetworkConnectionToClient sender)
        {
            try
            {
                // Get the current tick from MachineManager if available, otherwise use cached value
                int tickToSend = (int)_currentSimTick;

                // Try to get actual MachineManager.instance.curTick
                var machineManagerType = AccessTools.TypeByName("MachineManager");
                if (machineManagerType != null)
                {
                    var instanceField = machineManagerType.GetField("instance", BindingFlags.Public | BindingFlags.Static);
                    if (instanceField != null)
                    {
                        var mmInstance = instanceField.GetValue(null);
                        if (mmInstance != null)
                        {
                            var curTickField = machineManagerType.GetField("curTick", BindingFlags.Public | BindingFlags.Instance);
                            if (curTickField != null)
                            {
                                tickToSend = (int)curTickField.GetValue(mmInstance);
                            }
                        }
                    }
                }

                // Also update FactorySimManager.lastSyncTick if available
                var factorySimType = AccessTools.TypeByName("FactorySimManager");
                if (factorySimType != null)
                {
                    var fsmInstanceProp = factorySimType.GetProperty("instance", BindingFlags.Public | BindingFlags.Static);
                    if (fsmInstanceProp != null)
                    {
                        var fsmInstance = fsmInstanceProp.GetValue(null);
                        if (fsmInstance != null)
                        {
                            var lastSyncField = factorySimType.GetField("_lastSyncTick", BindingFlags.NonPublic | BindingFlags.Instance);
                            if (lastSyncField != null)
                            {
                                lastSyncField.SetValue(fsmInstance, tickToSend);
                            }
                        }
                    }
                }

                Plugin.Log.LogInfo($"[HeadlessPatches] RequestCurrentSimTick from conn={sender?.connectionId}, tick={tickToSend}");

                // Try to find the ProcessCurrentSimTick method to respond to client
                var relayType = __instance.GetType();
                var processMethod = relayType.GetMethod("ProcessCurrentSimTick",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (processMethod != null && sender != null)
                {
                    // Call ProcessCurrentSimTick(sender, tick) to send response to client
                    processMethod.Invoke(__instance, new object[] { sender, tickToSend });
                    Plugin.Log.LogInfo($"[HeadlessPatches] Sent ProcessCurrentSimTick({tickToSend}) to connection {sender.connectionId}");
                }
                else
                {
                    Plugin.Log.LogWarning($"[HeadlessPatches] Cannot send ProcessCurrentSimTick - method={processMethod != null}, sender={sender != null}");
                }

                // Skip original - we handled it
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] RequestCurrentSimTick error: {ex.Message}");
                return false; // Skip original to prevent crash
            }
        }

        #region Wine Socket Compatibility Patches

        private static int _createMismatchCount = 0;

        /// <summary>
        /// Patches IPEndPointNonAlloc.DeepCopyIPEndPoint to handle Wine's incorrect address families.
        /// Wine/Mono sometimes returns AddressFamily values that are not IPv4 (2) or IPv6 (23).
        /// This causes KCP connections to fail with "Unexpected SocketAddress family" errors.
        /// </summary>
        private static void PatchIPEndPointNonAlloc(Harmony harmony)
        {
            try
            {
                Plugin.Log.LogInfo("[HeadlessPatches] Patching IPEndPointNonAlloc for Wine compatibility...");

                // Find the IPEndPointNonAlloc type in WhereAllocation namespace
                var ipEndPointNonAllocType = AccessTools.TypeByName("WhereAllocation.IPEndPointNonAlloc");
                if (ipEndPointNonAllocType == null)
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] IPEndPointNonAlloc type not found - checking kcp2k assembly");

                    // Try to find it in loaded assemblies
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            var type = asm.GetType("WhereAllocation.IPEndPointNonAlloc");
                            if (type != null)
                            {
                                ipEndPointNonAllocType = type;
                                Plugin.Log.LogInfo($"[HeadlessPatches] Found IPEndPointNonAlloc in {asm.GetName().Name}");
                                break;
                            }
                        }
                        catch { }
                    }
                }

                if (ipEndPointNonAllocType == null)
                {
                    Plugin.Log.LogError("[HeadlessPatches] Could not find IPEndPointNonAlloc type!");
                    return;
                }

                // Patch DeepCopyIPEndPoint method
                var deepCopyMethod = ipEndPointNonAllocType.GetMethod("DeepCopyIPEndPoint",
                    BindingFlags.Public | BindingFlags.Instance);
                if (deepCopyMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(IPEndPointNonAlloc_DeepCopyIPEndPoint_Prefix));
                    harmony.Patch(deepCopyMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] IPEndPointNonAlloc.DeepCopyIPEndPoint patched for Wine compatibility");
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] DeepCopyIPEndPoint method not found");
                }

                // Also patch the Create method which throws on address family mismatch
                var createMethod = ipEndPointNonAllocType.GetMethod("Create",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { typeof(System.Net.SocketAddress) },
                    null);
                if (createMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(IPEndPointNonAlloc_Create_Prefix));
                    harmony.Patch(createMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] IPEndPointNonAlloc.Create patched for Wine compatibility");
                }
                else
                {
                    Plugin.Log.LogWarning("[HeadlessPatches] Create method not found");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Failed to patch IPEndPointNonAlloc: {ex.Message}");
            }
        }

        /// <summary>
        /// Prefix for IPEndPointNonAlloc.DeepCopyIPEndPoint.
        /// Handles Wine's incorrect address family by defaulting to IPv4.
        /// </summary>
        public static bool IPEndPointNonAlloc_DeepCopyIPEndPoint_Prefix(object __instance, ref System.Net.IPEndPoint __result)
        {
            try
            {
                // Get the temp SocketAddress field
                var tempField = __instance.GetType().GetField("temp", BindingFlags.Public | BindingFlags.Instance);
                if (tempField == null)
                {
                    return true; // Let original run
                }

                var temp = tempField.GetValue(__instance) as System.Net.SocketAddress;
                if (temp == null)
                {
                    return true; // Let original run
                }

                // Check address family
                var family = temp.Family;
                System.Net.IPAddress address;

                if (family == System.Net.Sockets.AddressFamily.InterNetworkV6)
                {
                    address = System.Net.IPAddress.IPv6Any;
                }
                else if (family == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    address = System.Net.IPAddress.Any;
                }
                else
                {
                    // WINE COMPATIBILITY: Unknown address family - default to IPv4
                    // Wine sometimes returns incorrect address family values (e.g., 22 = Atm)
                    // We treat these as IPv4 to prevent connection failures
                    Plugin.DebugLog($"[HeadlessPatches] DeepCopyIPEndPoint: Unknown address family {family} ({(int)family}), defaulting to IPv4");
                    address = System.Net.IPAddress.Any;
                }

                // Create the endpoint
                var baseEndPoint = new System.Net.IPEndPoint(address, 0);
                __result = (System.Net.IPEndPoint)baseEndPoint.Create(temp);
                return false; // Skip original
            }
            catch (Exception ex)
            {
                // If our patch fails, let the original run (which will probably throw too)
                Plugin.Log.LogWarning($"[HeadlessPatches] DeepCopyIPEndPoint patch error: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Prefix for IPEndPointNonAlloc.Create.
        /// Handles Wine's incorrect address family by being more lenient.
        /// </summary>
        public static bool IPEndPointNonAlloc_Create_Prefix(object __instance, System.Net.SocketAddress socketAddress, ref System.Net.EndPoint __result)
        {
            try
            {
                // Get the expected address family
                var addressFamilyProp = __instance.GetType().GetProperty("AddressFamily", BindingFlags.Public | BindingFlags.Instance);
                if (addressFamilyProp == null)
                {
                    return true; // Let original run
                }

                var expectedFamily = (System.Net.Sockets.AddressFamily)addressFamilyProp.GetValue(__instance);
                var actualFamily = socketAddress.Family;

                // If families match, let original handle it
                if (actualFamily == expectedFamily)
                {
                    return true;
                }

                // WINE COMPATIBILITY: If families don't match but both are IP-based or unknown, proceed anyway
                // Wine sometimes returns incorrect address family values
                bool isExpectedIP = expectedFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                                    expectedFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
                bool isActualIP = actualFamily == System.Net.Sockets.AddressFamily.InterNetwork ||
                                  actualFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
                bool isActualUnknown = (int)actualFamily != 2 && (int)actualFamily != 23;

                if (isExpectedIP && (isActualIP || isActualUnknown))
                {
                    // Log occasionally (use a counter stored in a static field)
                    _createMismatchCount++;
                    if (_createMismatchCount <= 5 || _createMismatchCount % 10000 == 0)
                    {
                        Plugin.DebugLog($"[HeadlessPatches] Create: Address family mismatch (expected={expectedFamily}, actual={actualFamily}), allowing anyway (Wine compat)");
                    }

                    // Get temp field and update it - IMPORTANT: Must do the hash trick like original
                    var tempField = __instance.GetType().GetField("temp", BindingFlags.Public | BindingFlags.Instance);
                    if (tempField != null)
                    {
                        var currentTemp = tempField.GetValue(__instance) as System.Net.SocketAddress;
                        if (socketAddress != currentTemp)
                        {
                            tempField.SetValue(__instance, socketAddress);

                            // CRITICAL: Trigger the m_changed flag for proper GetHashCode
                            // This is what the original code does: temp[0]++; temp[0]--;
                            try
                            {
                                byte original = socketAddress[0];
                                socketAddress[0] = (byte)(original + 1);
                                socketAddress[0] = original;
                            }
                            catch { }
                        }
                    }

                    __result = (System.Net.EndPoint)__instance;
                    return false; // Skip original
                }

                // If not an IP-related case, let original throw
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[HeadlessPatches] Create patch error: {ex.Message}");
                return true;
            }
        }

        #endregion
    }

    /// <summary>
    /// Patches for auto-starting the server when configured.
    /// Hooks into the main menu to trigger auto-load of save files.
    /// </summary>
    public static class AutoStartPatches
    {
        private static bool _hasTriggeredAutoLoad;
        private static int _updateCount;
        private static float _startTime;

        public static void ApplyPatches(Harmony harmony)
        {
            try
            {
                _startTime = Time.realtimeSinceStartup;

                // Hook into MainMenuManager or similar to trigger auto-load
                var mainMenuType = AccessTools.TypeByName("MainMenuManager") ??
                                  AccessTools.TypeByName("MainMenu") ??
                                  AccessTools.TypeByName("TitleScreenManager");

                if (mainMenuType != null)
                {
                    var startMethod = AccessTools.Method(mainMenuType, "Start") ??
                                     AccessTools.Method(mainMenuType, "Awake");

                    if (startMethod != null)
                    {
                        var postfix = new HarmonyMethod(typeof(AutoStartPatches), nameof(MainMenu_Postfix));
                        harmony.Patch(startMethod, postfix: postfix);
                        Plugin.Log.LogInfo("[AutoStartPatches] Hooked main menu for auto-load");
                    }

                    // IMPORTANT: Also hook the Update method for continuous checking
                    var updateMethod = AccessTools.Method(mainMenuType, "Update");
                    if (updateMethod != null)
                    {
                        var updatePostfix = new HarmonyMethod(typeof(AutoStartPatches), nameof(MainMenu_Update_Postfix));
                        harmony.Patch(updateMethod, postfix: updatePostfix);
                        Plugin.Log.LogInfo("[AutoStartPatches] Hooked MainMenu.Update for auto-load polling");
                    }
                }

                // Also hook FlowManager.Start as a fallback
                var flowManagerType = AccessTools.TypeByName("FlowManager");
                if (flowManagerType != null)
                {
                    var startMethod = AccessTools.Method(flowManagerType, "Start");
                    if (startMethod != null)
                    {
                        var postfix = new HarmonyMethod(typeof(AutoStartPatches), nameof(FlowManager_Start_Postfix));
                        harmony.Patch(startMethod, postfix: postfix);
                        Plugin.Log.LogInfo("[AutoStartPatches] Hooked FlowManager.Start for auto-load");
                    }

                    // Also hook FlowManager.Update for polling
                    var updateMethod = AccessTools.Method(flowManagerType, "Update");
                    if (updateMethod != null)
                    {
                        var updatePostfix = new HarmonyMethod(typeof(AutoStartPatches), nameof(FlowManager_Update_Postfix));
                        harmony.Patch(updateMethod, postfix: updatePostfix);
                        Plugin.Log.LogInfo("[AutoStartPatches] Hooked FlowManager.Update for auto-load polling");
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[AutoStartPatches] FlowManager.Update method not found!");
                    }
                }

                // Try hooking into EventSystem.Update (UI input processing)
                try
                {
                    var eventSystemType = AccessTools.TypeByName("UnityEngine.EventSystems.EventSystem");
                    if (eventSystemType != null)
                    {
                        var esUpdateMethod = AccessTools.Method(eventSystemType, "Update");
                        if (esUpdateMethod != null)
                        {
                            var esPostfix = new HarmonyMethod(typeof(AutoStartPatches), nameof(EventSystem_Update_Postfix));
                            harmony.Patch(esUpdateMethod, postfix: esPostfix);
                            Plugin.Log.LogInfo("[AutoStartPatches] Hooked EventSystem.Update for auto-load polling");
                        }
                        else
                        {
                            Plugin.Log.LogWarning("[AutoStartPatches] EventSystem.Update not found");
                        }
                    }
                    else
                    {
                        Plugin.Log.LogWarning("[AutoStartPatches] EventSystem type not found");
                    }
                }
                catch (Exception hookEx)
                {
                    Plugin.Log.LogWarning($"[AutoStartPatches] EventSystem hook failed: {hookEx.Message}");
                }

                // Hook into Camera.Render as a reliable fallback - this is called every frame
                try
                {
                    var cameraType = typeof(UnityEngine.Camera);
                    var renderMethod = AccessTools.Method(cameraType, "Render");
                    if (renderMethod != null)
                    {
                        var renderPostfix = new HarmonyMethod(typeof(AutoStartPatches), nameof(Camera_Render_Postfix));
                        harmony.Patch(renderMethod, postfix: renderPostfix);
                        Plugin.Log.LogInfo("[AutoStartPatches] Hooked Camera.Render for auto-load polling");
                    }
                }
                catch (Exception cameraEx)
                {
                    Plugin.Log.LogWarning($"[AutoStartPatches] Camera.Render hook failed: {cameraEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AutoStartPatches] Failed to apply patches: {ex}");
            }
        }

        private static bool _firstCameraRenderCall = true;

        public static void Camera_Render_Postfix()
        {
            if (_firstCameraRenderCall)
            {
                _firstCameraRenderCall = false;
                Plugin.DebugLog("[AutoStartPatches] Camera.Render postfix FIRST CALL!");
            }
            CheckAutoLoadTrigger("Camera.Render");
        }

        public static void MainMenu_Postfix()
        {
            TriggerAutoLoad("MainMenu.Start");
        }

        public static void FlowManager_Start_Postfix()
        {
            Plugin.DebugLog("[AutoStartPatches] FlowManager.Start postfix called!");
            TriggerAutoLoad("FlowManager.Start");
        }

        public static void MainMenu_Update_Postfix()
        {
            CheckAutoLoadTrigger("MainMenu.Update");
        }

        private static bool _firstFlowManagerUpdateCall = true;
        private static int _flowManagerUpdateTickCount = 0;

        public static void FlowManager_Update_Postfix()
        {
            if (_firstFlowManagerUpdateCall)
            {
                _firstFlowManagerUpdateCall = false;
                Plugin.DebugLog("[AutoStartPatches] FlowManager.Update postfix FIRST CALL!");
            }
            CheckAutoLoadTrigger("FlowManager.Update");

            // CRITICAL: Tick network transport from Unity's main thread
            // This avoids Wine's critical section deadlock that blocks the background timer
            _flowManagerUpdateTickCount++;
            try
            {
                TickNetworkFromMainThread();
            }
            catch (Exception ex)
            {
                if (_flowManagerUpdateTickCount % 1000 == 1)
                {
                    Plugin.Log.LogWarning($"[AutoStartPatches] Network tick error: {ex.Message}");
                }
            }
        }

        private static int _mainThreadNetTickLogCount = 0;
        private static int _mainThreadLastConnectionCount = 0;

        /// <summary>
        /// Tick the network transport from Unity's main thread.
        /// This is more reliable than the background timer under Wine.
        /// </summary>
        private static void TickNetworkFromMainThread()
        {
            if (!Mirror.NetworkServer.active) return;

            var transport = Mirror.Transport.activeTransport;
            if (transport == null) return;

            // Log connection count changes
            int currentConnections = Mirror.NetworkServer.connections.Count;
            if (currentConnections != _mainThreadLastConnectionCount)
            {
                Plugin.Log.LogInfo($"[AutoStartPatches] Connection count changed: {_mainThreadLastConnectionCount} -> {currentConnections}");
                _mainThreadLastConnectionCount = currentConnections;
            }

            // Log every 1000 ticks
            _mainThreadNetTickLogCount++;
            if (_mainThreadNetTickLogCount % 1000 == 1)
            {
                Plugin.Log.LogInfo($"[AutoStartPatches] MainThread tick #{_mainThreadNetTickLogCount}, connections={currentConnections}");
            }

            // Call ServerEarlyUpdate to receive data
            try
            {
                transport.ServerEarlyUpdate();
            }
            catch (Exception) { }

            // Call ServerLateUpdate to send data
            try
            {
                transport.ServerLateUpdate();
            }
            catch (Exception) { }
        }

        private static bool _firstEventSystemCall = true;

        public static void EventSystem_Update_Postfix()
        {
            if (_firstEventSystemCall)
            {
                _firstEventSystemCall = false;
                Plugin.DebugLog("[AutoStartPatches] EventSystem.Update postfix FIRST CALL!");
            }
            CheckAutoLoadTrigger("EventSystem.Update");
        }

        private static void CheckAutoLoadTrigger(string source)
        {
            _updateCount++;

            // Log periodically to confirm hooks are working
            if (_updateCount % 300 == 0)
            {
                var elapsed = Time.realtimeSinceStartup - _startTime;
                Plugin.DebugLog($"[AutoStartPatches] {source} called {_updateCount} times, elapsed: {elapsed:F1}s");
            }

            // Check if thread triggered auto-load (15 seconds after start)
            var timeSinceStart = Time.realtimeSinceStartup - _startTime;
            if (!_hasTriggeredAutoLoad && timeSinceStart > 15f)
            {
                TriggerAutoLoad($"{source} (time-based)");
            }
        }

        private static void TriggerAutoLoad(string source)
        {
            if (_hasTriggeredAutoLoad) return;
            _hasTriggeredAutoLoad = true;

            // Check if auto-load is configured
            if (Plugin.AutoStartServer.Value &&
                (!string.IsNullOrEmpty(Plugin.AutoLoadSave.Value) || Plugin.AutoLoadSlot.Value >= 0))
            {
                Plugin.DebugLog($"[AutoStartPatches] Triggering auto-load from {source}!");
                Networking.AutoLoadManager.TryAutoLoad();
            }
        }
    }
}
