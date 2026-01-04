using HarmonyLib;
using Mirror;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
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

                _patchesApplied = true;
                Plugin.Log.LogInfo("[HeadlessPatches] Headless patches applied");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] Failed to apply patches: {ex}");
            }
        }

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
                // Patch SceneManager.LoadSceneAsync to return pre-completed operations
                var sceneManagerType = typeof(UnityEngine.SceneManagement.SceneManager);

                // Get all LoadSceneAsync overloads
                var loadSceneAsyncMethods = sceneManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .Where(m => m.Name == "LoadSceneAsync");

                foreach (var method in loadSceneAsyncMethods)
                {
                    try
                    {
                        var postfix = new HarmonyMethod(typeof(HeadlessPatches), nameof(LoadSceneAsync_Postfix));
                        harmony.Patch(method, postfix: postfix);
                        Plugin.Log.LogInfo($"[HeadlessPatches] Patched SceneManager.LoadSceneAsync ({method.GetParameters().Length} params)");
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

                // Patch CmdSendNetworkAction (server-side command handler)
                var cmdSendActionMethod = AccessTools.Method(relayType, "CmdSendNetworkAction");
                if (cmdSendActionMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkMessageRelay_CmdSendNetworkAction_Prefix));
                    harmony.Patch(cmdSendActionMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] NetworkMessageRelay.CmdSendNetworkAction patched");
                }

                // Patch UserCode_CmdSendNetworkAction (actual handler)
                var userCodeMethod = AccessTools.Method(relayType, "UserCode_CmdSendNetworkAction");
                if (userCodeMethod != null)
                {
                    var prefix = new HarmonyMethod(typeof(HeadlessPatches), nameof(NetworkMessageRelay_UserCode_CmdSendNetworkAction_Prefix));
                    harmony.Patch(userCodeMethod, prefix: prefix);
                    Plugin.Log.LogInfo("[HeadlessPatches] NetworkMessageRelay.UserCode_CmdSendNetworkAction patched");
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
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] NetworkMessageRelay patch failed: {ex}");
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
        /// Prefix for NetworkMessageRelay.UserCode_CmdSendNetworkAction - actual server handler
        /// </summary>
        public static bool NetworkMessageRelay_UserCode_CmdSendNetworkAction_Prefix(object __instance, object action)
        {
            try
            {
                var actionType = action?.GetType().Name ?? "null";
                Plugin.Log.LogInfo($"[HeadlessPatches] UserCode_CmdSendNetworkAction: {actionType}");

                // Try to execute the action
                if (action != null)
                {
                    var executeMethod = action.GetType().GetMethod("Execute",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (executeMethod != null)
                    {
                        try
                        {
                            Plugin.Log.LogInfo($"[HeadlessPatches] Executing action via UserCode: {actionType}");
                            executeMethod.Invoke(action, null);
                        }
                        catch (Exception execEx)
                        {
                            Plugin.Log.LogWarning($"[HeadlessPatches] Action execute failed: {execEx.Message}");
                        }
                    }
                }
                return true; // Allow original to continue (for RPC relay to clients)
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[HeadlessPatches] UserCode_CmdSendNetworkAction error: {ex.Message}");
                return true;
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
                Plugin.Log.LogInfo($"[HeadlessPatches] RequestCurrentSimTick from conn={sender?.connectionId}, tick={_currentSimTick}");

                // Try to find the ProcessCurrentSimTick method to respond to client
                var relayType = __instance.GetType();
                var processMethod = relayType.GetMethod("ProcessCurrentSimTick",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (processMethod != null && sender != null)
                {
                    // Call ProcessCurrentSimTick(sender, tick) to send response to client
                    processMethod.Invoke(__instance, new object[] { sender, (int)_currentSimTick });
                    Plugin.Log.LogInfo($"[HeadlessPatches] Sent ProcessCurrentSimTick({_currentSimTick}) to connection {sender.connectionId}");
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

        public static void FlowManager_Update_Postfix()
        {
            if (_firstFlowManagerUpdateCall)
            {
                _firstFlowManagerUpdateCall = false;
                Plugin.DebugLog("[AutoStartPatches] FlowManager.Update postfix FIRST CALL!");
            }
            CheckAutoLoadTrigger("FlowManager.Update");
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
