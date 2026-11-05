// QuestMods 1.12.0 - mod wheel ("Quest Mods") for the Quest port of A Township Tale.
// 1.10.0 added a "Super Speed" (5x local speed) toggle beside Grabby Hands and
// per-action bubble colours (Fly red, Grabby Hands green, Super Speed blue).
// 1.10.1 broadened every lookup. 1.11.0 fixed the speed stat route (it now
// resolves live StatManager instances and writes Stat.Base), widened Grabby
// Hands to any slot-like object, and added the "Grab by Name" bubble.
// 1.13.0 lets Grabby Hands reach into another player's worn backpack or pouch:
// the slot is detected as foreign (not in the local hand's hierarchy) and the
// pickup docked inside it is targeted instead of the slot itself.
// 1.12.0 turns the grab-by-name bubble into a three-item group:
//   - "Grab by Name" grabs matching pickups (amount from its own wheel item),
//   - "Type Keyword" spawns the platform keyboard (UnityEngine.TouchScreenKeyboard)
//     so the keyword can be typed in headset, and saves it to UserData,
//   - "Grab Amount" cycles 1 / 5 / 10 / 20 / 40 / 100 pickups per press.
// Grabby Hands now also reports the owner of the slot it chose (for slots worn
// by other players) and Super Speed no longer floods the log with every live
// StatManager.
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

[assembly: MelonInfo(typeof(QuestMods.Mod), "QuestMods", "1.13.0", "you")]
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
            LoggerInstance.Msg("=== QuestMods 1.13.0: experimental Quest Mods wheel ===");
            PatchInitializers();
            GrabbyHands.Install(HarmonyInstance);
            SuperSpeed.Install();
            MelonLogger.Msg("[GrabByName] Keyword file: " + (GrabByName.KeywordPath() ?? "unresolved") + " (one line: an item-name fragment).");
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
            LoggerInstance.Msg("Super Speed defaults OFF and only writes the local '" + SuperSpeed.StatName + "' stat while ON (5x). Bubble colours are display only.");
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
            SuperSpeed.SceneLoaded();
            GrabByName.SceneLoaded();
            BubbleTint.SceneLoaded();
            LoggerInstance.Msg($"Scene loaded: '{sceneName}' ({buildIndex}). {Counts()}");
            try { TabletProbe.SceneLoaded(buildIndex, sceneName); DevWheelBridge.SceneLoaded(); }
            catch (Exception ex) { LoggerInstance.Warning("Tablet probe scene event: " + ex.Message); }
        }

        public override void OnUpdate()
        {
            try { TabletProbe.Tick(); DevWheelBridge.Tick(); DebugStateMonitor.Tick(); GrabbyHands.Tick(); SuperSpeed.Tick(); GrabByName.Tick(); BubbleTint.Tick(); }
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
                        grabby = CreateToggleClone(source, instantiate, spawnedProperty, nestedProperty,
                                                   debugAction, nameProperty, "Grabby Hands: OFF");
                        GrabbyHands.Register(grabby);
                        made.Add(grabby);
                        pages.Add(grabby);
                        BubbleTint.Register(grabby, "Grabby Hands: ON / OFF", 0.03f, 1.00f, 0.08f);
                    }
                    catch (Exception ex)
                    {
                        GrabbyHands.Unregister(grabby);
                        if (grabby != null) DestroyClone(grabby);
                        MelonLogger.Warning("[GrabbyHands] Could not create wheel toggle: " + ex);
                    }
                }
                if (SuperSpeed.Available && debugAction != null)
                {
                    // Sits directly beside Grabby Hands as a second direct toggle.
                    object speed = null;
                    try
                    {
                        speed = CreateToggleClone(source, instantiate, spawnedProperty, nestedProperty,
                                                  debugAction, nameProperty, "Super Speed: OFF");
                        SuperSpeed.Register(speed);
                        made.Add(speed);
                        pages.Add(speed);
                        BubbleTint.Register(speed, "Super Speed: ON / OFF", 0.05f, 0.30f, 1.00f);
                    }
                    catch (Exception ex)
                    {
                        SuperSpeed.Unregister(speed);
                        if (speed != null) DestroyClone(speed);
                        MelonLogger.Warning("[SuperSpeed] Could not create wheel toggle: " + ex);
                    }
                }
                if (GrabByName.Available && debugAction != null)
                {
                    // Grab group: one action that grabs, one that opens the
                    // keyboard for the keyword, one that cycles the amount.
                    CreateGrabItem(source, instantiate, spawnedProperty, nestedProperty, debugAction, nameProperty,
                                   made, pages, GrabByName.KindRun, "Grab by Name", "Grab by Name (one shot)",
                                   0.85f, 0.05f, 1.00f);
                    CreateGrabItem(source, instantiate, spawnedProperty, nestedProperty, debugAction, nameProperty,
                                   made, pages, GrabByName.KindType, "Type Keyword", "Type Keyword (keyboard)",
                                   1.00f, 0.10f, 0.75f);
                    CreateGrabItem(source, instantiate, spawnedProperty, nestedProperty, debugAction, nameProperty,
                                   made, pages, GrabByName.KindAmount, "Grab Amount: 10", "Grab Amount",
                                   0.05f, 0.90f, 0.95f);
                }
                if (debugAction != null)
                    BubbleTint.Register(debugAction, "Fly (Debug Movement)", 1.00f, 0.02f, 0.02f);
                if (pages.Count == 0) throw new Exception("No mod actions are available; cannot create empty Quest Mods menu.");
                object root = CloneNest(source, instantiate, spawnedProperty);
                made.Add(root);
                nameProperty.SetValue(root, "Quest Mods");
                BubbleTint.RegisterRoot(root);

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
                foreach (object obj in made)
                {
                    GrabbyHands.Unregister(obj);
                    SuperSpeed.Unregister(obj);
                    GrabByName.Unregister(obj);
                    BubbleTint.Unregister(obj);
                    DestroyClone(obj);
                }
                BubbleTint.ClearRoots();
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

        // A direct toggle is a native nesting action clone that never opens a
        // submenu: the shared spawn hook suppresses exactly these clones. If the
        // hook is ever bypassed, the single native Fly child is a harmless page.
        private static object CreateToggleClone(object source, MethodInfo instantiate, PropertyInfo spawnedProperty,
                                                PropertyInfo nestedProperty, object fallbackChild,
                                                PropertyInfo nameProperty, string initialLabel)
        {
            object clone = null;
            try
            {
                clone = CloneNest(source, instantiate, spawnedProperty);
                object sourceArray = nestedProperty.GetValue(source);
                if (sourceArray == null) throw new Exception("Source nesting action has no nestedActions.");
                Type nativeArrayType = sourceArray.GetType();
                ConstructorInfo itemCtor = nativeArrayType.GetConstructor(new[] { typeof(long) }) ?? nativeArrayType.GetConstructor(new[] { typeof(int) });
                if (itemCtor == null) throw new Exception("No action-array constructor for " + initialLabel + ".");
                object fallbackChildren = itemCtor.Invoke(new[] { Convert.ChangeType(1, itemCtor.GetParameters()[0].ParameterType) });
                PropertyInfo item = nativeArrayType.GetProperties(Instance).FirstOrDefault(p => p.Name == "Item" && p.GetIndexParameters().Length == 1 && p.GetSetMethod(true) != null);
                if (item == null) throw new Exception("No action-array setter for " + initialLabel + ".");
                item.SetValue(fallbackChildren, fallbackChild, new[] { Convert.ChangeType(0, item.GetIndexParameters()[0].ParameterType) });
                nestedProperty.SetValue(clone, fallbackChildren);
                nameProperty.SetValue(clone, initialLabel);
                return clone;
            }
            catch
            {
                if (clone != null) DestroyClone(clone);
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

        // One "Grab by Name" wheel item: a clone of the native debug action that
        // is routed to GrabByName by its own label and instance id.
        private static object CreateGrabItem(object source, MethodInfo instantiate, PropertyInfo spawnedProperty,
                                             PropertyInfo nestedProperty, object fallbackChild, PropertyInfo nameProperty,
                                             List<object> made, List<object> pages, int kind, string label,
                                             string tintLabel, float red, float green, float blue)
        {
            object action = null;
            try
            {
                action = CreateToggleClone(source, instantiate, spawnedProperty, nestedProperty,
                                           fallbackChild, nameProperty, label);
                GrabByName.Register(action, kind);
                made.Add(action);
                pages.Add(action);
                BubbleTint.Register(action, tintLabel, red, green, blue);
            }
            catch (Exception ex)
            {
                GrabByName.Unregister(action);
                if (action != null) DestroyClone(action);
                MelonLogger.Warning("[GrabByName] Could not create '" + label + "': " + ex);
            }
            return action;
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

    // One shared prefix body for the single native SpawnNestedBubbles hook.
    // Each direct toggle claims only its own clone; every other nesting page
    // (including the Quest Mods root and the game's developer pages) is untouched.
    internal static class WheelToggleRouter
    {
        public static bool BeforeSpawn(object __instance)
        {
            if (__instance == null) return true;
            try { if (!GrabbyHands.BeforeWheelRun(__instance)) return false; }
            catch { /* never block the game's own menus on our failure */ }
            try { if (!SuperSpeed.BeforeWheelRun(__instance)) return false; }
            catch { }
            try { if (!GrabByName.BeforeWheelRun(__instance)) return false; }
            catch { }
            return true;
        }
    }

    // "Super Speed" direct wheel toggle (5x local movement).
    // Behaviour mirrors the game's own ToggleLocalSpeedMenuAction: read the local
    // PlayerCharacter, resolve its Health.StatManager, look up the "speed" stat
    // with GetStat("speed", out Stat), then flip Stat.Base between the normal
    // value (1) and a boosted value (here 5 instead of 2). Everything is done
    // through runtime reflection, so no new compile-time game reference is
    // needed. Default OFF; the state is not persisted and is not re-applied on
    // scene load.
    // "Super Speed" direct wheel toggle (5x local movement).
    // Mirrors the game's own ToggleLocalSpeedMenuAction: locate the local
    // player's character, resolve its stat manager, look the "speed" stat up and
    // flip Stat.Base between 1 (normal) and 5 (this mod's multiplier instead of
    // the game's 2). Everything goes through runtime reflection, so no new
    // compile-time game reference is needed. Default OFF, reset on scene load.
    //
    // 1.10.1: the first build found a stat manager but no "speed" stat, so the
    // lookup now tries every plausible manager on the character, several
    // GetStat shapes, a string indexer and a plain stat-collection scan, and
    // logs a one-time, bounded diagnostic dump of what the proxies expose.
    internal static class SuperSpeed
    {
        public const string StatName = "speed";
        private const float NormalSpeed = 1f;
        private const float BoostedSpeed = 5f;
        private const string LabelPrefix = "Super Speed: ";
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] StatNames = { "speed", "Speed", "SPEED" };
        private static readonly string[] CollectionNames =
        {
            "Stats", "stats", "AllStats", "allStats", "StatDictionary", "statDictionary",
            "StatList", "statList", "BaseStats", "baseStats", "m_Stats", "_stats"
        };

        private static readonly List<object> actions = new List<object>();
        private static readonly HashSet<int> ids = new HashSet<int>();
        private static bool enabled, fallbackDetected, locomotionFallbackActive;
        private static int pending, applyLogs, diagnosticsLogged;
        private static DateTime nextLocomotionReapply;
        private static DateTime nextFallbackCheck;
        private static Type playerCharacterType, controllerType, playerType;
        private static PropertyInfo controllerCurrent, playerCurrent;
        private static bool typesResolved;

        public static bool Available => GrabbyHands.Available;
        public static bool Enabled => enabled;

        public static void Install()
        {
            // Startup stays passive: like Grabby Hands nothing is hooked until it
            // is safe to add the wheel item in-game.
            MelonLogger.Msg("[SuperSpeed] Passive at startup. Toggle is added next to Grabby Hands; default OFF, 5x when ON.");
        }

        public static void Register(object action)
        {
            try
            {
                int id = Id(action);
                ids.Add(id);
                actions.Add(action);
                UpdateLabels();
                MelonLogger.Msg("[SuperSpeed] Added wheel toggle id=" + id + " (default OFF).");
            }
            catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] Could not register toggle: " + Detail(ex)); }
        }

        public static void Unregister(object action)
        {
            try { if (action != null) { ids.Remove(Id(action)); actions.Remove(action); } } catch { }
        }

        public static void SceneLoaded()
        {
            Interlocked.Exchange(ref pending, 0);
            // A new scene creates a new local character whose stat starts at the
            // game's normal value. Objects from the old scene are never touched.
            if (enabled) MelonLogger.Msg("[SuperSpeed] Turned OFF on scene load; the new character starts at normal speed.");
            enabled = false;
            fallbackDetected = false;
            applyLogs = 0;
            locomotionOriginals.Clear();
            locomotionFallbackActive = false;
            UpdateLabels();
            actions.Clear();
            // IDs are kept so live clones stay ignored by the native submenu spawn.
        }

        public static bool BeforeWheelRun(object __instance)
        {
            bool ours = false;
            try
            {
                if (__instance == null || !ids.Contains(Id(__instance))) return true;
                string name = Read(__instance, "DisplayName") as string;
                if (name == null || !name.StartsWith(LabelPrefix, StringComparison.Ordinal)) return true;
                ours = true;
                Interlocked.Increment(ref pending);
                MelonLogger.Msg("[SuperSpeed] Toggle requested.");
            }
            catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] Wheel request failed: " + Detail(ex)); }
            return !ours;
        }

        public static void Tick()
        {
            int requests = Interlocked.Exchange(ref pending, 0);
            CheckFallback();
            if (enabled && locomotionFallbackActive && DateTime.UtcNow >= nextLocomotionReapply)
            {
                // Locomotion speed floats can be recomputed by the game every
                // frame, so re-apply the recorded multiplier while it is ON.
                nextLocomotionReapply = DateTime.UtcNow.AddMilliseconds(500);
                TryLocomotionSpeed(true);
            }
            if (requests <= 0 || fallbackDetected) return;
            // Applied on the update loop, never inside the wheel callback.
            for (int i = 0; i < requests && i < 10; i++)
            {
                bool desired = !enabled;
                if (Apply(desired)) enabled = desired;
                else MelonLogger.Warning("[SuperSpeed] Speed change was not applied; toggle state left unchanged.");
            }
            UpdateLabels();
        }

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
                    MelonLogger.Warning("[SuperSpeed] Native spawn hook was bypassed: the Super Speed toggle is unavailable and no speed change was made.");
                    UpdateLabels();
                    break;
                }
                catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] Fallback check failed: " + Detail(ex)); break; }
            }
        }

        private static void UpdateLabels()
        {
            string label = fallbackDetected ? LabelPrefix + "UNAVAILABLE" : LabelPrefix + (enabled ? "ON (5x)" : "OFF");
            PropertyInfo property = typeof(QuickAccessMenuAction).GetProperty("displayName", I);
            foreach (object action in actions)
            {
                try { if (property?.GetSetMethod(true) != null) property.SetValue(action, label); }
                catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] Could not refresh label: " + Detail(ex)); }
            }
        }

        private static bool Apply(bool boosted)
        {
            try
            {
                object character = FindPlayerCharacter();
                if (character == null)
                {
                    MelonLogger.Warning("[SuperSpeed] Local player character is not available yet.");
                    return false;
                }
                List<KeyValuePair<string, object>> managers = FindStatManagers(character);
                if (managers.Count == 0)
                {
                    if (TryLocomotionSpeed(boosted))
                    {
                        locomotionFallbackActive = boosted;
                        MelonLogger.Msg("[SuperSpeed] " + (boosted ? "ON (5x)" : "OFF") + " applied through the locomotion fallback.");
                        return true;
                    }
                    DumpDiagnostics(character, null);
                    MelonLogger.Warning("[SuperSpeed] No stat manager found (character.Health=" + Describe(SafeRead(character, "Health")) +
                        ", locomotion=" + Describe(FindLocomotionOf()) + ").");
                    return false;
                }
                float target = boosted ? BoostedSpeed : NormalSpeed;
                object usedStat = null, usedManager = null;
                string usedFrom = null;
                foreach (KeyValuePair<string, object> entry in managers)
                {
                    string how;
                    object stat = FindSpeedStat(entry.Value, out how);
                    if (stat == null) continue;
                    if (!TrySetFloat(stat, "Base", target)) continue;
                    usedStat = stat;
                    usedManager = entry.Value;
                    usedFrom = entry.Key + " via " + how;
                    break;
                }
                if (usedStat == null)
                {
                    // Second route: the local locomotion component often holds the
                    // same speed stat (or its speed floats) directly.
                    if (TryLocomotionSpeed(boosted))
                    {
                        locomotionFallbackActive = boosted;
                        MelonLogger.Msg("[SuperSpeed] " + (boosted ? "ON (5x)" : "OFF") + " applied through the locomotion fallback.");
                        return true;
                    }
                    DumpDiagnostics(character, managers[0].Value);
                    MelonLogger.Warning("[SuperSpeed] No writable '" + StatName + "' stat among " + managers.Count +
                        " candidate manager(s): the locomotion fallback did not find one either (" + DescribeManagers(managers) + ").");
                    return false;
                }
                if (locomotionOriginals.Count > 0)
                {
                    // The stat route took over; undo any earlier fallback scaling.
                    TryLocomotionSpeed(false);
                }
                locomotionFallbackActive = false;
                // Confirm the write survived. A boxed struct copy would not.
                string verifyHow;
                object verify = FindSpeedStat(usedManager, out verifyHow);
                float newBase = float.NaN;
                if (verify != null) TryGetFloat(verify, "Base", out newBase);
                bool confirmed = !float.IsNaN(newBase) && Math.Abs(newBase - target) < 0.01f;
                if (!confirmed)
                    MelonLogger.Warning("[SuperSpeed] The stat write could not be confirmed by a re-read; assuming the proxy wrote through.");
                if (applyLogs++ < 20)
                    MelonLogger.Msg("[SuperSpeed] " + (boosted ? "ON (5x)" : "OFF") + ": " + StatName + ".Base -> " +
                        (float.IsNaN(newBase) ? "?" : newBase.ToString("0.##")) + " from " + usedFrom +
                        "; stat=" + usedStat.GetType().FullName + ", manager=" + usedManager.GetType().FullName + ".");
                return true;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[SuperSpeed] Speed change failed: " + Detail(ex));
                return false;
            }
        }

        // Fallback used only when no stat manager exposes "speed". The local
        // SmoothLocomotion component is reachable the same way the debug-movement
        // monitor reads it, and a game speed stat or plain speed floats can live
        // on it. Originals are recorded once and restored when turned off.
        private static readonly Dictionary<string, float> locomotionOriginals = new Dictionary<string, float>();

        private static bool TryLocomotionSpeed(bool boosted)
        {
            object controller = null;
            try { controller = controllerCurrent?.GetValue(null); } catch { }
            object locomotion = FindLocomotion(controller);
            if (locomotion == null)
            {
                if (locomotionOriginals.Count > 0) locomotionOriginals.Clear();
                return false;
            }
            int id;
            try { id = Id(locomotion); } catch { id = 0; }
            float target = boosted ? BoostedSpeed : NormalSpeed;
            var names = new List<string>();
            foreach (PropertyInfo property in locomotion.GetType().GetProperties(I))
            {
                if (property.GetIndexParameters().Length == 0) names.Add(property.Name);
            }
            for (Type current = locomotion.GetType(); current != null; current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(I | BindingFlags.DeclaredOnly)) names.Add(field.Name);
            }
            int changed = 0;
            foreach (string name in names)
            {
                if (name.IndexOf("speed", StringComparison.OrdinalIgnoreCase) < 0) continue;
                // A speed member that is itself a Stat proxy is the cleanest hit.
                object holder = SafeRead(locomotion, name);
                if (holder != null && holder != locomotion && HasBase(holder))
                {
                    if (TrySetFloat(holder, "Base", target))
                    {
                        float readBack;
                        bool ok = TryGetFloat(holder, "Base", out readBack) && Math.Abs(readBack - target) < 0.01f;
                        MelonLogger.Msg("[SuperSpeed] Locomotion fallback: wrote " + name + ".Base -> " + target +
                            " (" + (ok ? "confirmed" : "not confirmed") + "; proxy " + holder.GetType().FullName + ").");
                        if (ok) return true;
                    }
                }
                string lower = name.ToLowerInvariant();
                if (lower.Contains("debug") || lower.Contains("turn") || lower.Contains("rotation") ||
                    lower.Contains("anim") || lower.Contains("lerp") || lower.Contains("fov") ||
                    lower.Contains("volume") || lower.Contains("threshold")) continue;
                float value;
                if (!TryReadFloatMember(locomotion, name, out value)) continue;
                string key = id + ":" + name;
                if (boosted)
                {
                    if (!locomotionOriginals.ContainsKey(key)) locomotionOriginals[key] = value;
                    // A squared speed field needs its square root scaled by the
                    // multiplier to yield a 5x linear speed (otherwise it would
                    // read as 25x).
                    float factor = lower.Contains("squared") ? (float)Math.Sqrt(BoostedSpeed) : BoostedSpeed;
                    float scaled = locomotionOriginals[key] * factor;
                    if (TrySetFloat(locomotion, name, scaled))
                    {
                        if (changed++ < 8)
                            MelonLogger.Msg("[SuperSpeed] Locomotion fallback: " + name + " " +
                                locomotionOriginals[key].ToString("0.###") + " -> " + scaled.ToString("0.###") +
                                " (x" + factor.ToString("0.###") + ").");
                    }
                }
                else
                {
                    float original;
                    if (!locomotionOriginals.TryGetValue(key, out original)) continue;
                    if (TrySetFloat(locomotion, name, original)) { changed++; locomotionOriginals.Remove(key); }
                }
            }
            return changed > 0;
        }

        private static object FindLocomotionOf()
        {
            object controller = null;
            try { controller = controllerCurrent?.GetValue(null); } catch { }
            return FindLocomotion(controller);
        }

        private static object FindLocomotion(object controller)
        {
            if (controller == null) return null;
            Type smoothType = FindType("Il2Cpp.SmoothLocomotionPlayerController") ?? FindTypeBySimpleName("SmoothLocomotionPlayerController");
            object smooth = null;
            if (smoothType != null)
                smooth = smoothType.IsInstanceOfType(controller) ? controller : QuestModsMenu.TryCast(controller, smoothType);
            object source = smooth ?? controller;
            foreach (string name in new[] { "SmoothLocomotion", "smoothLocomotion", "Locomotion", "locomotion", "Movement", "movement" })
            {
                object direct = SafeRead(source, name);
                if (direct != null) return direct;
            }
            return null;
        }

        private static bool TryReadFloatMember(object target, string name, out float value)
        {
            value = 0f;
            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, I);
                if (property != null && property.PropertyType == typeof(float) && property.GetGetMethod(true) != null &&
                    property.GetIndexParameters().Length == 0)
                { value = (float)property.GetValue(target); return true; }
                FieldInfo field = FindField(type, name);
                if (field != null && field.FieldType == typeof(float))
                { value = (float)field.GetValue(target); return true; }
            }
            catch { }
            return false;
        }

        private static object FindPlayerCharacter()
        {
            ResolveTypes();
            object current = null;
            try { current = controllerCurrent?.GetValue(null); } catch { }
            object character = AsCharacter(current);
            if (character != null) return character;
            // Some builds expose the character only through Player.Current.
            object player = null;
            try { player = playerCurrent?.GetValue(null); } catch { }
            if (player != null)
            {
                foreach (string name in new[] { "PlayerCharacter", "playerCharacter", "Character", "character", "PlayerController", "Controller" })
                {
                    character = AsCharacter(SafeRead(player, name));
                    if (character != null) return character;
                }
            }
            // Last resort: a live component that belongs to the local hierarchy.
            foreach (string typeName in new[] { "PlayerCharacter", "Health" })
            {
                object local = FindLocalComponent(typeName, current);
                if (local != null) return local;
            }
            return null;
        }

        private static object AsCharacter(object candidate)
        {
            if (candidate == null) return null;
            if (playerCharacterType != null)
            {
                if (playerCharacterType.IsInstanceOfType(candidate)) return candidate;
                object cast = QuestModsMenu.TryCast(candidate, playerCharacterType);
                if (cast != null) return cast;
            }
            // Any proxy that actually exposes Health (or a stat manager) works
            // with the reference member chain, even without the exact type.
            return SafeRead(candidate, "Health") != null || SafeRead(candidate, "StatManager") != null ? candidate : null;
        }

        // Every plausible route from the character to a stat manager. The
        // 1.10.1 log showed PlayerCharacter.Health reading null and no
        // StatManager member on the character, while SmoothLocomotion exposes
        // Character / character and the assembly carries PlayerStatManager and
        // IStatManager. So: gather candidate owners, accept anything that can
        // answer a GetStat-style call, then fall back to live managers.
        private static List<KeyValuePair<string, object>> FindStatManagers(object character)
        {
            var found = new List<KeyValuePair<string, object>>();
            var seen = new HashSet<int>();
            var owners = new List<KeyValuePair<string, object>>();
            object controller = null;
            try { controller = controllerCurrent?.GetValue(null); } catch { }
            object player = null;
            try { player = playerCurrent?.GetValue(null); } catch { }

            AddOwner(owners, character, "character");
            AddOwner(owners, controller, "controller");
            object locomotion = FindLocomotion(controller);
            AddOwner(owners, locomotion, "locomotion");
            if (locomotion != null)
            {
                AddOwner(owners, SafeRead(locomotion, "Character") ?? SafeRead(locomotion, "character"), "locomotion.Character");
                AddOwner(owners, SafeRead(locomotion, "CharacterController") ?? SafeRead(locomotion, "characterController"), "locomotion.CharacterController");
                AddOwner(owners, SafeRead(locomotion, "Controller") ?? SafeRead(locomotion, "controller"), "locomotion.Controller");
            }
            AddOwner(owners, player, "player");
            if (player != null)
            {
                foreach (string inner in new[] { "PlayerCharacter", "playerCharacter", "Character", "character", "PlayerController", "Controller" })
                    AddOwner(owners, SafeRead(player, inner), "player." + inner);
            }
            foreach (KeyValuePair<string, object> owner in owners.ToArray())
            {
                AddOwner(owners, SafeRead(owner.Value, "Health") ?? SafeRead(owner.Value, "health"), owner.Key + ".Health");
                AddOwner(owners, SafeRead(owner.Value, "PlayerCharacter"), owner.Key + ".PlayerCharacter");
            }

            foreach (KeyValuePair<string, object> owner in owners)
            {
                if (owner.Value == null) continue;
                if (LooksLikeManager(owner.Value)) AddManager(found, seen, owner.Value, owner.Key);
                foreach (string name in new[] { "StatManager", "statManager", "Stats", "stats", "StatCollection", "statCollection" })
                    AddManager(found, seen, SafeRead(owner.Value, name), owner.Key + "." + name);
                ScanStatMembers(found, seen, owner.Value, owner.Key);
            }
            AddManagersByType(found, seen);
            if (found.Count > 0) MelonLogger.Msg("[SuperSpeed] Stat manager candidate(s)=" + found.Count + "; first: " + DescribeManagers(found) + ".");
            return found;
        }

        private static void AddOwner(List<KeyValuePair<string, object>> owners, object owner, string label)
        {
            if (owner == null) return;
            int id;
            try { id = Id(owner); } catch { return; }
            foreach (KeyValuePair<string, object> existing in owners)
            {
                try { if (Id(existing.Value) == id) return; } catch { }
            }
            owners.Add(new KeyValuePair<string, object>(label, owner));
        }

        // Any member whose name mentions "stat" and whose value can answer a
        // GetStat-style call is a manager. This catches the real member name on
        // a build where it is not spelled "StatManager".
        private static void ScanStatMembers(List<KeyValuePair<string, object>> found, HashSet<int> seen, object owner, string label)
        {
            var names = new List<string>();
            try
            {
                Type type = owner.GetType();
                foreach (PropertyInfo property in type.GetProperties(I))
                {
                    if (property.GetIndexParameters().Length != 0 || property.GetGetMethod(true) == null) continue;
                    if (property.Name.IndexOf("stat", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    names.Add(property.Name);
                }
                for (Type current = type; current != null; current = current.BaseType)
                {
                    foreach (FieldInfo field in current.GetFields(I | BindingFlags.DeclaredOnly))
                    {
                        if (field.Name.IndexOf("stat", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        names.Add(field.Name);
                    }
                }
            }
            catch { }
            int scanned = 0;
            foreach (string name in names)
            {
                if (scanned++ > 40) break;
                object value;
                try { value = SafeRead(owner, name); } catch { continue; }
                if (value == null) continue;
                if (LooksLikeManager(value)) { AddManager(found, seen, value, label + "." + name); continue; }
                if (HasBase(value))
                {
                    foreach (string inner in new[] { "Manager", "manager", "StatManager", "statManager", "Owner", "owner" })
                        AddManager(found, seen, SafeRead(value, inner), label + "." + name + "." + inner);
                }
            }
        }

        private static bool LooksLikeManager(object candidate)
        {
            if (candidate == null || candidate is string) return false;
            try
            {
                foreach (MethodInfo method in MethodsIncludingBases(candidate.GetType()))
                {
                    if (method.IsStatic) continue;
                    if (method.Name == "GetStat" || method.Name == "TryGetStat" || method.Name == "GetStatValue" ||
                        method.Name == "TryGetStatValue") return true;
                    if (method.Name == "Get" && method.GetParameters().Length == 1) return true;
                }
            }
            catch { }
            return false;
        }

        // Last resort: live PlayerStatManager / StatManager instances.
        private static void AddManagersByType(List<KeyValuePair<string, object>> found, HashSet<int> seen)
        {
            foreach (string typeName in new[] { "PlayerStatManager", "StatManager" })
            {
                Type type = FindTypeBySimpleName(typeName);
                if (type == null) continue;
                foreach (object instance in FindLiveObjects(type))
                {
                    if (instance == null || !LooksLikeManager(instance)) continue;
                    AddManager(found, seen, instance, "live " + typeName);
                }
            }
        }

        private static List<object> FindLiveObjects(Type type)
        {
            var result = new List<object>();
            try
            {
                Type unityObject = FindType("UnityEngine.Object");
                if (unityObject == null || !unityObject.IsAssignableFrom(type)) return result;
                Type resources = FindType("UnityEngine.Resources");
                MethodInfo find = resources == null ? null : resources.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll" && m.IsGenericMethodDefinition &&
                                         m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
                if (find == null) return result;
                result.AddRange(DevWheelBridge.ReadArray(find.MakeGenericMethod(type).Invoke(null, null)));
            }
            catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] Live " + type.Name + " search failed: " + Detail(ex)); }
            return result;
        }

        private static void AddManager(List<KeyValuePair<string, object>> found, HashSet<int> seen, object manager, string label)
        {
            if (manager == null) return;
            try
            {
                if (!seen.Add(Id(manager))) return;
            }
            catch { return; }
            found.Add(new KeyValuePair<string, object>(label, manager));
        }

        private static string DescribeManagers(List<KeyValuePair<string, object>> managers)
        {
            var parts = new List<string>();
            int shown = 0;
            foreach (KeyValuePair<string, object> entry in managers)
            {
                if (shown++ >= 4) break;
                string type = "?";
                try { type = entry.Value.GetType().Name; } catch { }
                parts.Add(entry.Key + "=" + type);
            }
            if (managers.Count > parts.Count) parts.Add("(+" + (managers.Count - parts.Count) + " more)");
            return string.Join(", ", parts.ToArray());
        }

        // Reference shape: StatManager.GetStat(string, out Stat) : bool. Also
        // accepts a single-argument getter, a string indexer, or a plain stat
        // collection that can be scanned for a "speed" entry.
        private static object FindSpeedStat(object manager, out string how)
        {
            how = "not found";
            foreach (string statName in StatNames)
            {
                object stat = InvokeGetStat(manager, statName);
                if (stat != null && HasBase(stat))
                {
                    how = "GetStat(\"" + statName + "\")";
                    return stat;
                }
            }
            object indexed = ReadStringIndexer(manager, StatName);
            if (indexed != null && HasBase(indexed)) { how = "indexer[\"" + StatName + "\"]"; return indexed; }
            foreach (KeyValuePair<string, object> collection in StatCollections(manager))
            {
                object match = FindStatsNamed(collection.Value, StatName);
                if (match != null && HasBase(match)) { how = collection.Key + " entry \"" + StatName + "\""; return match; }
            }
            return null;
        }

        private static object InvokeGetStat(object manager, string statName)
        {
            try
            {
                foreach (MethodInfo method in MethodsIncludingBases(manager.GetType()))
                {
                    if (method.Name != "GetStat" || method.IsStatic) continue;
                    ParameterInfo[] parameters = method.GetParameters();
                    if (parameters.Length == 2 && parameters[0].ParameterType == typeof(string) && parameters[1].ParameterType.IsByRef)
                    {
                        // A byref value type needs a boxed default instance; null
                        // would throw before the native call even runs.
                        Type element = parameters[1].ParameterType.GetElementType();
                        object slot = element != null && element.IsValueType && element != typeof(string)
                            ? Activator.CreateInstance(element)
                            : null;
                        object[] args = { statName, slot };
                        object result = method.Invoke(manager, args);
                        if (result is bool found && !found) continue;
                        if (args[1] != null) return args[1];
                        continue;
                    }
                    if (parameters.Length == 1 && parameters[0].ParameterType == typeof(string))
                    {
                        object stat = method.Invoke(manager, new object[] { statName });
                        if (stat != null && HasBase(stat)) return stat;
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] GetStat invoke failed: " + Detail(ex)); }
            return null;
        }

        private static object ReadStringIndexer(object manager, string key)
        {
            try
            {
                foreach (PropertyInfo property in manager.GetType().GetProperties(I))
                {
                    ParameterInfo[] index = property.GetIndexParameters();
                    if (index.Length != 1 || index[0].ParameterType != typeof(string)) continue;
                    if (property.GetGetMethod(true) == null) continue;
                    object value = property.GetValue(manager, new object[] { key });
                    if (value != null && HasBase(value)) return value;
                }
            }
            catch { }
            return null;
        }

        // Any member that exposes a collection of stats (name heuristics only).
        private static List<KeyValuePair<string, object>> StatCollections(object manager)
        {
            var found = new List<KeyValuePair<string, object>>();
            if (manager == null) return found;
            try
            {
                Type type = manager.GetType();
                foreach (string name in CollectionNames)
                {
                    object value = SafeRead(manager, name);
                    if (value != null) found.Add(new KeyValuePair<string, object>(name, value));
                }
                foreach (PropertyInfo property in type.GetProperties(I))
                {
                    if (property.GetIndexParameters().Length != 0 || property.GetGetMethod(true) == null) continue;
                    if (property.Name.IndexOf("stat", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (property.PropertyType == typeof(string) || property.PropertyType.IsPrimitive) continue;
                    object value;
                    try { value = property.GetValue(manager); } catch { continue; }
                    if (value == null) continue;
                    if (found.Exists(e => ReferenceEquals(e.Value, value))) continue;
                    found.Add(new KeyValuePair<string, object>(property.Name, value));
                }
            }
            catch { }
            return found;
        }

        // Walks a dictionary/list of stats and returns the one whose key or name
        // matches, without assuming a concrete collection type.
        private static object FindStatsNamed(object collection, string statName)
        {
            foreach (object item in Enumerate(collection))
            {
                if (item == null) continue;
                object key = SafeRead(item, "Key") ?? SafeRead(item, "key");
                if (key != null)
                {
                    string keyText = key as string ?? SafeRead(key, "name") as string;
                    if (Matches(keyText, statName)) return SafeRead(item, "Value") ?? SafeRead(item, "value");
                }
                foreach (string nameMember in new[] { "Name", "name", "StatName", "statName", "Id", "id" })
                {
                    string text = SafeRead(item, nameMember) as string;
                    if (text != null && Matches(text, statName)) return item;
                }
            }
            return null;
        }

        private static bool Matches(string text, string statName)
        {
            if (string.IsNullOrEmpty(text)) return false;
            return string.Equals(text, statName, StringComparison.OrdinalIgnoreCase) ||
                   text.IndexOf(statName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static List<object> Enumerate(object collection)
        {
            var items = new List<object>();
            if (collection == null) return items;
            try
            {
                MethodInfo getEnumerator = collection.GetType().GetMethod("GetEnumerator", I, null, Type.EmptyTypes, null);
                if (getEnumerator == null) return items;
                object enumerator = getEnumerator.Invoke(collection, null);
                if (enumerator == null) return items;
                MethodInfo moveNext = enumerator.GetType().GetMethod("MoveNext", I, null, Type.EmptyTypes, null);
                PropertyInfo current = enumerator.GetType().GetProperty("Current", I);
                if (moveNext == null || current == null) return items;
                while (items.Count < 300)
                {
                    if (!(moveNext.Invoke(enumerator, null) is bool more) || !more) break;
                    items.Add(current.GetValue(enumerator));
                }
            }
            catch { }
            return items;
        }

        private static object FindLocalComponent(string typeName, object localController)
        {
            try
            {
                Type type = FindTypeBySimpleName(typeName);
                if (type == null) return null;
                Type resources = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("UnityEngine.Resources", false)).FirstOrDefault(t => t != null);
                MethodInfo find = resources?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll" && m.IsGenericMethodDefinition &&
                                         m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
                if (find == null) return null;
                int root = -1;
                object controllerTransform = SafeRead(localController, "transform");
                object controllerRoot = SafeRead(controllerTransform, "root");
                if (controllerRoot != null) root = Id(controllerRoot);
                foreach (object item in Enumerate(find.MakeGenericMethod(type).Invoke(null, null)))
                {
                    if (item == null) continue;
                    if (root == -1) return item;
                    object itemRoot = SafeRead(SafeRead(item, "transform"), "root");
                    if (itemRoot != null && Id(itemRoot) == root) return item;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] Local component search failed: " + Detail(ex)); }
            return null;
        }

        private static bool HasBase(object stat)
        {
            if (stat == null) return false;
            return HasMember(stat.GetType(), "Base") || HasMember(stat.GetType(), "baseValue");
        }

        private static bool TryGetFloat(object target, string name, out float value)
        {
            value = 0f;
            try
            {
                Type type = target.GetType();
                PropertyInfo property = type.GetProperty(name, I);
                if (property != null && property.GetGetMethod(true) != null && property.GetIndexParameters().Length == 0)
                { value = Convert.ToSingle(property.GetValue(target)); return true; }
                FieldInfo field = FindField(type, name);
                if (field != null) { value = Convert.ToSingle(field.GetValue(target)); return true; }
            }
            catch { }
            return false;
        }

        private static bool TrySetFloat(object target, string name, float value)
        {
            foreach (string candidate in new[] { name, "baseValue", "BaseValue" })
            {
                try
                {
                    Type type = target.GetType();
                    PropertyInfo property = type.GetProperty(candidate, I);
                    if (property != null && property.GetSetMethod(true) != null && property.GetIndexParameters().Length == 0)
                    { property.SetValue(target, ChangeTo(value, property.PropertyType)); return true; }
                    FieldInfo field = FindField(type, candidate);
                    if (field != null) { field.SetValue(target, ChangeTo(value, field.FieldType)); return true; }
                }
                catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] Could not write " + candidate + ": " + Detail(ex)); }
            }
            try
            {
                foreach (string setter in new[] { "SetBase", "set_Base", "SetBaseValue" })
                {
                    foreach (MethodInfo method in MethodsIncludingBases(target.GetType()))
                    {
                        if (method.Name != setter || method.IsStatic) continue;
                        ParameterInfo[] parameters = method.GetParameters();
                        if (parameters.Length != 1) continue;
                        method.Invoke(target, new[] { ChangeTo(value, parameters[0].ParameterType) });
                        return true;
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] Could not call a Base setter: " + Detail(ex)); }
            return false;
        }

        private static object ChangeTo(float value, Type type)
        {
            if (type == typeof(float) || type == typeof(double) || type == typeof(int) || type == typeof(long) || type == typeof(short))
                return Convert.ChangeType(value, type);
            return value;
        }

        // One bounded, read-only dump so a failing build tells us what exists
        // instead of needing another guessing round.
        private static void DumpDiagnostics(object character, object manager)
        {
            if (diagnosticsLogged++ > 0) return;
            try
            {
                MelonLogger.Msg("[SuperSpeed] Diagnostics (one-time, read-only):");
                MelonLogger.Msg("  character=" + Describe(character) + ", manager=" + Describe(manager));
                if (character != null)
                {
                    LogMembers("  character member: ", character.GetType());
                    object health = SafeRead(character, "Health") ?? SafeRead(character, "health");
                    if (health != null) LogMembers("  health member: ", health.GetType());
                    object controller = null;
                    try { controller = controllerCurrent?.GetValue(null); } catch { }
                    object locomotion = FindLocomotion(controller);
                    if (locomotion != null)
                    {
                        MelonLogger.Msg("[SuperSpeed]   locomotion=" + Describe(locomotion));
                        LogMembers("  locomotion member: ", locomotion.GetType());
                    }
                }
                if (manager != null)
                {
                    LogMembers("  manager member: ", manager.GetType());
                    foreach (KeyValuePair<string, object> collection in StatCollections(manager))
                        LogCollection("  " + collection.Key + ": ", collection.Value);
                }
                LogTypes("  type: ", type =>
                    type.Name.IndexOf("StatManager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    type.Name.Equals("Stat", StringComparison.OrdinalIgnoreCase) ||
                    type.Name.IndexOf("PlayerCharacter", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] Diagnostics failed: " + Detail(ex)); }
        }

        private static string Describe(object obj)
        {
            if (obj == null) return "null";
            try { return obj.GetType().FullName; } catch { return "?"; }
        }

        private static void LogMembers(string prefix, Type type)
        {
            var parts = new List<string>();
            try
            {
                foreach (MemberInfo member in type.GetMembers(I))
                {
                    string name = member.Name;
                    if (name.IndexOf("stat", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("health", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("speed", StringComparison.OrdinalIgnoreCase) < 0 &&
                        name.IndexOf("character", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (member.MemberType == MemberTypes.Property) parts.Add(name + ":prop");
                    else if (member.MemberType == MemberTypes.Field && !name.StartsWith("Native")) parts.Add(name + ":field");
                    else if (member.MemberType == MemberTypes.Method && !name.StartsWith("get_") && !name.StartsWith("set_")) parts.Add(name + "()");
                    if (parts.Count >= 30) break;
                }
            }
            catch { }
            if (parts.Count > 0) MelonLogger.Msg("[SuperSpeed]" + prefix + string.Join(", ", parts.ToArray()));
        }

        private static void LogCollection(string prefix, object collection)
        {
            var names = new List<string>();
            try
            {
                foreach (object item in Enumerate(collection))
                {
                    if (item == null) continue;
                    string name = (SafeRead(item, "Key") as string) ??
                                  (SafeRead(item, "Name") as string) ??
                                  (SafeRead(item, "name") as string) ??
                                  (SafeRead(item, "StatName") as string) ??
                                  (SafeRead(item, "Id") as string) ?? item.GetType().Name;
                    names.Add(name);
                    if (names.Count >= 30) break;
                }
            }
            catch { }
            MelonLogger.Msg("[SuperSpeed]" + prefix + (names.Count == 0 ? "(empty/unreadable)" : string.Join(", ", names.ToArray())));
        }

        private static void LogTypes(string prefix, Func<Type, bool> filter)
        {
            var names = new List<string>();
            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string assemblyName = assembly.GetName().Name ?? "";
                    if (assemblyName.StartsWith("System") || assemblyName.StartsWith("Mono") ||
                        assemblyName.StartsWith("netstandard") || assemblyName.StartsWith("mscorlib") ||
                        assemblyName.StartsWith("HarmonyLib") || assemblyName.StartsWith("MelonLoader") ||
                        assemblyName.StartsWith("Il2CppInterop")) continue;
                    Type[] types;
                    try { types = assembly.GetTypes(); } catch (ReflectionTypeLoadException ex) { types = ex.Types; } catch { continue; }
                    foreach (Type type in types)
                    {
                        if (type == null) continue;
                        bool match;
                        try { match = filter(type); } catch { continue; }
                        if (!match) continue;
                        names.Add(type.Name);
                        if (names.Count >= 20) break;
                    }
                    if (names.Count >= 20) break;
                }
            }
            catch { }
            MelonLogger.Msg("[SuperSpeed]" + prefix + (names.Count == 0 ? "no matching types" : string.Join(", ", names.ToArray())));
        }

        private static void ResolveTypes()
        {
            if (typesResolved) return;
            typesResolved = true;
            try
            {
                controllerType = FindType("Il2Cpp.PlayerController") ?? FindTypeBySimpleName("PlayerController");
                playerType = FindType("Il2Cpp.Player") ?? FindTypeBySimpleName("Player");
                controllerCurrent = controllerType?.GetProperty("Current", S | BindingFlags.FlattenHierarchy);
                playerCurrent = playerType?.GetProperty("Current", S | BindingFlags.FlattenHierarchy);
                // Prefer a PlayerCharacter that the controller is assignable from
                // and that carries Health, but accept the best partial match: on
                // this build the strict filter matched nothing at all.
                Type fallbackType = null;
                foreach (Type type in AllTypes())
                {
                    if (type == null || type.Name != "PlayerCharacter" || type.IsAbstract) continue;
                    bool assignable = controllerType != null && controllerType.IsAssignableFrom(type);
                    bool hasHealth = HasMember(type, "Health") || HasMember(type, "health") ||
                                     HasMember(type, "StatManager") || HasMember(type, "Stats");
                    if (assignable && hasHealth) { playerCharacterType = type; break; }
                    if (fallbackType == null && (assignable || hasHealth)) fallbackType = type;
                }
                if (playerCharacterType == null) playerCharacterType = fallbackType;
                MelonLogger.Msg("[SuperSpeed] Local character type=" + (playerCharacterType?.FullName ?? "unresolved") +
                    ", controller type=" + (controllerType?.FullName ?? "unresolved") +
                    ", Player.Current=" + (playerCurrent != null) + ".");
            }
            catch (Exception ex) { MelonLogger.Warning("[SuperSpeed] Type resolution failed: " + Detail(ex)); }
        }

        private static List<Type> AllTypes()
        {
            var types = new List<Type>();
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name ?? "";
                if (name.StartsWith("System") || name.StartsWith("Mono") || name.StartsWith("netstandard") ||
                    name.StartsWith("mscorlib") || name.StartsWith("MelonLoader") || name.StartsWith("HarmonyLib")) continue;
                try { types.AddRange(assembly.GetTypes()); }
                catch (ReflectionTypeLoadException ex) { types.AddRange(ex.Types.Where(t => t != null)); }
                catch { }
            }
            return types;
        }

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { Type type = assembly.GetType(fullName, false); if (type != null) return type; }
                catch { }
            }
            return null;
        }

        private static Type FindTypeBySimpleName(string name)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type type in assembly.GetTypes())
                    {
                        if (type != null && type.Name == name) return type;
                    }
                }
                catch (ReflectionTypeLoadException ex)
                {
                    foreach (Type type in ex.Types)
                    {
                        if (type != null && type.Name == name) return type;
                    }
                }
                catch { }
            }
            return null;
        }

        private static List<MethodInfo> MethodsIncludingBases(Type type)
        {
            var result = new List<MethodInfo>();
            var seen = new HashSet<string>();
            for (Type current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                MethodInfo[] methods;
                try { methods = current.GetMethods(I | BindingFlags.DeclaredOnly); }
                catch { break; }
                foreach (MethodInfo method in methods)
                {
                    string key = method.Name + "/" + method.GetParameters().Length;
                    if (seen.Add(key)) result.Add(method);
                }
            }
            return result;
        }

        private static bool HasMember(object target, string name) => target != null && HasMember(target.GetType(), name);

        private static bool HasMember(Type type, string name)
        {
            try { return type.GetProperty(name, I) != null || FindField(type, name) != null; }
            catch { return false; }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, I | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            return null;
        }

        private static object SafeRead(object obj, string name)
        {
            try { return Read(obj, name); } catch { return null; }
        }

        private static object Read(object obj, string name)
        {
            if (obj == null) return null;
            Type type = obj.GetType();
            PropertyInfo property = type.GetProperty(name, I);
            if (property != null && property.GetGetMethod(true) != null && property.GetIndexParameters().Length == 0) return property.GetValue(obj);
            return FindField(type, name)?.GetValue(obj);
        }

        internal static int Id(object obj)
        {
            if (obj == null) throw new Exception("null Unity object");
            MethodInfo method = obj.GetType().GetMethod("GetInstanceID", I, null, Type.EmptyTypes, null);
            if (method == null) throw new Exception("GetInstanceID unavailable on " + obj.GetType().FullName);
            return Convert.ToInt32(method.Invoke(obj, null));
        }

        private static string Detail(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    // Colours the Quest Mods wheel bubbles per action, because the game picks a
    // bubble material purely from the action's validity. The bubble's current
    // shared material is duplicated once per action colour and the tinted copy is
    // assigned to that bubble's renderer, so the game's active/inactive switch
    // still works (the tint is re-applied for the new source material).
    // This is display only and never changes menu behaviour.
    internal static class BubbleTint
    {
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly string[] ColorProperties = { "_Color", "_BaseColor", "_TintColor", "_Base_Color", "_MainColor" };
        // Colour values above 1 survive in most shaders as a clamped, fully
        // saturated channel; 2.2 turns the pastel multiply into a vivid bubble.
        private const float ColorGain = 2.2f;
        private static readonly Dictionary<int, float[]> colors = new Dictionary<int, float[]>();   // action id -> rgb
        private static readonly Dictionary<int, string> labels = new Dictionary<int, string>();      // action id -> name for logs
        private static readonly Dictionary<string, object> materials = new Dictionary<string, object>(); // "action:source" -> tinted material
        private static readonly HashSet<int> tintedIds = new HashSet<int>();
        private static readonly List<object> roots = new List<object>();
        private static DateTime next;
        private static int applyLogs, failureLogs;
        private static MethodInfo instantiate;
        private static MethodInfo setColor;
        private static MethodInfo hasProperty;
        private static Type colorType;
        private static bool hooksResolved;

        public static void Register(object action, string label, float r, float g, float b)
        {
            try
            {
                int id = InstanceId(action);
                colors[id] = new[] { r, g, b };
                labels[id] = label;
                MelonLogger.Msg("[BubbleTint] Queued colour rgb(" + r + ", " + g + ", " + b + ") for '" + label + "' id=" + id + ".");
            }
            catch (Exception ex) { MelonLogger.Warning("[BubbleTint] Could not queue colour: " + Detail(ex)); }
        }

        public static void Unregister(object action)
        {
            try
            {
                if (action == null) return;
                int id = InstanceId(action);
                colors.Remove(id);
                labels.Remove(id);
            }
            catch { }
        }

        public static void RegisterRoot(object root)
        {
            try
            {
                if (root == null) return;
                int id = InstanceId(root);
                for (int i = roots.Count - 1; i >= 0; i--)
                {
                    try { if (InstanceId(roots[i]) == id) roots.RemoveAt(i); } catch { roots.RemoveAt(i); }
                }
                roots.Add(root);
            }
            catch (Exception ex) { MelonLogger.Warning("[BubbleTint] Could not track Quest Mods page: " + Detail(ex)); }
        }

        public static void ClearRoots()
        {
            roots.Clear();
        }

        public static void SceneLoaded()
        {
            foreach (object material in materials.Values) DestroyMaterial(material);
            materials.Clear();
            tintedIds.Clear();
            colors.Clear();
            labels.Clear();
            roots.Clear();
            applyLogs = 0;
            failureLogs = 0;
            next = DateTime.MinValue;
        }

        // Bubbles are pooled and appear a moment after the page opens, so this
        // small poll (only while our page owns bubbles) is safer than assuming
        // the spawn is synchronous.
        public static void Tick()
        {
            if (colors.Count == 0 || roots.Count == 0) return;
            if (DateTime.UtcNow < next) return;
            next = DateTime.UtcNow.AddMilliseconds(250);
            foreach (object root in roots.ToArray())
            {
                if (root == null) continue;
                List<object> bubbles;
                try { bubbles = DevWheelBridge.ReadArray(Read(root, "spawnedBubbles")); }
                catch { continue; }
                foreach (object bubble in bubbles) Apply(bubble);
            }
        }

        private static void Apply(object bubble)
        {
            if (bubble == null) return;
            try
            {
                object content = Read(bubble, "content");
                if (content == null) return;
                int actionId = InstanceId(content);
                float[] color;
                if (!colors.TryGetValue(actionId, out color)) return;
                object renderer = Read(bubble, "bubbleRenderer");
                if (renderer == null)
                {
                    if (failureLogs++ < 6)
                        MelonLogger.Warning("[BubbleTint] Bubble '" + (Read(bubble, "name") ?? "?") + "' has no readable bubbleRenderer; colour skipped.");
                    return;
                }
                object shared = Read(renderer, "sharedMaterial") ?? Read(renderer, "SharedMaterial");
                if (shared == null) return;
                int sharedId = InstanceId(shared);
                if (tintedIds.Contains(sharedId)) return; // already one of our tinted copies
                string key = actionId + ":" + sharedId;
                object tinted;
                if (!materials.TryGetValue(key, out tinted) || tinted == null)
                {
                    tinted = CreateTintedMaterial(shared, color, actionId);
                    if (tinted == null) return;
                    materials[key] = tinted;
                    tintedIds.Add(InstanceId(tinted));
                }
                Assign(renderer, tinted);
            }
            catch (Exception ex)
            {
                if (failureLogs++ < 6) MelonLogger.Warning("[BubbleTint] Could not colour a bubble: " + Detail(ex));
            }
        }

        private static object CreateTintedMaterial(object source, float[] color, int actionId)
        {
            ResolveHooks(source);
            if (instantiate == null) return null;
            object copy = null;
            try { copy = instantiate.Invoke(null, new[] { source }); }
            catch (Exception ex)
            {
                if (failureLogs++ < 6) MelonLogger.Warning("[BubbleTint] Material copy failed: " + Detail(ex));
                return null;
            }
            if (copy == null) return null;
            if (InstanceId(copy) == InstanceId(source))
            {
                MelonLogger.Warning("[BubbleTint] Material copy was not a separate object; colours are skipped.");
                return null;
            }
            // The bubble art is textured and sits behind the menu lighting, so a
            // plain 0..1 tint only reads as pastel. Overdrive the colour (values
            // above 1 clamp to a saturated channel) and add emission when the
            // shader supports it, which is what makes the three bubbles pop.
            var boosted = new[] { color[0] * ColorGain, color[1] * ColorGain, color[2] * ColorGain };
            object value = MakeColor(boosted, 1f);
            if (value == null || setColor == null)
            {
                MelonLogger.Warning("[BubbleTint] No usable UnityEngine.Color/Material.SetColor; colours are skipped.");
                return null;
            }
            var applied = new List<string>();
            foreach (string property in ColorProperties)
            {
                bool exists;
                try { exists = hasProperty == null || Convert.ToBoolean(hasProperty.Invoke(copy, new object[] { property })); }
                catch { continue; }
                if (!exists) continue;
                try { setColor.Invoke(copy, new[] { (object)property, value }); applied.Add(property); }
                catch (Exception ex) { if (failureLogs++ < 6) MelonLogger.Warning("[BubbleTint] SetColor " + property + " failed: " + Detail(ex)); }
            }
            if (HasProperty(copy, "_EmissionColor"))
            {
                try
                {
                    setColor.Invoke(copy, new[] { (object)"_EmissionColor", MakeColor(boosted, 1f) });
                    EnableKeyword(copy, "_EMISSION");
                    applied.Add("_EmissionColor");
                }
                catch (Exception ex) { if (failureLogs++ < 6) MelonLogger.Warning("[BubbleTint] Emission tint failed: " + Detail(ex)); }
            }
            if (applied.Count == 0)
            {
                string named = null;
                labels.TryGetValue(actionId, out named);
                if (failureLogs++ < 6)
                    MelonLogger.Warning("[BubbleTint] Material '" + (Read(copy, "name") ?? "?") + "' (shader " +
                        (Read(Read(copy, "shader"), "name") ?? "?") + ") has no known colour property; '" +
                        (named ?? actionId.ToString()) + "' keeps its original colour.");
                DestroyMaterial(copy);
                return null;
            }
            string tintedName = null;
            labels.TryGetValue(actionId, out tintedName);
            if (applyLogs++ < 12)
                MelonLogger.Msg("[BubbleTint] Tinted '" + (tintedName ?? actionId.ToString()) +
                    "' via " + string.Join("/", applied.ToArray()) + " (shader " +
                    (Read(Read(copy, "shader"), "name") ?? "?") + ", gain x" + ColorGain + ").");
            return copy;
        }

        private static void ResolveHooks(object source)
        {
            if (hooksResolved) return;
            hooksResolved = true;
            try
            {
                Type unityObject = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("UnityEngine.Object", false)).FirstOrDefault(t => t != null);
                if (unityObject != null)
                {
                    MethodInfo generic = unityObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "Instantiate" && m.IsGenericMethodDefinition &&
                                             m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 1 &&
                                             m.GetParameters()[0].ParameterType.IsGenericParameter);
                    // Only keep the closed generic; an unclosed definition cannot
                    // be invoked.
                    if (generic != null)
                    {
                        try { instantiate = generic.MakeGenericMethod(source.GetType()); }
                        catch (Exception ex) { MelonLogger.Warning("[BubbleTint] Instantiate<" + source.GetType().Name + "> unavailable: " + Detail(ex)); }
                    }
                }
                Type materialType = source.GetType();
                setColor = materialType.GetMethods(I).FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == "SetColor" && !m.IsStatic && p.Length == 2 && p[0].ParameterType == typeof(string) && p[1].ParameterType.IsValueType;
                });
                hasProperty = materialType.GetMethods(I).FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == "HasProperty" && !m.IsStatic && m.ReturnType == typeof(bool) && p.Length == 1 && p[0].ParameterType == typeof(string);
                });
                colorType = setColor?.GetParameters()[1].ParameterType;
                if (colorType == null)
                    colorType = AppDomain.CurrentDomain.GetAssemblies()
                        .Select(a => a.GetType("UnityEngine.Color", false)).FirstOrDefault(t => t != null);
                MelonLogger.Msg("[BubbleTint] Material instantiate=" + (instantiate != null) + ", SetColor=" + (setColor != null) +
                    ", HasProperty=" + (hasProperty != null) + ", Color type=" + (colorType?.FullName ?? "unresolved") + ".");
            }
            catch (Exception ex) { MelonLogger.Warning("[BubbleTint] Setup failed: " + Detail(ex)); }
        }

        private static bool HasProperty(object material, string property)
        {
            if (hasProperty == null) return true;
            try { return Convert.ToBoolean(hasProperty.Invoke(material, new object[] { property })); }
            catch { return false; }
        }

        private static void EnableKeyword(object material, string keyword)
        {
            try
            {
                MethodInfo enable = material.GetType().GetMethods(I).FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == "EnableKeyword" && !m.IsStatic && p.Length == 1 && p[0].ParameterType == typeof(string);
                });
                if (enable != null) enable.Invoke(material, new object[] { keyword });
            }
            catch { }
        }

        private static object MakeColor(float[] color, float alpha)
        {
            if (colorType == null) return null;
            try { return Activator.CreateInstance(colorType, new object[] { color[0], color[1], color[2], alpha }); }
            catch { }
            try
            {
                object value = Activator.CreateInstance(colorType);
                foreach (string channel in new[] { "r", "g", "b", "a" })
                {
                    FieldInfo field = colorType.GetField(channel, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field == null || field.FieldType != typeof(float)) continue;
                    field.SetValue(value, channel == "r" ? color[0] : channel == "g" ? color[1] : channel == "b" ? color[2] : alpha);
                }
                return value;
            }
            catch { return null; }
        }

        private static void Assign(object renderer, object material)
        {
            PropertyInfo property = renderer.GetType().GetProperty("sharedMaterial", I);
            if (property?.GetSetMethod(true) != null && property.PropertyType.IsInstanceOfType(material))
            { property.SetValue(renderer, material); return; }
            MethodInfo setter = renderer.GetType().GetMethods(I).FirstOrDefault(m =>
            {
                ParameterInfo[] p = m.GetParameters();
                return m.Name == "SetSharedMaterial" && !m.IsStatic && p.Length == 1 && p[0].ParameterType.IsInstanceOfType(material);
            });
            if (setter != null) { setter.Invoke(renderer, new[] { material }); return; }
            throw new Exception("Renderer.sharedMaterial is not writable on " + renderer.GetType().FullName);
        }

        private static void DestroyMaterial(object material)
        {
            try
            {
                Type unityObject = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEngine.Object", false)).FirstOrDefault(t => t != null);
                MethodInfo destroy = unityObject?.GetMethod("Destroy", BindingFlags.Static | BindingFlags.Public, null, new[] { unityObject }, null);
                destroy?.Invoke(null, new[] { material });
            }
            catch { }
        }

        // Local instance-id helper (the mod's other helper classes have their own).
        private static int InstanceId(object obj)
        {
            if (obj == null) throw new Exception("null Unity object");
            MethodInfo method = obj.GetType().GetMethod("GetInstanceID", I, null, Type.EmptyTypes, null);
            if (method == null) throw new Exception("GetInstanceID unavailable on " + obj.GetType().FullName);
            return Convert.ToInt32(method.Invoke(obj, null));
        }

        private static object Read(object obj, string name)
        {
            if (obj == null) return null;
            Type type = obj.GetType();
            PropertyInfo property = type.GetProperty(name, I);
            if (property != null && property.GetGetMethod(true) != null && property.GetIndexParameters().Length == 0) return property.GetValue(obj);
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(name, I | BindingFlags.DeclaredOnly);
                if (field != null) return field.GetValue(obj);
            }
            return null;
        }

        private static string Detail(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
        }
    }

    // "Grab by Name" wheel group, ported from the PC mod's GrabByName:
    // a keyword, a grab amount, and the grab itself. Three wheel items share this
    // class: "Grab by Name" runs it, "Type Keyword" opens the platform keyboard,
    // "Grab Amount" cycles how many pickups one press may take.
    internal static class GrabByName
    {
        private const BindingFlags I = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private const BindingFlags S = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        public const int KindRun = 0;
        public const int KindType = 1;
        public const int KindAmount = 2;
        private const string RunLabel = "Grab by Name";
        private const string TypeLabel = "Type Keyword";
        private const string AmountLabel = "Grab Amount";
        private static readonly int[] AmountSteps = { 1, 5, 10, 20, 40, 100 };

        private static readonly Dictionary<int, int> kinds = new Dictionary<int, int>();
        private static readonly List<object> actions = new List<object>();
        private static int amount = 10;
        private static int pendingKind = -1;
        private static int missingFileLogged;
        private static int keyboardLogs;
        private static string keyword = "";
        private static DateTime nextKeywordRead = DateTime.MinValue;
        private static object keyboard;
        private static bool keyboardOpen;

        public static bool Available => GrabbyHands.Available;

        public static void Register(object action, int kind)
        {
            try
            {
                int id = Id(action);
                kinds[id] = kind;
                if (!actions.Contains(action)) actions.Add(action);
                string stored = ReadKeyword();
                if (stored != null) keyword = stored;
                amount = ReadAmount();
                UpdateLabels();
                MelonLogger.Msg("[GrabByName] Added wheel item id=" + id + " (" + KindName(kind) + "); keyword '" +
                    keyword + "', amount " + amount + ", file " + (KeywordPath() ?? "unresolved") + ".");
            }
            catch (Exception ex) { MelonLogger.Warning("[GrabByName] Could not register item: " + Detail(ex)); }
        }

        public static void Unregister(object action)
        {
            try
            {
                if (action == null) return;
                kinds.Remove(Id(action));
                actions.Remove(action);
            }
            catch { }
        }

        public static void SceneLoaded()
        {
            Interlocked.Exchange(ref pendingKind, -1);
            missingFileLogged = 0;
            keyboardLogs = 0;
            nextKeywordRead = DateTime.MinValue;
            keyboard = null;
            keyboardOpen = false;
            UpdateLabels();
        }

        // Runs on the game's own wheel spawn; the actual work is queued so it
        // happens on the normal update tick instead of inside a Harmony patch.
        public static bool BeforeWheelRun(object __instance)
        {
            bool ours = false;
            try
            {
                if (__instance == null) return true;
                int id;
                try { id = Id(__instance); } catch { return true; }
                int kind;
                if (!kinds.TryGetValue(id, out kind)) return true;
                string name = SafeRead(__instance, "DisplayName") as string;
                if (name == null) return true;
                if (!name.StartsWith(RunLabel, StringComparison.Ordinal) &&
                    !name.StartsWith(TypeLabel, StringComparison.Ordinal) &&
                    !name.StartsWith(AmountLabel, StringComparison.Ordinal)) return true;
                ours = true;
                Interlocked.Exchange(ref pendingKind, kind);
                MelonLogger.Msg("[GrabByName] " + KindName(kind) + " requested.");
            }
            catch (Exception ex) { MelonLogger.Warning("[GrabByName] Wheel request failed: " + Detail(ex)); }
            return !ours;
        }

        public static void Tick()
        {
            if (keyboardOpen) { PollKeyboard(); return; }
            int requested = Interlocked.Exchange(ref pendingKind, -1);
            if (requested < 0) return;
            if (requested == KindType) { OpenKeyboard(); return; }
            if (requested == KindAmount) { CycleAmount(); return; }
            string fresh = ReadKeyword();
            if (fresh != null && fresh != keyword) { keyword = fresh; UpdateLabels(); }
            if (string.IsNullOrEmpty(keyword))
            {
                MelonLogger.Warning("[GrabByName] No keyword yet; opening the keyboard.");
                OpenKeyboard();
                return;
            }
            try { GrabMatching(keyword, amount); }
            catch (Exception ex) { MelonLogger.Warning("[GrabByName] Grab failed: " + Detail(ex)); }
        }

        private static string KindName(int kind) =>
            kind == KindType ? "Type Keyword" : kind == KindAmount ? "Grab Amount" : "Grab by Name";

        private static void CycleAmount()
        {
            int index = Array.IndexOf(AmountSteps, amount);
            index = index < 0 ? 0 : (index + 1) % AmountSteps.Length;
            amount = AmountSteps[index];
            WriteAmount(amount);
            UpdateLabels();
            MelonLogger.Msg("[GrabByName] Grab amount is now " + amount + " pickup(s) per press.");
        }

        // ---- keyboard -------------------------------------------------------

        private static void OpenKeyboard()
        {
            try
            {
                Type type = FindType("UnityEngine.TouchScreenKeyboard");
                if (type == null)
                {
                    if (keyboardLogs++ < 2)
                        MelonLogger.Warning("[GrabByName] TouchScreenKeyboard is not available; edit " +
                            (KeywordPath() ?? "the keyword file") + " instead.");
                    return;
                }
                MethodInfo open = type.GetMethods(S)
                    .Where(m => m.Name == "Open" && m.ReturnType == type)
                    .OrderByDescending(m => m.GetParameters().Length)
                    .FirstOrDefault();
                if (open == null)
                {
                    if (keyboardLogs++ < 2) MelonLogger.Warning("[GrabByName] TouchScreenKeyboard.Open was not found.");
                    return;
                }
                keyboard = open.Invoke(null, KeyboardArgs(open));
                keyboardOpen = keyboard != null;
                if (!keyboardOpen)
                {
                    if (keyboardLogs++ < 2) MelonLogger.Warning("[GrabByName] The keyboard did not open; using the keyword file.");
                    return;
                }
                MelonLogger.Msg("[GrabByName] Keyboard opened (current text '" + keyword + "'). Type, then press Done.");
            }
            catch (Exception ex)
            {
                keyboard = null;
                keyboardOpen = false;
                if (keyboardLogs++ < 3) MelonLogger.Warning("[GrabByName] Keyboard failed: " + Detail(ex));
            }
        }

        private static object[] KeyboardArgs(MethodInfo open)
        {
            ParameterInfo[] p = open.GetParameters();
            var args = new object[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                Type t = p[i].ParameterType;
                if (t == typeof(string)) args[i] = i == 0 ? keyword : "";
                else if (t.IsEnum) args[i] = Enum.ToObject(t, 0);
                else if (t == typeof(bool)) args[i] = false;
                else if (t == typeof(int)) args[i] = 0;
                else args[i] = t.IsValueType ? Activator.CreateInstance(t) : null;
            }
            return args;
        }

        private static void PollKeyboard()
        {
            try
            {
                if (keyboard == null) { keyboardOpen = false; return; }
                string status = (SafeRead(keyboard, "status") ?? "Visible").ToString();
                string text = SafeRead(keyboard, "text") as string;
                if (status == "Visible")
                {
                    if (!string.IsNullOrEmpty(text) && text != keyword)
                    {
                        keyword = Clean(text);
                        UpdateLabels();
                    }
                    return;
                }
                keyboardOpen = false;
                keyboard = null;
                if (status == "Done" && !string.IsNullOrEmpty(text))
                {
                    SetKeyword(text);
                    MelonLogger.Msg("[GrabByName] Keyboard accepted '" + keyword + "'; grabbing up to " + amount + ".");
                    GrabMatching(keyword, amount);
                }
                else MelonLogger.Msg("[GrabByName] Keyboard closed without a value (" + status + ").");
            }
            catch (Exception ex)
            {
                keyboardOpen = false;
                keyboard = null;
                MelonLogger.Warning("[GrabByName] Keyboard read failed: " + Detail(ex));
            }
        }

        private static void SetKeyword(string text)
        {
            string clean = Clean(text);
            if (clean.Length == 0) return;
            keyword = clean;
            WriteKeyword(clean);
            UpdateLabels();
        }

        private static string Clean(string text)
        {
            if (text == null) return "";
            string clean = text.Trim();
            return clean.Length > 32 ? clean.Substring(0, 32) : clean;
        }

        // ---- grabbing -------------------------------------------------------

        private static void GrabMatching(string needle, int limit)
        {
            List<object> interactors = GrabbyHands.LocalInteractorObjects();
            if (interactors.Count == 0)
            {
                MelonLogger.Warning("[GrabByName] No local interactor yet. Move a hand once, then press again.");
                return;
            }
            Type pickupType = FindPickupType();
            var candidates = new List<object>();
            var seen = new HashSet<int>();
            if (pickupType != null)
            {
                foreach (object live in LiveObjects(pickupType))
                {
                    if (live == null) continue;
                    try { if (seen.Add(Id(live))) candidates.Add(live); } catch { }
                }
            }
            if (candidates.Count == 0)
            {
                Type mono = FindType("UnityEngine.MonoBehaviour");
                if (mono != null)
                {
                    foreach (object live in LiveObjects(mono))
                    {
                        if (live == null) continue;
                        bool typed = false;
                        try { typed = IsPickupTyped(live.GetType()); } catch { }
                        if (!typed) continue;
                        try { if (seen.Add(Id(live))) candidates.Add(live); } catch { }
                    }
                }
            }
            MelonLogger.Msg("[GrabByName] Scanning " + candidates.Count + " pickup object(s) for '" + needle +
                "' (limit " + limit + ").");
            int matched = 0, grabbed = 0;
            foreach (object candidate in candidates)
            {
                if (grabbed >= limit) break;
                string name = SafeRead(candidate, "name") as string;
                if (name == null || name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                matched++;
                if (TryGrab(interactors, candidate, name)) grabbed++;
            }
            MelonLogger.Msg("[GrabByName] '" + needle + "': matched " + matched + ", grabbed " + grabbed + ".");
        }

        private static bool TryGrab(List<object> interactors, object pickup, string name)
        {
            foreach (object interactor in interactors)
            {
                try
                {
                    object busy = SafeRead(interactor, "IsInteracting") ?? SafeRead(interactor, "isInteracting");
                    if (busy is bool isBusy && isBusy && !IsHeldNow(interactor, pickup)) StopInteract(interactor);
                    MethodInfo start = StartMethod(interactor, pickup);
                    if (start == null) continue;
                    start.Invoke(interactor, BuildArgs(start, pickup));
                    if (IsHeldNow(interactor, pickup)) { MelonLogger.Msg("[GrabByName] Grabbed '" + name + "'."); return true; }
                    // Shelf items may stay docked after a plain grab; only then
                    // is the pickup undocked and the grab retried once.
                    if (IsDockedNow(pickup) && TryUndock(pickup))
                    {
                        start.Invoke(interactor, BuildArgs(start, pickup));
                        MelonLogger.Msg("[GrabByName] Grabbed '" + name + "' after undocking.");
                        return true;
                    }
                    MelonLogger.Msg("[GrabByName] Sent grab for '" + name + "'.");
                    return true;
                }
                catch (Exception ex) { MelonLogger.Warning("[GrabByName] '" + name + "' failed: " + Detail(ex)); }
            }
            return false;
        }

        private static object[] BuildArgs(MethodInfo method, object pickup)
        {
            ParameterInfo[] p = method.GetParameters();
            var args = new object[p.Length];
            for (int i = 0; i < p.Length; i++)
            {
                Type t = p[i].ParameterType;
                if (t.IsInstanceOfType(pickup)) args[i] = pickup;
                else if (t == typeof(bool)) args[i] = false;
                else if (t == typeof(int)) args[i] = 0;
                else args[i] = t.IsValueType ? Activator.CreateInstance(t) : null;
            }
            return args;
        }

        private static MethodInfo StartMethod(object interactor, object pickup)
        {
            MethodInfo best = null;
            foreach (MethodInfo method in interactor.GetType().GetMethods(I))
            {
                if (method.IsStatic || method.IsAbstract) continue;
                if (method.Name != "StartInteract" && method.Name != "TryStartInteract" && method.Name != "Interact") continue;
                ParameterInfo[] p = method.GetParameters();
                if (p.Length == 0 || !p[0].ParameterType.IsInstanceOfType(pickup)) continue;
                if (method.Name == "StartInteract" && p.Length == 4) return method;
                if (best == null) best = method;
            }
            return best;
        }

        private static void StopInteract(object interactor)
        {
            try
            {
                MethodInfo stop = interactor.GetType().GetMethods(I).FirstOrDefault(m =>
                    !m.IsStatic && m.Name == "StopInteract" && m.GetParameters().Length == 0);
                stop?.Invoke(interactor, null);
            }
            catch { }
        }

        private static bool TryUndock(object pickup)
        {
            try
            {
                foreach (MethodInfo method in pickup.GetType().GetMethods(I))
                {
                    if (method.IsStatic || method.GetParameters().Length != 0) continue;
                    if (method.Name != "Undock" && method.Name != "TryUndock" && method.Name != "ForceUndock") continue;
                    object result = method.Invoke(pickup, null);
                    return !(result is bool ok) || ok;
                }
                object dock = SafeRead(pickup, "Dock");
                if (dock == null) return false;
                foreach (MethodInfo method in dock.GetType().GetMethods(I))
                {
                    if (method.IsStatic || method.Name != "Undock" || method.GetParameters().Length != 0) continue;
                    object result = method.Invoke(dock, null);
                    return !(result is bool ok) || ok;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[GrabByName] Undock failed: " + Detail(ex)); }
            return false;
        }

        private static bool IsHeldNow(object interactor, object pickup)
        {
            object current = SafeRead(interactor, "CurrentInteractable") ?? SafeRead(interactor, "currentInteractable") ??
                             SafeRead(interactor, "Held") ?? SafeRead(interactor, "held");
            if (current == null) return false;
            try { return Id(current) == Id(pickup); } catch { return false; }
        }

        private static bool IsDockedNow(object pickup)
        {
            object docked = SafeRead(pickup, "IsDocked");
            if (docked is bool flag) return flag;
            return SafeRead(pickup, "Dock") != null;
        }

        private static Type FindPickupType()
        {
            foreach (Type type in GrabbyHands.PickupTypeCandidates())
            {
                if (type != null && type.Name == "Pickup") return type;
            }
            foreach (Type type in GrabbyHands.PickupTypeCandidates())
            {
                if (type != null) return type;
            }
            return FindType("Pickup");
        }

        private static bool IsPickupTyped(Type type)
        {
            int depth = 0;
            for (Type current = type; current != null && depth < 12; current = current.BaseType, depth++)
            {
                if (current.Name == "Pickup") return true;
            }
            return false;
        }

        private static List<object> LiveObjects(Type type)
        {
            var result = new List<object>();
            try
            {
                Type unityObject = FindType("UnityEngine.Object");
                if (unityObject == null || !unityObject.IsAssignableFrom(type)) return result;
                Type resources = FindType("UnityEngine.Resources");
                MethodInfo find = resources == null ? null : resources.GetMethods(S)
                    .FirstOrDefault(m => m.Name == "FindObjectsOfTypeAll" && m.IsGenericMethodDefinition &&
                                         m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0);
                if (find == null) return result;
                result.AddRange(DevWheelBridge.ReadArray(find.MakeGenericMethod(type).Invoke(null, null)));
            }
            catch (Exception ex) { MelonLogger.Warning("[GrabByName] Object scan failed for " + type.Name + ": " + Detail(ex)); }
            return result;
        }

        // ---- storage --------------------------------------------------------

        private static string DataFolder()
        {
            try
            {
                string location = null;
                try { location = Assembly.GetExecutingAssembly().Location; } catch { }
                string mods = string.IsNullOrEmpty(location) ? null : System.IO.Path.GetDirectoryName(location);
                string root = string.IsNullOrEmpty(mods) ? null : System.IO.Path.GetDirectoryName(mods);
                if (string.IsNullOrEmpty(root)) return null;
                return System.IO.Path.Combine(root, "UserData");
            }
            catch { return null; }
        }

        internal static string KeywordPath()
        {
            string folder = DataFolder();
            return folder == null ? null : System.IO.Path.Combine(folder, "questmods_grabname.txt");
        }

        private static string AmountPath()
        {
            string folder = DataFolder();
            return folder == null ? null : System.IO.Path.Combine(folder, "questmods_grabamount.txt");
        }

        private static string ReadKeyword()
        {
            try
            {
                string path = KeywordPath();
                if (path == null || !System.IO.File.Exists(path))
                {
                    if (missingFileLogged++ == 0)
                        MelonLogger.Msg("[GrabByName] No keyword file yet (" + (path ?? "unresolved") +
                            "); use the 'Type Keyword' wheel item or create it with one item-name fragment.");
                    return null;
                }
                foreach (string raw in System.IO.File.ReadAllLines(path))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    return line.Length > 32 ? line.Substring(0, 32) : line;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[GrabByName] Could not read keyword file: " + Detail(ex)); }
            return null;
        }

        private static void WriteKeyword(string value)
        {
            try
            {
                string path = KeywordPath();
                if (path != null) System.IO.File.WriteAllText(path, value + Environment.NewLine);
            }
            catch (Exception ex) { MelonLogger.Warning("[GrabByName] Could not save keyword: " + Detail(ex)); }
        }

        private static int ReadAmount()
        {
            try
            {
                string path = AmountPath();
                if (path == null || !System.IO.File.Exists(path)) return amount;
                foreach (string raw in System.IO.File.ReadAllLines(path))
                {
                    int value;
                    if (int.TryParse(raw.Trim(), out value) && value > 0)
                        return value > 100 ? 100 : value;
                }
            }
            catch { }
            return amount;
        }

        private static void WriteAmount(int value)
        {
            try
            {
                string path = AmountPath();
                if (path != null) System.IO.File.WriteAllText(path, value + Environment.NewLine);
            }
            catch { }
        }

        // ---- labels and helpers --------------------------------------------

        private static void UpdateLabels()
        {
            PropertyInfo property = typeof(QuickAccessMenuAction).GetProperty("displayName", I);
            foreach (object action in actions)
            {
                string label;
                try
                {
                    int kind;
                    if (!kinds.TryGetValue(Id(action), out kind)) continue;
                    label = kind == KindType ? TypeLabel + ": " + (keyword.Length == 0 ? "(empty)" : keyword)
                          : kind == KindAmount ? AmountLabel + ": " + amount
                          : RunLabel + " x" + amount + " '" + keyword + "'";
                }
                catch { continue; }
                try { if (property?.GetSetMethod(true) != null) property.SetValue(action, label); }
                catch (Exception ex) { MelonLogger.Warning("[GrabByName] Could not refresh label: " + Detail(ex)); }
            }
        }

        private static object SafeRead(object obj, string name)
        {
            if (obj == null) return null;
            try
            {
                Type type = obj.GetType();
                PropertyInfo property = type.GetProperty(name, I);
                if (property != null && property.GetGetMethod(true) != null && property.GetIndexParameters().Length == 0)
                    return property.GetValue(obj);
                for (Type current = type; current != null; current = current.BaseType)
                {
                    FieldInfo field = current.GetField(name, I | BindingFlags.DeclaredOnly);
                    if (field != null) return field.GetValue(obj);
                }
            }
            catch { }
            return null;
        }

        private static int Id(object obj)
        {
            if (obj == null) throw new Exception("null Unity object");
            MethodInfo method = obj.GetType().GetMethod("GetInstanceID", I, null, Type.EmptyTypes, null);
            if (method == null) throw new Exception("GetInstanceID unavailable on " + obj.GetType().FullName);
            return Convert.ToInt32(method.Invoke(obj, null));
        }

        private static Type FindType(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            string shortName = name.Substring(name.LastIndexOf('.') + 1);
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetTypes().FirstOrDefault(t => t.Name == shortName);
                    if (type != null) return type;
                }
                catch { }
            }
            return null;
        }

        private static string Detail(Exception ex)
        {
            if (ex is TargetInvocationException && ex.InnerException != null) ex = ex.InnerException;
            return ex.GetType().Name + ": " + ex.Message;
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
        private static readonly List<object> localInteractors = new List<object>();
        private static readonly List<Type> pickupTypes = new List<Type>();
        private static int ownershipLogs;
        private static bool ready, enabled, grabScanDone, wheelHookAttempted;
        private static int grabHooks, holdHooks, pending, startLogs, holdLogs;
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
                // One shared prefix routes every direct toggle (Grabby Hands and
                // Super Speed) so both clones reuse this single verified hook.
                grabHarmony.Patch(spawn, prefix: new HarmonyMethod(typeof(WheelToggleRouter).GetMethod(nameof(WheelToggleRouter.BeforeSpawn), S)));
                ready = true;
                MelonLogger.Msg("[GrabbyHands] Spawn hook registered in-game; native interception is not yet verified. Grabby Hands and Super Speed default OFF.");
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
            enabled = false; fallbackDetected = false;
            ownershipLogs = 0; startLogs = 0; holdLogs = 0;
            UpdateLabels();
            actions.Clear();
            localHandTransforms.Clear();
            localControllerIds.Clear(); localInteractorIds.Clear();
            localInteractors.Clear();
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
            if (interactor != null)
            {
                iid = Id(interactor);
                localInteractorIds.Add(iid);
                bool known = false;
                foreach (object existing in localInteractors)
                {
                    try { if (Id(existing) == iid) { known = true; break; } } catch { }
                }
                if (!known) localInteractors.Add(interactor);
            }
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
                if (enabled) { startLogs = 0; holdLogs = 0; ownershipLogs = 0; }
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
                int count = countObj is int buffered ? buffered : 0;
                List<object> colliders = null;
                if (count > 0 && count < 10 && overlapArgs[0] != null)
                {
                    colliders = DevWheelBridge.ReadArray(overlapArgs[0]);
                    if (colliders.Count == 0 || count > colliders.Count) colliders = null;
                }
                // The game buffers ten colliders and only samples the exact
                // reach. When that finds nothing, retry the same capsule with a
                // larger radius so slots slightly out of reach still resolve.
                if (colliders == null)
                {
                    foreach (float multiplier in new[] { 1f, 1.8f, 3f })
                    {
                        List<object> wider = FullOverlapCapsule(__instance, multiplier);
                        if (wider == null || wider.Count == 0) continue;
                        if (wider.Count >= 64) { HoldLog("expanded overlap too crowded (" + wider.Count + ") at x" + multiplier + "; no target chosen"); return true; }
                        colliders = wider;
                        HoldLog("expanded overlap x" + multiplier + ": " + wider.Count + " collider(s)");
                        break;
                    }
                }
                if (colliders == null || colliders.Count == 0)
                {
                    // Not fatal: the unmasked hand sweep below still runs.
                    HoldLog("no overlap found, even with an expanded capsule (" + (countObj ?? "null") + "); trying the hand sweep");
                    colliders = new List<object>();
                }
                MethodInfo getInteractable = component.MakeGenericMethod(interactableType);
                var unique = new Dictionary<int, object>();
                AddInteractables(unique, colliders, getInteractable, interactableType);
                var slots = new List<object>();
                var dockedPickups = new List<object>();
                var otherNames = new List<string>();
                Classify(unique, dockType, slots, dockedPickups, otherNames);
                if (slots.Count == 0 && dockedPickups.Count == 0)
                {
                    // Nothing usable at the game's own reach. Sweep around the
                    // hand without the interactor's collision mask and accept
                    // ANY slot-like object (shop, storage, backpack, pockets),
                    // not only things castable to PickupDock.
                    List<object> nearby = FindNearbyColliders(__instance, 2.5f);
                    if (nearby != null && nearby.Count > 0)
                    {
                        var extra = new Dictionary<int, object>();
                        AddInteractables(extra, nearby, getInteractable, interactableType);
                        int before = slots.Count + dockedPickups.Count;
                        Classify(extra, dockType, slots, dockedPickups, otherNames);
                        HoldLog("hand sweep found " + extra.Count + " interactable(s), " +
                            (slots.Count + dockedPickups.Count - before) + " slot-like");
                    }
                }
                object target = null;
                object chosenDock = null;
                if (slots.Count > 0)
                {
                    chosenDock = Nearest(__instance, slots);
                    target = chosenDock;
                    if (slots.Count > 1) HoldLog("multiple slots overlapped; chose the nearest of " + slots.Count);
                    // A slot worn by somebody else is not in the local player's
                    // hierarchy. Interacting with the slot itself is refused for
                    // those, so aim at the pickup docked inside it instead.
                    if (IsAnotherPlayersSlot(chosenDock, __instance))
                    {
                        object inner = SafeRead(chosenDock, "DockedPickup") ?? SafeRead(chosenDock, "DockedItem") ??
                                       SafeRead(chosenDock, "Docked");
                        if (inner != null)
                        {
                            target = inner;
                            HoldLog("that slot belongs to another player; targeting the pickup inside it");
                        }
                        else HoldLog("that slot belongs to another player and holds no readable pickup");
                    }
                }
                else if (dockedPickups.Count > 0)
                {
                    object pickupTarget = Nearest(__instance, dockedPickups);
                    target = pickupTarget;
                    object rawDock = SafeRead(pickupTarget, "Dock");
                    chosenDock = rawDock != null && IsSlotLike(rawDock, dockType) ? rawDock : null;
                    if (dockedPickups.Count > 1) HoldLog("multiple docked pickups overlapped; chose the nearest of " + dockedPickups.Count);
                }
                else
                {
                    HoldLog("no slot-like interactable in reach (" +
                        (otherNames.Count == 0 ? "none" : string.Join(", ", otherNames.ToArray())) + "); native grab continues");
                    return true;
                }
                if (target == null) { HoldLog("no usable target in the overlap"); return true; }
                if (otherNames.Count > 0)
                    HoldLog("ignoring " + otherNames.Count + " non-slot overlap(s): " + string.Join(", ", otherNames.ToArray()));
                // Report the slot state but attempt it anyway: the native
                // confirmation, forwarding and the server still decide. The old
                // strict guards silently skipped most slots on this build.
                object ownerFlag = chosenDock == null ? null : SafeRead(chosenDock, "IsOwnedByPlayer");
                object quantity = chosenDock == null ? null : SafeRead(chosenDock, "QuantityStored");
                object dockedFlag = chosenDock == null ? null : SafeRead(chosenDock, "HasDockedPickup");
                object activeFlag = SafeRead(SafeRead(chosenDock ?? target, "gameObject"), "activeInHierarchy");
                object ownerInfo = SafeRead(chosenDock ?? target, "OwnerName") ?? SafeRead(chosenDock ?? target, "Owner") ??
                                   SafeRead(chosenDock ?? target, "Player") ?? SafeRead(chosenDock ?? target, "IsOwnedByPlayer");
                HoldLog("target=" + (SafeRead(chosenDock ?? target, "name") ?? "unknown") +
                    " active=" + (activeFlag ?? "?") + " docked=" + (dockedFlag ?? "?") +
                    " quantity=" + (quantity ?? "?") + " owned=" + (ownerFlag ?? "?") +
                    " owner=" + DescribeValue(ownerInfo) +
                    "; requesting native confirmation (game and server decide)");
                MethodInfo confirm = t.GetMethods(I).FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == "GetConfirmationOnInteractable" && m.ReturnType == typeof(void) && p.Length == 1 && p[0].ParameterType == interactableType;
                });
                MethodInfo onAttempt = t.GetMethods(I).FirstOrDefault(m => m.Name == "OnStartInteractionAttempt" &&
                    m.GetParameters().Length == 0 && m.ReturnType == typeof(void));
                if (confirm == null || onAttempt == null) { HoldLog("native confirmation/attempt method unavailable"); return true; }
                object arg = interactableType.IsInstanceOfType(target) ? target : QuestModsMenu.TryCast(target, interactableType);
                if (arg == null) { HoldLog("could not cast the chosen slot"); return true; }
                insideFallback = true;
                try
                {
                    onAttempt.Invoke(__instance, null);
                    // Even if this throws partway through, do not run a second
                    // native grab that might duplicate partial side effects.
                    confirmationEntered = true;
                    confirm.Invoke(__instance, new object[] { arg });
                    object held = SafeRead(__instance, "InteractingWith");
                    object resultingContent = chosenDock == null ? null : SafeRead(chosenDock, "DockedPickup");
                    HoldLog("confirmation returned; Interacting=" + (SafeRead(__instance, "IsInteracting") ?? "unknown") +
                        " held=" + (held == null ? "none/unknown" : (SafeRead(held, "name") ?? "unknown").ToString()) +
                        " slot content now=" + (resultingContent == null ? "none/unknown" : (SafeRead(resultingContent, "name") ?? "unknown").ToString()));
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
        // Repeats the reference Interactor.GetColliderOverlap capsule with the
        // same start/end points, collision mask and trigger policy. Used when the
        // game's 10-element buffer is full and, as of 1.10.1, when the exact
        // reach found nothing (with a modest radius multiplier).
        private static List<object> FullOverlapCapsule(object interactor, float radiusMultiplier)
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
                float worldRadius = Math.Abs(Convert.ToSingle(sx)) * Convert.ToSingle(radius) * Math.Max(1f, radiusMultiplier);
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
        // Nearest candidate to the interactor's trigger, used when several
        // slots or docked pickups overlap at once.
        private static object Nearest(object interactor, List<object> candidates)
        {
            if (candidates.Count == 0) return null;
            if (candidates.Count == 1) return candidates[0];
            float rx, ry, rz;
            bool haveReference = TryPosition(SafeRead(interactor, "triggerObjectTransform"), out rx, out ry, out rz);
            object best = candidates[0];
            float bestDistance = float.MaxValue;
            foreach (object candidate in candidates)
            {
                float distance = 0f;
                float x, y, z;
                if (haveReference && TryPosition(SafeRead(candidate, "transform"), out x, out y, out z))
                {
                    float dx = x - rx, dy = y - ry, dz = z - rz;
                    distance = dx * dx + dy * dy + dz * dz;
                }
                if (distance < bestDistance) { bestDistance = distance; best = candidate; }
            }
            return best;
        }

        private static bool TryPosition(object source, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            try
            {
                object position = SafeRead(source, "position") ?? source;
                if (position == null) return false;
                object vx = SafeRead(position, "x");
                object vy = SafeRead(position, "y");
                object vz = SafeRead(position, "z");
                if (vx == null || vy == null || vz == null) return false;
                x = Convert.ToSingle(vx);
                y = Convert.ToSingle(vy);
                z = Convert.ToSingle(vz);
                return true;
            }
            catch { return false; }
        }

        // Collect interactables from a collider list: the game's own lookup
        // first, then a component search that walks up the hierarchy (many slot
        // colliders sit on children of the interactable).
        private static void AddInteractables(Dictionary<int, object> unique, List<object> colliders, MethodInfo getInteractable, Type interactableType)
        {
            foreach (object collider in colliders)
            {
                if (collider == null) continue;
                object item = null;
                try { item = getInteractable.Invoke(null, new object[] { collider }); } catch { }
                if (item == null) item = FindComponentUpwards(collider, interactableType, true);
                if (item == null) item = FindComponentUpwards(collider, interactableType, false);
                if (item == null) continue;
                try { unique[Id(item)] = item; } catch { }
            }
        }

        private static object FindComponentUpwards(object component, Type type, bool inParent)
        {
            string name = inParent ? "GetComponentInParent" : "GetComponent";
            try
            {
                foreach (MethodInfo method in component.GetType().GetMethods(I).Where(m => m.Name == name && !m.IsStatic))
                {
                    ParameterInfo[] p = method.GetParameters();
                    if (p.Length == 1 && p[0].ParameterType == typeof(Type))
                    {
                        try { object found = method.Invoke(component, new object[] { type }); if (found != null) return found; } catch { }
                    }
                    else if (p.Length == 0 && method.IsGenericMethodDefinition)
                    {
                        try { object found = method.MakeGenericMethod(type).Invoke(component, null); if (found != null) return found; } catch { }
                    }
                }
            }
            catch { }
            return null;
        }

        // Unmasked Physics.OverlapSphere around the hand, used only when the
        // game's own reach returned nothing usable.
        private static List<object> FindNearbyColliders(object interactor, float radius)
        {
            try
            {
                object trigger = SafeRead(interactor, "triggerObjectTransform") ?? SafeRead(interactor, "transform");
                object center = SafeRead(trigger, "position");
                if (center == null) return null;
                Type physics = null;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    physics = assembly.GetType("UnityEngine.Physics", false);
                    if (physics != null) break;
                }
                if (physics == null) return null;
                MethodInfo sphere = physics.GetMethods(S).FirstOrDefault(m =>
                {
                    ParameterInfo[] p = m.GetParameters();
                    return m.Name == "OverlapSphere" && m.ReturnType != typeof(void) && p.Length == 2 &&
                           p[0].ParameterType == center.GetType() && p[1].ParameterType == typeof(float);
                });
                if (sphere == null) return null;
                object result = sphere.Invoke(null, new object[] { center, radius });
                return result == null ? null : DevWheelBridge.ReadArray(result);
            }
            catch (Exception ex) { HoldLog("hand sweep failed: " + Detail(ex)); return null; }
        }

        private static void Classify(Dictionary<int, object> unique, Type dockType, List<object> slots, List<object> pickups, List<string> otherNames)
        {
            foreach (object item in unique.Values)
            {
                if (item == null) continue;
                if (IsSlotLike(item, dockType)) { AddUnique(slots, item); continue; }
                object dockedFlag = SafeRead(item, "IsDocked");
                bool dockedNow = dockedFlag is bool docked && docked;
                if (dockedNow || SafeRead(item, "Dock") != null) { AddUnique(pickups, item); continue; }
                string name = (SafeRead(item, "name") ?? "unknown").ToString();
                if (!otherNames.Contains(name)) otherNames.Add(name);
            }
        }

        // A slot is anything with docked content, whatever its concrete type:
        // PickupDock, StockDock, backpack/pocket slots, ...
        private static bool IsSlotLike(object item, Type dockType)
        {
            if (item == null) return false;
            try { if (dockType != null && QuestModsMenu.TryCast(item, dockType) != null) return true; } catch { }
            return SafeRead(item, "DockedPickup") != null || SafeRead(item, "HasDockedPickup") != null ||
                   SafeRead(item, "QuantityStored") != null || SafeRead(item, "DockedItem") != null;
        }

        private static void AddUnique(List<object> list, object item)
        {
            try
            {
                int id = Id(item);
                foreach (object existing in list)
                {
                    try { if (Id(existing) == id) return; } catch { }
                }
            }
            catch { return; }
            list.Add(item);
        }

        // Interactors seen for the local player, exposed for Grab by Name.
        internal static List<object> LocalInteractorObjects()
        {
            var result = new List<object>();
            foreach (object interactor in localInteractors)
            {
                if (interactor != null) result.Add(interactor);
            }
            return result;
        }

        internal static List<Type> PickupTypeCandidates()
        {
            var result = new List<Type>();
            foreach (Type type in pickupTypes)
            {
                if (type != null) result.Add(type);
            }
            return result;
        }

        // True when the slot is not part of the local hand's hierarchy, i.e. it
        // is worn by another player. Compared over a few ancestor levels so a
        // shared scene root does not count as "same player".
        private static bool IsAnotherPlayersSlot(object slot, object interactor)
        {
            try
            {
                object slotTransform = SafeRead(slot, "transform") ?? SafeRead(slot, "Transform");
                object handTransform = SafeRead(interactor, "transform") ?? SafeRead(interactor, "triggerObjectTransform");
                if (slotTransform == null || handTransform == null) return false;
                HashSet<int> handChain = TransformChain(handTransform, 6);
                foreach (int id in TransformChain(slotTransform, 6))
                {
                    if (handChain.Contains(id)) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static HashSet<int> TransformChain(object transform, int maxDepth)
        {
            var ids = new HashSet<int>();
            object current = transform;
            for (int depth = 0; depth < maxDepth && current != null; depth++)
            {
                try { ids.Add(Id(current)); } catch { }
                object parent = null;
                try { parent = SafeRead(current, "parent"); } catch { }
                if (parent == null) break;
                current = parent;
            }
            return ids;
        }

        private static string DescribeValue(object value)
        {
            if (value == null) return "?";
            try { return value.GetType().Name + "(" + (value.ToString() ?? "") + ")"; }
            catch { return "unreadable"; }
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
