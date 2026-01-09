using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using UnityEngine;

namespace TechtonicaDirectConnect
{
    /// <summary>
    /// Client-side patches to handle inventory synchronization when connected to a headless server.
    ///
    /// The headless server broadcasts actions but can't calculate inventory changes (no game state).
    /// These patches make the client handle inventory updates locally using the "trust the client" model.
    /// </summary>
    public static class InventorySyncPatches
    {
        private static ManualLogSource Log => Plugin.Log;
        private static bool _patchesApplied = false;

        public static void ApplyPatches(Harmony harmony)
        {
            if (_patchesApplied) return;

            try
            {
                // Patch TakeAllInfo.ProcessOnClient to add items to local inventory
                var takeAllProcessOnClient = AccessTools.Method("TakeAllInfo:ProcessOnClient");
                if (takeAllProcessOnClient != null)
                {
                    var prefix = new HarmonyMethod(typeof(InventorySyncPatches), nameof(TakeAllInfo_ProcessOnClient_Prefix));
                    harmony.Patch(takeAllProcessOnClient, prefix: prefix);
                    Log.LogInfo("[InventorySync] Patched TakeAllInfo.ProcessOnClient");
                }

                // Patch HitDestructibleInfo.ProcessOnClient to add drops to inventory
                var hitDestructibleProcessOnClient = AccessTools.Method("HitDestructibleInfo:ProcessOnClient");
                if (hitDestructibleProcessOnClient != null)
                {
                    var prefix = new HarmonyMethod(typeof(InventorySyncPatches), nameof(HitDestructibleInfo_ProcessOnClient_Prefix));
                    harmony.Patch(hitDestructibleProcessOnClient, prefix: prefix);
                    Log.LogInfo("[InventorySync] Patched HitDestructibleInfo.ProcessOnClient");
                }

                // Patch MOLEActionInfo.ProcessOnClient - use PREFIX to set canMineOre=true
                // The server can't determine ore presence (no terrain data), but the client can
                var moleProcessOnClient = AccessTools.Method("MOLEActionInfo:ProcessOnClient");
                if (moleProcessOnClient != null)
                {
                    var prefix = new HarmonyMethod(typeof(InventorySyncPatches), nameof(MOLEActionInfo_ProcessOnClient_Prefix));
                    harmony.Patch(moleProcessOnClient, prefix: prefix);
                    Log.LogInfo("[InventorySync] Patched MOLEActionInfo.ProcessOnClient (prefix)");
                }

                // Patch CraftActionInfo.ProcessOnClient to execute crafts locally
                // Try CraftActionInfo first (follows pattern of MOLEActionInfo, etc.)
                var craftInfoProcessOnClient = AccessTools.Method("CraftActionInfo:ProcessOnClient");
                if (craftInfoProcessOnClient == null)
                {
                    // Fallback to CraftInfo
                    craftInfoProcessOnClient = AccessTools.Method("CraftInfo:ProcessOnClient");
                }
                if (craftInfoProcessOnClient != null)
                {
                    var prefix = new HarmonyMethod(typeof(InventorySyncPatches), nameof(CraftInfo_ProcessOnClient_Prefix));
                    harmony.Patch(craftInfoProcessOnClient, prefix: prefix);
                    Log.LogInfo($"[InventorySync] Patched {craftInfoProcessOnClient.DeclaringType.Name}.ProcessOnClient");
                }
                else
                {
                    Log.LogWarning("[InventorySync] CraftActionInfo/CraftInfo.ProcessOnClient not found");
                }

                // Patch for building - intercept SimpleBuildAction
                // Try SimpleBuildActionInfo first (follows pattern of MOLEActionInfo, etc.)
                var simpleBuildProcessOnClient = AccessTools.Method("SimpleBuildActionInfo:ProcessOnClient");
                if (simpleBuildProcessOnClient == null)
                {
                    simpleBuildProcessOnClient = AccessTools.Method("SimpleBuildInfo:ProcessOnClient");
                }
                if (simpleBuildProcessOnClient != null)
                {
                    var postfix = new HarmonyMethod(typeof(InventorySyncPatches), nameof(SimpleBuildInfo_ProcessOnClient_Postfix));
                    harmony.Patch(simpleBuildProcessOnClient, postfix: postfix);
                    Log.LogInfo($"[InventorySync] Patched {simpleBuildProcessOnClient.DeclaringType.Name}.ProcessOnClient");
                }
                else
                {
                    Log.LogWarning("[InventorySync] SimpleBuildActionInfo/SimpleBuildInfo.ProcessOnClient not found");
                }

                // Patch for single item exchange - intercept ExchangeMachineInfo (clicking single items in storage)
                // Try ExchangeMachineActionInfo first (follows pattern of MOLEActionInfo, etc.)
                var exchangeMachineProcessOnClient = AccessTools.Method("ExchangeMachineActionInfo:ProcessOnClient");
                if (exchangeMachineProcessOnClient == null)
                {
                    exchangeMachineProcessOnClient = AccessTools.Method("ExchangeMachineInfo:ProcessOnClient");
                }
                if (exchangeMachineProcessOnClient != null)
                {
                    var postfix = new HarmonyMethod(typeof(InventorySyncPatches), nameof(ExchangeMachineInfo_ProcessOnClient_Postfix));
                    harmony.Patch(exchangeMachineProcessOnClient, postfix: postfix);
                    Log.LogInfo($"[InventorySync] Patched {exchangeMachineProcessOnClient.DeclaringType.Name}.ProcessOnClient");
                }
                else
                {
                    Log.LogWarning("[InventorySync] ExchangeMachineActionInfo/ExchangeMachineInfo.ProcessOnClient not found");
                }

                _patchesApplied = true;
                Log.LogInfo("[InventorySync] All inventory sync patches applied successfully");
            }
            catch (Exception ex)
            {
                Log.LogError($"[InventorySync] Error applying patches: {ex.Message}");
            }
        }

        /// <summary>
        /// REPLACE TakeAllInfo.ProcessOnClient - we call TakeAll() and ALSO add items to inventory.
        /// The original method calls TakeAll() which returns items but discards them.
        /// </summary>
        public static bool TakeAllInfo_ProcessOnClient_Prefix(object __instance)
        {
            Log.LogInfo($"[InventorySync] TakeAllInfo_ProcessOnClient_Prefix CALLED! instance={__instance?.GetType()?.Name}");

            try
            {
                // Only intercept if we're a client connected to a remote server (not host)
                if (!NetworkClient.active || NetworkServer.active)
                {
                    Log.LogInfo($"[InventorySync] TakeAll: Skipping - NetworkClient.active={NetworkClient.active}, NetworkServer.active={NetworkServer.active}");
                    return true;
                }

                // Get the TakeAll method and call it ourselves
                var takeAllMethod = __instance.GetType().GetMethod("TakeAll");
                if (takeAllMethod == null)
                {
                    Log.LogWarning("[InventorySync] TakeAll: TakeAll method not found!");
                    return true;
                }

                // Call TakeAll() - this empties machines and returns the items
                Log.LogInfo("[InventorySync] TakeAll: Calling TakeAll() method...");
                var itemsObj = takeAllMethod.Invoke(__instance, null);
                Log.LogInfo($"[InventorySync] TakeAll: TakeAll() returned: {itemsObj?.GetType()?.Name ?? "null"}");
                var items = itemsObj as System.Collections.IList;

                if (items == null || items.Count == 0)
                {
                    Log.LogInfo($"[InventorySync] TakeAll: No items returned (items={items != null}, count={items?.Count ?? 0})");
                    return false; // Skip original, we already called TakeAll
                }

                Log.LogInfo($"[InventorySync] TakeAll: Got {items.Count} item stacks to add");

                // Get Player - try multiple methods since multiplayer doesn't use Player.instance
                var playerType = AccessTools.TypeByName("Player");
                Log.LogInfo($"[InventorySync] TakeAll: playerType={playerType?.Name ?? "null"}");

                object playerInstance = null;

                // Method 1: Try Player.instance (works in single player)
                playerInstance = playerType?.GetField("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (playerInstance != null)
                {
                    Log.LogInfo("[InventorySync] TakeAll: Got player via Player.instance");
                }

                // Method 2: Try NetworkClient.localPlayer.GetComponent<Player>()
                if (playerInstance == null && NetworkClient.localPlayer != null)
                {
                    var localPlayerGO = NetworkClient.localPlayer.gameObject;
                    playerInstance = localPlayerGO.GetComponent(playerType);
                    if (playerInstance != null)
                    {
                        Log.LogInfo("[InventorySync] TakeAll: Got player via NetworkClient.localPlayer");
                    }
                }

                // Method 3: Try FindObjectOfType
                if (playerInstance == null)
                {
                    playerInstance = UnityEngine.Object.FindObjectOfType(playerType);
                    if (playerInstance != null)
                    {
                        Log.LogInfo("[InventorySync] TakeAll: Got player via FindObjectOfType");
                    }
                }

                if (playerInstance == null)
                {
                    Log.LogWarning("[InventorySync] TakeAll: Could not find Player instance via any method!");
                    return false;
                }

                var inventory = playerType.GetProperty("inventory")?.GetValue(playerInstance);
                if (inventory == null)
                {
                    Log.LogWarning("[InventorySync] TakeAll: Player.inventory is NULL!");
                    return false;
                }
                Log.LogInfo($"[InventorySync] TakeAll: Got inventory of type {inventory.GetType().Name}");

                // Get the AddResources method on inventory
                var addResourcesMethod = inventory.GetType().GetMethod("AddResources",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { AccessTools.TypeByName("ResourceInfo"), typeof(int) },
                    null);

                if (addResourcesMethod == null)
                {
                    // Try without specific parameter types
                    addResourcesMethod = inventory.GetType().GetMethod("AddResources", BindingFlags.Public | BindingFlags.Instance);
                    Log.LogInfo($"[InventorySync] TakeAll: AddResources fallback lookup = {addResourcesMethod != null}");
                }
                Log.LogInfo($"[InventorySync] TakeAll: AddResources method = {addResourcesMethod?.Name ?? "null"}");

                int itemsAdded = 0;

                // Add each item stack to inventory
                foreach (var item in items)
                {
                    // ResourceStack has .info (ResourceInfo PROPERTY) and .count (int field)
                    // Note: info is a PROPERTY that calls SaveState.GetResInfoFromId(id), NOT a field!
                    var infoProp = item.GetType().GetProperty("info");
                    var countField = item.GetType().GetField("count");

                    var resourceInfo = infoProp?.GetValue(item);
                    var count = countField?.GetValue(item);

                    Log.LogInfo($"[InventorySync] TakeAll: Item - info={resourceInfo != null}, count={count}, infoType={resourceInfo?.GetType()?.Name ?? "null"}");

                    if (resourceInfo != null && count != null)
                    {
                        try
                        {
                            addResourcesMethod?.Invoke(inventory, new object[] { resourceInfo, count });
                            itemsAdded++;
                            Log.LogInfo($"[InventorySync] TakeAll: Added item #{itemsAdded}");
                        }
                        catch (Exception ex)
                        {
                            Log.LogWarning($"[InventorySync] Failed to add resource: {ex.Message}");
                        }
                    }
                }

                Log.LogInfo($"[InventorySync] TakeAll: Added {itemsAdded} item stacks to inventory");

                return false; // Skip original, we handled it
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[InventorySync] TakeAllInfo_Prefix error: {ex.Message}");
                return true; // Let original run on error
            }
        }

        /// <summary>
        /// Before HitDestructibleInfo.ProcessOnClient runs, we'll capture what we're about to destroy
        /// and add any rewards to inventory.
        /// </summary>
        public static void HitDestructibleInfo_ProcessOnClient_Prefix(object __instance)
        {
            Log.LogInfo($"[InventorySync] HitDestructibleInfo_ProcessOnClient_Prefix CALLED! instance={__instance?.GetType()?.Name}");

            try
            {
                // Only do this if we're a client connected to a remote server
                if (!NetworkClient.active || NetworkServer.active)
                {
                    Log.LogInfo($"[InventorySync] HitDestructible: Skipping - NetworkClient.active={NetworkClient.active}, NetworkServer.active={NetworkServer.active}");
                    return;
                }

                // Get propLookups from the info
                var propLookupsField = __instance.GetType().GetField("propLookups");
                if (propLookupsField == null)
                {
                    Log.LogWarning("[InventorySync] HitDestructible: propLookups field not found!");
                    return;
                }
                var propLookups = propLookupsField.GetValue(__instance) as Array;
                if (propLookups == null || propLookups.Length == 0)
                {
                    Log.LogInfo($"[InventorySync] HitDestructible: No propLookups (null={propLookups == null})");
                    return;
                }
                Log.LogInfo($"[InventorySync] HitDestructible: Processing {propLookups.Length} prop lookups");

                // Get PropManager.instance
                var pmType = AccessTools.TypeByName("PropManager");
                var pmInstance = pmType?.GetField("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (pmInstance == null)
                {
                    Log.LogWarning("[InventorySync] HitDestructible: PropManager.instance is NULL!");
                    return;
                }

                // Get Player - try multiple methods since multiplayer doesn't use Player.instance
                var playerType = AccessTools.TypeByName("Player");
                object playerInstance = null;

                // Method 1: Try Player.instance
                playerInstance = playerType?.GetField("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

                // Method 2: Try NetworkClient.localPlayer
                if (playerInstance == null && NetworkClient.localPlayer != null)
                {
                    playerInstance = NetworkClient.localPlayer.gameObject.GetComponent(playerType);
                }

                // Method 3: Try FindObjectOfType
                if (playerInstance == null)
                {
                    playerInstance = UnityEngine.Object.FindObjectOfType(playerType);
                }

                if (playerInstance == null)
                {
                    Log.LogWarning("[InventorySync] HitDestructible: Could not find Player!");
                    return;
                }
                Log.LogInfo("[InventorySync] HitDestructible: Got player instance");

                var inventory = playerType.GetProperty("inventory")?.GetValue(playerInstance);
                if (inventory == null)
                {
                    Log.LogWarning("[InventorySync] HitDestructible: Player.inventory is NULL!");
                    return;
                }

                // Get methods
                var getDestructibleDataMethod = pmType.GetMethod("GetDestructibleData");

                int itemsAdded = 0;

                // Process each prop lookup
                for (int i = 0; i < propLookups.Length; i++)
                {
                    var lookup = propLookups.GetValue(i);

                    // Try to get destructible data
                    try
                    {
                        // GetDestructibleData(in InstanceLookup lookup, out DestructibleData data)
                        var parameters = new object[] { lookup, null };
                        var result = getDestructibleDataMethod?.Invoke(pmInstance, parameters);

                        if (result is bool success && success)
                        {
                            var destructibleData = parameters[1];
                            if (destructibleData != null)
                            {
                                // Get rewards
                                var rewardsField = destructibleData.GetType().GetField("rewards");
                                var rewards = rewardsField?.GetValue(destructibleData) as System.Collections.IList;

                                if (rewards != null && rewards.Count > 0)
                                {
                                    foreach (var reward in rewards)
                                    {
                                        var resourceField = reward.GetType().GetField("resource");
                                        var quantityField = reward.GetType().GetField("quantity");

                                        var resource = resourceField?.GetValue(reward);
                                        var quantity = quantityField?.GetValue(reward);

                                        if (resource != null && quantity != null)
                                        {
                                            // Add to inventory - use the actual resource type for method lookup
                                            try
                                            {
                                                var addResourcesMethod = inventory.GetType().GetMethod("AddResources",
                                                    BindingFlags.Public | BindingFlags.Instance,
                                                    null,
                                                    new Type[] { resource.GetType(), typeof(int) },
                                                    null);

                                                if (addResourcesMethod != null)
                                                {
                                                    addResourcesMethod.Invoke(inventory, new object[] { resource, quantity });
                                                    itemsAdded++;
                                                }
                                                else
                                                {
                                                    Log.LogWarning($"[InventorySync] HitDestructible: AddResources method not found for type {resource.GetType().Name}");
                                                }
                                            }
                                            catch (Exception addEx)
                                            {
                                                Log.LogWarning($"[InventorySync] HitDestructible: AddResources failed: {addEx.Message}");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                if (itemsAdded > 0)
                {
                    Log.LogInfo($"[InventorySync] HitDestructible: Added {itemsAdded} item stacks to inventory");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[InventorySync] HitDestructibleInfo_Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Before MOLEActionInfo.ProcessOnClient runs, set canMineOre=true.
        /// The headless server sends canMineOre=false because it has no terrain data.
        /// The client has terrain data and will properly check for ore during ProcessOnClient.
        /// </summary>
        public static void MOLEActionInfo_ProcessOnClient_Prefix(object __instance)
        {
            try
            {
                // Only do this if we're a client connected to a remote server
                if (!NetworkClient.active || NetworkServer.active) return;

                // Get the current canMineOre value
                var canMineOreField = __instance.GetType().GetField("canMineOre");
                if (canMineOreField != null)
                {
                    bool originalValue = (bool)canMineOreField.GetValue(__instance);

                    // Set canMineOre to true so the client can check locally for ore
                    // The ProcessOnClient will use the client's local terrain data
                    canMineOreField.SetValue(__instance, true);

                    Log.LogInfo($"[InventorySync] MOLE: Set canMineOre={originalValue}->true (client will check locally)");
                }
                else
                {
                    Log.LogWarning("[InventorySync] MOLE: canMineOre field not found!");
                }

                // Also log the dig position for debugging
                var digPosField = __instance.GetType().GetField("digPos");
                if (digPosField != null)
                {
                    var digPos = digPosField.GetValue(__instance);
                    Log.LogInfo($"[InventorySync] MOLE: digPos={digPos}");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[InventorySync] MOLEActionInfo_Prefix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle CraftInfo.ProcessOnClient - execute crafts locally on client.
        /// </summary>
        public static bool CraftInfo_ProcessOnClient_Prefix(object __instance)
        {
            Log.LogInfo($"[InventorySync] CraftInfo_ProcessOnClient_Prefix CALLED! instance={__instance?.GetType()?.Name}");

            try
            {
                // Only intercept if we're a client connected to a remote server (not host)
                if (!NetworkClient.active || NetworkServer.active)
                {
                    Log.LogInfo($"[InventorySync] Craft: Skipping - not remote client");
                    return true;
                }

                // Log CraftInfo fields for debugging
                var fields = __instance.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance);
                var fieldInfo = string.Join(", ", Array.ConvertAll(fields, f => $"{f.Name}={f.GetValue(__instance)}"));
                Log.LogInfo($"[InventorySync] CraftInfo fields: {fieldInfo}");

                // Get Player instance
                var playerType = AccessTools.TypeByName("Player");
                object playerInstance = playerType?.GetField("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

                if (playerInstance == null && NetworkClient.localPlayer != null)
                {
                    playerInstance = NetworkClient.localPlayer.gameObject.GetComponent(playerType);
                }

                if (playerInstance == null)
                {
                    playerInstance = UnityEngine.Object.FindObjectOfType(playerType);
                }

                if (playerInstance == null)
                {
                    Log.LogWarning("[InventorySync] Craft: Could not find Player!");
                    return true;
                }

                // Get crafting component
                var craftingProp = playerType.GetProperty("crafting");
                var crafting = craftingProp?.GetValue(playerInstance);

                if (crafting == null)
                {
                    Log.LogWarning("[InventorySync] Craft: Player.crafting is null!");
                    return true;
                }

                // Try to get the recipe field from CraftInfo
                var recipeField = __instance.GetType().GetField("recipe");
                var recipe = recipeField?.GetValue(__instance);

                if (recipe != null)
                {
                    Log.LogInfo($"[InventorySync] Craft: Found recipe of type {recipe.GetType().Name}");

                    // Try to call crafting.Craft(recipe) or similar
                    var craftMethod = crafting.GetType().GetMethod("Craft", BindingFlags.Public | BindingFlags.Instance);
                    if (craftMethod != null)
                    {
                        Log.LogInfo("[InventorySync] Craft: Calling Craft method...");
                        try
                        {
                            craftMethod.Invoke(crafting, new object[] { recipe });
                            Log.LogInfo("[InventorySync] Craft: Craft method executed!");
                        }
                        catch (Exception craftEx)
                        {
                            Log.LogWarning($"[InventorySync] Craft method failed: {craftEx.InnerException?.Message ?? craftEx.Message}");
                        }
                    }
                    else
                    {
                        // Log available methods
                        var methods = crafting.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance);
                        var methodNames = string.Join(", ", Array.ConvertAll(methods, m => m.Name));
                        Log.LogInfo($"[InventorySync] Craft: Available methods: {methodNames}");
                    }
                }

                return true; // Let original run too
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[InventorySync] CraftInfo_Prefix error: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// After SimpleBuildInfo.ProcessOnClient runs, deduct build costs from inventory.
        /// </summary>
        public static void SimpleBuildInfo_ProcessOnClient_Postfix(object __instance)
        {
            try
            {
                // Only do this if we're a client connected to a remote server
                if (!NetworkClient.active || NetworkServer.active) return;

                Log.LogInfo("[InventorySync] SimpleBuildInfo postfix - build action processed");

                // Get the machine type from the build info
                var machineTypeField = __instance.GetType().GetField("machineType");
                if (machineTypeField == null) return;
                var machineType = machineTypeField.GetValue(__instance);

                // The build costs are defined in the machine's recipe
                // This needs to deduct those costs from Player.instance.inventory
                // Complex - requires understanding the recipe/cost system
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[InventorySync] SimpleBuildInfo_Postfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// After ExchangeMachineInfo.ProcessOnClient runs, add items to player inventory when taking from machines.
        /// ExchangeMachineInfo handles single item transfers (clicking items in storage).
        /// When addingToMachine=false (taking from machine), we need to add to player inventory.
        /// </summary>
        public static void ExchangeMachineInfo_ProcessOnClient_Postfix(object __instance)
        {
            try
            {
                // Only do this if we're a client connected to a remote server
                if (!NetworkClient.active || NetworkServer.active) return;

                // Get the fields from ExchangeMachineInfo
                var addingToMachineField = __instance.GetType().GetField("addingToMachine");
                var quantityField = __instance.GetType().GetField("quantity");
                var resIDField = __instance.GetType().GetField("resID");

                if (addingToMachineField == null || quantityField == null || resIDField == null)
                {
                    Log.LogWarning("[InventorySync] ExchangeMachine: Required fields not found");
                    return;
                }

                bool addingToMachine = (bool)addingToMachineField.GetValue(__instance);
                int quantity = (int)quantityField.GetValue(__instance);
                int resID = (int)resIDField.GetValue(__instance);

                // Only handle TAKING from machine (addingToMachine = false)
                if (addingToMachine)
                {
                    // This is player -> machine, the original handles this
                    return;
                }

                Log.LogInfo($"[InventorySync] ExchangeMachine: Taking {quantity} of resID {resID} from machine");

                // Get ResourceInfo from resID
                var saveStateType = AccessTools.TypeByName("SaveState");
                var getResInfoMethod = saveStateType?.GetMethod("GetResInfoFromId", BindingFlags.Public | BindingFlags.Static);
                if (getResInfoMethod == null)
                {
                    Log.LogWarning("[InventorySync] ExchangeMachine: GetResInfoFromId not found");
                    return;
                }

                var resourceInfo = getResInfoMethod.Invoke(null, new object[] { resID });
                if (resourceInfo == null)
                {
                    Log.LogWarning($"[InventorySync] ExchangeMachine: No ResourceInfo for resID {resID}");
                    return;
                }

                // Get Player inventory
                var playerType = AccessTools.TypeByName("Player");
                object playerInstance = null;

                // Try Player.instance
                playerInstance = playerType?.GetField("instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

                // Try NetworkClient.localPlayer
                if (playerInstance == null && NetworkClient.localPlayer != null)
                {
                    playerInstance = NetworkClient.localPlayer.gameObject.GetComponent(playerType);
                }

                // Try FindObjectOfType
                if (playerInstance == null)
                {
                    playerInstance = UnityEngine.Object.FindObjectOfType(playerType);
                }

                if (playerInstance == null)
                {
                    Log.LogWarning("[InventorySync] ExchangeMachine: Could not find Player!");
                    return;
                }

                var inventory = playerType.GetProperty("inventory")?.GetValue(playerInstance);
                if (inventory == null)
                {
                    Log.LogWarning("[InventorySync] ExchangeMachine: Player.inventory is null!");
                    return;
                }

                // Add to player inventory
                var addResourcesMethod = inventory.GetType().GetMethod("AddResources",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new Type[] { resourceInfo.GetType(), typeof(int) },
                    null);

                if (addResourcesMethod != null)
                {
                    addResourcesMethod.Invoke(inventory, new object[] { resourceInfo, quantity });
                    Log.LogInfo($"[InventorySync] ExchangeMachine: Added {quantity} items to inventory");
                }
                else
                {
                    Log.LogWarning($"[InventorySync] ExchangeMachine: AddResources method not found for type {resourceInfo.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[InventorySync] ExchangeMachineInfo_Postfix error: {ex.Message}");
            }
        }
    }
}
