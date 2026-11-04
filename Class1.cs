// QuestMods 1.9.6 - existing Questmods mod with experimental developer wheel bridge.
// Experimental native 3D nested wheel, not a tablet or rectangular panel.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Il2CppAlta.Api.DataTransferModels.Models.Shared;
using Il2CppAlta.QuickAccessActions;
using MelonLoader;

[assembly: MelonInfo(typeof(QuestMods.Mod), "QuestMods", "1.9.6", "you")]
[assembly: MelonGame(null, null)]

namespace QuestMods
{
    public class Mod : MelonMod
    {
        private const int MaxSubclassPatches = 32;
        private DateTime nextReport;
        private int reports;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("=== QuestMods 1.9.6: experimental Quest Mods wheel ===");
            PatchInitializers();
            GrabbyHands.Install(HarmonyInstance);
            PatchGetter(typeof(QuickAccessMenuAction), "IsDev", typeof(MenuDevPatch));
            PatchGetter(typeof(UserRoles), nameof(UserRoles.IsDeveloper), typeof(UserRolesPatch));
            PatchGetter(typeof(QuickAccessMenuAction), "IsIgnored", typeof(IgnoredPatch));
            PatchGetter(typeof(NestingMenuAction), "IsIgnored", typeof(IgnoredPatch));
            // Do not detour the debug/speed action getters or Run. On this
            // IL2CPP build different getters can resolve to the same patch target.
            // The custom button uses the real native debug-movement action.
            nextReport = DateTime.UtcNow.AddSeconds(20);
            try { TabletProbe.Initialize(); DevWheelBridge.Initialize(); }
            catch (Exception ex) { LoggerInstance.Warning("Tablet probe setup failed: " + ex); }
            LoggerInstance.Warning("Debug movement is not automatically enabled. Experimental IsIgnored overrides remain enabled.");
        }

        private void PatchInitializers()
        {
            Type baseType = typeof(QuickAccessMenuAction);
            LoggerInstance.Msg("Menu type: " + baseType.FullName + " in " + baseType.Assembly.FullName);
            MethodInfo prefix = typeof(InitializePatch).GetMethod(nameof(InitializePatch.Prefix), BindingFlags.Public | BindingFlags.Static);
            if (prefix == null) { LoggerInstance.Error("Initializer prefix not found."); return; }
            try
            {
                MethodInfo method = AccessTools.Method(baseType, "InitializeForPlayer");
                if (method == null) LoggerInstance.Warning("Base initializer not found.");
                else PatchInitializer(method, prefix, "base");
            }
            catch (Exception ex) { LoggerInstance.Error("Base initializer: " + ex); }
            Type[] types;
            try { types = baseType.Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; LoggerInstance.Warning("Some subclass types failed to load."); }
            catch (Exception ex) { LoggerInstance.Error("Subclass scan: " + ex); return; }
            int found = 0, patched = 0;
            foreach (Type type in types)
            {
                if (type == null || type == baseType) continue;
                try
                {
                    if (!baseType.IsAssignableFrom(type)) continue;
                    foreach (MethodInfo method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        if (method.Name != "InitializeForPlayer") continue;
                        found++;
                        if (patched >= MaxSubclassPatches) { LoggerInstance.Warning("Initializer patch limit reached: " + type.FullName); continue; }
                        if (PatchInitializer(method, prefix, "subclass")) patched++;
                    }
                }
                catch (Exception ex) { LoggerInstance.Warning("Inspect " + type.FullName + ": " + ex.Message); }
            }
            LoggerInstance.Msg($"Initializer subclass scan: found={found}, patched={patched}.");
        }

        private bool PatchInitializer(MethodInfo target, MethodInfo prefix, string label)
        {
            try
            {
                ParameterInfo[] args = target.GetParameters();
                LoggerInstance.Msg($"Found {label} initializer: {target.DeclaringType?.FullName}.{target}");
                if (target.IsStatic || target.IsAbstract || target.ReturnType != typeof(void) || args.Length != 2 || args[1].ParameterType != typeof(bool))
                { LoggerInstance.Warning("Skipping abstract or unexpected initializer."); return false; }
                HarmonyInstance.Patch(target, prefix: new HarmonyMethod(prefix) { priority = global::HarmonyLib.Priority.Last });
                LoggerInstance.Msg($"Patched {label} initializer: {target.DeclaringType?.FullName}");
                return true;
            }
            catch (Exception ex) { LoggerInstance.Error($"Failed to patch {label} initializer: {ex}"); return false; }
        }

        private void PatchGetter(Type type, string propertyName, Type patchType)
        {
            try
            {
                MethodInfo target = AccessTools.PropertyGetter(type, propertyName);
                if (target == null) { LoggerInstance.Warning($"Getter not found: {type.FullName}.{propertyName}"); return; }
                if (target.ReturnType != typeof(bool)) { LoggerInstance.Warning("Unexpected getter type: " + target); return; }
                MethodInfo postfix = patchType.GetMethod("Postfix", BindingFlags.Public | BindingFlags.Static);
                if (postfix == null) { LoggerInstance.Warning("Postfix not found: " + patchType.FullName); return; }
                HarmonyInstance.Patch(target, postfix: new HarmonyMethod(postfix) { priority = global::HarmonyLib.Priority.Last });
                LoggerInstance.Msg($"Patched getter: {type.FullName}.{propertyName}");
            }
            catch (Exception ex) { LoggerInstance.Error($"Failed to patch {type.FullName}.{propertyName}: {ex}"); }
        }

        public override void OnSceneWasLoaded(int buildIndex, string sceneName)
        {
            DebugStateMonitor.SceneLoaded();
            GrabbyHands.SceneLoaded();
            LoggerInstance.Msg($"Scene loaded: '{sceneName}' ({buildIndex}). {Counts()}");
            try { TabletProbe.SceneLoaded(buildIndex, sceneName); DevWheelBridge.SceneLoaded(); }
            catch (Exception ex) { LoggerInstance.Warning("Tablet probe scene event: " + ex.Message); }
        }

        public override void OnUpdate()
        {
            try { TabletProbe.Tick(); DevWheelBridge.Tick(); DebugStateMonitor.Tick(); GrabbyHands.Tick(); }
            catch (Exception ex) { LoggerInstance.Warning("Tablet probe tick: " + ex.Message); }
            if (reports >= 12 || DateTime.UtcNow < nextReport) return;
            reports++;
            nextReport = DateTime.UtcNow.AddSeconds(20);
            LoggerInstance.Msg($"Periodic report #{reports}: {Counts()}");
        }

        private static string Counts() => $"initializer={InitializePatch.Calls}, menu IsDev={MenuDevPatch.Calls}, UserRoles IsDeveloper={UserRolesPatch.Calls}, IsIgnored={IgnoredPatch.Calls}, debug movement IsValid={DebugMovementValidPatch.Calls}.";
    }

    public static class InitializePatch
    {
        private static int calls;
        public static int Calls => Volatile.Read(ref calls);
        public static void Prefix(ref bool __1)
        {
            int n = Interlocked.Increment(ref calls); bool old = __1; __1 = true;
            if (n <= 50 || n % 1000 == 0) MelonLogger.Msg($"[QuestMods] Initialize #{n}: isDev {old} -> {__1}");
        }
    }
    public static class MenuDevPatch
    {
        private static int calls;
        public static int Calls => Volatile.Read(ref calls);
        public static void Postfix(ref bool __result)
        {
            int n = Interlocked.Increment(ref calls); bool old = __result; __result = true;
            if (n <= 20 || n % 1000 == 0) MelonLogger.Msg($"[QuestMods] Menu IsDev #{n}: {old} -> {__result}");
        }
    }
    public static class UserRolesPatch
    {
        private static int calls;
        public static int Calls => Volatile.Read(ref calls);
        public static void Postfix(ref bool __result)
        {
            int n = Interlocked.Increment(ref calls); bool old = __result; __result = true;
            if (n <= 20 || n % 1000 == 0) MelonLogger.Msg($"[QuestMods] UserRoles IsDeveloper #{n}: {old} -> {__result}");
        }
    }
    public static class IgnoredPatch
    {
        private static int calls;
        public static int Calls => Volatile.Read(ref calls);
        public static void Postfix(ref bool __result)
        {
            int n = Interlocked.Increment(ref calls); bool old = __result; __result = false;
            if (n <= 50 || n % 1000 == 0) MelonLogger.Msg($"[QuestMods] IsIgnored #{n}: {old} -> {__result}");
        }
    }
    public static class DebugMovementValidPatch
    {
        private static int calls;
        public static int Calls => Volatile.Read(ref calls);
        public static void Postfix(ref bool __result)
        {
            int n = Interlocked.Increment(ref calls); bool old = __result; __result = true;
            if (n <= 20 || n % 1000 == 0) MelonLogger.Msg($"[QuestMods] Debug movement IsValid #{n}: {old} -> {__result}");
        }
    }

    internal static class TabletProbe
    {
        private static DateTime nextScan;
        private static int scanNumber;
        private const BindingFlags InstanceFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags AllFlags = InstanceFlags | BindingFlags.Static;

        public static void Initialize()
        {
            MelonLogger.Msg("[TabletProbe] starting read-only Quest UI diagnostics.");
            MelonLogger.Msg("[TabletProbe] Diagnostics only: no tablet or bubble is created yet.");
            DumpTypes();
            nextScan = DateTime.UtcNow.AddSeconds(10);
        }

        public static void SceneLoaded(int buildIndex, string sceneName)
        {
            MelonLogger.Msg($"Scene: {sceneName} ({buildIndex}); scheduling UI scan.");
            nextScan = DateTime.UtcNow.AddSeconds(5);
        }

        public static void Tick()
        {
            if (DateTime.UtcNow < nextScan) return;
            nextScan = DateTime.UtcNow.AddSeconds(25);
            try { Scan(); }
            catch (Exception ex) { MelonLogger.Warning("Scan failed: " + Describe(ex)); }
        }

        private static void DumpTypes()
        {
            string[] names = {
                "QuickAccessMenuController", "QuickAccessMenuAction", "NestingMenuAction",
                "ToggleDebugMovementMenuAction", "ActionItemsManager", "SummonActionItemQuickAccess",
                "SocialTablet", "TouchScreenMenuBase", "Window", "Panel", "Button", "TextRenderer"
            };
            foreach (string name in names)
            {
                try
                {
                    Type[] matches = FindTypes(name);
                    MelonLogger.Msg($"TYPE {name}: {matches.Length} match(es)");
                    foreach (Type t in matches.Take(3))
                    {
                        MelonLogger.Msg($"  {t.FullName} | assembly={t.Assembly.GetName().Name}");
                        var members = t.GetMembers(AllFlags | BindingFlags.DeclaredOnly)
                            .Where(m => m.MemberType == MemberTypes.Field || m.MemberType == MemberTypes.Property || m.MemberType == MemberTypes.Method)
                            .Where(m => Relevant(m.Name))
                            .Take(40);
                        foreach (MemberInfo m in members)
                            MelonLogger.Msg("    " + m.MemberType + ": " + m);
                    }
                }
                catch (Exception ex) { MelonLogger.Warning($"TYPE {name} failed: {Describe(ex)}"); }
            }
        }

        private static bool Relevant(string name)
        {
            string n = name.ToLowerInvariant();
            return n.Contains("menu") || n.Contains("action") || n.Contains("tablet") ||
                   n.Contains("panel") || n.Contains("button") || n.Contains("setup") ||
                   n.Contains("displayname") || n.Contains("spawn") || n.Contains("initialize") ||
                   n.Contains("ignored") || n.Contains("valid") || n == "run" ||
                   n.Contains("released") || n.Contains("pressed") || n.Contains("switch") ||
                   n.Contains("current") || n.Contains("fade");
        }

        private static Type[] FindTypes(string name)
        {
            var found = new List<Type>();
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;
                foreach (Type t in types)
                    if (t != null && t.Name == name && t.FullName != null && t.FullName.StartsWith("Il2Cpp", StringComparison.Ordinal))
                        found.Add(t);
            }
            return found.ToArray();
        }

        private static void Scan()
        {
            scanNumber++;
            MelonLogger.Msg($"=== UI scan #{scanNumber} ===");
            Type resourceType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEngine.Resources", false)).FirstOrDefault(t => t != null);
            if (resourceType == null)
            {
                MelonLogger.Warning("UnityEngine.Resources not found among loaded assemblies.");
                return;
            }
            MethodInfo find = resourceType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll" && m.IsGenericMethodDefinition &&
                    m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
            if (find == null)
            {
                MelonLogger.Warning("Generic Resources.FindObjectsOfTypeAll<T>() not found.");
                return;
            }
            ScanObjects(find, "QuickAccessMenuController", true);
            ScanObjects(find, "SocialTablet", false);
        }

        private static void ScanObjects(MethodInfo find, string typeName, bool inspectMenus)
        {
            Type[] types = FindTypes(typeName);
            if (types.Length != 1)
            {
                MelonLogger.Warning($"Scan {typeName}: expected one IL2CPP type; found {types.Length}.");
                return;
            }
            try
            {
                object array = find.MakeGenericMethod(types[0]).Invoke(null, null);
                List<object> items = Items(array, 12);
                MelonLogger.Msg($"{typeName}: length={Length(array)}, inspecting={items.Count}");
                foreach (object item in items)
                {
                    if (item == null) continue;
                    MelonLogger.Msg($"  object wrapper={item.GetType().FullName}, name='{Read(item, "name") ?? "?"}'");
                    if (inspectMenus)
                    {
                        object menus = Read(item, "quickAccessMenus");
                        MelonLogger.Msg($"  quickAccessMenus: type={menus?.GetType().FullName ?? "(null)"}, length={Length(menus)}");
                        foreach (object action in Items(menus, 40))
                        {
                            if (action == null) continue;
                            object name = Read(action, "DisplayName") ?? Read(action, "displayName") ?? Read(action, "name");
                            MelonLogger.Msg($"    action name='{name ?? "?"}' wrapper={action.GetType().FullName}");
                            // This is only a direct member read. A base-class proxy may not
                            // expose the actual native subclass or its nestedActions member.
                            object nested = Read(action, "nestedActions");
                            if (nested != null)
                                MelonLogger.Msg($"      nestedActions: type={nested.GetType().FullName}, length={Length(nested)}");
                        }
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning($"Scan {typeName} failed: {Describe(ex)}"); }
        }

        private static object Read(object instance, string name)
        {
            if (instance == null) return null;
            try
            {
                Type t = instance.GetType();
                PropertyInfo p = t.GetProperty(name, InstanceFlags);
                if (p != null && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null)
                    return p.GetValue(instance);
                FieldInfo f = t.GetField(name, InstanceFlags);
                return f?.GetValue(instance);
            }
            catch { return null; }
        }

        private static string Length(object array)
        {
            if (array == null) return "null";
            if (array is Array a) return a.Length.ToString();
            object length = Read(array, "Length") ?? Read(array, "Count");
            return length?.ToString() ?? "unknown";
        }

        private static List<object> Items(object collection, int max)
        {
            var list = new List<object>();
            if (collection == null) return list;
            if (collection is IEnumerable enumerable)
            {
                try
                {
                    foreach (object item in enumerable)
                    {
                        list.Add(item);
                        if (list.Count >= max) break;
                    }
                    return list;
                }
                catch { list.Clear(); }
            }
            object countValue = Read(collection, "Length") ?? Read(collection, "Count");
            if (countValue == null) return list;
            int count;
            try { count = Math.Min(Convert.ToInt32(countValue), max); }
            catch { return list; }
            PropertyInfo indexer = collection.GetType().GetProperties(InstanceFlags)
                .FirstOrDefault(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetGetMethod(true) != null);
            if (indexer == null) return list;
            Type indexType = indexer.GetIndexParameters()[0].ParameterType;
            for (int i = 0; i < count; i++)
            {
                try { list.Add(indexer.GetValue(collection, new[] { Convert.ChangeType(i, indexType) })); }
                catch { break; }
            }
            return list;
        }

        private static string Describe(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    // Best-effort test: copy developer actions already present in a local
    // controller's devQuickAccessMenus into that controller's regular array.
    // No action assets are renamed or created. This is NOT a tablet port.
    internal static class DevWheelBridge
    {
        private static DateTime nextCheck;
        private static int attempts;
        private static bool treeDumped;
        private static MethodInfo find;
        private static Type controllerType;
        private const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public static void Initialize()
        {
            controllerType = typeof(QuickAccessMenuAction).Assembly.GetType("Il2CppAlta.QuickAccessActions.QuickAccessMenuController", false);
            Type resources = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.Resources", false)).FirstOrDefault(t => t != null);
            if (resources != null)
                find = resources.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
            MelonLogger.Msg($"[DevWheelBridge] controller type found={controllerType != null}; resource finder found={find != null}.");
            SceneLoaded();
        }
        public static void SceneLoaded()
        {
            attempts = 0;
            treeDumped = false;
            nextCheck = DateTime.UtcNow.AddSeconds(5);
        }
        public static void Tick()
        {
            if (attempts >= 8 || DateTime.UtcNow < nextCheck || find == null || controllerType == null) return;
            attempts++;
            nextCheck = DateTime.UtcNow.AddSeconds(8);
            try
            {
                object controllers = find.MakeGenericMethod(controllerType).Invoke(null, null);
                foreach (object controller in ReadArray(controllers))
                {
                    if (controller == null) continue;
                    string name = Read(controller, "name") as string;
                    // Only touch runtime player-controller clones, not prefab assets.
                    if (name == null || !name.Contains("Owned Controller Extensions") || !name.Contains("(Clone)")) continue;
                    GrabbyHands.RememberHand(controller);
                    TryAdd(controller, name);
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[DevWheelBridge] Scan failed: " + Detail(ex)); }
        }
        private static void TryAdd(object controller, string name)
        {
            try
            {
                PropertyInfo regularProperty = controller.GetType().GetProperty("quickAccessMenus", Flags);
                PropertyInfo devProperty = controller.GetType().GetProperty("devQuickAccessMenus", Flags);
                if (regularProperty == null || devProperty == null || regularProperty.GetSetMethod(true) == null)
                { MelonLogger.Warning("[DevWheelBridge] Required wheel properties/setter missing."); return; }
                object regularArray = regularProperty.GetValue(controller);
                object devArray = devProperty.GetValue(controller);
                var regular = ReadArray(regularArray);
                var dev = ReadArray(devArray);
                MelonLogger.Msg($"[DevWheelBridge] {name}: regular={regular.Count}, developer={dev.Count}.");
                if (regularArray == null || dev.Count == 0) return;
                if (!treeDumped)
                {
                    treeDumped = true;
                    LogDevTree(dev);
                }
                var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object item in regular)
                {
                    string label = (Read(item, "DisplayName") ?? Read(item, "displayName")) as string;
                    if (!string.IsNullOrEmpty(label)) existingNames.Add(label);
                }
                var additions = new List<object>();
                foreach (object item in dev)
                {
                    if (item == null) continue;
                    string label = (Read(item, "DisplayName") ?? Read(item, "displayName")) as string;
                    if (string.IsNullOrEmpty(label))
                    { MelonLogger.Warning("[DevWheelBridge] Skipping developer action with no readable name."); continue; }
                    MelonLogger.Msg("[DevWheelBridge] Developer action: " + label);
                    if (existingNames.Add(label)) additions.Add(item);
                }
                if (!existingNames.Contains("Quest Mods"))
                {
                    // Install the optional wheel hook only after the player and
                    // controller exist in-game, never during game startup.
                    GrabbyHands.EnsureWheelHook();
                    object custom = QuestModsMenu.TryCreate(devArray, dev);
                    if (custom != null)
                    {
                        additions.Add(custom);
                        MelonLogger.Msg("[QuestModsMenu] Prepared Quest Mods submenu.");
                    }
                }
                if (additions.Count == 0) return;
                Type arrayType = regularArray.GetType();
                object replacement = CreateArray(arrayType, regular.Count + additions.Count);
                PropertyInfo indexer = arrayType.GetProperties(Flags).FirstOrDefault(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetSetMethod(true) != null);
                if (indexer == null) throw new InvalidOperationException("Array writable indexer not found on " + arrayType.FullName);
                Type indexType = indexer.GetIndexParameters()[0].ParameterType;
                int i = 0;
                foreach (object item in regular.Concat(additions))
                    indexer.SetValue(replacement, item, new object[] { Convert.ChangeType(i++, indexType) });
                // Commit only after the new array is fully constructed.
                regularProperty.SetValue(controller, replacement);
                MelonLogger.Msg($"[DevWheelBridge] Added {additions.Count} root action(s) to {name}. Open the wheel again.");
            }
            catch (Exception ex) { MelonLogger.Warning("[DevWheelBridge] Could not extend wheel: " + Detail(ex)); }
        }
        private static void LogDevTree(List<object> dev)
        {
            MelonLogger.Msg("[DevWheelBridge] Inspecting existing developer pages (no changes).");
            foreach (object entry in dev)
            {
                try
                {
                    string rootName = (Read(entry, "DisplayName") ?? Read(entry, "displayName")) as string ?? "(unnamed)";
                    object nest = QuestModsMenu.TryCast(entry, typeof(NestingMenuAction));
                    if (nest == null)
                    {
                        MelonLogger.Msg("[DevWheelBridge] " + rootName + ": not a castable nesting page.");
                        continue;
                    }
                    object nested = Read(nest, "nestedActions");
                    List<object> children = ReadArray(nested);
                    MelonLogger.Msg($"[DevWheelBridge] Page '{rootName}' has {children.Count} action(s).");
                    foreach (object child in children.Take(40))
                    {
                        if (child == null) continue;
                        string label = (Read(child, "DisplayName") ?? Read(child, "displayName")) as string ?? "(unnamed)";
                        MelonLogger.Msg($"[DevWheelBridge]   action='{label}', proxy={child.GetType().FullName}");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("[DevWheelBridge] Page inspection failed: " + Detail(ex)); }
            }
        }
        private static object CreateArray(Type type, int length)
        {
            ConstructorInfo ctor = type.GetConstructor(new[] { typeof(long) });
            if (ctor != null) return ctor.Invoke(new object[] { (long)length });
            ctor = type.GetConstructor(new[] { typeof(int) });
            if (ctor != null) return ctor.Invoke(new object[] { length });
            throw new InvalidOperationException("No integer length constructor found for " + type.FullName);
        }
        internal static List<object> ReadArray(object array)
        {
            var result = new List<object>();
            if (array == null) return result;
            object lengthValue = Read(array, "Length") ?? Read(array, "Count");
            if (lengthValue == null) throw new InvalidOperationException("Array length not readable: " + array.GetType().FullName);
            int length = Convert.ToInt32(lengthValue);
            if (length < 0 || length > 200) throw new InvalidOperationException("Unexpected array length " + length);
            PropertyInfo indexer = array.GetType().GetProperties(Flags).FirstOrDefault(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetGetMethod(true) != null);
            if (indexer == null) throw new InvalidOperationException("Array indexer not readable: " + array.GetType().FullName);
            Type indexType = indexer.GetIndexParameters()[0].ParameterType;
            for (int i = 0; i < length; i++) result.Add(indexer.GetValue(array, new object[] { Convert.ChangeType(i, indexType) }));
            return result;
        }
        private static object Read(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            PropertyInfo p = t.GetProperty(name, Flags);
            if (p != null && p.GetIndexParameters().Length == 0 && p.GetGetMethod(true) != null) return p.GetValue(obj);
            FieldInfo f = t.GetField(name, Flags);
            return f?.GetValue(obj);
        }
        private static string Detail(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    // Experimental Quest Mods nested radial menu. This reuses the game's
    // native action assets, not an injected IL2CPP class or a custom tablet.
    internal static class QuestModsMenu
    {
        private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static DateTime nextFailureLog = DateTime.MinValue;

        public static object TryCreate(object devArray, List<object> dev)
        {
            var made = new List<object>();
            try
            {
                Type nestType = typeof(NestingMenuAction);
                Type unityObject = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("UnityEngine.Object", false)).FirstOrDefault(t => t != null);
                Type playerType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("Il2Cpp.Player", false)).FirstOrDefault(t => t != null);
                if (unityObject == null || playerType == null) throw new Exception("Unity Object or Player type not loaded.");
                PropertyInfo current = playerType.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                object player = current?.GetValue(null);
                if (player == null) throw new Exception("Local Player.Current is not available yet.");
                // Player.Current can be exposed as an IPlayer proxy even when
                // InitializeForPlayer expects Il2Cpp.Player. Try an IL2CPP cast;
                // do not pass an incompatible proxy or a fake/null player.
                if (!playerType.IsInstanceOfType(player))
                {
                    string wrapperType = player.GetType().FullName;
                    player = TryCast(player, playerType);
                    if (player == null)
                        throw new Exception("Player.Current proxy " + wrapperType + " could not be cast to " + playerType.FullName);
                    MelonLogger.Msg("[QuestModsMenu] Converted Player.Current to " + playerType.FullName + ".");
                }
                MethodInfo instantiate = unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Instantiate" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsGenericParameter);
                if (instantiate == null) throw new Exception("Generic Unity Object.Instantiate<T>(T) not found.");
                instantiate = instantiate.MakeGenericMethod(nestType);
                PropertyInfo nestedProperty = nestType.GetProperty("nestedActions", Instance);
                PropertyInfo spawnedProperty = nestType.GetProperty("spawnedBubbles", Instance);
                PropertyInfo nameProperty = typeof(QuickAccessMenuAction).GetProperty("displayName", Instance);
                if (nestedProperty == null || spawnedProperty == null || nameProperty == null ||
                    nestedProperty.GetSetMethod(true) == null || spawnedProperty.GetSetMethod(true) == null || nameProperty.GetSetMethod(true) == null)
                    throw new Exception("Required menu properties or setters are missing.");

                // The developer pages remain on the MAIN wheel only. This
                // submenu contains the two mod actions, never copies of pages.
                var pages = new List<object>();
                object source = null;
                foreach (object candidate in dev)
                {
                    source = TryCast(candidate, nestType);
                    if (source != null) break;
                }
                if (source == null) throw new Exception("No developer roots could be cast to NestingMenuAction.");
                object debugAction = null;
                try
                {
                    debugAction = CreateNativeDebugAction(dev, nestedProperty, unityObject, nameProperty);
                    if (debugAction != null)
                    {
                        made.Add(debugAction);
                        pages.Insert(0, debugAction);
                    }
                }
                catch (Exception ex)
                {
                    if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
                    MelonLogger.Warning("[QuestDebugToggle] Could not create native button: " + ex);
                }
                if (GrabbyHands.Available && debugAction != null)
                {
                    object grabby = null;
                    try
                    {
                        // Use a native nesting action as the selectable wheel item;
                        // intercept its concrete spawn for our clone only.
                        grabby = CloneNest(source, instantiate, spawnedProperty);
                        // Validity is derived from the native Fly action. If the
                        // native spawn hook is bypassed, this safe fallback can
                        // expose Fly, but never the duplicated developer pages.
                        object sourceArray = nestedProperty.GetValue(source);
                        Type nativeArrayType = sourceArray.GetType();
                        ConstructorInfo itemCtor = nativeArrayType.GetConstructor(new[] { typeof(long) }) ?? nativeArrayType.GetConstructor(new[] { typeof(int) });
                        if (itemCtor == null) throw new Exception("No action-array constructor for Grabby Hands.");
                        object fallbackChildren = itemCtor.Invoke(new[] { Convert.ChangeType(1, itemCtor.GetParameters()[0].ParameterType) });
                        PropertyInfo item = nativeArrayType.GetProperties(Instance).FirstOrDefault(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetSetMethod(true) != null);
                        if (item == null) throw new Exception("No action-array setter for Grabby Hands.");
                        item.SetValue(fallbackChildren, debugAction, new[] { Convert.ChangeType(0, item.GetIndexParameters()[0].ParameterType) });
                        nestedProperty.SetValue(grabby, fallbackChildren);
                        nameProperty.SetValue(grabby, "Grabby Hands: OFF");
                        GrabbyHands.Register(grabby);
                        made.Add(grabby);
                        pages.Insert(debugAction != null ? 1 : 0, grabby);
                    }
                    catch (Exception ex)
                    {
                        GrabbyHands.Unregister(grabby);
                        if (grabby != null) DestroyClone(grabby);
                        MelonLogger.Warning("[GrabbyHands] Could not create wheel toggle: " + ex);
                    }
                }
                if (pages.Count == 0) throw new Exception("No mod actions are available; cannot create empty Quest Mods menu.");
                object root = CloneNest(source, instantiate, spawnedProperty);
                made.Add(root);
                nameProperty.SetValue(root, "Quest Mods");

                Type arrayType = devArray.GetType();
                ConstructorInfo ctor = arrayType.GetConstructor(new[] { typeof(long) }) ?? arrayType.GetConstructor(new[] { typeof(int) });
                if (ctor == null) throw new Exception("IL2CPP action-array length constructor not found.");
                Type lengthType = ctor.GetParameters()[0].ParameterType;
                object nested = ctor.Invoke(new object[] { Convert.ChangeType(pages.Count, lengthType) });
                PropertyInfo indexer = arrayType.GetProperties(Instance).FirstOrDefault(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetSetMethod(true) != null);
                if (indexer == null) throw new Exception("IL2CPP action-array setter not found.");
                Type indexType = indexer.GetIndexParameters()[0].ParameterType;
                for (int i = 0; i < pages.Count; i++)
                    indexer.SetValue(nested, pages[i], new object[] { Convert.ChangeType(i, indexType) });
                nestedProperty.SetValue(root, nested);

                MethodInfo init = nestType.GetMethod("InitializeForPlayer", Instance);
                if (init == null) throw new Exception("NestingMenuAction.InitializeForPlayer not found.");
                init.Invoke(root, new object[] { player, true });
                if (debugAction != null)
                {
                    try
                    {
                        // Explicitly initialize this clone with the local player and
                        // developer flag, even if child initialization changes later.
                        MethodInfo childInit = typeof(ToggleDebugMovementMenuAction).GetMethod("InitializeForPlayer", Instance);
                        if (childInit == null) throw new Exception("Debug action initializer not found.");
                        childInit.Invoke(debugAction, new object[] { player, true });
                        PropertyInfo valid = typeof(ToggleDebugMovementMenuAction).GetProperty("IsValid", Instance);
                        PropertyInfo active = typeof(ToggleDebugMovementMenuAction).GetProperty("IsActive", Instance);
                        MelonLogger.Msg("[QuestDebugToggle] Native button ready: IsValid=" + (valid?.GetValue(debugAction) ?? "unknown") +
                            " IsActive=" + (active?.GetValue(debugAction) ?? "unknown") +
                            ". State is unchanged until the button is used.");
                    }
                    catch (Exception ex) { MelonLogger.Warning("[QuestDebugToggle] Native initialization/check failed: " + ex); }
                }
                MelonLogger.Msg($"[QuestModsMenu] Created Quest Mods wheel with {pages.Count} mod action(s).");
                return root;
            }
            catch (Exception ex)
            {
                foreach (object obj in made) { GrabbyHands.Unregister(obj); DestroyClone(obj); }
                if (DateTime.UtcNow >= nextFailureLog)
                {
                    nextFailureLog = DateTime.UtcNow.AddSeconds(15);
                    if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
                    MelonLogger.Warning("[QuestModsMenu] Could not create submenu: " + ex);
                }
                return null;
            }
        }

        // Use the game's actual ToggleDebugMovementMenuAction. It owns native
        // IsValid, IsActive and Run behavior; no speed action is reused or hooked.
        // On the reference game Run calls SmoothLocomotion.ToggleDebug().
        private static object CreateNativeDebugAction(List<object> dev, PropertyInfo nestedProperty,
                                                       Type unityObject, PropertyInfo nameProperty)
        {
            Type debugType = typeof(ToggleDebugMovementMenuAction);
            object template = null;
            foreach (object entry in dev)
            {
                object nest = TryCast(entry, typeof(NestingMenuAction));
                if (nest == null) continue;
                foreach (object child in DevWheelBridge.ReadArray(nestedProperty.GetValue(nest)))
                {
                    object candidate = TryCast(child, debugType);
                    if (candidate != null) { template = candidate; break; }
                }
                if (template != null) break;
            }
            if (template == null)
            {
                try
                {
                    Type resources = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("UnityEngine.Resources", false)).FirstOrDefault(t => t != null);
                    MethodInfo find = resources?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll" && m.IsGenericMethodDefinition &&
                                             m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
                    if (find != null)
                    {
                        foreach (object item in DevWheelBridge.ReadArray(find.MakeGenericMethod(debugType).Invoke(null, null)))
                        {
                            template = TryCast(item, debugType);
                            if (template != null) break;
                        }
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("[QuestDebugToggle] Native template search failed: " + ex.Message); }
            }

            object action = null;
            bool safeToDestroy = false;
            try
            {
                if (template != null)
                {
                    MethodInfo instantiate = unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "Instantiate" && m.IsGenericMethodDefinition &&
                                             m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1 &&
                                             m.GetParameters()[0].ParameterType.IsGenericParameter);
                    if (instantiate == null) throw new Exception("Unity Instantiate<T> not found.");
                    action = instantiate.MakeGenericMethod(debugType).Invoke(null, new[] { template });
                    if (action == null) throw new Exception("Native debug action clone was null.");
                    MethodInfo id = unityObject.GetMethod("GetInstanceID", Instance, null, Type.EmptyTypes, null);
                    if (id == null || Convert.ToInt32(id.Invoke(action, null)) == Convert.ToInt32(id.Invoke(template, null)))
                        throw new Exception("Could not verify debug action is a separate clone.");
                    safeToDestroy = true;
                    // Clone may retain event subscribers belonging to the template.
                    foreach (string eventName in new[] { "UpdatedValid", "UpdatedActive" })
                    {
                        PropertyInfo eventProperty = typeof(QuickAccessMenuAction).GetProperty(eventName, Instance);
                        if (eventProperty?.GetSetMethod(true) != null) eventProperty.SetValue(action, null);
                    }
                    MelonLogger.Msg("[QuestDebugToggle] Cloned native debug-movement action.");
                }
                else
                {
                    Type scriptable = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("UnityEngine.ScriptableObject", false)).FirstOrDefault(t => t != null);
                    MethodInfo create = scriptable?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "CreateInstance" && m.IsGenericMethodDefinition &&
                                             m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
                    if (create == null) throw new Exception("No native debug action asset and no CreateInstance<T>().");
                    action = create.MakeGenericMethod(debugType).Invoke(null, null);
                    if (action == null) throw new Exception("CreateInstance<ToggleDebugMovementMenuAction> returned null.");
                    safeToDestroy = true;
                    MelonLogger.Msg("[QuestDebugToggle] Created native debug-movement action (no template found).");
                }
                nameProperty.SetValue(action, "Fly (Debug Movement)");
                return action;
            }
            catch
            {
                if (action != null && safeToDestroy) DestroyClone(action);
                throw;
            }
        }

        private static object CloneNest(object source, MethodInfo instantiate, PropertyInfo spawnedProperty)
        {
            object clone = instantiate.Invoke(null, new[] { source });
            if (clone == null) throw new Exception("Instantiate returned null.");
            try
            {
                MethodInfo id = source.GetType().GetMethod("GetInstanceID", Instance, null, Type.EmptyTypes, null);
                if (id == null || Convert.ToInt32(id.Invoke(source, null)) == Convert.ToInt32(id.Invoke(clone, null)))
                    throw new Exception("Could not verify that the action is a separate clone.");
                // Unity cloning can copy a reference to the old list: replace it.
                object emptyList = Activator.CreateInstance(spawnedProperty.PropertyType);
                spawnedProperty.SetValue(clone, emptyList);
                PropertyInfo selected = clone.GetType().GetProperty("isSelected", Instance);
                if (selected?.GetSetMethod(true) != null) selected.SetValue(clone, false);
                PropertyInfo bubble = clone.GetType().GetProperty("currentBubble", Instance);
                if (bubble?.GetSetMethod(true) != null) bubble.SetValue(clone, null);
                return clone;
            }
            catch { DestroyClone(clone); throw; }
        }

        internal static object TryCast(object obj, Type type)
        {
            if (obj == null) return null;
            if (type.IsInstanceOfType(obj)) return obj;
            // Prefer the non-throwing interop cast, then try Cast<T> as a
            // fallback. Never fabricate a wrapper from an unchecked pointer.
            MethodInfo[] methods = obj.GetType().GetMethods(Instance);
            foreach (string name in new[] { "TryCast", "Cast" })
            {
                foreach (MethodInfo method in methods.Where(m => m.Name == name && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0))
                {
                    try
                    {
                        object converted = method.MakeGenericMethod(type).Invoke(obj, null);
                        if (converted != null && type.IsInstanceOfType(converted)) return converted;
                    }
                    catch { /* Different interop versions expose different casts. */ }
                }
            }
            return null;
        }

        private static void DestroyClone(object obj)
        {
            try
            {
                Type unityObject = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.Object", false)).FirstOrDefault(t => t != null);
                MethodInfo destroy = unityObject?.GetMethod("Destroy", BindingFlags.Static | BindingFlags.Public, null, new[] { unityObject }, null);
                destroy?.Invoke(null, new[] { obj });
            }
            catch { }
        }
    }

    // Observe actual local debug movement, without writing to it.
    internal static class DebugStateMonitor
    {
        private static DateTime next;
        private static bool? last;
        private static Type playerController, smoothController;
        private const BindingFlags Inst = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags Stat = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy;
        public static void SceneLoaded() { last = null; next = DateTime.UtcNow.AddSeconds(3); }
        public static void Tick()
        {
            if (DateTime.UtcNow < next) return;
            next = DateTime.UtcNow.AddSeconds(1);
            try
            {
                if (playerController == null) playerController = Find("Il2Cpp.PlayerController");
                if (smoothController == null) smoothController = Find("Il2Cpp.SmoothLocomotionPlayerController");
                if (playerController == null || smoothController == null) return;
                object current = Read(playerController, null, "Current", Stat);
                object smooth = QuestModsMenu.TryCast(current, smoothController);
                if (smooth == null) return;
                object locomotion = Read(smoothController, smooth, "SmoothLocomotion", Inst);
                if (locomotion == null) return;
                object result = Read(locomotion.GetType(), locomotion, "IsDebugMovement", Inst);
                if (!(result is bool state)) return;
                if (!last.HasValue || last.Value != state)
                {
                    MelonLogger.Msg("[QuestDebugToggle] Actual local IsDebugMovement=" + state +
                        (last.HasValue ? " (changed)" : " (initial observation; no change made)"));
                    last = state;
                }
            }
            catch (Exception ex)
            {
                // Throttle transient reflection failures; no gameplay values are written.
                next = DateTime.UtcNow.AddSeconds(15);
                MelonLogger.Warning("[QuestDebugToggle] State observation failed: " + ex.Message);
            }
        }
        private static Type Find(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            { Type t = a.GetType(name, false); if (t != null) return t; }
            return null;
        }
        private static object Read(Type t, object target, string name, BindingFlags flags)
        {
            PropertyInfo p = t.GetProperty(name, flags);
            if (p != null && p.GetGetMethod(true) != null) return p.GetValue(target);
            return t.GetField(name, flags)?.GetValue(target);
        }
    }

    // Read-only logging for speed/debug movement menu actions. It does NOT
    // block the game from changing speed or force any movement state.
    internal static class SpeedDiagnostics
    {
        private const BindingFlags Declared = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        public static void Install(global::HarmonyLib.Harmony harmony)
        {
            try
            {
                Type baseType = typeof(QuickAccessMenuAction);
                Type[] types;
                try { types = baseType.Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                int found = 0;
                foreach (Type type in types)
                {
                    if (type == null || type.FullName == null || !type.FullName.StartsWith("Il2CppAlta.QuickAccessActions.") || !baseType.IsAssignableFrom(type)) continue;
                    if (type.Name.IndexOf("speed", StringComparison.OrdinalIgnoreCase) < 0 && type != typeof(ToggleDebugMovementMenuAction)) continue;
                    found++;
                    MelonLogger.Msg("[SpeedDiagnostics] Candidate: " + type.FullName);
                    foreach (MethodInfo method in type.GetMethods(Declared))
                    {
                        try
                        {
                            if (method.Name == "Run" && !method.IsAbstract && method.ReturnType == typeof(void) && method.GetParameters().Length == 1)
                            {
                                harmony.Patch(method, prefix: new HarmonyMethod(typeof(SpeedDiagnostics).GetMethod(nameof(RunPrefix), BindingFlags.Public | BindingFlags.Static)));
                                MelonLogger.Msg("[SpeedDiagnostics] Watching Run: " + type.FullName + "." + method);
                            }
                            else if ((method.Name == "get_IsValid" || method.Name == "get_IsIgnored" || method.Name == "get_IsActive") && !method.IsAbstract && method.ReturnType == typeof(bool) && method.GetParameters().Length == 0)
                            {
                                harmony.Patch(method, postfix: new HarmonyMethod(typeof(SpeedDiagnostics).GetMethod(nameof(GetterPostfix), BindingFlags.Public | BindingFlags.Static)) { priority = global::HarmonyLib.Priority.First });
                                MelonLogger.Msg("[SpeedDiagnostics] Watching getter: " + type.FullName + "." + method);
                            }
                        }
                        catch (Exception ex) { MelonLogger.Warning("[SpeedDiagnostics] Could not watch " + type.FullName + "." + method.Name + ": " + ex.Message); }
                    }
                }
                MelonLogger.Msg("[SpeedDiagnostics] Candidate count=" + found + ". Logging only; speed is not changed or protected.");
            }
            catch (Exception ex) { MelonLogger.Warning("[SpeedDiagnostics] Install failed: " + ex); }
        }
        private static int runs, getters;
        public static void RunPrefix()
        {
            int count = Interlocked.Increment(ref runs);
            if (count <= 30) MelonLogger.Msg("[SpeedDiagnostics] A watched speed/debug movement Run was called (#" + count + ").");
        }
        public static void GetterPostfix(bool __result)
        {
            int count = Interlocked.Increment(ref getters);
            if (count <= 30) MelonLogger.Msg("[SpeedDiagnostics] A watched getter returned " + __result + " (#" + count + ").");
        }
    }

    // No custom board or Unity objects. The wheel item and the grab hook are
    // experimental; actual native call dispatch varies between Quest builds.
    internal static class GrabbyHands
    {
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly HashSet<int> ids = new HashSet<int>();
        private static readonly List<object> actions = new List<object>();
        private static readonly List<object> localHandTransforms = new List<object>();
        private static readonly HashSet<int> localControllerIds = new HashSet<int>();
        private static readonly HashSet<int> localInteractorIds = new HashSet<int>();
        private static readonly List<Type> pickupTypes = new List<Type>();
        private static int ownershipLogs, pickupLogs;
        [ThreadStatic] private static bool insideUndock;
        private static bool ready, enabled, grabScanDone, wheelHookAttempted;
        private static int grabHooks, holdHooks, seen, undockAttempts, pending, earlyLogs, startLogs, holdLogs;
        [ThreadStatic] private static bool insideFallback;
        private static global::HarmonyLib.Harmony grabHarmony;
        public static bool Available => ready;

        public static void Install(global::HarmonyLib.Harmony harmony)
        {
            // Startup is intentionally free of new Harmony patches/scans.
            grabHarmony = harmony;
            MelonLogger.Msg("[GrabbyHands] Startup is passive; wheel hook deferred until the player controller is available.");
        }
        public static void EnsureWheelHook()
        {
            if (wheelHookAttempted) return;
            wheelHookAttempted = true;
            try
            {
                if (grabHarmony == null) throw new Exception("Harmony instance unavailable");
                // Run interception was bypassed on the reported Quest build.
                // Try the concrete native spawn method instead; only our
                // verified clone is suppressed, other nesting menus are intact.
                MethodInfo spawn = typeof(NestingMenuAction).GetMethods(I | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == "SpawnNestedBubbles" && m.GetParameters().Length == 3 && m.ReturnType == typeof(void) && !m.IsAbstract);
                if (spawn == null) throw new Exception("NestingMenuAction.SpawnNestedBubbles not found");
                grabHarmony.Patch(spawn, prefix: new HarmonyMethod(typeof(GrabbyHands).GetMethod(nameof(BeforeWheelRun), S)));
                ready = true;
                MelonLogger.Msg("[GrabbyHands] Spawn hook registered in-game; native interception is not yet verified. Default OFF.");
            }
            catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] Wheel hook unavailable; no toggle will be added: " + Detail(ex)); }
        }
        private static void InstallGrabHooks()
        {
            if (grabScanDone) return;
            grabScanDone = true;
            // Scan only the known game assembly and install a small bounded set.
            // This happens on the normal update loop, never inside the wheel callback.
            try
            {
                var allTypes = new List<Type>();
                Assembly assembly = typeof(QuickAccessMenuAction).Assembly;
                try { allTypes.AddRange(assembly.GetTypes()); }
                catch (ReflectionTypeLoadException ex) { allTypes.AddRange(ex.Types.Where(t => t != null)); }
                foreach (Type type in allTypes)
                {
                    if (type == null || type.Name.IndexOf("Pickup", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        PropertyInfo docked = type.GetProperty("IsDocked", I);
                        if (docked != null && docked.PropertyType == typeof(bool) &&
                            type.GetMethods(I).Any(m => m.Name == "Undock" && m.GetParameters().Length == 3))
                            pickupTypes.Add(type);
                    }
                    catch { }
                }
                MelonLogger.Msg("[GrabbyHands] Castable pickup type candidates=" + pickupTypes.Count);
                foreach (Type type in allTypes)
                {
                    if (type == null || type.Name.IndexOf("Interactor", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    foreach (MethodInfo m in type.GetMethods(I | BindingFlags.DeclaredOnly))
                    {
                        ParameterInfo[] p = m.GetParameters();
                        if (m.Name != "StartInteract" || m.IsAbstract || m.IsStatic || m.IsGenericMethod || p.Length != 4 ||
                            p[0].ParameterType.IsValueType || p[0].ParameterType.IsByRef ||
                            p[1].ParameterType != typeof(bool) || p[2].ParameterType != typeof(bool) || p[3].ParameterType != typeof(bool)) continue;
                        if (grabHooks >= 4) { MelonLogger.Warning("[GrabbyHands] Hook limit reached."); break; }
                        try
                        {
                            grabHarmony.Patch(m, prefix: new HarmonyMethod(typeof(GrabbyHands).GetMethod(nameof(BeforeInteract), S)));
                            grabHooks++;
                            MelonLogger.Msg("[GrabbyHands] Watching " + type.FullName + "." + m);
                        }
                        catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] Cannot hook " + type.FullName + ": " + Detail(ex)); }
                    }
                }
                // Install only after the player enables the toggle.  The reference
                // CheckHold handles the actual local interaction input.
                try
                {
                    Type interactor = assembly.GetType("Il2Cpp.Interactor", false);
                    Type dock = assembly.GetType("Il2CppAlta.Inventory.PickupDock", false);
                    Type colliderExtension = assembly.GetType("Il2Cpp.ColliderExtension", false);
                    Type interactable = assembly.GetType("Il2Cpp.Interactable", false);
                    MethodInfo hold = interactor?.GetMethods(I | BindingFlags.DeclaredOnly).FirstOrDefault(m =>
                    {
                        ParameterInfo[] p = m.GetParameters();
                        return m.Name == "CheckHold" && !m.IsStatic && !m.IsAbstract && m.ReturnType == typeof(void) &&
                               p.Length == 1 && p[0].ParameterType.FullName == "Il2Cpp.PlayerInput";
                    });
                    if (hold == null || dock == null || interactable == null)
                        MelonLogger.Warning("[GrabbyHands] Slot hook unavailable: CheckHold=" + (hold != null) + " PickupDock=" + (dock != null) + " Interactable=" + (interactable != null));
                    else
                    {
                        grabHarmony.Patch(hold, prefix: new HarmonyMethod(typeof(GrabbyHands).GetMethod(nameof(BeforeCheckHold), S)));
                        holdHooks++;
                        MelonLogger.Msg("[GrabbyHands] Slot input hook registered: " + hold + "; collider extension=" + (colliderExtension != null) + ". Native interception unverified.");
                    }
                }
                catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] Slot input hook failed: " + Detail(ex)); }
                MelonLogger.Msg("[GrabbyHands] StartInteract hook(s)=" + grabHooks + "; default OFF.");
            }
            catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] Interaction scan failed: " + Detail(ex)); }
        }

        public static void Register(object action)
        {
            int id = Id(action);
            ids.Add(id);
            actions.Add(action);
            UpdateLabels();
            MelonLogger.Msg("[GrabbyHands] Added wheel toggle id=" + id + ".");
        }
        public static void Unregister(object action)
        {
            try { if (action != null) { ids.Remove(Id(action)); actions.Remove(action); } } catch { }
        }
        public static void SceneLoaded()
        {
            Interlocked.Exchange(ref pending, 0);
            if (enabled) MelonLogger.Msg("[GrabbyHands] Turned OFF on scene load.");
            enabled = false; seen = 0; undockAttempts = 0; fallbackDetected = false;
            ownershipLogs = 0; pickupLogs = 0; earlyLogs = 0; startLogs = 0; holdLogs = 0;
            UpdateLabels();
            actions.Clear();
            localHandTransforms.Clear();
            localControllerIds.Clear(); localInteractorIds.Clear();
            // Keep IDs for still-live clones; also check their exact label before
            // suppressing spawn, guarding against ID reuse on a different action.
        }
        // The wheel extension and Interactor are both ControllerExtensions.
        // Their transforms need not be nearby in the hierarchy. Record the
        // actual controller and its interactor, not guessed ancestor names.
        public static void RememberHand(object wheelExtension)
        {
            try
            {
                object controller = SafeRead(wheelExtension, "Controller") ?? SafeRead(wheelExtension, "controller");
                if (controller != null) RecordController(controller, "local wheel");
                object transform = SafeRead(wheelExtension, "transform");
                if (transform != null)
                {
                    int id = Id(transform);
                    bool found = false;
                    foreach (object existing in localHandTransforms) if (Id(existing) == id) { found = true; break; }
                    if (!found) localHandTransforms.Add(transform);
                }
                if (controller == null && ownershipLogs++ < 4)
                    MelonLogger.Warning("[GrabbyHands] Wheel extension has no readable Controller yet; will also try PlayerController.Current.");
            }
            catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] Could not record local hand: " + Detail(ex)); }
        }
        private static void RecordController(object controller, string source)
        {
            if (controller == null) return;
            int cid = Id(controller);
            bool added = localControllerIds.Add(cid);
            object interactor = SafeRead(controller, "Interactor") ?? SafeRead(controller, "interactor");
            int iid = 0;
            if (interactor != null) { iid = Id(interactor); localInteractorIds.Add(iid); }
            if (added) MelonLogger.Msg("[GrabbyHands] Local controller from " + source + ": controller=" + cid + " interactor=" + (iid == 0 ? "unavailable" : iid.ToString()));
        }
        private static void RememberCurrentPlayerControllers()
        {
            // Secondary source is the explicit local PlayerController.Current.
            // If the runtime does not expose IsLocalPlayer, it is not trusted.
            Type t = null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            { t = a.GetType("Il2Cpp.PlayerController", false); if (t != null) break; }
            if (t == null) return;
            PropertyInfo prop = t.GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
            object current = prop?.GetValue(null);
            if (current == null) return;
            object local = SafeRead(current, "IsLocalPlayer");
            if (!(local is bool isLocal) || !isLocal) return;
            RecordController(SafeRead(current, "LeftController") ?? SafeRead(current, "leftController"), "PlayerController.Current left");
            RecordController(SafeRead(current, "RightController") ?? SafeRead(current, "rightController"), "PlayerController.Current right");
        }
        public static bool BeforeWheelRun(object __instance)
        {
            bool ours = false;
            try
            {
                if (__instance == null || !ids.Contains(Id(__instance))) return true;
                string name = Read(__instance, "DisplayName") as string;
                ours = name == "Grabby Hands: OFF" || name == "Grabby Hands: ON" || name == "Grabby Hands: UNAVAILABLE";
                if (!ours) return true;
                Interlocked.Increment(ref pending);
                MelonLogger.Msg("[GrabbyHands] Toggle requested.");
            }
            catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] Wheel request failed: " + Detail(ex)); }
            return !ours;
        }
        public static void Tick()
        {
            int requests = Interlocked.Exchange(ref pending, 0);
            CheckFallback();
            if (requests <= 0 || fallbackDetected) return;
            for (int i = 0; i < requests && i < 10; i++)
            {
                if (!enabled && !grabScanDone) InstallGrabHooks();
                if (holdHooks == 0)
                {
                    enabled = false;
                    MelonLogger.Warning("[GrabbyHands] No compatible slot input hook; remains OFF.");
                    break;
                }
                enabled = !enabled;
                if (enabled) { earlyLogs = 0; startLogs = 0; holdLogs = 0; ownershipLogs = 0; pickupLogs = 0; seen = 0; }
                MelonLogger.Msg("[GrabbyHands] " + (enabled ? "ON" : "OFF") + ".");
            }
            UpdateLabels();
        }
        private static DateTime nextFallbackCheck;
        private static bool fallbackDetected;
        private static void CheckFallback()
        {
            if (fallbackDetected || DateTime.UtcNow < nextFallbackCheck) return;
            nextFallbackCheck = DateTime.UtcNow.AddMilliseconds(150);
            foreach (object action in actions)
            {
                try
                {
                    object bubbles = Read(action, "spawnedBubbles");
                    object count = Read(bubbles, "Count");
                    if (count == null || Convert.ToInt32(count) <= 0) continue;
                    fallbackDetected = true;
                    enabled = false;
                    MelonLogger.Warning("[GrabbyHands] Native spawn hook was bypassed: toggle is unavailable; a Fly fallback may have opened. No toggle was performed.");
                    UpdateLabels();
                    break;
                }
                catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] Fallback check failed: " + Detail(ex)); break; }
            }
        }
        private static void UpdateLabels()
        {
            string label = fallbackDetected || (grabScanDone && holdHooks == 0) ? "Grabby Hands: UNAVAILABLE" : "Grabby Hands: " + (enabled ? "ON" : "OFF");
            PropertyInfo prop = typeof(QuickAccessMenuAction).GetProperty("displayName", I);
            foreach (object action in actions)
            {
                try { if (prop?.GetSetMethod(true) != null) prop.SetValue(action, label); }
                catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] Could not refresh label: " + Detail(ex)); }
            }
        }
        // The supplied Mono reference calls CheckHold for local grab input.
        // This fallback runs only if native scoring found no target and all
        // overlapping interactables resolve to one unambiguous PickupDock or
        // its own docked Pickup. Other nearby interactables are not guessed.
        // It asks the GAME to confirm/forward/interact: Pickup.Interact is
        // responsible for Undock(interactor, true, 1). Never pre-undock a slot.
        public static bool BeforeCheckHold(object __instance)
        {
            if (!enabled || insideFallback || __instance == null) return true;
            bool confirmationEntered = false;
            try
            {
                string reason;
                if (!IsLocalHand(__instance, out reason)) { HoldLog("not a verified local hand: " + reason); return true; }
                if (!(SafeRead(__instance, "IsLocal") is bool isLocal) || !isLocal) { HoldLog("IsLocal not verified"); return true; }
                if (!(SafeRead(__instance, "IsInteracting") is bool interacting) || interacting ||
                    !(SafeRead(__instance, "IsToggleHolding") is bool toggleHolding) || toggleHolding)
                { HoldLog("already interacting/toggle holding, or state unavailable"); return true; }
                Type t = __instance.GetType();
                // Do not disable the game's grab assist or require it to be off.
                // A uniquely identified nearby dock gets priority only when
                // direct native selection finds no target. If we actually issue
                // confirmation, this may take priority over a more distant
                // grab-assist target for this single input while the toggle is on.
                MethodInfo select = t.GetMethods(I).FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == "SelectBestFromCurrent" && m.ReturnType == typeof(bool) && p.Length == 1 && p[0].ParameterType.IsByRef;
                });
                if (select == null) { HoldLog("game selector unavailable"); return true; }
                object[] selectArgs = { null };
                insideFallback = true;
                bool nativeHasTarget;
                try
                {
                    object result = select.Invoke(__instance, selectArgs);
                    if (!(result is bool selected)) { HoldLog("game selector returned no boolean"); return true; }
                    nativeHasTarget = selected;
                }
                finally { insideFallback = false; }
                if (nativeHasTarget) { HoldLog("game selected a target; normal grab continues"); return true; }
                HoldLog("native direct selection found no target; checking for one nearby slot");

                Assembly game = typeof(QuickAccessMenuAction).Assembly;
                Type dockType = game.GetType("Il2CppAlta.Inventory.PickupDock", false);
                Type interactableType = game.GetType("Il2Cpp.Interactable", false);
                Type extensionType = game.GetType("Il2Cpp.ColliderExtension", false);
                if (dockType == null || interactableType == null || extensionType == null)
                { HoldLog("slot/collider runtime types unavailable"); return true; }
                MethodInfo overlap = t.GetMethods(I).FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == "GetColliderOverlap" && m.ReturnType == typeof(int) && p.Length == 1 && p[0].ParameterType.IsByRef;
                });
                MethodInfo component = extensionType.GetMethods(S).FirstOrDefault(m =>
                    m.Name == "GetComponentIncludingBody" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1);
                if (overlap == null || component == null) { HoldLog("game collider lookup unavailable"); return true; }
                object[] overlapArgs = { null };
                object countObj = overlap.Invoke(__instance, overlapArgs);
                if (!(countObj is int count) || count <= 0 || overlapArgs[0] == null)
                { HoldLog("no overlap or count unavailable: " + (countObj ?? "null")); return true; }
                List<object> colliders;
                if (count >= 10)
                {
                    // The game's non-alloc buffer is ten colliders; an exact
                    // capacity result may be truncated. Retry the SAME capsule,
                    // radius, collision mask and trigger policy without that cap.
                    colliders = FullOverlapCapsule(__instance);
                    if (colliders == null) { HoldLog("saturated collider buffer; full overlap unavailable, refusing partial result"); return true; }
                    if (colliders.Count >= 64) { HoldLog("full overlap too crowded (" + colliders.Count + "); no target chosen"); return true; }
                    count = colliders.Count;
                    HoldLog("expanded saturated collider buffer: " + count + " collider(s)");
                }
                else
                {
                    colliders = DevWheelBridge.ReadArray(overlapArgs[0]);
                    if (count > colliders.Count) { HoldLog("collider count exceeds buffer"); return true; }
                }
                MethodInfo getInteractable = component.MakeGenericMethod(interactableType);
                var unique = new Dictionary<int, object>();
                for (int i = 0; i < count; i++)
                {
                    if (colliders[i] == null) continue;
                    object item = getInteractable.Invoke(null, new object[] { colliders[i] });
                    if (item == null) continue;
                    unique[Id(item)] = item;
                }
                if (unique.Count == 0) { HoldLog("no overlapping interactable"); return true; }
                // A dock and the particular pickup currently docked there are
                // the same logical target; don't reject that common overlap.
                // Other interactables or two different docks remain ambiguous.
                object target = null;
                object dock = null;
                foreach (object item in unique.Values)
                {
                    object itemDock = QuestModsMenu.TryCast(item, dockType);
                    bool itemIsDock = itemDock != null;
                    if (itemDock == null)
                    {
                        object directPickup = null;
                        foreach (Type pickupType in pickupTypes)
                        {
                            object possible = QuestModsMenu.TryCast(item, pickupType);
                            if (possible != null && SafeRead(possible, "IsDocked") is bool state && state)
                            { directPickup = possible; break; }
                        }
                        if (directPickup == null)
                        { HoldLog("overlap contains non-docked/non-slot target name=" + (SafeRead(item, "name") ?? "unknown") + " proxy=" + item.GetType().FullName + "; no guess"); return true; }
                        object rawDock = SafeRead(directPickup, "Dock");
                        itemDock = rawDock == null ? null : QuestModsMenu.TryCast(rawDock, dockType);
                        object content = itemDock == null ? null : SafeRead(itemDock, "DockedPickup");
                        if (itemDock == null || content == null || Id(content) != Id(directPickup))
                        { HoldLog("overlapping pickup is not the slot's verified current content; no guess"); return true; }
                    }
                    if (dock != null && Id(dock) != Id(itemDock))
                    { HoldLog("multiple distinct slots overlap; no guess"); return true; }
                    dock = itemDock;
                    // Prefer the dock if both representations were hit; otherwise
                    // send the exact touched pickup through native forwarding.
                    if (target == null || itemIsDock) target = item;
                }
                if (dock == null || target == null) { HoldLog("no unambiguous slot"); return true; }
                if (unique.Count > 1) HoldLog("overlap resolves to one slot and its own pickup");
                object active = SafeRead(SafeRead(dock, "gameObject"), "activeInHierarchy");
                if (!(active is bool activeNow) || !activeNow) { HoldLog("slot not active or activity unavailable"); return true; }
                if (!(SafeRead(dock, "HasDockedPickup") is bool hasPickup) || !hasPickup)
                { HoldLog("slot has no readable docked pickup"); return true; }
                object quantity = SafeRead(dock, "QuantityStored");
                if (!(quantity is int amount) || amount <= 0) { HoldLog("slot quantity empty or unavailable"); return true; }
                object pickup = SafeRead(dock, "DockedPickup");
                if (pickup == null || !(SafeRead(pickup, "IsDocked") is bool isDocked) || !isDocked)
                { HoldLog("docked pickup state unavailable"); return true; }
                object pickupDock = SafeRead(pickup, "Dock");
                if (pickupDock == null || Id(pickupDock) != Id(dock)) { HoldLog("pickup does not belong to this slot"); return true; }
                object ownerFlag = SafeRead(dock, "IsOwnedByPlayer");
                if (!(ownerFlag is bool owned)) { HoldLog("slot ownership unavailable"); return true; }
                if (owned)
                {
                    object controller = SafeRead(__instance, "Controller");
                    object player = SafeRead(controller, "PlayerController");
                    object owner = SafeRead(dock, "PlayerController");
                    if (owner == null || player == null || Id(owner) != Id(player))
                    { HoldLog("slot belongs to another player or ownership is unknown"); return true; }
                }
                MethodInfo confirm = t.GetMethods(I).FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == "GetConfirmationOnInteractable" && m.ReturnType == typeof(void) && p.Length == 1 && p[0].ParameterType == interactableType;
                });
                MethodInfo onAttempt = t.GetMethods(I).FirstOrDefault(m => m.Name == "OnStartInteractionAttempt" &&
                    m.GetParameters().Length == 0 && m.ReturnType == typeof(void));
                if (confirm == null || onAttempt == null) { HoldLog("native confirmation/attempt method unavailable"); return true; }
                object arg = interactableType.IsInstanceOfType(target) ? target : QuestModsMenu.TryCast(target, interactableType);
                if (arg == null) { HoldLog("could not cast selected slot"); return true; }
                HoldLog("unique slot=" + (SafeRead(dock, "name") ?? "unknown") + " quantity=" + amount +
                    "; requesting native confirmation; game will forward and undock if allowed");
                insideFallback = true;
                try
                {
                    onAttempt.Invoke(__instance, null);
                    // Even if this throws partway through, do not run a second
                    // native grab that might duplicate partial side effects.
                    confirmationEntered = true;
                    confirm.Invoke(__instance, new object[] { arg });
                    object held = SafeRead(__instance, "InteractingWith");
                    object currentContent = SafeRead(dock, "DockedPickup");
                    string heldName = held == null ? "none/unknown" : (SafeRead(held, "name") ?? "unknown").ToString();
                    string heldRelation = "unknown";
                    try
                    {
                        if (held != null) heldRelation = Id(held) == Id(pickup) ? "original pickup" : (Id(held) == Id(dock) ? "slot" : "other");
                    }
                    catch { }
                    string contentRelation = "none/unknown";
                    try { if (currentContent != null) contentRelation = Id(currentContent) == Id(pickup) ? "original pickup" : "replacement/different"; } catch { }
                    HoldLog("confirmation returned; Interacting=" + (SafeRead(__instance, "IsInteracting") ?? "unknown") +
                        " held=" + heldName + " (" + heldRelation + ") original IsDocked=" + (SafeRead(pickup, "IsDocked") ?? "unknown") +
                        " slot content=" + contentRelation);
                }
                finally { insideFallback = false; }
                return false;
            }
            catch (Exception ex)
            {
                HoldLog("slot fallback failed: " + Detail(ex) + (confirmationEntered ? "; not duplicating confirmation" : "; normal grab continues"));
                return !confirmationEntered;
            }
        }
        // Repeats the reference Interactor.GetColliderOverlap capsule only when
        // the game's 10-element buffer is full. No guessed larger radius/mask.
        private static List<object> FullOverlapCapsule(object interactor)
        {
            try
            {
                object trigger = SafeRead(interactor, "triggerObjectTransform") ?? SafeRead(interactor, "TriggerObjectTransform");
                object end = SafeRead(trigger, "position");
                object start = SafeRead(interactor, "lastPhysicsTriggerPosition");
                object scale = SafeRead(SafeRead(interactor, "transform"), "lossyScale");
                object sx = SafeRead(scale, "x");
                object radius = SafeRead(interactor, "radius");
                object mask = SafeRead(interactor, "collisionMask");
                if (start == null || end == null || sx == null || radius == null || !(mask is int)) return null;
                float worldRadius = Math.Abs(Convert.ToSingle(sx)) * Convert.ToSingle(radius);
                if (float.IsNaN(worldRadius) || float.IsInfinity(worldRadius) || worldRadius <= 0f) return null;
                Type physics = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.Physics", false)).FirstOrDefault(x => x != null);
                if (physics == null) return null;
                MethodInfo method = physics.GetMethods(BindingFlags.Static | BindingFlags.Public).Where(m => m.Name == "OverlapCapsule")
                    .FirstOrDefault(m =>
                    {
                        ParameterInfo[] p = m.GetParameters();
                        return p.Length == 4 && p[0].ParameterType == start.GetType() && p[1].ParameterType == end.GetType() &&
                               p[2].ParameterType == typeof(float) && p[3].ParameterType == typeof(int);
                    });
                object result;
                if (method != null) result = method.Invoke(null, new object[] { start, end, worldRadius, mask });
                else
                {
                    method = physics.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(m =>
                    {
                        ParameterInfo[] p = m.GetParameters();
                        return m.Name == "OverlapCapsule" && p.Length == 5 && p[0].ParameterType == start.GetType() &&
                               p[1].ParameterType == end.GetType() && p[2].ParameterType == typeof(float) &&
                               p[3].ParameterType == typeof(int) && p[4].ParameterType.IsEnum;
                    });
                    if (method == null) return null;
                    // Unity QueryTriggerInteraction.UseGlobal has value 0, the
                    // same default as the game's four-argument non-alloc call.
                    result = method.Invoke(null, new object[] { start, end, worldRadius, mask, Enum.ToObject(method.GetParameters()[4].ParameterType, 0) });
                }
                if (result == null) return null;
                if (result is Array managedArray)
                {
                    if (managedArray.Length > 200) return null;
                    var managed = new List<object>();
                    foreach (object collider in managedArray) managed.Add(collider);
                    return managed;
                }
                return DevWheelBridge.ReadArray(result);
            }
            catch (Exception ex) { HoldLog("expanded overlap failed: " + Detail(ex)); return null; }
        }
        private static void HoldLog(string message)
        {
            if (holdLogs++ < 100) MelonLogger.Msg("[GrabbyHands] CheckHold: " + message);
        }
        // Observes only; undocking before StartInteract can break the game's
        // PickupDock.CheckForward and its dock-specific grab behaviour.
        public static void BeforeInteract(object __instance, object __0, bool __1)
        {
            if (!enabled || __instance == null || __0 == null || __1) return;
            try
            {
                string reason;
                if (!IsLocalHand(__instance, out reason)) return;
                if (startLogs++ < 16) MelonLogger.Msg("[GrabbyHands] Native StartInteract target=" +
                    (SafeRead(__0, "name") ?? "unknown") + "; local hand verified; normal forwarding/undock unchanged.");
            }
            catch (Exception ex) { if (startLogs++ < 16) MelonLogger.Warning("[GrabbyHands] Interaction diagnostic: " + Detail(ex)); }
        }
        // Authorize only an exact interactor or controller identity belonging
        // to a local wheel/controller, never proximity or ancestor-name guesses.
        private static bool IsLocalHand(object interactor, out string reason)
        {
            reason = "unknown";
            try
            {
                object localValue = SafeRead(interactor, "IsLocal");
                if (localValue is bool flag && !flag)
                { reason = "Interactor.IsLocal=False"; return false; }
                int iid = Id(interactor);
                object controller = SafeRead(interactor, "Controller") ?? SafeRead(interactor, "controller");
                int cid = controller == null ? 0 : Id(controller);
                if (!localInteractorIds.Contains(iid) && (cid == 0 || !localControllerIds.Contains(cid)))
                    RememberCurrentPlayerControllers();
                bool byInteractor = localInteractorIds.Contains(iid);
                bool byController = cid != 0 && localControllerIds.Contains(cid);
                reason = "interactor=" + iid + " controller=" + cid + " IsLocal=" + (localValue ?? "unknown") +
                    " exactInteractor=" + byInteractor + " exactController=" + byController +
                    " knownControllers=" + localControllerIds.Count + " knownInteractors=" + localInteractorIds.Count;
                return byInteractor || byController;
            }
            catch (Exception ex) { reason = "identity lookup failed: " + Detail(ex); return false; }
        }
        private static object SafeRead(object obj, string name)
        {
            try { return Read(obj, name); } catch { return null; }
        }
        private static int Id(object obj)
        {
            if (obj == null) throw new Exception("null Unity object");
            MethodInfo method = obj.GetType().GetMethod("GetInstanceID", I, null, Type.EmptyTypes, null);
            if (method == null) throw new Exception("GetInstanceID unavailable on " + obj.GetType().FullName);
            return Convert.ToInt32(method.Invoke(obj, null));
        }
        private static object Read(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType(); PropertyInfo p = t.GetProperty(name, I);
            if (p != null && p.GetGetMethod(true) != null && p.GetIndexParameters().Length == 0) return p.GetValue(obj);
            return t.GetField(name, I)?.GetValue(obj);
        }
        private static string Detail(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

}
