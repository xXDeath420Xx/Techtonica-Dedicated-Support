using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace TechtonicaPreloader
{
    /// <summary>
    /// BepInEx preloader patcher that modifies Assembly-CSharp.dll before it loads.
    /// This patches the SteamPlatform constructor to skip Steam initialization.
    /// </summary>
    public static class SteamBypassPatcher
    {
        // List of assemblies to patch
        public static IEnumerable<string> TargetDLLs { get; } = new[] { "Assembly-CSharp.dll" };

        // Check if we should enable the bypass (read from config file)
        private static bool ShouldBypassSteam()
        {
            try
            {
                // Look for the config file
                var configPath = Path.Combine(
                    Path.GetDirectoryName(typeof(SteamBypassPatcher).Assembly.Location),
                    "..", "..", "config", "com.community.techtonicadedicatedserver.cfg"
                );

                if (!File.Exists(configPath))
                {
                    // Try alternate path
                    configPath = Path.Combine(
                        Path.GetDirectoryName(typeof(SteamBypassPatcher).Assembly.Location),
                        "..", "config", "com.community.techtonicadedicatedserver.cfg"
                    );
                }

                if (File.Exists(configPath))
                {
                    var content = File.ReadAllText(configPath);
                    // Check if AutoStartServer = true and EnableDirectConnect = true
                    bool autoStart = content.Contains("AutoStartServer = true") ||
                                    content.Contains("AutoStartServer=true");
                    bool directConnect = content.Contains("EnableDirectConnect = true") ||
                                        content.Contains("EnableDirectConnect=true");

                    Console.WriteLine($"[SteamBypassPatcher] Config found. AutoStart={autoStart}, DirectConnect={directConnect}");
                    return autoStart && directConnect;
                }

                Console.WriteLine("[SteamBypassPatcher] Config not found, defaulting to enabled");
                return true; // Default to enabled for server mode
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] Error reading config: {ex.Message}");
                return true; // Default to enabled
            }
        }

        // Called by BepInEx to patch the assembly
        public static void Patch(AssemblyDefinition assembly)
        {
            Console.WriteLine("[SteamBypassPatcher] Patching Assembly-CSharp...");

            if (!ShouldBypassSteam())
            {
                Console.WriteLine("[SteamBypassPatcher] Steam bypass disabled by config");
                return;
            }

            try
            {
                var module = assembly.MainModule;

                // Find SteamPlatform type
                var steamPlatformType = module.Types.FirstOrDefault(t => t.Name == "SteamPlatform");
                if (steamPlatformType == null)
                {
                    Console.WriteLine("[SteamBypassPatcher] SteamPlatform type not found!");
                    return;
                }

                Console.WriteLine($"[SteamBypassPatcher] Found SteamPlatform type: {steamPlatformType.FullName}");
                Console.WriteLine($"[SteamBypassPatcher] Base type: {steamPlatformType.BaseType?.FullName ?? "null"}");

                // Find constructor with uint parameter
                var ctor = steamPlatformType.Methods.FirstOrDefault(m =>
                    m.IsConstructor &&
                    m.Parameters.Count == 1 &&
                    m.Parameters[0].ParameterType.Name == "UInt32");

                if (ctor == null)
                {
                    Console.WriteLine("[SteamBypassPatcher] SteamPlatform(uint) constructor not found!");
                    // List available constructors
                    foreach (var c in steamPlatformType.Methods.Where(m => m.IsConstructor))
                    {
                        var parms = string.Join(", ", c.Parameters.Select(p => p.ParameterType.Name));
                        Console.WriteLine($"[SteamBypassPatcher] Found constructor: ({parms})");
                    }
                    return;
                }

                Console.WriteLine($"[SteamBypassPatcher] Found constructor with {ctor.Body.Instructions.Count} instructions");
                Console.WriteLine("[SteamBypassPatcher] Patching...");

                // Get the IL processor
                var il = ctor.Body.GetILProcessor();

                // Clear existing instructions and exception handlers
                ctor.Body.Instructions.Clear();
                ctor.Body.ExceptionHandlers.Clear();
                ctor.Body.Variables.Clear();

                Console.WriteLine("[SteamBypassPatcher] Cleared existing IL");

                // Find base constructor - try to resolve base type
                var baseType = steamPlatformType.BaseType;
                MethodReference baseCtor = null;

                if (baseType != null)
                {
                    Console.WriteLine($"[SteamBypassPatcher] Looking for base constructor in {baseType.FullName}");

                    var resolvedBaseType = baseType.Resolve();
                    if (resolvedBaseType != null)
                    {
                        var baseCtorDef = resolvedBaseType.Methods.FirstOrDefault(m =>
                            m.IsConstructor && m.Parameters.Count == 0);

                        if (baseCtorDef != null)
                        {
                            baseCtor = module.ImportReference(baseCtorDef);
                            Console.WriteLine($"[SteamBypassPatcher] Found base constructor: {baseCtor.FullName}");
                        }
                    }
                }

                // Add base constructor call
                il.Append(il.Create(OpCodes.Ldarg_0)); // this

                if (baseCtor != null)
                {
                    il.Append(il.Create(OpCodes.Call, baseCtor));
                }
                else
                {
                    // Fallback to System.Object constructor
                    Console.WriteLine("[SteamBypassPatcher] WARNING: Using Object constructor as fallback");
                    var objectCtor = module.ImportReference(typeof(object).GetConstructor(Type.EmptyTypes));
                    il.Append(il.Create(OpCodes.Call, objectCtor));
                }

                // Add debug log to show the patched constructor runs
                // Find Debug.Log method
                try
                {
                    var debugType = module.Types.FirstOrDefault(t => t.Name == "Debug") ??
                                   module.GetTypeReferences().FirstOrDefault(t => t.Name == "Debug");
                    if (debugType != null)
                    {
                        var resolvedDebug = debugType.Resolve() ??
                            module.AssemblyResolver.Resolve(new AssemblyNameReference("UnityEngine.CoreModule", null))
                                ?.MainModule.Types.FirstOrDefault(t => t.Name == "Debug");

                        if (resolvedDebug != null)
                        {
                            var logMethod = resolvedDebug.Methods.FirstOrDefault(m =>
                                m.Name == "Log" && m.Parameters.Count == 1 &&
                                m.Parameters[0].ParameterType.FullName == "System.Object");

                            if (logMethod != null)
                            {
                                il.Append(il.Create(OpCodes.Ldstr, "[SteamBypassPatcher] PATCHED SteamPlatform constructor running - Steam skipped!"));
                                il.Append(il.Create(OpCodes.Call, module.ImportReference(logMethod)));
                            }
                        }
                    }
                }
                catch (Exception debugEx)
                {
                    Console.WriteLine($"[SteamBypassPatcher] Could not add debug log: {debugEx.Message}");
                }

                // Return
                il.Append(il.Create(OpCodes.Ret));

                Console.WriteLine($"[SteamBypassPatcher] New constructor has {ctor.Body.Instructions.Count} instructions");
                foreach (var instr in ctor.Body.Instructions)
                {
                    Console.WriteLine($"[SteamBypassPatcher]   {instr.OpCode} {instr.Operand}");
                }

                Console.WriteLine("[SteamBypassPatcher] SteamPlatform constructor patched - Steam initialization SKIPPED");

                // Patch IsClientValid to return true
                PatchIsClientValid(steamPlatformType, module);

                // Also patch any "IsValid" or "IsInitialized" property to return true
                PatchIsValidProperty(steamPlatformType, module);

                // Patch the quit check in initialization
                PatchQuitOnSteamFail(module);

                // GHOST HOST MODE: Do NOT patch NetworkedPlayer or SaveState!
                // Since we're loading the game properly, these will work normally.
                // The server will have the full game state and can respond to save data requests.
                Console.WriteLine("[SteamBypassPatcher] GHOST HOST MODE: NetworkedPlayer and SaveState will work normally!");
                // PatchNetworkedPlayerSaveData(module); // DISABLED for ghost host
                // PatchSaveStateMethods(module); // DISABLED for ghost host

                // CRITICAL: Patch TVEGlobalVolume to prevent graphics errors
                PatchTVEGlobalVolume(module);

                // GHOST HOST MODE: Do NOT skip scene loading!
                // Let the server load into the game world properly like a normal player.
                // The server will act as a "ghost host" - a real player in the world.
                // This allows the game state (GameLogic, MachineManager, etc.) to initialize properly.
                // Clients can then connect and interact with the world normally.
                Console.WriteLine("[SteamBypassPatcher] GHOST HOST MODE: Scene loading will proceed normally!");
                Console.WriteLine("[SteamBypassPatcher] Server will load into game world as a proper host player.");
                // PatchFlowManagerSceneLoading(module); // DISABLED - let scenes load!
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] Error patching: {ex}");
            }
        }

        /// <summary>
        /// Patch FlowManager's LoadScreenCoroutine to use synchronous scene loading.
        /// Unity's async scene loading doesn't complete properly in Wine headless mode.
        /// This patch replaces the entire MoveNext with one that loads scenes synchronously.
        /// </summary>
        private static void PatchFlowManagerSceneLoading(ModuleDefinition module)
        {
            try
            {
                var flowManagerType = module.Types.FirstOrDefault(t => t.Name == "FlowManager");
                if (flowManagerType == null)
                {
                    Console.WriteLine("[SteamBypassPatcher] FlowManager type not found");
                    return;
                }

                Console.WriteLine("[SteamBypassPatcher] Found FlowManager type");

                // Find the LoadScreenCoroutine state machine
                // It's compiled as a nested type like <LoadScreenCoroutine>d__XX
                var coroutineType = flowManagerType.NestedTypes.FirstOrDefault(t =>
                    t.Name.Contains("LoadScreenCoroutine"));

                if (coroutineType == null)
                {
                    Console.WriteLine("[SteamBypassPatcher] LoadScreenCoroutine state machine not found");
                    // List nested types for debugging
                    foreach (var nested in flowManagerType.NestedTypes)
                    {
                        Console.WriteLine($"[SteamBypassPatcher]   Nested type: {nested.Name}");
                    }
                    return;
                }

                Console.WriteLine($"[SteamBypassPatcher] Found coroutine type: {coroutineType.Name}");

                var moveNextMethod = coroutineType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
                if (moveNextMethod == null)
                {
                    Console.WriteLine("[SteamBypassPatcher] MoveNext method not found");
                    return;
                }

                Console.WriteLine($"[SteamBypassPatcher] Found MoveNext with {moveNextMethod.Body.Instructions.Count} instructions");

                // First, analyze what scenes are being loaded and log them
                AnalyzeSceneLoading(moveNextMethod, module);

                // Now patch: Replace entire MoveNext to load scenes synchronously
                PatchMoveNextForSyncLoading(moveNextMethod, coroutineType, module);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] FlowManager patch error: {ex}");
            }
        }

        /// <summary>
        /// Analyze the MoveNext to understand what scenes are being loaded
        /// </summary>
        private static void AnalyzeSceneLoading(MethodDefinition method, ModuleDefinition module)
        {
            try
            {
                var instructions = method.Body.Instructions;

                Console.WriteLine("[SteamBypassPatcher] Analyzing LoadScreenCoroutine scene loading:");

                // Find string constants that look like scene names
                foreach (var instr in instructions)
                {
                    if (instr.OpCode == OpCodes.Ldstr)
                    {
                        var str = instr.Operand as string;
                        if (str != null && (str.Contains("Scene") || str.Contains("Loading") || str.Contains("Player") || str.Contains("Menu")))
                        {
                            Console.WriteLine($"[SteamBypassPatcher]   Scene string: \"{str}\"");
                        }
                    }
                }

                // Find calls to LoadSceneAsync
                int asyncCallCount = 0;
                foreach (var instr in instructions)
                {
                    if (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt)
                    {
                        var methodRef = instr.Operand as MethodReference;
                        if (methodRef != null)
                        {
                            if (methodRef.Name == "LoadSceneAsync")
                            {
                                Console.WriteLine($"[SteamBypassPatcher]   Found LoadSceneAsync: {methodRef.FullName}");
                                asyncCallCount++;
                            }
                            else if (methodRef.Name == "LoadScene")
                            {
                                Console.WriteLine($"[SteamBypassPatcher]   Found LoadScene: {methodRef.FullName}");
                            }
                        }
                    }
                }

                Console.WriteLine($"[SteamBypassPatcher] Total LoadSceneAsync calls: {asyncCallCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] Analyze error: {ex.Message}");
            }
        }

        /// <summary>
        /// Replace MoveNext with a version that loads scenes synchronously.
        /// The original coroutine waits for async operations that never complete in Wine.
        /// Our version will:
        /// 1. Load "Loading" scene synchronously
        /// 2. Load "Player Scene" synchronously
        /// 3. Call the completion callback
        /// 4. Return false (coroutine done)
        /// </summary>
        private static void PatchMoveNextForSyncLoading(MethodDefinition moveNext, TypeDefinition coroutineType, ModuleDefinition module)
        {
            try
            {
                Console.WriteLine("[SteamBypassPatcher] Patching MoveNext for synchronous scene loading...");

                // Find the fields we need from the coroutine state machine
                // The coroutine stores: <>4__this (FlowManager instance), sceneName, callback, etc.
                FieldDefinition sceneNameField = null;
                FieldDefinition callbackField = null;
                FieldDefinition thisField = null;
                FieldDefinition stateField = null;

                foreach (var field in coroutineType.Fields)
                {
                    Console.WriteLine($"[SteamBypassPatcher]   Coroutine field: {field.Name} : {field.FieldType.Name}");

                    if (field.Name.Contains("sceneName") || (field.FieldType.FullName == "System.String" && !field.Name.Contains("state")))
                    {
                        sceneNameField = field;
                    }
                    if (field.Name.Contains("callback") || field.FieldType.Name == "Action")
                    {
                        callbackField = field;
                    }
                    if (field.Name.Contains("this") || field.FieldType.Name == "FlowManager")
                    {
                        thisField = field;
                    }
                    if (field.Name.Contains("state") && field.FieldType.FullName == "System.Int32")
                    {
                        stateField = field;
                    }
                }

                // HEADLESS MODE: We don't need to load scenes, just skip and call callback
                // Scene loading doesn't work in Wine/headless mode anyway
                Console.WriteLine("[SteamBypassPatcher] HEADLESS MODE: Will skip scene loading in LoadScreenCoroutine");

                // Clear the existing method body
                var il = moveNext.Body.GetILProcessor();
                moveNext.Body.Instructions.Clear();
                moveNext.Body.ExceptionHandlers.Clear();
                moveNext.Body.Variables.Clear();

                // Add a local for the state check
                moveNext.Body.Variables.Add(new VariableDefinition(module.TypeSystem.Int32));

                // Build new method body:
                // if (state != 0) return false; // Already ran
                // state = -1;
                // SceneManager.LoadScene("Loading");
                // SceneManager.LoadScene("Player Scene");
                // Time.timeScale = 1; // Important!
                // if (callback != null) callback();
                // return false;

                var returnFalseInstr = il.Create(OpCodes.Ldc_I4_0);

                // Check state field
                if (stateField != null)
                {
                    // if (this.<>1__state != 0) return false
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldfld, stateField));
                    il.Append(il.Create(OpCodes.Brtrue, returnFalseInstr));

                    // this.<>1__state = -1
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldc_I4_M1));
                    il.Append(il.Create(OpCodes.Stfld, stateField));
                }

                // Log that we're using patched loading
                // HEADLESS SERVER MODE: Skip scene loading entirely
                // Unity scene loading doesn't work properly in Wine headless mode
                // The server doesn't need scenes loaded - it just relays save data to clients
                AddDebugLog(il, module, "[PRELOADER] LoadScreenCoroutine PATCHED - HEADLESS MODE: Skipping scene loading!");

                // Don't try to load scenes - they fail in Wine with isLoaded=False
                // Just proceed directly to callback so the server can start
                AddDebugLog(il, module, "[PRELOADER] Skipping scene loading (not needed for dedicated server)");

                // Set Time.timeScale = 1f
                try
                {
                    MethodReference timeScaleSetter = null;
                    foreach (var asm in module.AssemblyReferences)
                    {
                        try
                        {
                            var resolvedAsm = module.AssemblyResolver.Resolve(asm);
                            if (resolvedAsm != null)
                            {
                                var timeType = resolvedAsm.MainModule.Types
                                    .FirstOrDefault(t => t.Name == "Time" && t.Namespace == "UnityEngine");
                                if (timeType != null)
                                {
                                    var prop = timeType.Properties.FirstOrDefault(p => p.Name == "timeScale");
                                    if (prop != null && prop.SetMethod != null)
                                    {
                                        timeScaleSetter = module.ImportReference(prop.SetMethod);
                                        break;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (timeScaleSetter != null)
                    {
                        il.Append(il.Create(OpCodes.Ldc_R4, 1.0f));
                        il.Append(il.Create(OpCodes.Call, timeScaleSetter));
                        Console.WriteLine("[SteamBypassPatcher] Added Time.timeScale = 1f");
                    }
                }
                catch (Exception timeEx)
                {
                    Console.WriteLine($"[SteamBypassPatcher] Could not add Time.timeScale setter: {timeEx.Message}");
                }

                // Call callback if not null
                if (callbackField != null)
                {
                    var skipCallbackLabel = il.Create(OpCodes.Nop);

                    // if (callback != null)
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldfld, callbackField));
                    il.Append(il.Create(OpCodes.Brfalse, skipCallbackLabel));

                    // callback.Invoke()
                    il.Append(il.Create(OpCodes.Ldarg_0));
                    il.Append(il.Create(OpCodes.Ldfld, callbackField));

                    // Find Action.Invoke
                    var actionType = callbackField.FieldType.Resolve();
                    if (actionType != null)
                    {
                        var invokeMethod = actionType.Methods.FirstOrDefault(m => m.Name == "Invoke");
                        if (invokeMethod != null)
                        {
                            il.Append(il.Create(OpCodes.Callvirt, module.ImportReference(invokeMethod)));
                            AddDebugLog(il, module, "[PRELOADER] Called load completion callback");
                        }
                    }

                    il.Append(skipCallbackLabel);
                }

                AddDebugLog(il, module, "[PRELOADER] LoadScreenCoroutine completed (sync)");

                // return false (coroutine finished)
                il.Append(returnFalseInstr);
                il.Append(il.Create(OpCodes.Ret));

                Console.WriteLine($"[SteamBypassPatcher] Patched MoveNext with {moveNext.Body.Instructions.Count} instructions");
                foreach (var instr in moveNext.Body.Instructions)
                {
                    Console.WriteLine($"[SteamBypassPatcher]   {instr.OpCode} {instr.Operand}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] MoveNext patch error: {ex}");
            }
        }

        /// <summary>
        /// Patch NetworkedPlayer.UserCode_RequestInitialSaveData to skip the problematic code.
        /// In headless mode, this method crashes because SaveAsString calls PrepSave which has null refs.
        /// We patch it to simply return immediately - the ServerConnectionHandler will send save data instead.
        /// </summary>
        private static void PatchNetworkedPlayerSaveData(ModuleDefinition module)
        {
            try
            {
                var networkedPlayerType = module.Types.FirstOrDefault(t => t.Name == "NetworkedPlayer");
                if (networkedPlayerType == null)
                {
                    Console.WriteLine("[SteamBypassPatcher] NetworkedPlayer type not found");
                    return;
                }

                Console.WriteLine("[SteamBypassPatcher] Found NetworkedPlayer type");

                // Find UserCode_RequestInitialSaveData
                var userCodeMethod = networkedPlayerType.Methods.FirstOrDefault(m =>
                    m.Name == "UserCode_RequestInitialSaveData");

                if (userCodeMethod != null)
                {
                    Console.WriteLine($"[SteamBypassPatcher] Found UserCode_RequestInitialSaveData with {userCodeMethod.Body.Instructions.Count} instructions");

                    // Replace the entire method body with just a return
                    // This prevents the crash - ServerConnectionHandler will send save data separately
                    var il = userCodeMethod.Body.GetILProcessor();
                    userCodeMethod.Body.Instructions.Clear();
                    userCodeMethod.Body.ExceptionHandlers.Clear();
                    userCodeMethod.Body.Variables.Clear();

                    // Log that we're skipping
                    AddDebugLog(il, module, "[PRELOADER] UserCode_RequestInitialSaveData SKIPPED - save data sent via ServerConnectionHandler");

                    // Just return immediately
                    il.Append(il.Create(OpCodes.Ret));

                    Console.WriteLine("[SteamBypassPatcher] Patched UserCode_RequestInitialSaveData to skip");
                }

                // Also patch SendSaveString coroutine's MoveNext
                var sendSaveStringType = networkedPlayerType.NestedTypes.FirstOrDefault(t =>
                    t.Name.Contains("SendSaveString"));

                if (sendSaveStringType != null)
                {
                    var moveNextMethod = sendSaveStringType.Methods.FirstOrDefault(m => m.Name == "MoveNext");
                    if (moveNextMethod != null)
                    {
                        Console.WriteLine($"[SteamBypassPatcher] Found SendSaveString.MoveNext with {moveNextMethod.Body.Instructions.Count} instructions");

                        var il = moveNextMethod.Body.GetILProcessor();
                        moveNextMethod.Body.Instructions.Clear();
                        moveNextMethod.Body.ExceptionHandlers.Clear();
                        moveNextMethod.Body.Variables.Clear();

                        // Return false (coroutine finished)
                        il.Append(il.Create(OpCodes.Ldc_I4_0));
                        il.Append(il.Create(OpCodes.Ret));

                        Console.WriteLine("[SteamBypassPatcher] Patched SendSaveString.MoveNext to return false");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] Error patching NetworkedPlayer: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch SaveState.PrepSave and SaveAsString to handle headless mode gracefully
        /// </summary>
        private static void PatchSaveStateMethods(ModuleDefinition module)
        {
            try
            {
                var saveStateType = module.Types.FirstOrDefault(t => t.Name == "SaveState");
                if (saveStateType == null)
                {
                    Console.WriteLine("[SteamBypassPatcher] SaveState type not found");
                    return;
                }

                Console.WriteLine("[SteamBypassPatcher] Found SaveState type");

                // Patch PrepSave to do nothing
                var prepSaveMethod = saveStateType.Methods.FirstOrDefault(m => m.Name == "PrepSave");
                if (prepSaveMethod != null)
                {
                    Console.WriteLine($"[SteamBypassPatcher] Found PrepSave with {prepSaveMethod.Body.Instructions.Count} instructions");

                    var il = prepSaveMethod.Body.GetILProcessor();
                    prepSaveMethod.Body.Instructions.Clear();
                    prepSaveMethod.Body.ExceptionHandlers.Clear();
                    prepSaveMethod.Body.Variables.Clear();

                    // Just return
                    il.Append(il.Create(OpCodes.Ret));

                    Console.WriteLine("[SteamBypassPatcher] Patched PrepSave to skip");
                }

                // Patch SaveAsString to return empty string (our plugin will use cached data)
                var saveAsStringMethod = saveStateType.Methods.FirstOrDefault(m => m.Name == "SaveAsString");
                if (saveAsStringMethod != null)
                {
                    Console.WriteLine($"[SteamBypassPatcher] Found SaveAsString with {saveAsStringMethod.Body.Instructions.Count} instructions");

                    var il = saveAsStringMethod.Body.GetILProcessor();
                    saveAsStringMethod.Body.Instructions.Clear();
                    saveAsStringMethod.Body.ExceptionHandlers.Clear();
                    saveAsStringMethod.Body.Variables.Clear();

                    // Return empty string - our plugin handles the real save data
                    il.Append(il.Create(OpCodes.Ldstr, ""));
                    il.Append(il.Create(OpCodes.Ret));

                    Console.WriteLine("[SteamBypassPatcher] Patched SaveAsString to return empty string");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] Error patching SaveState: {ex.Message}");
            }
        }

        /// <summary>
        /// Patch TVEGlobalVolume to skip all methods - prevents graphics errors in headless mode
        /// </summary>
        private static void PatchTVEGlobalVolume(ModuleDefinition module)
        {
            try
            {
                var tveType = module.Types.FirstOrDefault(t => t.Name == "TVEGlobalVolume");
                if (tveType == null)
                {
                    // Check nested namespaces
                    foreach (var type in module.Types)
                    {
                        if (type.HasNestedTypes)
                        {
                            tveType = type.NestedTypes.FirstOrDefault(t => t.Name == "TVEGlobalVolume");
                            if (tveType != null) break;
                        }
                    }
                }

                if (tveType == null)
                {
                    Console.WriteLine("[SteamBypassPatcher] TVEGlobalVolume type not found (may be in separate assembly)");
                    return;
                }

                Console.WriteLine("[SteamBypassPatcher] Found TVEGlobalVolume type");

                // Patch all methods to do nothing
                string[] methodsToSkip = { "Update", "LateUpdate", "Start", "Awake", "OnEnable",
                    "ExecuteRenderBuffers", "CreateRenderBuffers", "UpdateRenderBuffers", "CheckRenderBuffers" };

                foreach (var methodName in methodsToSkip)
                {
                    var method = tveType.Methods.FirstOrDefault(m => m.Name == methodName);
                    if (method != null)
                    {
                        var il = method.Body.GetILProcessor();
                        method.Body.Instructions.Clear();
                        method.Body.ExceptionHandlers.Clear();
                        method.Body.Variables.Clear();
                        il.Append(il.Create(OpCodes.Ret));
                        Console.WriteLine($"[SteamBypassPatcher] Patched TVEGlobalVolume.{methodName} to skip");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] Error patching TVEGlobalVolume: {ex.Message}");
            }
        }

        /// <summary>
        /// Helper to add Debug.Log call to IL
        /// </summary>
        private static void AddDebugLog(ILProcessor il, ModuleDefinition module, string message)
        {
            try
            {
                // Find Console.WriteLine as a simpler alternative
                var consoleType = module.TypeSystem.String.Resolve().Module.Types
                    .FirstOrDefault(t => t.FullName == "System.Console");

                if (consoleType != null)
                {
                    var writeLineMethod = consoleType.Methods.FirstOrDefault(m =>
                        m.Name == "WriteLine" && m.Parameters.Count == 1 &&
                        m.Parameters[0].ParameterType.FullName == "System.String");

                    if (writeLineMethod != null)
                    {
                        il.Append(il.Create(OpCodes.Ldstr, message));
                        il.Append(il.Create(OpCodes.Call, module.ImportReference(writeLineMethod)));
                    }
                }
            }
            catch
            {
                // Ignore logging errors
            }
        }

        private static void PatchIsClientValid(TypeDefinition steamPlatformType, ModuleDefinition module)
        {
            try
            {
                // Find IsClientValid method
                var isClientValid = steamPlatformType.Methods.FirstOrDefault(m => m.Name == "IsClientValid");
                if (isClientValid != null)
                {
                    var il = isClientValid.Body.GetILProcessor();
                    isClientValid.Body.Instructions.Clear();
                    isClientValid.Body.ExceptionHandlers.Clear();

                    // Return true always
                    il.Append(il.Create(OpCodes.Ldc_I4_1));
                    il.Append(il.Create(OpCodes.Ret));

                    Console.WriteLine("[SteamBypassPatcher] Patched IsClientValid() to return true");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] Error patching IsClientValid: {ex.Message}");
            }
        }

        private static void PatchIsValidProperty(TypeDefinition steamPlatformType, ModuleDefinition module)
        {
            try
            {
                // Look for IsValid or similar property
                var isValidProp = steamPlatformType.Properties.FirstOrDefault(p =>
                    p.Name == "IsValid" || p.Name == "IsInitialized" || p.Name == "Initialized");

                if (isValidProp != null && isValidProp.GetMethod != null)
                {
                    var getter = isValidProp.GetMethod;
                    var il = getter.Body.GetILProcessor();
                    getter.Body.Instructions.Clear();

                    // Return true
                    il.Append(il.Create(OpCodes.Ldc_I4_1));
                    il.Append(il.Create(OpCodes.Ret));

                    Console.WriteLine($"[SteamBypassPatcher] Patched {isValidProp.Name} to return true");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] Error patching IsValid: {ex.Message}");
            }
        }

        private static void PatchQuitOnSteamFail(ModuleDefinition module)
        {
            try
            {
                // Find InitializationManager or similar that might quit on Steam fail
                var initTypes = new[] { "InitializationManager", "GameInitializer", "StartupManager", "Boot" };

                foreach (var typeName in initTypes)
                {
                    var type = module.Types.FirstOrDefault(t => t.Name == typeName);
                    if (type != null)
                    {
                        Console.WriteLine($"[SteamBypassPatcher] Found {typeName}, checking for quit calls...");
                        // Could patch quit calls here if needed
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] Error in PatchQuitOnSteamFail: {ex.Message}");
            }
        }
    }
}
