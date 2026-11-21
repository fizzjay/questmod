// QuestMods 1.9.12 - wheel + wrist HUD + sight guards + player ESP + graphics overhaul.
// Wrist HUD: FPS plus SAFE / NOT SAFE based on nearby players and line of sight.
// Sight guards: Disconnect / Turn Off / TP Away on sight. ESP: through-wall player highlight.
// Graphics Overhaul: no fog, no blue film, full-res textures, long draw distance.
// Experimental native 3D nested wheel, not a tablet or rectangular panel.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Il2Cpp;
using Il2CppAlta.Api.DataTransferModels.Models.Shared;
using Il2CppAlta.Character;
using Il2CppAlta.Crafting;
using Il2CppAlta.Inventory;
using Il2CppAlta.Networking.Scripts.Player;
using Il2CppAlta.QuickAccessActions;
using Il2CppAlta.StatSystem;
using Il2CppFeatures.Climbing;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTownship.Downed;
using MelonLoader;
using MelonLoader.Utils;
using TabletUILib;
using UnityEngine;

[assembly: MelonInfo(typeof(QuestMods.Mod), "QuestMods", "1.9.12", "you")]
[assembly: MelonGame(null, null)]
[assembly: MelonAdditionalDependencies("TabletUILib")]

namespace QuestMods
{
    public class Mod : MelonMod
    {
        private const int MaxSubclassPatches = 32;
        private DateTime nextReport;
        private int reports;

        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("=== QuestMods 1.9.12: wheel + wrist HUD + sight guards + ESP + graphics ===");
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
            try { InitTabletUI(); }
            catch (Exception ex) { LoggerInstance.Warning("[TabletUI] Setup failed: " + ex); }
            LoggerInstance.Warning("Debug movement is not automatically enabled. Experimental IsIgnored overrides remain enabled.");
        }

        private static MelonPreferences_Entry<bool> prefGrabbyHands;
        private static MelonPreferences_Entry<bool> prefSuperSpeed;
        private static MelonPreferences_Entry<bool> prefFly;
        private static MelonPreferences_Entry<bool> prefAirGrab;
        private static MelonPreferences_Entry<bool> prefAnyConnect;
        private static MelonPreferences_Entry<bool> prefAutoRevive;
        private static MelonPreferences_Entry<bool> prefNoclip;
        private static MelonPreferences_Entry<bool> prefDurability;
        private static MelonPreferences_Entry<bool> prefNoSocial;
        private static MelonPreferences_Entry<bool> prefDevLight;
        private static bool lastDevLightPref;
        private static MelonPreferences_Entry<bool> prefMakePearl;
        private static MelonPreferences_Entry<bool> prefWristHud;
        private static MelonPreferences_Entry<bool> prefDisconnectOnSight;
        private static MelonPreferences_Entry<bool> prefTurnOffOnSight;
        private static MelonPreferences_Entry<bool> prefTpAwayOnSight;
        private static MelonPreferences_Entry<bool> prefPlayerEsp;
        private static MelonPreferences_Entry<bool> prefGraphics;
        private static MelonPreferences_Entry<bool> prefMaxDetail;
        private static bool lastMaxDetailPref;
        private static bool lastGrabbyPref, lastSpeedPref, lastFlyPref, lastNoclipPref, lastDurabilityPref;

        private void InitTabletUI()
        {
            var category = MelonPreferences.CreateCategory("QuestMods", "Quest Mods");
            prefGrabbyHands = category.CreateEntry("GrabbyHands", false, "Grabby Hands");
            prefSuperSpeed = category.CreateEntry("SuperSpeed", false, "Super Speed (5x)");
            prefFly = category.CreateEntry("Fly", false, "Fly (Debug Movement)");
            prefAirGrab = category.CreateEntry("AirGrab", false, "Air Grab");
            prefAnyConnect = category.CreateEntry("AnyConnect", false, "Any Connect");
            prefAutoRevive = category.CreateEntry("AutoRevive", false, "Auto Revive");
            prefNoclip = category.CreateEntry("Noclip", false, "Noclip");
            prefDurability = category.CreateEntry("ShowDurability", false, "Show Durability");
            prefNoSocial = category.CreateEntry("NoSocial", false, "No Social");
            prefDevLight = category.CreateEntry("DevLight", false, "Dev Light");
            prefMakePearl = category.CreateEntry("MakePearl", false, "Make Pearl");
            // Wrist HUD defaults ON; unlike the gameplay toggles above it is not
            // force-reset on launch, so a player who turns it off stays off.
            prefWristHud = category.CreateEntry("WristHUD", true, "Wrist HUD (FPS / Safe)");
            prefDisconnectOnSight = category.CreateEntry("DisconnectOnSight", false, "Disconnect on Sight");
            prefTurnOffOnSight = category.CreateEntry("TurnOffOnSight", false, "Turn Off on Sight");
            prefTpAwayOnSight = category.CreateEntry("TpAwayOnSight", false, "TP Away on Sight");
            prefPlayerEsp = category.CreateEntry("PlayerEsp", false, "Player ESP (Thru Walls)");
            prefGraphics = category.CreateEntry("GraphicsOverhaul", false, "Graphics Overhaul");
            prefMaxDetail = category.CreateEntry("MaxDetail", false, "Max Detail (LOD 0)");

            prefGrabbyHands.Value = false;
            prefSuperSpeed.Value = false;
            prefFly.Value = false;
            prefAirGrab.Value = false;
            prefAnyConnect.Value = false;
            prefAutoRevive.Value = false;
            prefNoclip.Value = false;
            prefDurability.Value = false;
            prefNoSocial.Value = false;
            prefDevLight.Value = false;
            prefMakePearl.Value = false;
            prefDisconnectOnSight.Value = false;
            prefTurnOffOnSight.Value = false;
            prefTpAwayOnSight.Value = false;
            prefPlayerEsp.Value = false;
            prefGraphics.Value = false;
            prefMaxDetail.Value = false;

            InstallExtraPatches(HarmonyInstance);

            TabletUI.AddBubble("quest mods", "QM")
                .App("tablet", "TAB", BuildTabletPage)
                .App("tps", "TP", BuildPlayerTPsPage)
                .App("customs", "SAV", BuildCustomTPsPage)
                .App("caves", "CAV", BuildCavesPage)
                .App("info", "INFO", BuildSightHelpPage);

            LoggerInstance.Msg("[TabletUI] Registered 'quest mods' bubble with tablet app.");
        }

        private static Page BuildTabletPage()
        {
            return new Page("QUEST MODS", "Toggles & Actions")
                .Toggle("Grabby Hands", prefGrabbyHands)
                .Toggle("Super Speed (5x)", prefSuperSpeed)
                .Toggle("Fly", prefFly)
                .Button("Grab Coins", () => GrabCoins())
                .Button("Pouch Limit: " + (pouchLimit > 0 ? pouchLimit.ToString() : "OFF (100)"), () => CyclePouchLimit())
                .Button("Make Day", () => MakeDay())
                .Button("Player TPs", () => OpenPlayerTPs())
                .Toggle("Air Grab", prefAirGrab)
                .Toggle("Any Connect", prefAnyConnect)
                .Toggle("Auto Revive", prefAutoRevive)
                .Toggle("Noclip", prefNoclip)
                .Toggle("Show Durability", prefDurability)
                .Toggle("No Social", prefNoSocial)
                .Toggle("Dev Light", prefDevLight)
                .Toggle("Make Pearl", prefMakePearl)
                .Toggle("Wrist HUD (FPS/Safe)", prefWristHud)
                .Button("Safe Range: " + wristRange + "m", () => CycleWristRange())
                .Button("Wrist: " + (wristSideRight ? "RIGHT" : "LEFT"), () => CycleWristSide())
                .Button("Wrist Pose: " + WristPoseLabel(), () => CycleWristPose())
                .Toggle("Disconnect on Sight", prefDisconnectOnSight)
                .Toggle("Turn Off on Sight", prefTurnOffOnSight)
                .Toggle("TP Away on Sight", prefTpAwayOnSight)
                .Toggle("Player ESP (Thru Walls)", prefPlayerEsp)
                .Toggle("Graphics Overhaul", prefGraphics)
                .Button("Overhaul View: " + graphicsRanges[graphicsRangeIndex] + "m", () => CycleGraphicsRange())
                .Button("Overhaul Sharp: " + GraphicsSharpLabel(), () => CycleGraphicsSharp())
                .Toggle("Max Detail (LOD 0)", prefMaxDetail)
                .Button("Bag Looking", () => ToggleStorage());
        }

        private static Page BuildSightHelpPage()
        {
            return new Page("SIGHT & ESP", "What each toggle does")
                .Button("Disconnect on Sight: leave to menu when seen", () => { })
                .Button("Turn Off on Sight: ALL mods off until clear", () => { })
                .Button("TP Away on Sight: warp away when seen", () => { })
                .Button("Player ESP: full-body highlight thru walls", () => { })
                .Button("Graphics Overhaul: no fog, no blue film", () => { })
                .Button("Overhaul View: camera draw distance", () => { })
                .Button("Overhaul Sharp: headset render scale", () => { })
                .Button("Max Detail: force highest LOD everywhere", () => { })
                .Button("Wrist HUD: FPS + SAFE / NOT SAFE", () => { })
                .Button("Safe Range: how far 'around you' is", () => { })
                .Button("Seen = clear sight + looking at you", () => { })
                .Button("Fully away = nobody in range for 5s", () => { })
                .Button("Wrist buttons: side + pose of the display", () => { });
        }

        public static void ToggleStorage()
        {
            GameObject[] array = UnityEngine.Object.FindObjectsOfType<GameObject>();
            foreach (GameObject gameObject in array)
            {
                if (gameObject.name.EndsWith("Bag(Clone)"))
                {
                    Transform transform = gameObject.transform.Find("Storage");
                    if (transform != null)
                    {
                        transform.gameObject.SetActive(!transform.gameObject.activeSelf);
                    }
                }
            }
        }

        private static void SyncTabletToggles()
        {
            EnforceSightPanic();
            if (prefGrabbyHands != null)
            {
                if (prefGrabbyHands.Value != lastGrabbyPref)
                {
                    lastGrabbyPref = prefGrabbyHands.Value;
                    GrabbyHands.SetEnabled(lastGrabbyPref);
                }
                else if (GrabbyHands.IsEnabled != lastGrabbyPref)
                {
                    lastGrabbyPref = GrabbyHands.IsEnabled;
                    prefGrabbyHands.Value = lastGrabbyPref;
                }
            }

            if (prefSuperSpeed != null && prefSuperSpeed.Value != lastSpeedPref)
            {
                lastSpeedPref = prefSuperSpeed.Value;
                ApplySuperSpeed(lastSpeedPref);
            }

            if (prefFly != null && prefFly.Value != lastFlyPref)
            {
                lastFlyPref = prefFly.Value;
                ApplyFly(lastFlyPref);
                if (!lastFlyPref && prefNoclip != null && prefNoclip.Value)
                {
                    prefNoclip.Value = false;
                }
            }

            if (prefNoclip != null && prefNoclip.Value != lastNoclipPref)
            {
                lastNoclipPref = prefNoclip.Value;
                ApplyNoclip(lastNoclipPref);
            }

            if (prefDevLight != null && prefDevLight.Value != lastDevLightPref)
            {
                lastDevLightPref = prefDevLight.Value;
                ApplyDevLight(lastDevLightPref);
            }

            if (prefGraphics != null && prefGraphics.Value != graphicsAppliedPref)
            {
                graphicsAppliedPref = prefGraphics.Value;
                ApplyGraphicsOverhaul(graphicsAppliedPref);
            }

            if (prefMaxDetail != null && prefMaxDetail.Value != lastMaxDetailPref)
            {
                lastMaxDetailPref = prefMaxDetail.Value;
                if (!lastMaxDetailPref) RestoreMaxDetail();
                else maxDetailNext = 0f;
            }

            if (prefDurability != null)
            {
                if (prefDurability.Value != lastDurabilityPref)
                {
                    lastDurabilityPref = prefDurability.Value;
                    if (!lastDurabilityPref) ClearDurabilityDisplays();
                }
                if (lastDurabilityPref) UpdateDurabilityDisplay();
            }

            if (prefNoSocial != null && prefNoSocial.Value)
                TickAutoGrabTablet();

            TickMakePearl();
            TickCaveTeleport();
            UpdateWaypointKeyboard();

            if (prefAutoRevive != null && prefAutoRevive.Value)
            {
                AutoReviveUpdate();
            }

            // Read-only displays / guards: a failure here must not stop the other ticks.
            try { TickSightGuard(); }
            catch (Exception ex) { MelonLogger.Warning("[SightGuard] tick failed: " + ex.Message); }
            try { TickWristHud(); }
            catch (Exception ex) { MelonLogger.Warning("[WristHUD] tick failed: " + ex.Message); }
            try { TickPlayerEsp(); }
            catch (Exception ex) { MelonLogger.Warning("[PlayerESP] tick failed: " + ex.Message); }
            try { TickGraphicsOverhaul(); }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] tick failed: " + ex.Message); }
            try { TickMaxDetail(); }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] detail tick failed: " + ex.Message); }
        }

        private static void ApplySuperSpeed(bool enabled)
        {
            try
            {
                PlayerController pc = PlayerController.Current;
                if (pc == null) { MelonLogger.Warning("[QuestMods] SuperSpeed: PlayerController.Current is null."); return; }
                PlayerCharacter playerCharacter = pc.TryCast<PlayerCharacter>();
                if (playerCharacter != null && playerCharacter.Health != null && playerCharacter.Health.StatManager != null)
                {
                    if (playerCharacter.Health.StatManager.GetStat("speed", out Stat stat) && stat != null)
                    {
                        stat.Base = enabled ? 5f : 1f;
                        MelonLogger.Msg("[QuestMods] Super Speed " + (enabled ? "ON (5x)" : "OFF (1x)") + ".");
                        return;
                    }
                }
                MelonLogger.Warning("[QuestMods] SuperSpeed: could not get 'speed' stat on PlayerCharacter.");
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] ApplySuperSpeed failed: " + ex.Message); }
        }

        private static void ApplyFly(bool enabled)
        {
            try
            {
                PlayerController pc = PlayerController.Current;
                if (pc == null) { MelonLogger.Warning("[QuestMods] Fly: PlayerController.Current is null."); return; }
                SmoothLocomotionPlayerController slpc = pc.TryCast<SmoothLocomotionPlayerController>();
                if (slpc != null && slpc.SmoothLocomotion != null)
                {
                    if (slpc.SmoothLocomotion.IsDebugMovement != enabled)
                        slpc.SmoothLocomotion.ToggleDebug();
                    MelonLogger.Msg("[QuestMods] Fly (Debug Movement) " + (enabled ? "ON" : "OFF") + ".");
                }
                else
                {
                    MelonLogger.Warning("[QuestMods] Fly: SmoothLocomotionPlayerController not found.");
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] ApplyFly failed: " + ex.Message); }
        }

        private static int noclipPlayerLayer = -1, noclipTerrainLayer = -1;
        private static void ApplyNoclip(bool enabled)
        {
            try
            {
                if (!EnsurePhysicsReflection()) { MelonLogger.Warning("[QuestMods] Noclip: Physics reflection failed."); return; }
                MethodInfo ignoreCol = physType.GetMethod("IgnoreLayerCollision", new Type[] { typeof(int), typeof(int), typeof(bool) });
                if (ignoreCol == null) { MelonLogger.Warning("[QuestMods] Noclip: IgnoreLayerCollision not found."); return; }

                if (!enabled)
                {
                    if (noclipPlayerLayer != -1 && noclipTerrainLayer != -1)
                        ignoreCol.Invoke(null, new object[] { noclipPlayerLayer, noclipTerrainLayer, false });
                    MelonLogger.Msg("[QuestMods] Noclip OFF.");
                    return;
                }

                if (noclipTerrainLayer == -1 && MasterTerrain.Instance != null && MasterTerrain.Instance.Terrain != null)
                    noclipTerrainLayer = MasterTerrain.Instance.Terrain.gameObject.layer;

                SmoothLocomotionPlayerController slpc = PlayerController.Current == null ? null : PlayerController.Current.TryCast<SmoothLocomotionPlayerController>();
                if (slpc != null && slpc.SmoothLocomotion != null && slpc.SmoothLocomotion.CharacterController != null)
                    noclipPlayerLayer = slpc.SmoothLocomotion.CharacterController.gameObject.layer;

                if (noclipPlayerLayer != -1 && noclipTerrainLayer != -1)
                {
                    ignoreCol.Invoke(null, new object[] { noclipPlayerLayer, noclipTerrainLayer, true });
                    MelonLogger.Msg("[QuestMods] Noclip ON (layers " + noclipPlayerLayer + " / " + noclipTerrainLayer + ").");
                }
                else
                {
                    MelonLogger.Warning("[QuestMods] Noclip: layers not ready (player=" + noclipPlayerLayer + ", terrain=" + noclipTerrainLayer + ").");
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] ApplyNoclip failed: " + ex.Message); }
        }

        private static int GrabCoins()
        {
            try
            {
                Interactor localInteractor = null;
                foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Interactor>()))
                {
                    Interactor inter = obj.TryCast<Interactor>();
                    if (inter != null && inter.IsLocal)
                    {
                        localInteractor = inter;
                        break;
                    }
                }
                if (localInteractor == null)
                {
                    MelonLogger.Warning("[QuestMods] GrabCoins failed: no local interactor.");
                    return 0;
                }

                int grabbed = 0;
                foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Pickup>()))
                {
                    Pickup pickup = obj.TryCast<Pickup>();
                    if (pickup == null || pickup.name == null) continue;
                    if (pickup.name.IndexOf("coin", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try
                    {
                        if (pickup.IsDocked)
                        {
                            MethodInfo undock = pickup.GetType().GetMethod("Undock", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            undock?.Invoke(pickup, new object[] { localInteractor, true, 1 });
                        }
                        MethodInfo resetTimeout = localInteractor.GetType().GetMethod("ResetTimeout", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                        resetTimeout?.Invoke(localInteractor, null);
                        localInteractor.StartInteract(pickup, false, false, false);
                        grabbed++;
                    }
                    catch { }
                }
                MelonLogger.Msg("[QuestMods] GrabCoins: grabbed " + grabbed + " coin(s).");
                return grabbed;
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[QuestMods] GrabCoins failed: " + ex.Message);
                return 0;
            }
        }

        private static void InstallExtraPatches(global::HarmonyLib.Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo checkGrab = AccessTools.Method(typeof(ClimbingHand), "CheckGrab", null, null);
                if (checkGrab != null)
                {
                    harmony.Patch(checkGrab,
                        prefix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(CheckGrabPrefix), BindingFlags.Static | BindingFlags.NonPublic)),
                        postfix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(CheckGrabPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("[QuestMods] Patched ClimbingHand.CheckGrab for Air Grab.");
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] AirGrab patch failed: " + ex.Message); }

            try
            {
                MethodInfo supports = AccessTools.Method(typeof(JointSlot), "Supports", new Type[] { typeof(JointInsertType) }, null);
                if (supports != null)
                    harmony.Patch(supports, postfix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(SupportsPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                foreach (Type t in new[] { typeof(CraftingPart), typeof(ConfirmationCraftingPart) })
                {
                    MethodInfo attempt = AccessTools.Method(t, "AttemptCraft", new Type[] { typeof(JointSlot) }, null);
                    if (attempt != null)
                        harmony.Patch(attempt, postfix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(AttemptCraftPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                }
                MethodInfo tryConnect = AccessTools.Method(typeof(JointSlot), "TryConnect", null, null);
                if (tryConnect != null)
                    harmony.Patch(tryConnect, prefix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(TryConnectPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                MethodInfo craftWith = AccessTools.Method(typeof(CraftingPart), "CraftWithSlot", null, null);
                if (craftWith != null)
                    harmony.Patch(craftWith, prefix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(CraftWithSlotPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                MelonLogger.Msg("[QuestMods] Patched JointSlot/CraftingPart for Any Connect.");

                MethodInfo unlockCheck = AccessTools.Method(typeof(PlayerUnlockManager), "Check", null, null);
                if (unlockCheck != null)
                {
                    harmony.Patch(unlockCheck, postfix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(UnlockCheckPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("[QuestMods] Patched PlayerUnlockManager.Check for Air Grab.");
                }

                MethodInfo maxQty = AccessTools.PropertyGetter(typeof(PickupDock), "MaxQuantityCurrentlyPossible");
                if (maxQty != null)
                {
                    harmony.Patch(maxQty, postfix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(PouchMaxQuantityPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("[QuestMods] Patched PickupDock.MaxQuantityCurrentlyPossible for Pouch Limit.");
                }

                MethodInfo moveToSafety = AccessTools.Method(typeof(SmoothLocomotion), "MoveToSafety", null, null);
                if (moveToSafety != null)
                {
                    harmony.Patch(moveToSafety, prefix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(MoveToSafetyPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("[QuestMods] Patched SmoothLocomotion.MoveToSafety for Noclip No-Void-TP.");
                }

                MethodInfo handleClimbing = AccessTools.Method(typeof(ClimbingHandler), "HandleClimbing", null, null);
                if (handleClimbing != null)
                {
                    harmony.Patch(handleClimbing, postfix: new HarmonyMethod(typeof(Mod).GetMethod(nameof(HandleClimbingPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                    MelonLogger.Msg("[QuestMods] Patched ClimbingHandler.HandleClimbing for Air Grab infinite stamina.");
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] AnyConnect/Extra patch failed: " + ex.Message); }
        }

        private static void UnlockCheckPostfix(ref bool __result)
        {
            if (prefAirGrab != null && prefAirGrab.Value) __result = true;
        }

        private static void HandleClimbingPostfix(ClimbingHandler __instance)
        {
            if (prefAirGrab == null || !prefAirGrab.Value || __instance == null) return;
            try
            {
                if (__instance.Settings != null)
                {
                    FieldInfo f = typeof(ClimbingSettings).GetField("staminaLossPerSecond", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    f?.SetValue(__instance.Settings, 0f);
                }
                foreach (var mgr in new[] { __instance.LeftStamina, __instance.RightStamina })
                {
                    var hsm = mgr?.TryCast<HandStaminaManager>();
                    if (hsm != null && hsm.Stamina != null)
                        hsm.Stamina.Base = hsm.Stamina.Maximum;
                }
            }
            catch { }
        }

        private static float lastTabletGrabTime = -999f;
        private static void TickAutoGrabTablet()
        {
            if (Time.time - lastTabletGrabTime < 0.25f) return;
            lastTabletGrabTime = Time.time;
            var myPc = PlayerController.Current;
            if (myPc == null) return;
            var myInteractor = myPc.RightController?.Interactor;
            if (myInteractor == null) return;

            foreach (var pickup in UnityEngine.Object.FindObjectsOfType<Pickup>())
            {
                if (pickup == null) continue;
                string pName = pickup.name?.ToLower() ?? "";
                if (!pName.Contains("tablet")) continue;
                if (myInteractor.IsInteracting && myInteractor.InteractingWith != null && myInteractor.InteractingWith.Pointer == pickup.Pointer)
                    continue;
                try
                {
                    foreach (var interactor in UnityEngine.Object.FindObjectsOfType<Interactor>())
                    {
                        if (interactor == null || interactor.Pointer == myInteractor.Pointer) continue;
                        if (interactor.IsInteracting && interactor.InteractingWith != null && interactor.InteractingWith.Pointer == pickup.Pointer)
                            interactor.StopInteract(false);
                    }
                    if (pickup.IsDocked) pickup.Undock(myInteractor, true, 1);
                    myInteractor.ResetTimeout();
                    myInteractor.StartInteract(pickup, false, false, false);
                    return;
                }
                catch { }
            }
        }

        private static float caveTpActiveUntil = -1f;
        private static float caveTpEndTime = -1f;
        private static Vector3 caveTpTargetPos;
        private static string caveTpLabel;
        private static float caveTpNextTime;
        private static int currentCaveLayer = 0;
        private static GameObject caveRenderLabelObj;
        private static Component caveRenderLabelTm;

        private static int caveTpTargetLayer = -1;
        private static bool caveTpPrevFly = false;
        private static float caveTpFlyRestoreTime = -1f;

        private static void StartCaveTeleportSequence(Vector3 pos, string label, int targetLayer)
        {
            int diff = Math.Abs(targetLayer - currentCaveLayer);
            currentCaveLayer = targetLayer;
            caveTpTargetLayer = targetLayer;
            float duration = diff >= 25 ? 15f : (diff >= 15 ? 5f : 3f);

            caveTpPrevFly = prefFly != null && prefFly.Value;
            caveTpFlyRestoreTime = -1f;
            if (prefFly != null) prefFly.Value = true;

            caveTpTargetPos = pos;
            caveTpLabel = label;
            caveTpEndTime = Time.time + duration;
            caveTpNextTime = Time.time + 0.5f;
            caveTpActiveUntil = Time.time + duration + 5f;

            var sl = UnityEngine.Object.FindObjectOfType<SmoothLocomotion>();
            if (sl != null) sl.enabled = false;

            if (duration >= 5f)
            {
                EnsureTextMeshReflection();
                if (caveRenderLabelObj != null) UnityEngine.Object.Destroy(caveRenderLabelObj);
                if (textMeshType != null)
                {
                    caveRenderLabelObj = new GameObject("QuestMods_CaveRendering");
                    caveRenderLabelTm = caveRenderLabelObj.AddComponent(Il2CppInterop.Runtime.Il2CppType.From(textMeshType));
                    object typed = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)?.MakeGenericMethod(textMeshType).Invoke(caveRenderLabelTm, null);
                    if (typed != null)
                    {
                        tmCharSizeProp?.SetValue(typed, 0.04f);
                        tmColorProp?.SetValue(typed, Color.yellow);
                        tmAnchorProp?.SetValue(typed, 4);
                        tmAlignProp?.SetValue(typed, 1);
                    }
                }
            }

            TeleportLocalPlayer(pos, label);
        }

        private static void StopCaveTeleportSequence()
        {
            caveTpEndTime = -1f;
            caveTpTargetLayer = -1;
            if (caveRenderLabelObj != null)
            {
                UnityEngine.Object.Destroy(caveRenderLabelObj);
                caveRenderLabelObj = null;
                caveRenderLabelTm = null;
            }
            var sl = UnityEngine.Object.FindObjectOfType<SmoothLocomotion>();
            if (sl != null) sl.enabled = true;
            caveTpFlyRestoreTime = Time.time + 60f;
        }

        private static void TickCaveTeleport()
        {
            if (caveTpFlyRestoreTime > 0f && Time.time >= caveTpFlyRestoreTime)
            {
                caveTpFlyRestoreTime = -1f;
                if (prefFly != null) prefFly.Value = caveTpPrevFly;
            }

            if (caveTpEndTime < 0f) return;

            if (caveRenderLabelObj != null && Camera.main != null)
            {
                Transform cam = Camera.main.transform;
                caveRenderLabelObj.transform.position = cam.position + cam.forward * 1.2f;
                caveRenderLabelObj.transform.rotation = Quaternion.LookRotation(cam.forward, Vector3.up);
                if (caveRenderLabelTm != null && textMeshType != null)
                {
                    int secsLeft = Mathf.CeilToInt(Mathf.Max(0f, caveTpEndTime - Time.time));
                    object typed = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)?.MakeGenericMethod(textMeshType).Invoke(caveRenderLabelTm, null);
                    if (typed != null) tmTextProp?.SetValue(typed, "Rendering... " + secsLeft + "s");
                }
            }

            if (Time.time >= caveTpEndTime)
            {
                StopCaveTeleportSequence();
                return;
            }

            if (Time.time >= caveTpNextTime)
            {
                caveTpNextTime = Time.time + 0.5f;
                TeleportLocalPlayer(caveTpTargetPos, caveTpLabel);
            }
        }

        private static bool MoveToSafetyPrefix()
        {
            if (prefNoclip != null && prefNoclip.Value) return false;
            if (Time.time < caveTpActiveUntil) return false;
            return true;
        }

        private static int pouchLimit = 0; // 0 = OFF (normal 100), else 1000..5000
        private static void PouchMaxQuantityPostfix(ref int __result)
        {
            if (pouchLimit > 0 && __result >= 10) __result = pouchLimit;
        }

        private static void CyclePouchLimit()
        {
            if (pouchLimit == 0) pouchLimit = 1000;
            else if (pouchLimit < 5000) pouchLimit += 1000;
            else pouchLimit = 0;

            string msgText = pouchLimit > 0 ? ("Pouch Limit: " + pouchLimit) : "Pouch Limit: OFF (100)";
            MelonLogger.Msg("[QuestMods] " + msgText);
        }

        private static float nextDurabilityUpdate;
        private static readonly Dictionary<IntPtr, Component> durabilityLabels = new Dictionary<IntPtr, Component>();
        private static Type textMeshType;
        private static PropertyInfo tmTextProp, tmColorProp, tmCharSizeProp, tmFontSizeProp, tmAlignProp, tmAnchorProp;

        private static bool EnsureTextMeshReflection()
        {
            if (textMeshType != null) return tmTextProp != null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                textMeshType = a.GetType("UnityEngine.TextMesh", false);
                if (textMeshType != null) break;
            }
            if (textMeshType != null)
            {
                tmTextProp = textMeshType.GetProperty("text");
                tmColorProp = textMeshType.GetProperty("color");
                tmCharSizeProp = textMeshType.GetProperty("characterSize");
                tmFontSizeProp = textMeshType.GetProperty("fontSize");
                tmAlignProp = textMeshType.GetProperty("alignment");
                tmAnchorProp = textMeshType.GetProperty("anchor");
            }
            return tmTextProp != null;
        }

        private static void ClearDurabilityDisplays()
        {
            foreach (var kv in durabilityLabels)
            {
                if (kv.Value != null && !kv.Value.WasCollected && kv.Value.gameObject != null)
                    UnityEngine.Object.Destroy(kv.Value.gameObject);
            }
            durabilityLabels.Clear();
        }

        private static void UpdateDurabilityDisplay()
        {
            if (Time.time < nextDurabilityUpdate) return;
            nextDurabilityUpdate = Time.time + 0.25f;
            if (!EnsureTextMeshReflection()) return;
            Camera cam = Camera.main;
            foreach (DurabilityModule dm in UnityEngine.Object.FindObjectsOfType<DurabilityModule>())
            {
                if (dm == null || dm.WasCollected) continue;
                if (!durabilityLabels.TryGetValue(dm.Pointer, out Component tm) || tm == null || tm.WasCollected)
                {
                    GameObject go = new GameObject("QuestMods_DurabilityLabel");
                    go.transform.SetParent(dm.transform, false);
                    go.transform.localPosition = new Vector3(0f, 0.15f, 0f);
                    tm = go.AddComponent(Il2CppInterop.Runtime.Il2CppType.From(textMeshType));
                    if (tm != null)
                    {
                        MethodInfo tryCast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)?.MakeGenericMethod(textMeshType);
                        if (tryCast != null) tm = tryCast.Invoke(tm, null) as Component ?? tm;
                    }
                    if (tm == null) continue;
                    tmCharSizeProp?.SetValue(tm, 0.02f);
                    tmFontSizeProp?.SetValue(tm, 32);
                    if (tmAlignProp != null) tmAlignProp.SetValue(tm, Enum.ToObject(tmAlignProp.PropertyType, 1)); // Center
                    if (tmAnchorProp != null) tmAnchorProp.SetValue(tm, Enum.ToObject(tmAnchorProp.PropertyType, 4)); // MiddleCenter
                    durabilityLabels[dm.Pointer] = tm;
                }
                int pct = Mathf.Clamp(Mathf.RoundToInt(dm.Integrity * 100f), 0, 100);
                tmTextProp?.SetValue(tm, pct + "%");
                tmColorProp?.SetValue(tm, Color.Lerp(Color.red, Color.green, dm.Integrity));
                if (cam != null)
                    tm.transform.rotation = Quaternion.LookRotation(tm.transform.position - cam.transform.position);
            }
        }

        // --- Wrist HUD: FPS + SAFE / NOT SAFE --------------------------------
        // A small 3D TextMesh that follows the local player's left wrist.
        // SAFE  = no other player within wristRange can see your head
        //         (no one around, or everyone nearby is behind solid cover).
        // NOT SAFE = at least one nearby player has an unobstructed line of
        //         sight, or a nearby player's position could not be read.
        // This is client-side information only; it does not change gameplay.
        private const float WristRefreshSeconds = 0.25f;
        private static readonly int[] wristRanges = { 10, 20, 30, 50, 80 };
        private static int wristRange = 30;
        private static float wristFps;
        private static int wristFpsLastFrame;
        private static float wristFpsSampleTime = -1f;
        private static float wristNextRefresh;
        private static bool wristHudWarned;

        private static GameObject wristRoot;
        private static Component wristFpsTm, wristStatusTm, wristInfoTm;
        private static object wristFpsTyped, wristStatusTyped, wristInfoTyped;
        private static string wristFpsCache, wristStatusCache, wristInfoCache;
        private static string wristLoggedStatus;

        private struct WristScan
        {
            public bool Ready;      // player list was readable
            public int Nearby;      // other players inside wristRange
            public int Watchers;    // nearby players with a clear line of sight
            public int Unknown;     // nearby entries whose position could not be read
            public int Seers;       // nearby players actually looking at you
            public bool HasSeer;
            public Vector3 NearestSeer;
        }

        // Anchor presets: where the text sits on the controller and how it is
        // tilted, so it can be made to sit flush on the back of the wrist.
        // Cycle with the "Wrist Pose" tablet button until it looks right.
        private static readonly (string Label, Vector3 Pos, Quaternion Rot)[] wristPoses =
        {
            ("Back 60",    new Vector3(0f,  0.020f, -0.050f), Quaternion.Euler(60f, 0f, 0f)),
            ("Back 45",    new Vector3(0f,  0.018f, -0.048f), Quaternion.Euler(45f, 0f, 0f)),
            ("Back 75",    new Vector3(0f,  0.022f, -0.052f), Quaternion.Euler(75f, 0f, 0f)),
            ("Top 90",     new Vector3(0f,  0.055f, -0.010f), Quaternion.Euler(90f, 0f, 0f)),
            ("Palm 60",    new Vector3(0f, -0.028f, -0.050f), Quaternion.Euler(-60f, 0f, 0f)),
            ("Flipped 60", new Vector3(0f,  0.020f, -0.050f), Quaternion.Euler(60f, 180f, 0f)),
            ("Flat",       new Vector3(0f,  0.012f, -0.050f), Quaternion.identity)
        };
        private static int wristPoseIndex;
        private static bool wristSideRight;

        private static string WristPoseLabel()
        {
            return wristPoses[wristPoseIndex].Label;
        }

        private static void CycleWristPose()
        {
            wristPoseIndex = (wristPoseIndex + 1) % wristPoses.Length;
            MelonLogger.Msg("[WristHUD] Wrist pose: " + WristPoseLabel());
        }

        private static void CycleWristSide()
        {
            wristSideRight = !wristSideRight;
            MelonLogger.Msg("[WristHUD] Wrist side: " + (wristSideRight ? "RIGHT" : "LEFT"));
        }

        private static void CycleWristRange()
        {
            int idx = Array.IndexOf(wristRanges, wristRange);
            wristRange = wristRanges[(idx + 1) % wristRanges.Length];
            wristNextRefresh = 0f;
            MelonLogger.Msg("[WristHUD] Safe range set to " + wristRange + "m.");
        }

        public static void WristHudSceneLoaded()
        {
            ClearWristHud();
            wristFps = 0f;
            wristFpsSampleTime = -1f;
            wristFpsLastFrame = 0;
            wristNextRefresh = 0f;
        }

        private static void ClearWristHud()
        {
            if (wristRoot != null) UnityEngine.Object.Destroy(wristRoot);
            wristRoot = null;
            wristFpsTm = wristStatusTm = wristInfoTm = null;
            wristFpsTyped = wristStatusTyped = wristInfoTyped = null;
            wristFpsCache = wristStatusCache = wristInfoCache = null;
            wristLoggedStatus = null;
        }

        private static void TickWristHud()
        {
            if (prefWristHud == null || !prefWristHud.Value) { ClearWristHud(); return; }
            if (!EnsureWristHud()) return;

            // Average FPS over half a second using Unity's frame counter.
            float now = Time.realtimeSinceStartup;
            if (wristFpsSampleTime < 0f)
            {
                wristFpsSampleTime = now;
                wristFpsLastFrame = Time.frameCount;
            }
            else
            {
                float elapsed = now - wristFpsSampleTime;
                if (elapsed >= 0.5f)
                {
                    wristFps = (Time.frameCount - wristFpsLastFrame) / elapsed;
                    wristFpsSampleTime = now;
                    wristFpsLastFrame = Time.frameCount;
                }
            }

            FollowWristHud();
            if (Time.time < wristNextRefresh) return;
            wristNextRefresh = Time.time + WristRefreshSeconds;

            WristScan scan = lastWristScan;
            string fpsText = wristFps > 0f ? "FPS " + Mathf.RoundToInt(wristFps) : "FPS --";
            string statusText;
            Color statusColor;
            string infoText;
            if (!scan.Ready)
            {
                statusText = "SCANNING";
                statusColor = new Color(1f, 0.85f, 0.2f);
                infoText = "NO PLAYER DATA";
            }
            else if (scan.Watchers > 0 || scan.Unknown > 0)
            {
                statusText = "NOT SAFE";
                statusColor = new Color(1f, 0.18f, 0.18f);
                infoText = "N:" + scan.Nearby + " VIS:" + scan.Watchers + " LOOK:" + scan.Seers +
                    (scan.Unknown > 0 ? " ?:" + scan.Unknown : "");
            }
            else
            {
                statusText = "SAFE";
                statusColor = new Color(0.25f, 1f, 0.3f);
                infoText = scan.Nearby == 0 ? "CLEAR " + wristRange + "m" : "N:" + scan.Nearby + " HIDDEN";
            }

            if (wristLoggedStatus != statusText)
            {
                wristLoggedStatus = statusText;
                MelonLogger.Msg("[WristHUD] " + statusText + " (near=" + scan.Nearby + " visible=" + scan.Watchers +
                    " looking=" + scan.Seers + " unknown=" + scan.Unknown + " range=" + wristRange + "m)");
            }

            ApplyWristLabel(wristFpsTm, wristFpsTyped, ref wristFpsCache, fpsText, Color.white);
            ApplyWristLabel(wristStatusTm, wristStatusTyped, ref wristStatusCache, statusText, statusColor);
            ApplyWristLabel(wristInfoTm, wristInfoTyped, ref wristInfoCache, infoText, new Color(0.72f, 0.75f, 0.8f));
        }

        private static bool EnsureWristHud()
        {
            if (wristRoot != null) return wristFpsTm != null && wristStatusTm != null;
            if (!EnsureTextMeshReflection() || textMeshType == null)
            {
                if (!wristHudWarned) { wristHudWarned = true; MelonLogger.Warning("[WristHUD] TextMesh unavailable; wrist display disabled."); }
                return false;
            }
            try
            {
                // No plate/box: just clean text rigidly attached to the wrist.
                wristRoot = new GameObject("QuestMods_WristHUD");
                wristFpsTm = CreateWristText(wristRoot.transform, new Vector3(0f, 0.027f, -0.002f), 0.012f, 48, out wristFpsTyped);
                wristStatusTm = CreateWristText(wristRoot.transform, new Vector3(0f, -0.002f, -0.002f), 0.015f, 48, out wristStatusTyped);
                wristInfoTm = CreateWristText(wristRoot.transform, new Vector3(0f, -0.031f, -0.002f), 0.008f, 32, out wristInfoTyped);
                MelonLogger.Msg("[WristHUD] Created wrist display (FPS + SAFE/NOT SAFE, range " + wristRange + "m).");
                return wristFpsTm != null && wristStatusTm != null;
            }
            catch (Exception ex)
            {
                ClearWristHud();
                if (!wristHudWarned) { wristHudWarned = true; MelonLogger.Warning("[WristHUD] setup failed: " + ex.Message); }
                return false;
            }
        }

        private static Component CreateWristText(Transform parent, Vector3 localPos, float charSize, int fontSize, out object typed)
        {
            typed = null;
            GameObject go = new GameObject("QuestMods_WristLabel");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            Component tm = go.AddComponent(Il2CppInterop.Runtime.Il2CppType.From(textMeshType));
            if (tm == null) return null;
            try
            {
                MethodInfo tryCast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public)
                    .FirstOrDefault(m => m.Name == "TryCast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                typed = tryCast?.MakeGenericMethod(textMeshType).Invoke(tm, null);
                object target = typed ?? tm;
                tmTextProp?.SetValue(target, "");
                tmCharSizeProp?.SetValue(target, charSize);
                tmFontSizeProp?.SetValue(target, fontSize);
                SetWristEnum(tmAnchorProp, target, 4); // MiddleCenter
                SetWristEnum(tmAlignProp, target, 1);  // Center
                tmColorProp?.SetValue(target, Color.white);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[WristHUD] TextMesh setup: " + ex.Message);
            }
            return tm;
        }

        private static void SetWristEnum(PropertyInfo prop, object target, int value)
        {
            if (prop == null || target == null) return;
            try { prop.SetValue(target, prop.PropertyType.IsEnum ? Enum.ToObject(prop.PropertyType, value) : value); }
            catch { }
        }

        private static void ApplyWristLabel(Component tm, object typed, ref string cache, string text, Color color)
        {
            if (tm == null) return;
            try
            {
                object target = typed ?? tm;
                if (cache != text) { tmTextProp?.SetValue(target, text); cache = text; }
                tmColorProp?.SetValue(target, color);
            }
            catch { }
        }

        private static void FollowWristHud()
        {
            if (wristRoot == null) return;
            PlayerController pc = PlayerController.Current;
            Transform hand = null;
            if (pc != null)
            {
                Transform left = pc.LeftController != null ? pc.LeftController.transform : null;
                Transform right = pc.RightController != null ? pc.RightController.transform : null;
                hand = wristSideRight ? (right ?? left) : (left ?? right);
            }
            if (hand == null)
            {
                if (wristRoot.activeSelf) wristRoot.SetActive(false);
                return;
            }
            if (!wristRoot.activeSelf) wristRoot.SetActive(true);

            // Rigidly parented: the display conforms to the wrist angle and
            // sticks to it, instead of floating as a billboard or a black box.
            if (wristRoot.transform.parent != hand) wristRoot.transform.SetParent(hand, false);
            Vector3 sc = hand.lossyScale;
            wristRoot.transform.localScale = new Vector3(
                Mathf.Abs(sc.x) > 0.0001f ? 1f / sc.x : 1f,
                Mathf.Abs(sc.y) > 0.0001f ? 1f / sc.y : 1f,
                Mathf.Abs(sc.z) > 0.0001f ? 1f / sc.z : 1f);
            wristRoot.transform.localPosition = wristPoses[wristPoseIndex].Pos;
            wristRoot.transform.localRotation = wristPoses[wristPoseIndex].Rot;
        }

        private static WristScan ScanWristSafety()
        {
            WristScan scan = new WristScan();
            PlayerController pc = PlayerController.Current;
            if (pc == null) return scan;              // not in a world yet -> SCANNING
            Vector3 myHead = LocalHeadPosition(pc);
            var allPlayers = Player.AllPlayers;
            if (allPlayers == null) return scan;     // never claim SAFE on unknown data

            IntPtr localPtr = Player.Current != null ? Player.Current.Pointer : IntPtr.Zero;
            float rangeSq = (float)wristRange * wristRange;
            try
            {
                scan.Ready = true;
                foreach (var raw in allPlayers)
                {
                    if (raw == null) continue;
                    var il2 = raw as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                    if (il2 == null || il2.Pointer == localPtr) continue;
                    Player p = il2.TryCast<Player>();
                    if (p == null) continue;
                    PlayerController other = p.PlayerController;
                    if (other == null || other.Pointer == pc.Pointer) continue; // this is us
                    Vector3 feet;
                    bool haveFeet = TryGetRemoteFeet(p, other, out feet);
                    float farSq = (wristRange + 3f) * (wristRange + 3f);
                    if (haveFeet && (feet - myHead).sqrMagnitude > farSq) continue; // definitely outside range
                    Vector3 theirHead;
                    if (!TryGetRemoteHead(other, feet, haveFeet, out theirHead)) { scan.Unknown++; continue; }
                    Vector3 delta = theirHead - myHead;
                    if (delta.sqrMagnitude > rangeSq) continue;                  // too far to matter
                    scan.Nearby++;
                    if (HasLineOfSight(myHead, theirHead, pc.transform, other.transform))
                    {
                        scan.Watchers++;
                        // "Looks at you" = clear line of sight AND (when we can
                        // read their Head transform) you inside their view cone.
                        // If their gaze is unreadable, fail closed: count it.
                        bool looking = true;
                        Transform gaze = FindHeadTransform(other);
                        if (gaze != null)
                        {
                            Vector3 toMe = myHead - gaze.position;
                            if (toMe.sqrMagnitude > 0.0001f)
                                looking = Vector3.Dot(gaze.forward, toMe.normalized) > 0.35f;
                        }
                        if (looking)
                        {
                            scan.Seers++;
                            if (!scan.HasSeer ||
                                (theirHead - myHead).sqrMagnitude < (scan.NearestSeer - myHead).sqrMagnitude)
                            {
                                scan.HasSeer = true;
                                scan.NearestSeer = theirHead;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Warning("[WristHUD] player scan failed: " + ex.Message);
                scan.Ready = false;
            }
            return scan;
        }

        private static Vector3 LocalHeadPosition(PlayerController pc)
        {
            Vector3 feet = pc.PlayerFeetPosition;
            Camera cam = Camera.main;
            if (cam != null && cam.transform.position.y > feet.y + 0.3f)
                return cam.transform.position;
            // ReturnToMainMenuAction uses PlayerController.Current.Head; reach it
            // by reflection so a Mono-only field cannot break the IL2CPP build.
            Transform head = FindHeadTransform(pc);
            if (head != null && head.position.y > feet.y + 0.3f)
                return head.position;
            return feet + Vector3.up * 1.35f;
        }

        private static bool TryGetRemoteFeet(Player p, PlayerController other, out Vector3 feet)
        {
            feet = Vector3.zero;
            try
            {
                feet = other.PlayerFeetPosition;
                if (feet.sqrMagnitude > 0.0001f) return true;
            }
            catch { }
            try
            {
                if (p.transform != null)
                {
                    feet = p.transform.position;
                    return feet.sqrMagnitude > 0.0001f;
                }
            }
            catch { }
            return false;
        }

        private static bool TryGetRemoteHead(PlayerController other, Vector3 feet, bool haveFeet, out Vector3 head)
        {
            head = Vector3.zero;
            // A head/camera transform is preferred when available, but only if it
            // is plausibly attached to this player (within 3m of their feet).
            Transform gaze = FindHeadTransform(other);
            if (gaze != null && (!haveFeet || (gaze.position - feet).sqrMagnitude < 9f))
            {
                head = gaze.position;
                return true;
            }
            if (!haveFeet) return false;
            head = feet + Vector3.up * 1.35f;
            return true;
        }

        private static Transform FindHeadTransform(object controller)
        {
            if (controller == null) return null;
            string[] names = { "Head", "head", "HeadTransform", "headTransform", "Camera",
                               "camera", "CameraTransform", "ViewTransform", "HeadBone" };
            Type t = controller.GetType();
            foreach (string name in names)
            {
                try
                {
                    PropertyInfo prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    FieldInfo field = prop == null ? t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) : null;
                    object value = prop != null ? prop.GetValue(controller) : field?.GetValue(controller);
                    Transform tr = AsTransform(value);
                    if (tr != null) return tr;
                }
                catch { }
            }
            return null;
        }

        private static Transform AsTransform(object value)
        {
            if (value is Transform tr) return tr;
            var il2 = value as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
            return il2?.TryCast<Transform>();
        }

        // True when a ray from your head to theirs is not blocked by the world.
        // Player bodies, held items, triggers and our own HUD props are stepped
        // over; a wall, terrain or prop between you means they cannot see you.
        private static bool HasLineOfSight(Vector3 from, Vector3 to, Transform rootA, Transform rootB)
        {
            Vector3 delta = to - from;
            float dist = delta.magnitude;
            if (dist < 0.3f) return true;
            Vector3 dir = delta / dist;
            float remaining = dist - 0.25f; // stop short of their head capsule
            Vector3 origin = from;
            for (int i = 0; i < 12 && remaining > 0.02f; i++)
            {
                if (!Physics.Raycast(origin, dir, out RaycastHit hit, remaining)) return true;
                Collider col = hit.collider;
                Transform hitTransform = col != null ? col.transform : null;
                bool ignorable = col == null || col.isTrigger
                    || BelongsToPlayer(hitTransform, rootA, rootB)
                    || IsModObject(hitTransform);
                if (!ignorable) return false;
                float step = Mathf.Max(hit.distance + 0.08f, 0.08f);
                origin += dir * step;
                remaining -= step;
            }
            return true;
        }

        private static bool BelongsToPlayer(Transform t, Transform rootA, Transform rootB)
        {
            if (t == null) return false;
            if (rootA != null && (t == rootA || t.IsChildOf(rootA))) return true;
            if (rootB != null && (t == rootB || t.IsChildOf(rootB))) return true;
            return false;
        }

        private static bool IsModObject(Transform t)
        {
            while (t != null)
            {
                if (t.name != null && t.name.StartsWith("QuestMods_")) return true;
                t = t.parent;
            }
            return false;
        }

        // --- Sight Guard: react when someone is looking at you ---------------
        // The supplied ReturnToMainMenuAction reference shows PlayerController.Head
        // and GameModeManager.StopCurrentModeAsync("return to menu", true). Both
        // are reached by reflection here because Quest is IL2CPP. The supplied
        // ClientCommandHandler is SERVER-SIDE remote-console code, so this client
        // mod does not call it; leaving a game uses the ReturnToMainMenuAction path.
        private const float SightClearSeconds = 5f;
        private const float SightActionCooldown = 12f;
        private static bool sightPanic;
        private static float sightClearSince = -1f;
        private static float lastSightDisconnect = -999f, lastSightTp = -999f;
        private static float sightNextRefresh;
        private static WristScan lastWristScan;
        private static readonly List<(MelonPreferences_Entry<bool> Entry, bool Value)> sightSnapshot =
            new List<(MelonPreferences_Entry<bool> Entry, bool Value)>();

        private static IEnumerable<MelonPreferences_Entry<bool>> PanicEntries()
        {
            yield return prefGrabbyHands;
            yield return prefSuperSpeed;
            yield return prefFly;
            yield return prefAirGrab;
            yield return prefAnyConnect;
            yield return prefAutoRevive;
            yield return prefNoclip;
            yield return prefDurability;
            yield return prefNoSocial;
            yield return prefDevLight;
            yield return prefMakePearl;
            yield return prefPlayerEsp;
            yield return prefGraphics;
            yield return prefMaxDetail;
        }

        // Runs every frame from SyncTabletToggles, so a panic cannot be undone
        // by opening the tablet while somebody is still watching.
        private static void EnforceSightPanic()
        {
            if (!sightPanic) return;
            foreach (var e in PanicEntries()) if (e != null && e.Value) e.Value = false;
        }

        private static void EnterSightPanic()
        {
            if (sightPanic) return;
            sightSnapshot.Clear();
            foreach (var e in PanicEntries()) if (e != null) sightSnapshot.Add((e, e.Value));
            sightPanic = true;
            sightClearSince = -1f;
            foreach (var e in PanicEntries()) if (e != null) e.Value = false;
            MelonLogger.Msg("[SightGuard] Someone is looking: all gameplay mods OFF until fully away.");
        }

        private static void ExitSightPanic()
        {
            if (!sightPanic) return;
            sightPanic = false;
            foreach (var s in sightSnapshot) if (s.Entry != null) s.Entry.Value = s.Value;
            sightSnapshot.Clear();
            sightClearSince = -1f;
            MelonLogger.Msg("[SightGuard] Area clear; restored your mod toggles.");
        }

        // One shared 4 Hz safety scan feeds both the wrist HUD and the guards,
        // so the guards keep working even if the wrist HUD toggle is OFF.
        private static void TickSightGuard()
        {
            if (Time.time < sightNextRefresh) { EnforceSightPanic(); return; }
            sightNextRefresh = Time.time + WristRefreshSeconds;
            lastWristScan = ScanWristSafety();
            TickSightActions(lastWristScan);
            EnforceSightPanic();
        }

        private static void TickSightActions(WristScan scan)
        {
            bool seen = scan.Ready && scan.Seers > 0;
            bool fullyAway = scan.Ready && scan.Nearby == 0 && scan.Watchers == 0 && scan.Unknown == 0;

            if (prefTurnOffOnSight != null && prefTurnOffOnSight.Value)
            {
                if (seen && !sightPanic) EnterSightPanic();
                if (sightPanic)
                {
                    if (fullyAway)
                    {
                        if (sightClearSince < 0f) sightClearSince = Time.time;
                        else if (Time.time - sightClearSince >= SightClearSeconds) ExitSightPanic();
                    }
                    else sightClearSince = -1f;
                }
            }
            else if (sightPanic) ExitSightPanic();

            if (!seen) return;

            if (prefDisconnectOnSight != null && prefDisconnectOnSight.Value &&
                Time.time - lastSightDisconnect > SightActionCooldown)
            {
                lastSightDisconnect = Time.time;
                if (TryReturnToMainMenu()) MelonLogger.Msg("[SightGuard] Disconnect on Sight: returning to main menu.");
                else MelonLogger.Warning("[SightGuard] Disconnect on Sight: StopCurrentModeAsync not found on this build.");
            }

            if (prefTpAwayOnSight != null && prefTpAwayOnSight.Value &&
                Time.time - lastSightTp > SightActionCooldown)
            {
                lastSightTp = Time.time;
                TpAwayOnSight(scan);
            }
        }

        private static void TpAwayOnSight(WristScan scan)
        {
            try
            {
                PlayerController pc = PlayerController.Current;
                if (pc == null) return;
                Vector3 origin = pc.PlayerFeetPosition;
                Vector3 seer = scan.HasSeer ? scan.NearestSeer : origin + Vector3.forward;
                var waypoints = LoadWaypoints();
                Vector3 dest;
                string label;
                if (waypoints.Count > 0)
                {
                    // Prefer a saved waypoint (a known-good spot) far from the seer.
                    float best = -1f;
                    dest = waypoints[0].Pos;
                    label = waypoints[0].Name;
                    foreach (var w in waypoints)
                    {
                        float d = (w.Pos - seer).sqrMagnitude;
                        if (d > best) { best = d; dest = w.Pos; label = w.Name; }
                    }
                }
                else
                {
                    Vector3 away = origin - seer;
                    away.y = 0f;
                    if (away.sqrMagnitude < 0.01f)
                    {
                        Camera cam = Camera.main;
                        away = cam != null ? -cam.transform.forward : Vector3.back;
                        away.y = 0f;
                    }
                    away.Normalize();
                    dest = origin + away * 60f + Vector3.up * 0.2f;
                    label = "60m away";
                }
                TeleportLocalPlayer(dest, "Sight TP: " + label);
                MelonLogger.Msg("[SightGuard] TP Away on Sight -> " + label);
            }
            catch (Exception ex) { MelonLogger.Warning("[SightGuard] TP away failed: " + ex.Message); }
        }

        private static bool TryReturnToMainMenu()
        {
            try
            {
                Type gm = FindLoadedType("GameModeManager");
                if (gm == null) return false;
                foreach (MethodInfo m in gm.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (m.Name != "StopCurrentModeAsync") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    object[] args;
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(bool))
                        args = new object[] { "return to menu", true };
                    else if (ps.Length == 1 && ps[0].ParameterType == typeof(string))
                        args = new object[] { "return to menu" };
                    else if (ps.Length == 0) args = null;
                    else continue;
                    m.Invoke(null, args);
                    return true;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[SightGuard] StopCurrentModeAsync failed: " + ex.Message); }
            return false;
        }

        private static readonly Dictionary<string, Type> loadedTypeCache = new Dictionary<string, Type>();

        private static Type FindLoadedType(string simpleName)
        {
            if (loadedTypeCache.TryGetValue(simpleName, out Type cached)) return cached;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;
                foreach (Type t in types)
                {
                    if (t == null || t.Name != simpleName) continue;
                    loadedTypeCache[simpleName] = t;
                    return t;
                }
            }
            return null;
        }

        // --- Player ESP: full-body highlight visible through walls -----------
        // One capsule + head sphere + nameplate per remote player, drawn with the
        // built-in GUI/Text Shader (ZTest Always / ZWrite Off) so walls cannot
        // hide them. Colliders are removed so they never affect grabs or LOS.
        private static float espNextRefresh;
        private static Material espMaterial;
        private static bool espWarned;
        private static readonly Dictionary<IntPtr, EspMarker> espMarkers = new Dictionary<IntPtr, EspMarker>();

        private class EspMarker
        {
            public GameObject Root;
            public Component Text;
            public object Typed;
            public string Cached;
        }

        public static void EspSceneLoaded()
        {
            foreach (var kv in espMarkers) if (kv.Value.Root != null) UnityEngine.Object.Destroy(kv.Value.Root);
            espMarkers.Clear();
            if (espMaterial != null) UnityEngine.Object.Destroy(espMaterial);
            espMaterial = null;
            espWarned = false;
            espNextRefresh = 0f;
        }

        private static bool EnsureEspMaterial()
        {
            if (espMaterial != null) return true;
            try
            {
                // GUI/Text Shader has ZTest Always baked in, so it draws through
                // walls. Sprites/Default is only a fallback if it was stripped.
                Shader sh = Shader.Find("GUI/Text Shader") ?? Shader.Find("Sprites/Default");
                if (sh == null) return false;
                espMaterial = new Material(sh);
                Color c = new Color(0.15f, 1f, 0.95f, 0.34f);
                if (espMaterial.HasProperty("_Color")) espMaterial.SetColor("_Color", c);
                else espMaterial.color = c;
                if (espMaterial.HasProperty("_ZTest")) espMaterial.SetInt("_ZTest", 8); // CompareFunction.Always
                espMaterial.renderQueue = 4000;
                return true;
            }
            catch (Exception ex) { MelonLogger.Warning("[PlayerESP] material failed: " + ex.Message); return false; }
        }

        private static void TickPlayerEsp()
        {
            if (prefPlayerEsp == null || !prefPlayerEsp.Value)
            {
                if (espMarkers.Count > 0) EspSceneLoaded();
                return;
            }
            if (Time.time < espNextRefresh) return;
            espNextRefresh = Time.time + 0.2f;
            if (!EnsureEspMaterial())
            {
                if (!espWarned) { espWarned = true; MelonLogger.Warning("[PlayerESP] through-wall shader unavailable; ESP disabled."); }
                return;
            }
            PlayerController pc = PlayerController.Current;
            var allPlayers = Player.AllPlayers;
            if (pc == null || allPlayers == null) return;

            IntPtr localPtr = Player.Current != null ? Player.Current.Pointer : IntPtr.Zero;
            var seen = new HashSet<IntPtr>();
            try
            {
                foreach (var raw in allPlayers)
                {
                    if (raw == null) continue;
                    var il2 = raw as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                    if (il2 == null || il2.Pointer == localPtr) continue;
                    Player p = il2.TryCast<Player>();
                    if (p == null) continue;
                    PlayerController other = p.PlayerController;
                    if (other == null || other.Pointer == pc.Pointer) continue;
                    Vector3 feet;
                    if (!TryGetRemoteFeet(p, other, out feet)) continue;
                    seen.Add(il2.Pointer);
                    string name = p.UserInfo?.Username ?? p.name ?? "Player";
                    EspMarker marker = GetEspMarker(il2.Pointer, name);
                    if (marker == null || marker.Root == null) continue;
                    marker.Root.transform.position = feet + Vector3.up * 0.9f;
                    float dist = Vector3.Distance(pc.PlayerFeetPosition, feet);
                    string label = name + "  " + Mathf.RoundToInt(dist) + "m";
                    ApplyWristLabel(marker.Text, marker.Typed, ref marker.Cached, label, new Color(0.45f, 1f, 1f));
                    Camera cam = Camera.main;
                    if (cam != null && marker.Text != null)
                        marker.Text.transform.rotation = Quaternion.LookRotation(
                            marker.Text.transform.position - cam.transform.position, cam.transform.up);
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[PlayerESP] scan failed: " + ex.Message); }

            var stale = new List<IntPtr>();
            foreach (var kv in espMarkers) if (!seen.Contains(kv.Key)) stale.Add(kv.Key);
            foreach (IntPtr k in stale)
            {
                if (espMarkers[k].Root != null) UnityEngine.Object.Destroy(espMarkers[k].Root);
                espMarkers.Remove(k);
            }
        }

        private static EspMarker GetEspMarker(IntPtr ptr, string name)
        {
            if (espMarkers.TryGetValue(ptr, out EspMarker m) && m != null && m.Root != null) return m;
            GameObject root = new GameObject("QuestMods_ESPMarker");
            GameObject body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            body.name = "QuestMods_ESPBody";
            body.transform.SetParent(root.transform, false);
            body.transform.localScale = new Vector3(0.35f, 0.9f, 0.35f);
            Collider bodyCol = body.GetComponent<Collider>();
            if (bodyCol != null) UnityEngine.Object.Destroy(bodyCol);
            Renderer bodyRend = body.GetComponent<Renderer>();
            if (bodyRend != null) bodyRend.material = espMaterial;

            GameObject head = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            head.name = "QuestMods_ESPHead";
            head.transform.SetParent(root.transform, false);
            head.transform.localPosition = new Vector3(0f, 0.85f, 0f);
            head.transform.localScale = new Vector3(0.28f, 0.28f, 0.28f);
            Collider headCol = head.GetComponent<Collider>();
            if (headCol != null) UnityEngine.Object.Destroy(headCol);
            Renderer headRend = head.GetComponent<Renderer>();
            if (headRend != null) headRend.material = espMaterial;

            object typed;
            Component text = CreateWristText(root.transform, new Vector3(0f, 1.05f, 0f), 0.035f, 48, out typed);
            if (text != null)
            {
                Renderer tr = text.GetComponent<Renderer>();
                if (tr != null && tr.material != null && tr.material.HasProperty("_ZTest"))
                    tr.material.SetInt("_ZTest", 8);
            }
            m = new EspMarker { Root = root, Text = text, Typed = typed, Cached = null };
            espMarkers[ptr] = m;
            return m;
        }

        // --- Graphics Overhaul: no fog, no blue film, clean long-view image ----
        // Client-side visuals only. Every value changed here is snapshotted and
        // restored when the toggle is turned off or Escape turns all mods off.
        private static readonly int[] graphicsRanges = { 4000, 6000, 2500, 1500 };
        private static readonly float[] graphicsSharpness = { 1f, 1.15f, 1.3f, 1.5f };
        private static readonly string[] graphicsQualityKeys =
        {
            "masterTextureLimit", "maximumLODLevel", "lodBias", "anisotropicFiltering",
            "pixelLightCount", "antiAliasing", "shadowDistance", "shadowCascades",
            "shadowResolution", "shadowProjection", "softParticles", "softVegetation",
            "realtimeReflectionProbes", "billboardsFaceCameraPosition",
            "particleRaycastBudget", "skinWeights", "streamingMipmapsActive"
        };
        private static int graphicsRangeIndex;      // 0 = 4000m default
        private static int graphicsSharpIndex = 1;  // 1 = 1.15x default
        private static bool graphicsApplied, graphicsAppliedPref, graphicsWarned;
        private static float graphicsNextReapply;

        private static bool savedFog;
        private static float savedFogDensity, savedFogStart, savedFogEnd;
        private static Color savedFogColor;
        private static FogMode savedFogMode;
        private static UnityEngine.Rendering.AmbientMode savedAmbientMode;
        private static Color savedAmbientLight;
        private static float savedAmbientIntensity, savedReflectionIntensity;
        private static object savedXrResolutionScale, savedXrViewportScale;
        private static Material savedSkybox;
        private static float savedSkyboxExposure;
        private static bool savedSkyboxExposureOk;

        private class CamSnap
        {
            public Camera Cam;
            public float Far, Near;
            public bool Hdr, Msaa, Occlusion;
            public CameraClearFlags Flags;
        }
        private static readonly List<CamSnap> camSnaps = new List<CamSnap>();
        private static readonly List<Behaviour> graphicsDisabledBehaviours = new List<Behaviour>();
        private static readonly List<Renderer> graphicsDisabledRenderers = new List<Renderer>();
        private static readonly Dictionary<string, object> savedQuality = new Dictionary<string, object>();

        private static string GraphicsSharpLabel()
        {
            return graphicsSharpness[graphicsSharpIndex].ToString("0.00") + "x";
        }

        private static void CycleGraphicsSharp()
        {
            graphicsSharpIndex = (graphicsSharpIndex + 1) % graphicsSharpness.Length;
            if (graphicsApplied) SetXrValue("eyeTextureResolutionScale", graphicsSharpness[graphicsSharpIndex]);
            MelonLogger.Msg("[Graphics] Render scale: " + GraphicsSharpLabel());
        }

        private static void CycleGraphicsRange()
        {
            graphicsRangeIndex = (graphicsRangeIndex + 1) % graphicsRanges.Length;
            if (graphicsApplied) ApplyGraphicsCore();
            MelonLogger.Msg("[Graphics] Draw distance: " + graphicsRanges[graphicsRangeIndex] + "m");
        }

        private static void ApplyGraphicsOverhaul(bool on)
        {
            try
            {
                if (on)
                {
                    if (!graphicsApplied)
                    {
                        CaptureGraphicsSnapshot();
                        graphicsApplied = true;
                    }
                    ApplyGraphicsCore();
                    ScanAndDisableGraphicsExtras();
                    MelonLogger.Msg("[Graphics] Overhaul ON: fog off, blue film off, full textures, " +
                        graphicsRanges[graphicsRangeIndex] + "m view, " + GraphicsSharpLabel() + " render scale.");
                }
                else if (graphicsApplied)
                {
                    RestoreGraphics();
                    graphicsApplied = false;
                    MelonLogger.Msg("[Graphics] Overhaul OFF: original visuals restored.");
                }
            }
            catch (Exception ex)
            {
                if (!graphicsWarned) { graphicsWarned = true; MelonLogger.Warning("[Graphics] apply failed: " + ex.Message); }
            }
        }

        // Re-applied a few times a second so the game's own scene scripts cannot
        // quietly switch fog, texture limits or the far plane back on.
        private static void TickGraphicsOverhaul()
        {
            if (prefGraphics == null || !prefGraphics.Value) return;
            if (!graphicsApplied) { ApplyGraphicsOverhaul(true); return; }
            if (Time.time < graphicsNextReapply) return;
            graphicsNextReapply = Time.time + 2f;
            ApplyGraphicsCore();
        }

        public static void GraphicsSceneLoaded()
        {
            // Old scene objects are gone; forget them but keep the saved values.
            graphicsDisabledBehaviours.Clear();
            graphicsDisabledRenderers.Clear();
            camSnaps.Clear();
            terrainSnaps.Clear();
            terrainLogged = false;
            maxDetailLogged = false;
            maxDetailNext = 0f;
            graphicsNextReapply = 0f;
            if (prefGraphics != null && prefGraphics.Value)
            {
                CaptureCameraSnapshots();
                ApplyGraphicsCore();
                ScanAndDisableGraphicsExtras();
            }
        }

        private static PropertyInfo QualityProp(string name)
        {
            try { return typeof(QualitySettings).GetProperty(name, BindingFlags.Public | BindingFlags.Static); }
            catch { return null; }
        }

        private static bool TryReadQuality(string name, out object value)
        {
            value = null;
            PropertyInfo pi = QualityProp(name);
            if (pi == null || !pi.CanRead) return false;
            try { value = pi.GetValue(null); return value != null; }
            catch { return false; }
        }

        private static void SetQualityValue(string name, object value)
        {
            PropertyInfo pi = QualityProp(name);
            if (pi == null || !pi.CanWrite) return;
            try
            {
                Type pt = pi.PropertyType;
                object v = value;
                if (pt.IsEnum)
                {
                    if (value is string sname) v = Enum.Parse(pt, sname, true);
                    else if (value is Enum) v = value;
                    else v = Enum.ToObject(pt, Convert.ToInt32(value));
                }
                else if (v != null && v.GetType() != pt) v = Convert.ChangeType(v, pt);
                pi.SetValue(null, v);
            }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] " + name + ": " + ex.Message); }
        }

        private static object GetXrValue(string name)
        {
            Type t = FindLoadedType("XRSettings");
            if (t == null) return null;
            PropertyInfo pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
            if (pi == null || !pi.CanRead) return null;
            try { return pi.GetValue(null); } catch { return null; }
        }

        private static void SetXrValue(string name, object value)
        {
            try
            {
                Type t = FindLoadedType("XRSettings");
                if (t == null) return;
                PropertyInfo pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                if (pi == null || !pi.CanWrite) return;
                object v = value;
                if (v != null && v.GetType() != pi.PropertyType) v = Convert.ChangeType(v, pi.PropertyType);
                pi.SetValue(null, v);
            }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] XR " + name + ": " + ex.Message); }
        }

        private static void CaptureCameraSnapshots()
        {
            foreach (Camera cam in Camera.allCameras)
            {
                if (cam == null) continue;
                bool known = false;
                foreach (CamSnap existing in camSnaps) if (existing.Cam == cam) { known = true; break; }
                if (known) continue;
                camSnaps.Add(new CamSnap
                {
                    Cam = cam,
                    Far = cam.farClipPlane,
                    Near = cam.nearClipPlane,
                    Hdr = cam.allowHDR,
                    Msaa = cam.allowMSAA,
                    Occlusion = cam.useOcclusionCulling,
                    Flags = cam.clearFlags
                });
            }
        }

        private static void CaptureGraphicsSnapshot()
        {
            savedFog = RenderSettings.fog;
            savedFogDensity = RenderSettings.fogDensity;
            savedFogStart = RenderSettings.fogStartDistance;
            savedFogEnd = RenderSettings.fogEndDistance;
            savedFogColor = RenderSettings.fogColor;
            savedFogMode = RenderSettings.fogMode;
            savedAmbientMode = RenderSettings.ambientMode;
            savedAmbientLight = RenderSettings.ambientLight;
            savedAmbientIntensity = RenderSettings.ambientIntensity;
            savedReflectionIntensity = RenderSettings.reflectionIntensity;
            savedXrResolutionScale = GetXrValue("eyeTextureResolutionScale");
            savedXrViewportScale = GetXrValue("renderViewportScale");
            savedSkybox = RenderSettings.skybox;
            savedSkyboxExposureOk = false;
            if (savedSkybox != null && savedSkybox.HasProperty("_Exposure"))
            {
                try { savedSkyboxExposure = savedSkybox.GetFloat("_Exposure"); savedSkyboxExposureOk = true; }
                catch { savedSkyboxExposureOk = false; }
            }

            savedQuality.Clear();
            foreach (string key in graphicsQualityKeys)
            {
                object v;
                if (TryReadQuality(key, out v)) savedQuality[key] = v;
            }
            CaptureCameraSnapshots();
        }

        private static void ApplyGraphicsCore()
        {
            // 1. No fog: this alone removes the milky blue haze in the distance.
            RenderSettings.fog = false;
            RenderSettings.fogMode = FogMode.Linear;
            RenderSettings.fogDensity = 0f;
            RenderSettings.fogStartDistance = 100000f;
            RenderSettings.fogEndDistance = 100000f;

            // 2. Brighter, neutral ambient so the world is not tinted cold blue.
            RenderSettings.ambientIntensity = 1.35f;
            RenderSettings.reflectionIntensity = 1.15f;
            if (RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Flat)
                RenderSettings.ambientLight = new Color(0.62f, 0.64f, 0.68f);
            Material sky = RenderSettings.skybox;
            if (sky != null && sky.HasProperty("_Exposure")) sky.SetFloat("_Exposure", 1.25f);

            // 3. Full texture resolution, longer LODs, better shadows and filtering.
            SetQualityValue("masterTextureLimit", 0);
            SetQualityValue("maximumLODLevel", 0);
            SetQualityValue("lodBias", 4f);
            SetQualityValue("anisotropicFiltering", "ForceEnable");
            SetQualityValue("pixelLightCount", 8);
            SetQualityValue("antiAliasing", 4);
            SetQualityValue("shadowDistance", 200f);
            SetQualityValue("shadowCascades", 4);
            SetQualityValue("shadowResolution", "VeryHigh");
            SetQualityValue("shadowProjection", "CloseFit");
            SetQualityValue("softParticles", 1);
            SetQualityValue("softVegetation", 1);
            SetQualityValue("realtimeReflectionProbes", 1);
            SetQualityValue("billboardsFaceCameraPosition", 1);
            SetQualityValue("particleRaycastBudget", 4096);
            SetQualityValue("skinWeights", "FourBones");
            SetQualityValue("streamingMipmapsActive", 0);
            try { Texture.SetGlobalAnisotropicFilteringLimits(9, 9); }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] anisotropy: " + ex.Message); }

            // 4. Camera: HDR + MSAA where available and a far plane past the fog.
            int far = graphicsRanges[graphicsRangeIndex];
            foreach (Camera cam in Camera.allCameras)
            {
                if (cam == null || cam.cameraType != CameraType.Game) continue;
                try
                {
                    cam.allowHDR = true;
                    cam.allowMSAA = true;
                    if (cam.farClipPlane < far) cam.farClipPlane = far;
                    // Render more of the world: occlusion culling is what makes
                    // distant geometry and whole chunks vanish before you reach them.
                    cam.useOcclusionCulling = false;
                    if (cam.clearFlags == CameraClearFlags.SolidColor) cam.clearFlags = CameraClearFlags.Skybox;
                }
                catch (Exception ex) { MelonLogger.Warning("[Graphics] camera: " + ex.Message); }
            }

            // 5. Headset render scale (supersampling) through XR, by reflection.
            SetXrValue("eyeTextureResolutionScale", graphicsSharpness[graphicsSharpIndex]);
            SetXrValue("renderViewportScale", 1f);

            // 6. Far ground and foliage: the real reason distant hills go flat.
            ApplyTerrainBoost();
        }

        // Post-processing / colour grading / film overlays and distance fog
        // geometry. Matched by component type name so nothing game-logic related
        // is touched; everything disabled here is re-enabled on toggle off.
        private static readonly string[] graphicsKillTypes =
        {
            "postprocesslayer", "postprocessvolume", "postprocessprofile",
            "postprocesslayerphysicalcamera", "volume",
            "colorgrading", "colorgradingcurve", "colorlookup", "coloradjustments",
            "colorcorrectionlookup", "colorcurves", "tonemapping", "vignette",
            "filmgrain", "grain", "bloom", "chromaticaberration", "whitebalance",
            "liftgammagain", "shadowsmidtoneshighlights", "channelmixer",
            "screenspaceambientocclusion", "ambientocclusion", "depthoffield",
            "motionblur", "paniniprojection", "lensdistortion", "screeneffect",
            "screenfilter", "screenoverlay", "screenfade", "bluefilm", "bluetint",
            "underwater", "fogvolume", "volumetricfog", "distancefog", "heightfog",
            "weatherfog", "blurscreen"
        };

        // Distinctive fragments that already identify a visual effect; kept short
        // on purpose so gameplay systems (item tinting, etc.) are never matched.
        private static readonly string[] graphicsKillFragments =
        {
            "fog", "postprocess", "colorgrading", "colorcorrection", "colorlookup",
            "tonemap", "vignette", "bloom", "chromatic", "ambientocclusion",
            "ssao", "underwater", "bluefilm"
        };

        private static void ScanAndDisableGraphicsExtras()
        {
            try
            {
                MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
                foreach (MonoBehaviour mb in behaviours)
                {
                    if (mb == null || !mb.enabled) continue;
                    string typeName = mb.GetType().Name;
                    if (string.IsNullOrEmpty(typeName)) continue;
                    string lower = typeName.ToLowerInvariant();
                    bool match = false;
                    foreach (string key in graphicsKillTypes) if (lower == key) { match = true; break; }
                    if (!match)
                        foreach (string key in graphicsKillFragments) if (lower.Contains(key)) { match = true; break; }
                    if (match && graphicsDisabledBehaviours.Contains(mb)) match = false;
                    if (!match) continue;
                    try
                    {
                        mb.enabled = false;
                        graphicsDisabledBehaviours.Add(mb);
                        MelonLogger.Msg("[Graphics] Disabled " + typeName + " on '" + mb.gameObject.name + "'.");
                    }
                    catch { }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] volume scan: " + ex.Message); }

            // Blue film / tint / fade quads live in the camera hierarchy.
            try
            {
                foreach (Camera cam in Camera.allCameras)
                {
                    if (cam == null) continue;
                    foreach (Renderer r in cam.GetComponentsInChildren<Renderer>(true))
                        DisableGraphicsRenderer(r);
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] camera overlays: " + ex.Message); }

            // Distance-fog / mist particles that are geometry, not scripts.
            try
            {
                foreach (Renderer r in UnityEngine.Object.FindObjectsOfType<Renderer>())
                    DisableGraphicsRenderer(r);
            }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] fog particles: " + ex.Message); }
        }

        private static readonly string[] graphicsKillRendererNames = { "fog", "mist", "haze", "bluefilm", "bluetint", "tint", "vignette", "screenfade", "underwater", "overlay" };

        private static void DisableGraphicsRenderer(Renderer r)
        {
            if (r == null || !r.enabled) return;
            if (graphicsDisabledRenderers.Contains(r)) return;
            string label = r.gameObject.name;
            if (string.IsNullOrEmpty(label)) return;
            string lower = label.ToLowerInvariant();
            bool match = false;
            foreach (string key in graphicsKillRendererNames) if (lower.Contains(key)) { match = true; break; }
            if (!match) return;
            // Never touch our own visuals or a real player's body.
            if (lower.StartsWith("questmods_")) return;
            try
            {
                r.enabled = false;
                graphicsDisabledRenderers.Add(r);
                MelonLogger.Msg("[Graphics] Hid overlay renderer '" + label + "'.");
            }
            catch { }
        }

        private static void RestoreGraphics()
        {
            try
            {
                RenderSettings.fog = savedFog;
                RenderSettings.fogDensity = savedFogDensity;
                RenderSettings.fogStartDistance = savedFogStart;
                RenderSettings.fogEndDistance = savedFogEnd;
                RenderSettings.fogColor = savedFogColor;
                RenderSettings.fogMode = savedFogMode;
                RenderSettings.ambientMode = savedAmbientMode;
                RenderSettings.ambientLight = savedAmbientLight;
                RenderSettings.ambientIntensity = savedAmbientIntensity;
                RenderSettings.reflectionIntensity = savedReflectionIntensity;
            }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] restore world: " + ex.Message); }

            foreach (CamSnap snap in camSnaps)
            {
                if (snap == null || snap.Cam == null) continue;
                try
                {
                    snap.Cam.farClipPlane = snap.Far;
                    snap.Cam.nearClipPlane = snap.Near;
                    snap.Cam.allowHDR = snap.Hdr;
                    snap.Cam.allowMSAA = snap.Msaa;
                    snap.Cam.useOcclusionCulling = snap.Occlusion;
                    snap.Cam.clearFlags = snap.Flags;
                }
                catch { }
            }
            if (savedSkyboxExposureOk && savedSkybox != null)
            {
                try { savedSkybox.SetFloat("_Exposure", savedSkyboxExposure); } catch { }
            }
            RestoreTerrain();
            foreach (var kv in savedQuality) SetQualityValue(kv.Key, kv.Value);
            if (savedXrResolutionScale != null) SetXrValue("eyeTextureResolutionScale", savedXrResolutionScale);
            if (savedXrViewportScale != null) SetXrValue("renderViewportScale", savedXrViewportScale);

            foreach (Behaviour b in graphicsDisabledBehaviours) { if (b != null) { try { b.enabled = true; } catch { } } }
            foreach (Renderer r in graphicsDisabledRenderers) { if (r != null) { try { r.enabled = true; } catch { } } }
            graphicsDisabledBehaviours.Clear();
            graphicsDisabledRenderers.Clear();
            camSnaps.Clear();
        }

        // --- Far-detail work: terrain, foliage and LOD -------------------------
        // Terrain and LODGroup are reached by reflection: neither type is
        // guaranteed to be in this project's reference set.
        private class TerrainSnap
        {
            public UnityEngine.Object T;
            public object Base, MaxLod, Detail, Tree, Billboard, FullLod, Draw;
        }
        private static readonly List<TerrainSnap> terrainSnaps = new List<TerrainSnap>();
        private static bool terrainLogged, maxDetailLogged;
        private static float maxDetailNext;

        private static object ReadMember(object obj, string name)
        {
            if (obj == null) return null;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                Type t = obj.GetType();
                PropertyInfo p = t.GetProperty(name, f);
                if (p != null && p.CanRead) return p.GetValue(obj);
                FieldInfo fi = t.GetField(name, f);
                if (fi != null) return fi.GetValue(obj);
            }
            catch { }
            return null;
        }

        private static void WriteMember(object obj, string name, object value)
        {
            if (obj == null) return;
            const BindingFlags f = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                Type t = obj.GetType();
                PropertyInfo p = t.GetProperty(name, f);
                if (p != null && p.CanWrite)
                {
                    object v = value;
                    if (v != null && v.GetType() != p.PropertyType) v = Convert.ChangeType(v, p.PropertyType);
                    p.SetValue(obj, v);
                    return;
                }
                FieldInfo fi = t.GetField(name, f);
                if (fi != null)
                {
                    object v = value;
                    if (v != null && v.GetType() != fi.FieldType) v = Convert.ChangeType(v, fi.FieldType);
                    fi.SetValue(obj, v);
                }
            }
            catch { }
        }

        // Distant ground is drawn with a low-res basemap and a cut-down heightmap
        // LOD, which is why far hills look flat and blurry. Raising both keeps the
        // real terrain shader and full geometry far out, and the foliage values
        // keep grass and trees instead of bare ground.
        private static void ApplyTerrainBoost()
        {
            Type terrainType = FindLoadedType("Terrain");
            if (terrainType == null) return;
            int changed = 0;
            try
            {
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(terrainType)))
                {
                    if (o == null) continue;
                    bool known = false;
                    foreach (TerrainSnap s in terrainSnaps) if (s.T == o) { known = true; break; }
                    if (!known)
                    {
                        terrainSnaps.Add(new TerrainSnap
                        {
                            T = o,
                            Base = ReadMember(o, "basemapDistance"),
                            MaxLod = ReadMember(o, "heightmapMaximumLOD"),
                            Detail = ReadMember(o, "detailObjectDistance"),
                            Tree = ReadMember(o, "treeDistance"),
                            Billboard = ReadMember(o, "treeBillboardDistance"),
                            FullLod = ReadMember(o, "treeMaximumFullLODCount"),
                            Draw = ReadMember(o, "drawTreesAndFoliage")
                        });
                    }
                    WriteMember(o, "basemapDistance", 100000f);
                    WriteMember(o, "heightmapMaximumLOD", 0);
                    WriteMember(o, "detailObjectDistance", 450f);
                    WriteMember(o, "treeDistance", 4000f);
                    WriteMember(o, "treeBillboardDistance", 2000f);
                    WriteMember(o, "treeMaximumFullLODCount", 1200);
                    WriteMember(o, "drawTreesAndFoliage", true);
                    changed++;
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] terrain: " + ex.Message); }
            if (changed > 0 && !terrainLogged)
            {
                terrainLogged = true;
                MelonLogger.Msg("[Graphics] Terrain boosted on " + changed + " terrain(s): full-res far ground, more grass and trees.");
            }
        }

        private static void RestoreTerrain()
        {
            foreach (TerrainSnap s in terrainSnaps)
            {
                if (s == null || s.T == null) continue;
                if (s.Base != null) WriteMember(s.T, "basemapDistance", s.Base);
                if (s.MaxLod != null) WriteMember(s.T, "heightmapMaximumLOD", s.MaxLod);
                if (s.Detail != null) WriteMember(s.T, "detailObjectDistance", s.Detail);
                if (s.Tree != null) WriteMember(s.T, "treeDistance", s.Tree);
                if (s.Billboard != null) WriteMember(s.T, "treeBillboardDistance", s.Billboard);
                if (s.FullLod != null) WriteMember(s.T, "treeMaximumFullLODCount", s.FullLod);
                if (s.Draw != null) WriteMember(s.T, "drawTreesAndFoliage", s.Draw);
            }
            terrainSnaps.Clear();
            terrainLogged = false;
        }

        // Max Detail: force every LODGroup to its highest-detail mesh, so nothing
        // drops to a low-poly version in the distance. Heavy, so it is its own
        // toggle and is not needed for the normal overhaul.
        private static void TickMaxDetail()
        {
            if (prefMaxDetail == null || !prefMaxDetail.Value) return;
            if (Time.time < maxDetailNext) return;
            maxDetailNext = Time.time + 2f;
            Type lodType = FindLoadedType("LODGroup");
            if (lodType == null) return;
            MethodInfo force = lodType.GetMethod("ForceLOD", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (force == null) return;
            int shown = 0;
            try
            {
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(lodType)))
                {
                    if (o == null) continue;
                    try { force.Invoke(o, new object[] { 0 }); shown++; } catch { }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] LOD scan: " + ex.Message); }
            if (shown > 0 && !maxDetailLogged)
            {
                maxDetailLogged = true;
                MelonLogger.Msg("[Graphics] Max Detail: highest LOD forced on " + shown + " groups.");
            }
        }

        private static void RestoreMaxDetail()
        {
            Type lodType = FindLoadedType("LODGroup");
            MethodInfo force = lodType?.GetMethod("ForceLOD", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (force == null) return;
            try
            {
                foreach (UnityEngine.Object o in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.From(lodType)))
                {
                    if (o == null) continue;
                    try { force.Invoke(o, new object[] { -1 }); } catch { }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[Graphics] LOD restore: " + ex.Message); }
            maxDetailLogged = false;
            MelonLogger.Msg("[Graphics] Max Detail off; normal LOD restored.");
        }

        private static void OpenPlayerTPs()
        {
            try
            {
                MethodInfo openPage = typeof(TabletUI).GetMethod("OpenPage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? typeof(TabletUI).GetMethod("Navigate", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? typeof(TabletUI).GetMethod("ShowPage", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                Page page = BuildPlayerTPsPage();
                if (openPage != null) openPage.Invoke(null, new object[] { page });
                else MelonLogger.Warning("[QuestMods] TabletUI navigation method not found.");
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] OpenPlayerTPs failed: " + ex.Message); }
        }

        private static string WaypointsFilePath => Path.Combine(MelonEnvironment.UserDataDirectory, "questmods_waypoints.txt");

        private static void ApplyDevLight(bool enable)
        {
            try
            {
                var pc = PlayerController.Current;
                if (pc == null) { MelonLogger.Warning("[QuestMods] Dev Light: local player not ready."); return; }
                foreach (var ctrl in new[] { pc.LeftController, pc.RightController })
                {
                    if (ctrl == null) continue;
                    var lh = ctrl.GetControllerExtension<LightHands>();
                    if (lh != null && lh.enabled != enable)
                        lh.Toggle();
                }
                MelonLogger.Msg("[QuestMods] Dev Light " + (enable ? "enabled" : "disabled") + ".");
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] ApplyDevLight failed: " + ex.Message); }
        }

        private static int pendingDeleteIndex = -1;

        private static List<(string Name, Vector3 Pos)> LoadWaypoints()
        {
            var list = new List<(string Name, Vector3 Pos)>();
            try
            {
                if (!File.Exists(WaypointsFilePath)) return list;
                int autoIdx = 1;
                foreach (string line in File.ReadAllLines(WaypointsFilePath))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    string trimmed = line.Trim();
                    string name = "TP " + autoIdx;
                    string coordsPart = trimmed;
                    int sep = trimmed.IndexOf('|');
                    if (sep >= 0)
                    {
                        name = trimmed.Substring(0, sep).Trim();
                        if (string.IsNullOrEmpty(name)) name = "TP " + autoIdx;
                        coordsPart = trimmed.Substring(sep + 1).Trim();
                    }
                    string[] parts = coordsPart.Split(',');
                    if (parts.Length == 3 &&
                        float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float y) &&
                        float.TryParse(parts[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float z))
                    {
                        list.Add((name, new Vector3(x, y, z)));
                        autoIdx++;
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] LoadWaypoints failed: " + ex.Message); }
            return list;
        }

        private static void SaveWaypointWithPos(string name, Vector3 pos)
        {
            try
            {
                string cleanName = string.IsNullOrWhiteSpace(name) ? ("TP " + (LoadWaypoints().Count + 1)) : name.Replace("|", "").Trim();
                string line = string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}|{1:F3},{2:F3},{3:F3}", cleanName, pos.x, pos.y, pos.z);
                File.AppendAllText(WaypointsFilePath, line + Environment.NewLine);
                MelonLogger.Msg("[QuestMods] Saved waypoint: " + line);
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] SaveWaypointWithPos failed: " + ex.Message); }
        }

        private static void DeleteWaypoint(int index)
        {
            try
            {
                if (pendingDeleteIndex != index)
                {
                    pendingDeleteIndex = index;
                    MelonLogger.Msg("[QuestMods] Are you sure? Click Del again to confirm deleting waypoint #" + (index + 1));
                    return;
                }
                pendingDeleteIndex = -1;
                var list = LoadWaypoints();
                if (index < 0 || index >= list.Count) return;
                list.RemoveAt(index);
                var lines = new List<string>(list.Count);
                foreach (var wp in list)
                    lines.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0}|{1:F3},{2:F3},{3:F3}", wp.Name, wp.Pos.x, wp.Pos.y, wp.Pos.z));
                File.WriteAllLines(WaypointsFilePath, lines);
                MelonLogger.Msg("[QuestMods] Deleted waypoint #" + (index + 1));
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] DeleteWaypoint failed: " + ex.Message); }
        }

        private static GameObject kbRoot;
        private static LineRenderer kbRay;
        private static Component kbPreviewText;
        private static string kbTypedName = "";
        private static Vector3 kbPendingPos = Vector3.zero;
        private static readonly List<(GameObject Obj, string Key)> kbKeys = new List<(GameObject, string)>();

        private static void OpenWaypointKeyboard()
        {
            try
            {
                CloseWaypointKeyboard();
                var pc = PlayerController.Current;
                if (pc == null) { MelonLogger.Warning("[QuestMods] Save Pos: local player not ready."); return; }
                kbPendingPos = pc.PlayerFeetPosition;
                kbTypedName = "";

                Transform cam = Camera.main != null ? Camera.main.transform : pc.transform;
                Vector3 headPos = (Camera.main != null && Camera.main.transform.position.y > pc.PlayerFeetPosition.y + 0.3f) ? Camera.main.transform.position : (pc.PlayerFeetPosition + Vector3.up * 1.35f);
                Vector3 fwd = cam.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
                fwd.Normalize();

                kbRoot = new GameObject("QuestMods_VRKeyboard");
                kbRoot.transform.position = headPos + fwd * 0.85f;
                kbRoot.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);

                GameObject prevObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                prevObj.transform.SetParent(kbRoot.transform, false);
                prevObj.transform.localPosition = new Vector3(0f, 0.28f, 0f);
                prevObj.transform.localScale = new Vector3(0.85f, 0.09f, 0.02f);
                var prevRend = prevObj.GetComponent<Renderer>();
                if (prevRend != null) prevRend.material.color = new Color(0.12f, 0.12f, 0.12f, 1f);
                kbPreviewText = CreateKeyboardLabel(prevObj.transform, "Name: _", 0.03f);

                string[] rows = new[]
                {
                    "1 2 3 4 5 6 7 8 9 0",
                    "Q W E R T Y U I O P",
                    "A S D F G H J K L",
                    "Z X C V B N M",
                    "SPACE BACK SAVE CANCEL"
                };

                for (int r = 0; r < rows.Length; r++)
                {
                    string[] tokens = rows[r].Split(' ');
                    float spacing = (r == rows.Length - 1) ? 0.21f : 0.085f;
                    float startX = -((tokens.Length - 1) * spacing) * 0.5f;
                    float y = 0.16f - r * 0.095f;
                    for (int c = 0; c < tokens.Length; c++)
                    {
                        string key = tokens[c];
                        float w = (r == rows.Length - 1) ? 0.19f : 0.075f;
                        GameObject keyObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        keyObj.transform.SetParent(kbRoot.transform, false);
                        keyObj.transform.localPosition = new Vector3(startX + c * spacing, y, 0f);
                        keyObj.transform.localScale = new Vector3(w, 0.075f, 0.02f);
                        var rend = keyObj.GetComponent<Renderer>();
                        if (rend != null)
                        {
                            if (key == "SAVE") rend.material.color = new Color(0.15f, 0.65f, 0.2f, 1f);
                            else if (key == "CANCEL") rend.material.color = new Color(0.75f, 0.15f, 0.15f, 1f);
                            else rend.material.color = new Color(0.25f, 0.25f, 0.28f, 1f);
                        }
                        CreateKeyboardLabel(keyObj.transform, key, 0.025f);
                        kbKeys.Add((keyObj, key));
                    }
                }

                GameObject rayObj = new GameObject("QuestMods_KBRay");
                rayObj.transform.SetParent(kbRoot.transform, false);
                kbRay = rayObj.AddComponent<LineRenderer>();
                kbRay.positionCount = 2;
                kbRay.startWidth = 0.005f;
                kbRay.endWidth = 0.002f;
                kbRay.material = new Material(Shader.Find("Sprites/Default"));
                kbRay.startColor = Color.red;
                kbRay.endColor = Color.yellow;
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] OpenWaypointKeyboard failed: " + ex.Message); }
        }

        private static Component CreateKeyboardLabel(Transform parent, string text, float charSize)
        {
            EnsureTextMeshReflection();
            if (textMeshType == null) return null;
            GameObject lbl = new GameObject("Label");
            lbl.transform.SetParent(parent, false);
            lbl.transform.localPosition = new Vector3(0f, 0f, -0.7f);
            lbl.transform.localScale = new Vector3(1f / parent.localScale.x, 1f / parent.localScale.y, 1f);
            Component tm = lbl.AddComponent(Il2CppInterop.Runtime.Il2CppType.From(textMeshType));
            object typed = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)?.MakeGenericMethod(textMeshType).Invoke(tm, null);
            if (typed != null)
            {
                tmTextProp?.SetValue(typed, text);
                tmCharSizeProp?.SetValue(typed, charSize * 1.6f);
                tmColorProp?.SetValue(typed, Color.white);
                tmAnchorProp?.SetValue(typed, 4);
                tmAlignProp?.SetValue(typed, 1);
            }
            return tm;
        }

        private static bool lastMakePearlPref;
        private static Pickup pearlPickup;
        private static bool pearlWasHeld;
        private static float pearlReleaseTime;

        private static void TickMakePearl()
        {
            if (prefMakePearl == null) return;
            if (prefMakePearl.Value != lastMakePearlPref)
            {
                lastMakePearlPref = prefMakePearl.Value;
                pearlWasHeld = false;
                if (lastMakePearlPref)
                {
                    var pc = PlayerController.Current;
                    var held = pc?.RightController?.Interactor?.InteractingWith ?? pc?.LeftController?.Interactor?.InteractingWith;
                    pearlPickup = held != null ? held.TryCast<Pickup>() : null;
                    if (pearlPickup != null)
                        MelonLogger.Msg("[QuestMods] Make Pearl: tracking '" + pearlPickup.name + "'.");
                    else
                        MelonLogger.Warning("[QuestMods] Make Pearl: hold an item when toggling ON!");
                }
                else
                {
                    pearlPickup = null;
                }
            }

            if (!lastMakePearlPref || pearlPickup == null) return;
            try
            {
                var pc = PlayerController.Current;
                if (pc == null) return;
                bool isHeld =
                    (pc.RightController?.Interactor?.IsInteracting == true && pc.RightController.Interactor.InteractingWith?.Pointer == pearlPickup.Pointer) ||
                    (pc.LeftController?.Interactor?.IsInteracting == true && pc.LeftController.Interactor.InteractingWith?.Pointer == pearlPickup.Pointer);

                if (isHeld)
                {
                    pearlWasHeld = true;
                    pearlReleaseTime = Time.time;
                }
                else if (pearlWasHeld && Time.time - pearlReleaseTime > 0.25f)
                {
                    Rigidbody rb = pearlPickup.GetComponent<Rigidbody>();
                    if (rb == null || rb.velocity.sqrMagnitude < 0.08f || Time.time - pearlReleaseTime > 4.0f)
                    {
                        pearlWasHeld = false;
                        TeleportLocalPlayer(pearlPickup.transform.position + Vector3.up * 0.2f, "Ender Pearl");
                    }
                }
            }
            catch { }
        }

        private static void CloseWaypointKeyboard()
        {
            kbKeys.Clear();
            kbRay = null;
            kbPreviewText = null;
            if (kbRoot != null)
            {
                UnityEngine.Object.Destroy(kbRoot);
                kbRoot = null;
            }
        }

        private static void UpdateWaypointKeyboard()
        {
            if (kbRoot == null) return;
            try
            {
                var pc = PlayerController.Current;
                var hand = pc?.RightController?.transform;
                if (hand == null) return;

                Vector3 origin = hand.position;
                Vector3 dir = hand.forward;
                Vector3 end = origin + dir * 2.5f;

                if (kbRay != null)
                {
                    kbRay.SetPosition(0, origin);
                    kbRay.SetPosition(1, end);
                }

                if (Physics.Raycast(origin, dir, out RaycastHit hit, 3.0f))
                {
                    if (kbRay != null) kbRay.SetPosition(1, hit.point);
                    bool pressed = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch);
                    if (pressed && hit.collider != null)
                    {
                        GameObject hitObj = hit.collider.gameObject;
                        foreach (var k in kbKeys)
                        {
                            if (k.Obj != null && k.Obj.Pointer == hitObj.Pointer)
                            {
                                if (k.Key == "TP_CAVE")
                                {
                                    if (int.TryParse(kbTypedName.Trim(), out int caveLayer))
                                        TeleportToCaveLayer(caveLayer);
                                    CloseWaypointKeyboard();
                                    return;
                                }
                                if (k.Key == "SAVE")
                                {
                                    SaveWaypointWithPos(kbTypedName, kbPendingPos);
                                    CloseWaypointKeyboard();
                                    return;
                                }
                                if (k.Key == "CANCEL")
                                {
                                    CloseWaypointKeyboard();
                                    return;
                                }
                                if (k.Key == "BACK")
                                {
                                    if (kbTypedName.Length > 0) kbTypedName = kbTypedName.Substring(0, kbTypedName.Length - 1);
                                }
                                else if (k.Key == "SPACE")
                                {
                                    kbTypedName += " ";
                                }
                                else
                                {
                                    kbTypedName += k.Key;
                                }
                                if (kbPreviewText != null && textMeshType != null)
                                {
                                    object typed = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)?.MakeGenericMethod(textMeshType).Invoke(kbPreviewText, null);
                                    if (typed != null) tmTextProp?.SetValue(typed, "Name: " + kbTypedName + "_");
                                }
                                break;
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static Page BuildCustomTPsPage()
        {
            Page page = new Page("SAVED TPS", "Custom Waypoints");
            try
            {
                page.Button("Save Pos", () => OpenWaypointKeyboard());
                page.Button("TP 0,0,0", () => TeleportLocalPlayer(Vector3.zero, "0,0,0"));

                var waypoints = LoadWaypoints();
                for (int i = 0; i < waypoints.Count; i++)
                {
                    int wpIndex = i;
                    var wp = waypoints[i];
                    page.Button("TP: " + wp.Name, () => TeleportLocalPlayer(wp.Pos, wp.Name));
                    page.Button(pendingDeleteIndex == wpIndex ? "Are you sure? (" + wp.Name + ")" : "Del: " + wp.Name, () => DeleteWaypoint(wpIndex));
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] BuildCustomTPsPage failed: " + ex.Message); }
            return page;
        }

        private static void OpenCaveNumPad()
        {
            try
            {
                CloseWaypointKeyboard();
                var pc = PlayerController.Current;
                if (pc == null) { MelonLogger.Warning("[QuestMods] Cave NumPad: local player not ready."); return; }
                kbTypedName = "";

                Transform cam = Camera.main != null ? Camera.main.transform : pc.transform;
                Vector3 headPos = (Camera.main != null && Camera.main.transform.position.y > pc.PlayerFeetPosition.y + 0.3f) ? Camera.main.transform.position : (pc.PlayerFeetPosition + Vector3.up * 1.35f);
                Vector3 fwd = cam.forward; fwd.y = 0f;
                if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
                fwd.Normalize();

                kbRoot = new GameObject("QuestMods_CaveNumPad");
                kbRoot.transform.position = headPos + fwd * 0.85f;
                kbRoot.transform.rotation = Quaternion.LookRotation(fwd, Vector3.up);

                GameObject prevObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                prevObj.transform.SetParent(kbRoot.transform, false);
                prevObj.transform.localPosition = new Vector3(0f, 0.28f, 0f);
                prevObj.transform.localScale = new Vector3(0.55f, 0.09f, 0.02f);
                var prevRend = prevObj.GetComponent<Renderer>();
                if (prevRend != null) prevRend.material.color = new Color(0.12f, 0.12f, 0.12f, 1f);
                kbPreviewText = CreateKeyboardLabel(prevObj.transform, "Layer: _", 0.03f);

                string[] rows = new[]
                {
                    "1 2 3",
                    "4 5 6",
                    "7 8 9",
                    "BACK 0 TP_CAVE",
                    "CANCEL"
                };

                for (int r = 0; r < rows.Length; r++)
                {
                    string[] tokens = rows[r].Split(' ');
                    float spacing = 0.14f;
                    float startX = -((tokens.Length - 1) * spacing) * 0.5f;
                    float y = 0.16f - r * 0.105f;
                    for (int c = 0; c < tokens.Length; c++)
                    {
                        string key = tokens[c];
                        float w = (key.Length > 1) ? 0.13f : 0.10f;
                        GameObject keyObj = GameObject.CreatePrimitive(PrimitiveType.Cube);
                        keyObj.transform.SetParent(kbRoot.transform, false);
                        keyObj.transform.localPosition = new Vector3(startX + c * spacing, y, 0f);
                        keyObj.transform.localScale = new Vector3(w, 0.085f, 0.02f);
                        var rend = keyObj.GetComponent<Renderer>();
                        if (rend != null)
                        {
                            if (key == "TP_CAVE") rend.material.color = new Color(0.15f, 0.65f, 0.2f, 1f);
                            else if (key == "CANCEL") rend.material.color = new Color(0.75f, 0.15f, 0.15f, 1f);
                            else rend.material.color = new Color(0.25f, 0.25f, 0.28f, 1f);
                        }
                        CreateKeyboardLabel(keyObj.transform, key == "TP_CAVE" ? "TP" : key, 0.025f);
                        kbKeys.Add((keyObj, key));
                    }
                }

                GameObject rayObj = new GameObject("QuestMods_KBRay");
                rayObj.transform.SetParent(kbRoot.transform, false);
                kbRay = rayObj.AddComponent<LineRenderer>();
                kbRay.positionCount = 2;
                kbRay.startWidth = 0.005f;
                kbRay.endWidth = 0.002f;
                kbRay.material = new Material(Shader.Find("Sprites/Default"));
                kbRay.startColor = Color.red;
                kbRay.endColor = Color.yellow;
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] OpenCaveNumPad failed: " + ex.Message); }
        }

        private static void TeleportToCaveLayer(int layer)
        {
            try
            {
                Vector3 pos = Vector3.zero;
                bool found = false;

                var clm = Il2CppAlta.Caves.CaveLayerManager.Instance;
                if (clm != null && clm.Layers != null && clm.Layers.TryGetValue(layer, out var caveLayer) && caveLayer != null)
                {
                    var mb = caveLayer.TryCast<MonoBehaviour>();
                    if (mb != null && mb.transform != null)
                    {
                        pos = mb.transform.position;
                        found = true;
                    }
                }

                if (!found)
                {
                    foreach (var ct in UnityEngine.Object.FindObjectsOfType<Il2CppAlta.Caves.CaveTeleporter>())
                    {
                        if (ct == null) continue;
                        var f = typeof(Il2CppAlta.Caves.CaveTeleporter).GetField("layer", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                        if (f != null && f.GetValue(ct) is int l && l == layer)
                        {
                            pos = ct.TeleportToTarget;
                            found = true;
                            break;
                        }
                    }
                }

                if (!found)
                {
                    pos = Il2CppAlta.Caves.CaveTeleporterManager.GetTeleportPosition(layer);
                }

                StartCaveTeleportSequence(pos + Vector3.up * 0.2f, "Cave Layer " + layer, layer);
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] TeleportToCaveLayer failed: " + ex.Message); }
        }

        private static Page BuildCavesPage()
        {
            Page page = new Page("CAVES", "Cave Teleporter");
            try
            {
                page.Button("Custom Layer (NumPad)", () => OpenCaveNumPad());

                var layers = new SortedSet<int> { 0, 9, 19, 29, 39, 49, 59, 69, 79, 89, 99 };
                var mgr = Il2CppAlta.Caves.CaveTeleporterManager.Instance;
                if (mgr != null && mgr.AvailableLayers != null)
                {
                    foreach (var kv in mgr.AvailableLayers)
                        layers.Add(kv.Key);
                }

                if (caveTpEndTime > Time.time && caveTpTargetLayer >= 0 && !layers.Contains(caveTpTargetLayer))
                {
                    page.Button("End Render (Layer " + caveTpTargetLayer + ")", () => StopCaveTeleportSequence());
                }

                foreach (int layer in layers)
                {
                    int targetLayer = layer;
                    string label = targetLayer == 0 ? "Layer 0 (Surface)" : ("Layer " + targetLayer);
                    page.Button(label, () => TeleportToCaveLayer(targetLayer));
                    if (caveTpEndTime > Time.time && caveTpTargetLayer == targetLayer)
                    {
                        page.Button("End Render", () => StopCaveTeleportSequence());
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] BuildCavesPage failed: " + ex.Message); }
            return page;
        }

        private static Page BuildPlayerTPsPage()
        {
            Page page = new Page("PLAYER TPS", "Online Players");
            try
            {
                var list = new List<(string Name, Vector3 Pos)>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var allPlayers = Player.AllPlayers;
                if (allPlayers != null)
                {
                    IntPtr localPtr = Player.Current != null ? Player.Current.Pointer : IntPtr.Zero;
                    foreach (var raw in allPlayers)
                    {
                        if (raw == null) continue;
                        var il2 = raw as Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase;
                        if (il2 == null || il2.Pointer == localPtr) continue;
                        Player p = il2.TryCast<Player>();
                        string uname = p?.UserInfo?.Username ?? p?.name ?? raw.ToString() ?? "Player";
                        if (!seen.Add(uname)) continue;
                        Vector3 pos = Vector3.zero;
                        bool hasPos = false;
                        if (p != null && p.PlayerController != null)
                        {
                            pos = p.PlayerController.PlayerFeetPosition + Vector3.up * 0.2f;
                            hasPos = true;
                        }
                        else
                        {
                            PropertyInfo posProp = raw.GetType().GetProperty("Position", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (posProp != null && posProp.GetValue(raw) is Vector3 v && v.sqrMagnitude > 0.01f)
                            {
                                pos = v + Vector3.up * 0.2f;
                                hasPos = true;
                            }
                            else if (p != null && p.transform != null)
                            {
                                pos = p.transform.position + Vector3.up * 0.2f;
                                hasPos = true;
                            }
                        }
                        if (hasPos) list.Add((uname, pos));
                    }
                }
                list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var entry in list)
                {
                    string targetName = entry.Name;
                    Vector3 targetPos = entry.Pos;
                    page.Button(targetName, () => TeleportLocalPlayer(targetPos, targetName));
                }
                MelonLogger.Msg("[QuestMods] Player TPs: listed " + list.Count + " online player(s).");
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] BuildPlayerTPsPage failed: " + ex.Message); }
            return page;
        }

        private static void TeleportLocalPlayer(Vector3 pos, string targetName)
        {
            try
            {
                PlayerController pc = PlayerController.Current;
                if (pc == null) { MelonLogger.Warning("[QuestMods] TP: PlayerController.Current is null."); return; }
                SmoothLocomotionPlayerController slpc = pc.TryCast<SmoothLocomotionPlayerController>();
                object loco = slpc != null ? (object)slpc.LocomotionController : pc.LocomotionController;
                if (loco == null) { MelonLogger.Warning("[QuestMods] TP: LocomotionController not found."); return; }
                foreach (MethodInfo m in loco.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m.Name != "MoveTo") continue;
                    ParameterInfo[] ps = m.GetParameters();
                    if (ps.Length == 2 && ps[0].ParameterType == typeof(Vector3))
                    {
                        object arg2 = ps[1].ParameterType.IsEnum ? Enum.ToObject(ps[1].ParameterType, 0) : null;
                        m.Invoke(loco, new object[] { pos, arg2 });
                        MelonLogger.Msg("[QuestMods] Teleported to " + targetName + ".");
                        return;
                    }
                    if (ps.Length == 1 && ps[0].ParameterType == typeof(Vector3))
                    {
                        m.Invoke(loco, new object[] { pos });
                        MelonLogger.Msg("[QuestMods] Teleported to " + targetName + ".");
                        return;
                    }
                }
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] TeleportLocalPlayer failed: " + ex.Message); }
        }

        private static void MakeDay()
        {
            try
            {
                // Object.FindObjectOfType(Type) needs Il2CppSystem.Type in this
                // interop build, and the generic form needs the game type at
                // compile time. Walking MonoBehaviours by name avoids both.
                Type type = null;
                UnityEngine.MonoBehaviour target = null;
                foreach (UnityEngine.MonoBehaviour mb in UnityEngine.Object.FindObjectsOfType<UnityEngine.MonoBehaviour>())
                {
                    if (mb == null) continue;
                    Type mt = mb.GetType();
                    if (mt == null || mt.Name != "DayTimeManager") continue;
                    type = mt;
                    target = mb;
                    break;
                }
                if (type == null || target == null) { MelonLogger.Warning("[QuestMods] Make Day: no DayTimeManager found in scene."); return; }
                const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
                type.GetField("isUpdatingDaytime", flags)?.SetValue(target, false);
                type.GetMethod("UpdateRotation", flags)?.Invoke(target, new object[] { 0.25f, 0f });
                type.GetMethod("UpdateAmbience", flags)?.Invoke(target, null);
                MelonLogger.Msg("[QuestMods] Forced DayTimeManager to midday (client-side visual override only).");
            }
            catch (Exception ex) { MelonLogger.Warning("[QuestMods] MakeDay failed: " + ex.Message); }
        }

        // --- Air Grab ---
        [ThreadStatic] private static int airGrabDepth;
        private static Component airAnchorArmed;
        private static readonly Dictionary<IntPtr, Component> airAnchors = new Dictionary<IntPtr, Component>();
        private static Type physType, sphereColType;
        private static MethodInfo overlapSphereMethod, syncTransformsMethod;
        private static PropertyInfo colEnabledProp, sphereRadiusProp, colIsTriggerProp;

        private static bool EnsurePhysicsReflection()
        {
            if (physType != null && sphereColType != null) return overlapSphereMethod != null;
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (physType == null) physType = a.GetType("UnityEngine.Physics", false);
                if (sphereColType == null) sphereColType = a.GetType("UnityEngine.SphereCollider", false);
                if (physType != null && sphereColType != null) break;
            }
            if (physType != null)
            {
                overlapSphereMethod = physType.GetMethod("OverlapSphere", new Type[] { typeof(Vector3), typeof(float), typeof(int) });
                syncTransformsMethod = physType.GetMethod("SyncTransforms", Type.EmptyTypes);
            }
            if (sphereColType != null)
            {
                colEnabledProp = sphereColType.GetProperty("enabled");
                sphereRadiusProp = sphereColType.GetProperty("radius");
                colIsTriggerProp = sphereColType.GetProperty("isTrigger");
            }
            return overlapSphereMethod != null && sphereColType != null;
        }

        private static void CheckGrabPrefix(ClimbingHand __instance)
        {
            airGrabDepth++;
            if (airGrabDepth != 1 || prefAirGrab == null || !prefAirGrab.Value || __instance == null) return;
            try { PlaceAnchorIfNothingInReach(__instance); }
            catch (Exception ex) { airAnchorArmed = null; MelonLogger.Warning("[QuestMods] AirGrab anchor failed: " + ex.Message); }
        }

        private static void CheckGrabPostfix(ClimbingHand __instance, bool __result)
        {
            airGrabDepth--;
            if (airGrabDepth != 0 || airAnchorArmed == null) return;
            try { colEnabledProp?.SetValue(airAnchorArmed, false); }
            catch { }
            finally { airAnchorArmed = null; }
        }

        private static void PlaceAnchorIfNothingInReach(ClimbingHand hand)
        {
            if (!EnsurePhysicsReflection()) return;
            ClimbingHandler handler = hand.handler;
            ClimbingSettings settings = handler == null ? null : handler.Settings;
            Transform grip = hand.Controller == null ? null : hand.Controller.GripPoint;
            if (settings == null || grip == null) return;

            Vector3 pos = grip.position;
            Vector3 back = -grip.forward;
            Transform shoulder = hand.shoulder;
            float hangDist = settings.HangDistance;
            if (shoulder != null)
            {
                Vector3 diff = pos - shoulder.position;
                float mag = diff.magnitude;
                if (mag > 0.0001f)
                {
                    back = -diff / mag;
                    if (mag > hangDist) pos = shoulder.position - back * hangDist;
                }
            }
            int mask = handler.LayerMask.value;
            object hitsObj = overlapSphereMethod.Invoke(null, new object[] { pos, settings.GrabRadius, mask });
            if (hitsObj != null)
            {
                PropertyInfo lenProp = hitsObj.GetType().GetProperty("Length") ?? hitsObj.GetType().GetProperty("Count");
                if (lenProp != null && Convert.ToInt32(lenProp.GetValue(hitsObj)) > 0) return;
            }

            Component anchor = GetAirAnchor(hand, LowestLayer(mask));
            if (anchor == null) return;
            sphereRadiusProp?.SetValue(anchor, 0.03f);
            anchor.transform.position = pos + back * 0.05f;
            colEnabledProp?.SetValue(anchor, true);
            syncTransformsMethod?.Invoke(null, null);
            airAnchorArmed = anchor;
        }

        private static Component GetAirAnchor(ClimbingHand hand, int layer)
        {
            if (airAnchors.TryGetValue(hand.Pointer, out Component col) && col != null && !col.WasCollected)
                return col;
            GameObject go = new GameObject("QuestMods air anchor");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.layer = layer;
            col = go.AddComponent(Il2CppInterop.Runtime.Il2CppType.From(sphereColType));
            if (col != null)
            {
                MethodInfo tryCast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethod("TryCast", BindingFlags.Instance | BindingFlags.Public)?.MakeGenericMethod(sphereColType);
                if (tryCast != null) col = tryCast.Invoke(col, null) as Component ?? col;
            }
            colIsTriggerProp?.SetValue(col, false);
            colEnabledProp?.SetValue(col, false);
            airAnchors[hand.Pointer] = col;
            return col;
        }

        private static int LowestLayer(int mask)
        {
            for (int i = 0; i < 32; i++) if ((mask & (1 << i)) != 0) return i;
            return 8;
        }

        // --- Any Connect ---
        private static readonly Dictionary<IntPtr, float> anyConnectWidened = new Dictionary<IntPtr, float>();
        private static readonly Dictionary<IntPtr, float> anyConnectForced = new Dictionary<IntPtr, float>();

        private static void SupportsPostfix(JointSlot __instance, JointInsertType type, ref bool __result)
        {
            if (__result || type == null || prefAnyConnect == null || !prefAnyConnect.Value) return;
            __result = true;
            try { anyConnectWidened[__instance.Pointer] = Time.time; } catch { }
        }

        private static void AttemptCraftPostfix(CraftingPart __instance, JointSlot slot)
        {
            if (prefAnyConnect == null || !prefAnyConnect.Value || __instance == null || slot == null) return;
            try
            {
                if (NetworkSceneManager.IsServer) return;
                if (!anyConnectWidened.TryGetValue(slot.Pointer, out float t) || Time.time - t > 10f) return;
                if (slot.ConnectedPart != null || slot.IsPreConfirmationConnected) return;
                if (__instance.Insert == null || __instance.Insert.ConnectedSlot != null) return;
                if (__instance.Pickup != null && __instance.Pickup.IsCraftedChild) return;
                if (anyConnectForced.TryGetValue(__instance.Pointer, out float ft) && Time.time - ft < 15f) return;
                if (WouldLoop(__instance, slot)) return;
                anyConnectForced[__instance.Pointer] = Time.time;
                __instance.CraftWithSlot(slot, true);
            }
            catch { }
        }

        private static bool TryConnectPrefix(JointSlot __instance, CraftingPart insertPart, ref bool __result)
        {
            try
            {
                if (__instance != null && insertPart != null && WouldLoop(insertPart, __instance))
                {
                    __result = false;
                    return false;
                }
            }
            catch { }
            return true;
        }

        private static bool CraftWithSlotPrefix(CraftingPart __instance, JointSlot slot)
        {
            try { if (__instance != null && slot != null && WouldLoop(__instance, slot)) return false; } catch { }
            return true;
        }

        private static bool WouldLoop(CraftingPart part, JointSlot slot)
        {
            CraftingPart cur = slot.CraftingPart;
            int n = 0;
            while (cur != null && n < 64)
            {
                if (cur.Pointer == part.Pointer) return true;
                CraftingPart next = cur.ConnectedTo;
                if (next == null || next.Pointer == cur.Pointer) return false;
                cur = next;
                n++;
            }
            return cur != null;
        }

        // --- Auto Revive ---
        private static float nextReviveScanAt, nextReviveGrabAt;
        private static Interactor reviveRightHand;
        private static readonly List<ReviveOrb> myReviveOrbs = new List<ReviveOrb>();

        private static void AutoReviveUpdate()
        {
            try
            {
                PlayerController me = PlayerController.Current;
                if (me == null || !me.IsDowned) return;
                float now = Time.time;
                if (now >= nextReviveScanAt)
                {
                    nextReviveScanAt = now + 0.5f;
                    myReviveOrbs.Clear();
                    Player p = Player.Current == null ? null : Player.Current.TryCast<Player>();
                    int myId = (p != null && p.UserInfo != null) ? p.UserInfo.Identifier : 0;
                    if (myId != 0)
                    {
                        foreach (UnityEngine.Object obj in UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<ReviveOrb>()))
                        {
                            ReviveOrb orb = obj.TryCast<ReviveOrb>();
                            if (orb == null || orb.Entity == null || orb.Entity.IsBeingDestroyed) continue;
                            IPlayer dp = orb.downedPlayer;
                            if (dp != null && dp.UserInfo != null && dp.UserInfo.Identifier == myId && (orb.orbPickup == null || !orb.orbPickup.IsInteractedWith))
                                myReviveOrbs.Add(orb);
                        }
                    }
                }
                if (myReviveOrbs.Count == 0 || now < nextReviveGrabAt) return;
                if (reviveRightHand == null || reviveRightHand.WasCollected || !reviveRightHand.isActiveAndEnabled)
                {
                    reviveRightHand = null;
                    foreach (Interactor inter in me.GetComponentsInChildren<Interactor>())
                    {
                        Controller c = inter == null ? null : inter.Controller;
                        if (c != null && c.Hand != null && inter.IsLocal && c.Hand.HandIndex == HandIndex.Right)
                        {
                            reviveRightHand = inter;
                            break;
                        }
                    }
                }
                if (reviveRightHand == null || reviveRightHand.IsInteracting) return;
                ReviveOrb targetOrb = myReviveOrbs[0];
                Pickup pickup = targetOrb.orbPickup;
                if (pickup == null) return;
                targetOrb.SetIsOrbInteractable(true);
                nextReviveGrabAt = now + 1f;
                reviveRightHand.OnStartInteractionAttempt();
                reviveRightHand.StartInteract(pickup, false, false, false);
            }
            catch { }
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
            WristHudSceneLoaded();
            EspSceneLoaded();
            GraphicsSceneLoaded();
            LoggerInstance.Msg($"Scene loaded: '{sceneName}' ({buildIndex}). {Counts()}");
            try { TabletProbe.SceneLoaded(buildIndex, sceneName); DevWheelBridge.SceneLoaded(); }
            catch (Exception ex) { LoggerInstance.Warning("Tablet probe scene event: " + ex.Message); }
        }

        public override void OnUpdate()
        {
            try { TabletProbe.Tick(); DevWheelBridge.Tick(); DebugStateMonitor.Tick(); GrabbyHands.Tick(); SyncTabletToggles(); }
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
        private static int ownershipLogs;
        private static bool ready, enabled, grabScanDone, wheelHookAttempted;
        private static int grabHooks, holdHooks, pending, startLogs, holdLogs;
        [ThreadStatic] private static bool insideFallback;
        private static global::HarmonyLib.Harmony grabHarmony;
        public static bool Available => ready;
        public static bool IsEnabled => enabled;
        public static void SetEnabled(bool value)
        {
            if (value == enabled) return;
            if (value && !grabScanDone) InstallGrabHooks();
            if (value && holdHooks == 0)
            {
                enabled = false;
                MelonLogger.Warning("[GrabbyHands] No compatible slot input hook; remains OFF.");
                return;
            }
            enabled = value;
            if (enabled) { startLogs = 0; holdLogs = 0; ownershipLogs = 0; }
            MelonLogger.Msg("[GrabbyHands] " + (enabled ? "ON" : "OFF") + ".");
            UpdateLabels();
        }

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
                try
                {
                    MethodInfo getConf = AccessTools.Method(typeof(Interactor), "GetConfirmationOnInteractable", null, null);
                    if (getConf != null)
                    {
                        grabHarmony.Patch(getConf, prefix: new HarmonyMethod(typeof(GrabbyHands).GetMethod(nameof(BeforeGetConfirmation), S)));
                        MelonLogger.Msg("[GrabbyHands] Patched Interactor.GetConfirmationOnInteractable for hand snatching.");
                    }
                    else MelonLogger.Warning("[GrabbyHands] Interactor.GetConfirmationOnInteractable not found.");
                }
                catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] Hand snatch hook failed: " + Detail(ex)); }
                try
                {
                    MethodInfo dockCheck = AccessTools.Method(typeof(PickupDock), "CheckForward", null, null);
                    if (dockCheck != null)
                    {
                        grabHarmony.Patch(dockCheck, prefix: new HarmonyMethod(typeof(GrabbyHands).GetMethod(nameof(BeforeDockCheckForward), S)));
                        MelonLogger.Msg("[GrabbyHands] Patched PickupDock.CheckForward for hotbar/dock grabbing.");
                    }
                    else MelonLogger.Warning("[GrabbyHands] PickupDock.CheckForward not found.");
                }
                catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] PickupDock.CheckForward hook failed: " + Detail(ex)); }
                try
                {
                    MethodInfo docksFilter = AccessTools.Method(typeof(PlayerDocks), "Filter", null, null);
                    if (docksFilter != null)
                    {
                        grabHarmony.Patch(docksFilter, prefix: new HarmonyMethod(typeof(GrabbyHands).GetMethod(nameof(BeforePlayerDocksFilter), S)));
                        MelonLogger.Msg("[GrabbyHands] Patched PlayerDocks.Filter for hotbar/dock grabbing.");
                    }
                    else MelonLogger.Warning("[GrabbyHands] PlayerDocks.Filter not found.");
                }
                catch (Exception ex) { MelonLogger.Warning("[GrabbyHands] PlayerDocks.Filter hook failed: " + Detail(ex)); }
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
        private static int snatches, filterBypasses, remoteGrabChecks, docksReleased;

        private static bool BeforeGetConfirmation(Interactor __instance, Interactable targetInteractable)
        {
            if (!enabled) return true;
            Interactable interactable = targetInteractable;
            try
            {
                if (targetInteractable.Entity != null && targetInteractable.Entity.Identifier != 0U)
                {
                    Interactable prev = null;
                    int num = 0;
                    bool flag = false;
                    while (!flag && interactable != prev && num < 100)
                    {
                        num++;
                        prev = interactable;
                        interactable = interactable.CheckForward(__instance, out flag);
                        if (interactable == null) return false;
                    }
                    PickupContainer pickupContainer = interactable.TryCast<PickupContainer>();
                    if (pickupContainer != null && pickupContainer.Pickup != null)
                        interactable = pickupContainer.Pickup;
                }
            }
            catch (Exception ex)
            {
                if (holdLogs++ < 100) MelonLogger.Warning("[GrabbyHands] Hand snatch forward walk: " + ex.Message);
            }
            try
            {
                PlayerController holder = interactable.IsInteractedWith ? interactable.LastPlayerInteractor : null;
                if (holder != null && !holder.IsLocalPlayer && snatches++ == 0)
                    MelonLogger.Msg("[GrabbyHands] Hand snatch: first grab of an item another player is holding.");
                __instance.StartInteract(targetInteractable, false, false, false);
            }
            catch (Exception ex2)
            {
                if (holdLogs++ < 100) MelonLogger.Warning("[GrabbyHands] Hand snatch: " + ex2.Message);
            }
            return false;
        }

        private static void BeforeDockCheckForward(PickupDock __instance)
        {
            if (!enabled) return;
            try
            {
                PlayerController playerController = __instance.PlayerController;
                if (playerController == null || playerController.IsLocalPlayer) return;
                if (remoteGrabChecks++ == 0)
                    MelonLogger.Msg("[GrabbyHands] Hotbar ownership: first grab check on another player's dock.");
                if (__instance.IsOwnedByPlayer)
                {
                    __instance.IsOwnedByPlayer = false;
                    if (!__instance.IsOwnedByPlayer) docksReleased++;
                    else if (ownershipLogs++ < 8) MelonLogger.Warning("[GrabbyHands] Hotbar ownership: write to IsOwnedByPlayer did not take.");
                }
            }
            catch (Exception ex)
            {
                if (ownershipLogs++ < 8) MelonLogger.Warning("[GrabbyHands] Hotbar ownership: " + ex.Message);
            }
        }

        private static bool BeforePlayerDocksFilter(PlayerDocks __instance)
        {
            if (!enabled) return true;
            try
            {
                PlayerController owner = __instance.owner;
                if (owner == null || owner.IsLocalPlayer) return true;
                if (filterBypasses++ == 0)
                    MelonLogger.Msg("[GrabbyHands] Hotbar ownership: first owner filter bypassed on another player's dock.");
                return false;
            }
            catch (Exception ex)
            {
                if (ownershipLogs++ < 8) MelonLogger.Warning("[GrabbyHands] Hotbar ownership filter: " + ex.Message);
                return true;
            }
        }

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
