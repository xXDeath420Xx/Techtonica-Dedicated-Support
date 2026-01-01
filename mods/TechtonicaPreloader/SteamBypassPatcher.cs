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

                // CRITICAL: Patch NetworkedPlayer.UserCode_RequestInitialSaveData to skip the problematic code
                // This prevents crashes when clients request save data in headless mode
                PatchNetworkedPlayerSaveData(module);

                // CRITICAL: Patch SaveState methods to handle headless mode
                PatchSaveStateMethods(module);

                // CRITICAL: Patch TVEGlobalVolume to prevent graphics errors
                PatchTVEGlobalVolume(module);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SteamBypassPatcher] Error patching: {ex}");
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
