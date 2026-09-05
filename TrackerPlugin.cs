using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Alkawa.Gameplay;
using Alkawa.Gameplay.Controller;
using Alkawa.Engine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using System.Runtime.CompilerServices;

namespace PoPTracker
{
    [BepInPlugin("com.thyme.poplostcrown.tracker", "PoP Lost Crown Tracker", "0.5.2")]
    public class Plugin : BasePlugin
    {
        internal static HashSet<string> trackedLocations;
        internal static HashSet<string> trackedItems;
        internal static string GetSourceDirectory([CallerFilePath] string sourceFilePath = "") => Path.GetDirectoryName(sourceFilePath);
        internal static string repoDataDir = GetSourceDirectory();
        internal static string locationDir = Path.Combine(repoDataDir, "locations.csv");
        internal static string itemDir = Path.Combine(repoDataDir, "items.csv");
        internal static string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        private static readonly HashSet<EItemAcquisitionMode> ExcludedAcquisitionModes = new HashSet<EItemAcquisitionMode>
        {
            EItemAcquisitionMode.Init,
            EItemAcquisitionMode.Regen,
        };

        // Currency/bonus-tier items excluded from location logging entirely. 
        internal static readonly HashSet<EItemType> ExcludeFromLocationLog = new HashSet<EItemType>
        {
            EItemType.Crystal,
            EItemType.SmallHealthDrop,
            EItemType.Arrow,
        };

        // Hashes captured from LootManager.SpawnLootItem, consumed by AddItem_Patch
        // to give dynamically-spawned drops (boss/miniboss loot) a stable identity
        // independent of their variable landing position.
        internal static Dictionary<EItemType, int> PendingSpawnHashes = new Dictionary<EItemType, int>();
        internal static HashSet<EItemType> ItemsSeenViaLootManager = new HashSet<EItemType>();

        public static void initializeTrackedData(HashSet<string> data, string filepath)
        {
            string[] lines = File.ReadAllLines(filepath);
            foreach (var line in lines.Skip(1))
            {
                data.Add(line);
            }
        }

        public override void Load()
        {
            WriteGameDataToCSV.InitializeCSV(locationDir, itemDir);
            trackedLocations = new HashSet<string>();
            trackedItems = new HashSet<string>();
            TrackLog.Init(pluginDir);
            TrackLog.Log("Tracker plugin loaded.");

            initializeTrackedData(trackedLocations, locationDir);
            initializeTrackedData(trackedItems, itemDir);
            FuzzyLocationMatcher.SeedFromExistingLocations(trackedLocations);

            var harmony = new Harmony("com.thyme.poplostcrown.tracker");
            harmony.PatchAll();
            TrackLog.Log("Harmony patches applied.");
        }

        public static void addToFile(string data, string filepath, HashSet<string> trackedData)
        {
            if (trackedData.Add(data))
            {
                TrackLog.Log($"Adding new data to {filepath}: {data}");
                WriteGameDataToCSV.WriteToCSV(filepath, data);
            }
        }

        public static void DumpProperties(object obj, string label)
        {
            if (obj == null) return;
            var type = obj.GetType();
            while (type != null && type != typeof(object))
            {
                foreach (var prop in type.GetProperties(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (prop.GetIndexParameters().Length > 0) continue;
                    try
                    {
                        var value = prop.GetValue(obj);
                        TrackLog.Log($"  [{label}:{type.Name}] {prop.Name} = {value?.ToString() ?? "null"}");
                    }
                    catch (Exception e)
                    {
                        TrackLog.Log($"  [{label}:{type.Name}] {prop.Name} = <error: {e.Message}>");
                    }
                }
                type = type.BaseType;
            }
        }

        // Tracks which physical location(s) recently fired a trigger, so the
        // subsequent AddItem/AddUniqueItem/UnlockAbility call knows what to
        // attribute itself to. Queue-based so overlapping triggers preserve order;
        // AddItem-family patches PEEK (not dequeue) so one trigger producing
        // multiple grants (e.g. a token + several crystals) all attribute correctly.
        // The front entry is only retired when the NEXT trigger arrives.
        public static class LocationTracker
        {
            private static readonly Queue<string> pendingLocations = new Queue<string>();

            public static string BuildKey(GameObject owner)
            {
                if (owner == null) return null;
                var pos = owner.transform.position;
                return FuzzyLocationMatcher.ResolveKey(owner.name, pos);
            }

            public static void EnqueueLocation(string key)
            {
                if (pendingLocations.Count > 0)
                {
                    TrackLog.Log($"Retiring stale pending location: {pendingLocations.Peek()}");
                    pendingLocations.Dequeue();
                }
                pendingLocations.Enqueue(key);
            }

            public static string PeekLocation()
            {
                return pendingLocations.Count > 0 ? pendingLocations.Peek() : null;
            }
        }

        // Handles items whose collection position varies unpredictably (e.g.
        // PFB_GPE_Token-style "touch it, then reach safe ground" pickups, where
        // the safe spot depends on the player's movement options and skill).
        //
        // Rather than a fixed rounding grid (which still has hard boundary
        // failures), this keeps a running list of actually-observed positions per
        // name pattern. A new trigger reuses an existing key if it falls within a
        // wide tolerance of any prior observation for that same name; otherwise it
        // registers as a new distinct location.
        //
        // KNOWN TRADEOFF: a wide tolerance can incorrectly merge two genuinely
        // different nearby instances of the same name pattern into one key. This is
        // a limitation of this method, and ideally I will have figured out something better.
        public static class FuzzyLocationMatcher
        {
            // Name patterns known to have unpredictable/skill-dependent final
            // collection positions. Add to this list as new cases are found.
            private static readonly string[] JitteredPatterns = { "PFB_GPE_Token" };

            // Wide on purpose — deliberately generous per the known issue that
            // stronger movement options later in the game increase the spread.
            private const float ToleranceUnits = 8f;

            // name -> list of (position, canonical key) already seen this session
            // (plus seeded from existing CSV rows on load).
            private static readonly Dictionary<string, List<(Vector3 pos, string key)>> knownPositions
                = new Dictionary<string, List<(Vector3, string)>>();

            public static string ResolveKey(string name, Vector3 pos)
            {
                bool isJittered = JitteredPatterns.Any(p => name.Contains(p));
                if (!isJittered)
                {
                    return $"{name}@({pos.x:F2}, {pos.y:F2}, {pos.z:F2})";
                }

                if (!knownPositions.TryGetValue(name, out var list))
                {
                    list = new List<(Vector3, string)>();
                    knownPositions[name] = list;
                }

                foreach (var (knownPos, knownKey) in list)
                {
                    if (Vector3.Distance(knownPos, pos) <= ToleranceUnits)
                    {
                        return knownKey; // treat as the same location through key reuse
                    }
                }

                // No existing match within tolerance, make a new key.
                var newKey = $"{name}@(~{pos.x:F2}, {pos.y:F2}, {pos.z:F2})";
                list.Add((pos, newKey));
                TrackLog.Log($"FuzzyLocationMatcher: new jittered location registered: {newKey}");
                return newKey;
            }

            // Parses existing locations.csv rows matching jittered name patterns,
            // so positions already recorded in prior sessions are respected rather
            // than treated as new on next load.
            public static void SeedFromExistingLocations(HashSet<string> existingRows)
            {
                foreach (var row in existingRows)
                {
                    var locationPart = row.Split(',')[0]; // "Name@(x, y, z)"
                    var atIndex = locationPart.IndexOf('@');
                    if (atIndex < 0) continue;

                    var name = locationPart.Substring(0, atIndex);
                    if (!JitteredPatterns.Any(p => name.Contains(p))) continue;

                    var coordsPart = locationPart.Substring(atIndex + 1).Trim('(', ')', '~');
                    var parts = coordsPart.Split(',');
                    if (parts.Length != 3) continue;
                    if (!float.TryParse(parts[0], out var x)) continue;
                    if (!float.TryParse(parts[1], out var y)) continue;
                    if (!float.TryParse(parts[2], out var z)) continue;

                    if (!knownPositions.TryGetValue(name, out var list))
                    {
                        list = new List<(Vector3, string)>();
                        knownPositions[name] = list;
                    }
                    list.Add((new Vector3(x, y, z), locationPart));
                }
            }
        }

        // Below captures a wide range of triggers for items.
        [HarmonyPatch(typeof(InteractiveElementLogic_CutsceneBase), "TriggerLogic_Internal")]
        public class CutsceneTriggerLogic_Patch
        {
            static void Postfix(InteractiveElementLogic_CutsceneBase __instance, ETriggerLogicType _triggerType)
            {
                var key = LocationTracker.BuildKey(__instance.m_Owner);
                LocationTracker.EnqueueLocation(key);
                TrackLog.Log($"CUTSCENE TRIGGERED: location='{key}'");
            }
        }
        [HarmonyPatch(typeof(InteractiveElementLogic_TimelineLauncher), "TriggerLogic_Internal")]
        public class TimelineLauncherTrigger_Patch
        {
            static void Postfix(InteractiveElementLogic_TimelineLauncher __instance, ETriggerLogicType _triggerType)
            {
                var key = LocationTracker.BuildKey(__instance.m_Owner);
                LocationTracker.EnqueueLocation(key);
                TrackLog.Log($"TIMELINE LAUNCHER TRIGGERED: location='{key}'");
            }
        }

        [HarmonyPatch(typeof(InteractiveElementLogic_CollectibleItem), "TriggerLogic_Internal")]
        public class TriggerLogic_Patch
        {
            static void Postfix(InteractiveElementLogic_CollectibleItem __instance, ETriggerLogicType _triggerType)
            {
                var key = LocationTracker.BuildKey(__instance.m_Owner);
                LocationTracker.EnqueueLocation(key);
                TrackLog.Log($"COLLECTIBLE TRIGGERED: location='{key}'");
            }
        }

        [HarmonyPatch(typeof(InteractiveElementLogic_SimorghFeather), "TriggerLogic_Internal")]
        public class SimorghFeatherTriggerLogic_Patch
        {
            static void Postfix(InteractiveElementLogic_SimorghFeather __instance, ETriggerLogicType _triggerType)
            {
                var key = LocationTracker.BuildKey(__instance.m_Owner);
                LocationTracker.EnqueueLocation(key);
                TrackLog.Log($"SIMORGH FEATHER TRIGGERED: location='{key}'");
            }
        }
        //[HarmonyPatch(typeof(InteractiveElementLogic_PrepareVideo), "TriggerLogic_Internal")]
        public class PrepareVideoTrigger_Patch
        {
            static void Postfix(InteractiveElementLogic_PrepareVideo __instance, ETriggerLogicType _triggerType)
            {
                var key = LocationTracker.BuildKey(__instance.m_Owner);
                LocationTracker.EnqueueLocation(key);
                TrackLog.Log($"PREPARE VIDEO TRIGGERED: location='{key}'");
            }
        }

        [HarmonyPatch(typeof(InteractiveElementLogic_StoneOfKnowledge), "TriggerLogic_Internal")]
        public class StoneOfKnowledgeTriggerLogic_Patch
        {
            static void Postfix(InteractiveElementLogic_StoneOfKnowledge __instance, ETriggerLogicType _triggerType)
            {
                var key = LocationTracker.BuildKey(__instance.m_Owner);
                LocationTracker.EnqueueLocation(key);
                TrackLog.Log($"STONE OF KNOWLEDGE TRIGGERED: location='{key}'");
            }
        }

        [HarmonyPatch(typeof(InteractiveElementLogic_Chest), "TriggerLogic_Internal")]
        public class ChestTriggerLogic_Patch
        {
            static void Postfix(InteractiveElementLogic_Chest __instance, ETriggerLogicType _triggerType)
            {
                var key = LocationTracker.BuildKey(__instance.m_Owner);
                LocationTracker.EnqueueLocation(key);
                TrackLog.Log($"CHEST TRIGGERED: location='{key}'");
            }
        }
        // Shop items are handled differently, never calling addItem. Not a problem for tracking,
        // but will need new solution for randomizer.
        [HarmonyPatch(typeof(InteractiveElementLogic_ShopKeeper), "OpenShopMenu")]
        public class ShopCatalog_Tracking_Patch
        {
            static void Postfix(InteractiveElementLogic_ShopKeeper __instance)
            {
                var shopKey = LocationTracker.BuildKey(__instance.m_Owner);
                if (shopKey == null) return;

                LogCatalog(shopKey, __instance.m_availableItems, "Shop");
                LogCatalog(shopKey, __instance.m_availableUpgrades, "ShopUpgrade");
            }

            static void LogCatalog(string shopKey, Il2CppSystem.Collections.Generic.List<ShopItemTrade> list, string acquisitionLabel)
            {
                if (list == null) return;
                for (int i = 0; i < list.Count; i++)
                {
                    var itemType = list[i]?.m_item?.m_itemId;
                    if (itemType == null) continue;

                    var locationData = $"{shopKey},{itemType},{acquisitionLabel},,";
                    var itemData = $"{itemType}";
                    Plugin.addToFile(locationData, Plugin.locationDir, Plugin.trackedLocations);
                    Plugin.addToFile(itemData, Plugin.itemDir, Plugin.trackedItems);
                }
            }
        }

        [HarmonyPatch(typeof(LootManager), "SpawnLootItem")]
        public class SpawnLootItem_Tracking_Patch
        {
            static void Postfix(EItemType _itemType, int _spawnerHash)
            {
                Plugin.ItemsSeenViaLootManager.Add(_itemType);
                if (_spawnerHash != 0)
                {
                    Plugin.PendingSpawnHashes[_itemType] = _spawnerHash;
                }
            }
        }

        [HarmonyPatch(typeof(PlayerInventorySubComponent), "AddItem",
            new System.Type[] { typeof(EItemType), typeof(int), typeof(EItemAcquisitionMode) })]
        public class AddItem_Patch
        {
            static void Postfix(EItemType _itemType, int _amount, EItemAcquisitionMode _acquisitionMode)
            {
                if (ExcludedAcquisitionModes.Contains(_acquisitionMode))
                {
                    TrackLog.Log($"Skipping {_acquisitionMode} grant: {_itemType}");
                    return;
                }

                if (Plugin.ExcludeFromLocationLog.Contains(_itemType))
                {
                    return;
                }

                var hash = "";
                var locationKey = LocationTracker.PeekLocation();

                // If this item was seen spawning via LootManager with a real hash, upgrade
                // the location key from name@position (which varies per-kill) to
                // name#hash (which is stable across kills of the same source).
                if (Plugin.PendingSpawnHashes.TryGetValue(_itemType, out var h))
                {
                    hash = h.ToString();
                    Plugin.PendingSpawnHashes.Remove(_itemType);

                    if (locationKey != null)
                    {
                        locationKey = $"{ExtractName(locationKey)}#{hash}";
                    }
                }

                var notes = "";
                if (hash == "" && Plugin.ItemsSeenViaLootManager.Contains(_itemType))
                {
                    notes = "LootManager spawn with no hash captured (revisit/reload case)";
                }

                if (locationKey != null)
                {
                    var locationData = $"{locationKey},{_itemType},{_acquisitionMode},{hash},{notes}";
                    var itemData = $"{_itemType}";
                    Plugin.addToFile(locationData, Plugin.locationDir, Plugin.trackedLocations);
                    Plugin.addToFile(itemData, Plugin.itemDir, Plugin.trackedItems);
                }
                else
                {
                    TrackLog.Log($"{_acquisitionMode} AddItem with no pending location. Item: {_itemType}, Amount: {_amount}");
                }
            }

            // Extracts just the "name" portion from a "name@(x, y, z)" key, so it can be
            // rebuilt as "name#hash" instead when a stable hash is available.
            static string ExtractName(string key)
            {
                var atIndex = key.IndexOf('@');
                return atIndex >= 0 ? key.Substring(0, atIndex) : key;
            }
        }

        // Unsure of what items this actually handles, but included for thoroughness.
        [HarmonyPatch]
        public class AddUniqueItem_Patch
        {
            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(PlayerInventorySubComponent), "AddUniqueItem",
                    new System.Type[] { typeof(EItemType), typeof(EItemAcquisitionMode) });
            }

            static void Postfix(EItemType _itemType, EItemAcquisitionMode _acquisitionMode)
            {
                if (ExcludedAcquisitionModes.Contains(_acquisitionMode))
                {
                    TrackLog.Log($"Skipping {_acquisitionMode} grant: {_itemType}");
                    return;
                }

                var locationKey = LocationTracker.PeekLocation();
                if (locationKey != null)
                {
                    var locationData = $"{locationKey},{_itemType},{_acquisitionMode},,";
                    var itemData = $"{_itemType}";
                    Plugin.addToFile(locationData, Plugin.locationDir, Plugin.trackedLocations);
                    Plugin.addToFile(itemData, Plugin.itemDir, Plugin.trackedItems);
                }
                else
                {
                    TrackLog.Log($"{_acquisitionMode}-mode AddUniqueItem with no pending location. Item: {_itemType}");
                }
            }
        }
        
        [HarmonyPatch(typeof(PlayerAbilitiesSubComponent), "UnlockAbility")]
        public class UnlockAbility_Tracking_Patch
        {
            static void Postfix(EPlayerUnlockableAbility _ability, bool _unlock)
            {
                if (!_unlock)
                {
                    TrackLog.Log($"Skipping ability lock event: {_ability}, unlock={_unlock}");
                    return;
                }

                var locationKey = LocationTracker.PeekLocation();
                if (locationKey != null)
                {
                    var locationData = $"{locationKey},{_ability},Ability,,";
                    var itemData = $"{_ability}";
                    Plugin.addToFile(locationData, Plugin.locationDir, Plugin.trackedLocations);
                    Plugin.addToFile(itemData, Plugin.itemDir, Plugin.trackedItems);
                }
                else
                {
                    TrackLog.Log($"UnlockAbility with no pending location. Ability: {_ability}");
                }
            }
        }

        // --- Disabled investigation-only patches (attribute commented out) ---
        // Kept for future re-use, not currently active.

        //[HarmonyPatch(typeof(PlayerInventorySubComponent), "OnAbilityUnlocked",
        //    new System.Type[] { typeof(EPlayerAction), typeof(bool) })]
        public class OnAbilityUnlocked_Patch
        {
            static void Postfix(EPlayerAction _ability, bool _unlock)
            {
                TrackLog.Log($"(disabled) OnAbilityUnlocked: {_ability}, unlock={_unlock}");
            }
        }

        //[HarmonyPatch(typeof(PlayerInventorySubComponent), "OnAbilityUnlocked",
        //    new System.Type[] { typeof(EPlayerAction), typeof(bool) })]
        public class OnAbilityUnlocked_StackTrace_Patch
        {
            static void Postfix(PlayerInventorySubComponent __instance, EPlayerAction _ability, bool _unlock)
            {
                var stackTrace = new System.Diagnostics.StackTrace(true);
                TrackLog.Log($"OnAbilityUnlocked call stack:\n{stackTrace}");
                DumpProperties(__instance, "PlayerInventorySubComponent");
                DumpProperties(__instance.m_playerAbilitiesStateInfo, "PlayerAbilitiesStateInfo");
            }
        }

        //[HarmonyPatch(typeof(LootManager), "OnPlayerEnterLevel")]
        public class LevelScan_Patch
        {
            static void Postfix(LevelInstance _level)
            {
                TrackLog.Log("(disabled) scene scan skipped");
            }
        }
    }
}