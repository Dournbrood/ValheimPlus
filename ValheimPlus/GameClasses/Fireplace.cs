﻿using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using ValheimPlus.Configurations;

namespace ValheimPlus.GameClasses
{
    class FireplaceFuel
    {
        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Awake))]
        public static class Fireplace_Awake_Patch
        {
            /// <summary>
            /// When fire source is loaded in view, check for configurations and set its start fuel and current fuel to max fuel
            /// </summary>
            private static void Postfix(ref Fireplace __instance)
            {
                if (!Configuration.Current.FireSource.IsEnabled || !__instance.m_nview || __instance.m_nview.m_zdo == null) return;

                if (FireplaceExtensions.IsTorch(__instance.m_nview.GetPrefabName()))
                {
                    if (Configuration.Current.FireSource.torches)
                    {
                        __instance.m_startFuel = __instance.m_maxFuel;
                        __instance.m_nview.GetZDO().Set("fuel", __instance.m_maxFuel);
                    }
                }
                else if (Configuration.Current.FireSource.fires)
                {
                    __instance.m_startFuel = __instance.m_maxFuel;
                    __instance.m_nview.GetZDO().Set("fuel", __instance.m_maxFuel);
                }
            }
        }

        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.GetTimeSinceLastUpdate))]
        public static class Fireplace_GetTimeSinceLastUpdate_Patch
        {
            /// <summary>
            /// If fire source is configured to keep fire source lit, reset time since being lit to 0
            /// </summary>
            private static void Postfix(ref double __result, ref Fireplace __instance)
            {
                if (!Configuration.Current.FireSource.IsEnabled) return;

                if (FireplaceExtensions.IsTorch(__instance.m_name))
                {
                    if (Configuration.Current.FireSource.torches)
                    {
                        __result = 0.0;
                    }
                }
                else if (Configuration.Current.FireSource.fires)
                {
                    __result = 0.0;
                }
            }
        }

        [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.UpdateFireplace))]
        public static class Fireplace_UpdateFireplace_Transpiler
        {
            private static MethodInfo method_ZNetView_IsOwner = AccessTools.Method(typeof(ZNetView), nameof(ZNetView.IsOwner));
            private static MethodInfo method_addFuelFromNearbyChests = AccessTools.Method(typeof(Fireplace_UpdateFireplace_Transpiler), nameof(Fireplace_UpdateFireplace_Transpiler.AddFuelFromNearbyChests));

            [HarmonyTranspiler]
            public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                if (!Configuration.Current.FireSource.IsEnabled || !Configuration.Current.FireSource.autoFuel) return instructions;

                List<CodeInstruction> il = instructions.ToList();

                for (int i = 0; i < il.Count; i++)
                {
                    if (il[i].Calls(method_ZNetView_IsOwner))
                    {
                        ++i;
                        il.Insert(++i, new CodeInstruction(OpCodes.Ldarg_0));
                        il.Insert(++i, new CodeInstruction(OpCodes.Call, method_addFuelFromNearbyChests));

                        return il.AsEnumerable();
                    }
                }

                ValheimPlusPlugin.Logger.LogError("Failed to apply Fireplace_UpdateFireplace_Transpiler");

                return instructions;
            }

            private static void AddFuelFromNearbyChests(Fireplace __instance)
            {
                int toMaxFuel = (int)__instance.m_maxFuel - (int)Math.Ceiling(__instance.m_nview.GetZDO().GetFloat("fuel"));

                if (toMaxFuel > 0)
                {
                    Stopwatch delta = GameObjectAssistant.GetStopwatch(__instance.gameObject);

                    if (delta.IsRunning && delta.ElapsedMilliseconds < 1000) return;
                    delta.Restart();

                    ItemDrop.ItemData fuelItemData = __instance.m_fuelItem.m_itemData;

                    int addedFuel = InventoryAssistant.RemoveItemInAmountFromAllNearbyChests(__instance.gameObject, Helper.Clamp(Configuration.Current.FireSource.autoRange, 1, 50), fuelItemData, toMaxFuel, !Configuration.Current.FireSource.ignorePrivateAreaCheck);
                    __instance.m_nview.InvokeRPC("RPC_AddFuelAmount", new object[] { (float) addedFuel });
                    if (addedFuel > 0)
                        ValheimPlusPlugin.Logger.LogInfo("Added " + addedFuel + " fuel(" + fuelItemData.m_shared.m_name + ") in " + __instance.m_name);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Fireplace), nameof(Fireplace.Interact))]
    public static class Fireplace_Interact_Transpiler
    {
        private static List<Container> nearbyChests = null;

        private static MethodInfo method_Inventory_HaveItem = AccessTools.Method(typeof(Inventory), nameof(Inventory.HaveItem));
        private static MethodInfo method_ReplaceInventoryRefByChest = AccessTools.Method(typeof(Fireplace_Interact_Transpiler), nameof(Fireplace_Interact_Transpiler.ReplaceInventoryRefByChest));

        /// <summary>
        /// Patches out the code that looks for fuel item.
        /// When no fuel item has been found in the player inventory, check inside nearby chests.
        /// If found, replace the reference to the player Inventory by the one from the chest.
        /// </summary>
        [HarmonyTranspiler]
        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            if (!Configuration.Current.CraftFromChest.IsEnabled) return instructions;

            List<CodeInstruction> il = instructions.ToList();

            for (int i = 0; i < il.Count; i++)
            {
                if (il[i].Calls(method_Inventory_HaveItem))
                {

                    // replace call to `inventory.HaveItem(this.m_fuelItem.m_itemData.m_shared.m_name, true)`
                    // with call to `Fireplace_Interact_Transpiler.ReplaceInventoryRefByChest(ref inventory, this)`.
                    il[i - 7] = new CodeInstruction(OpCodes.Ldloca_S, 0).MoveLabelsFrom(il[i - 7]);
                    il[i] = new CodeInstruction(OpCodes.Call, method_ReplaceInventoryRefByChest);
                    il.RemoveRange(i - 5, 5);
                    return il.AsEnumerable();
                }
            }

            ValheimPlusPlugin.Logger.LogError("Failed to apply Fireplace_Interact_Transpiler");

            return instructions;
        }

        private static bool ReplaceInventoryRefByChest(ref Inventory inventory, Fireplace fireplace)
        {
            string itemName = fireplace.m_fuelItem.m_itemData.m_shared.m_name;
            if (inventory.HaveItem(itemName)) return true; // original code

            Stopwatch delta = GameObjectAssistant.GetStopwatch(fireplace.gameObject);
            int lookupInterval = Helper.Clamp(Configuration.Current.CraftFromChest.lookupInterval, 1, 10) * 1000;
            if (!delta.IsRunning || delta.ElapsedMilliseconds > lookupInterval)
            {
                nearbyChests = InventoryAssistant.GetNearbyChests(fireplace.gameObject, Helper.Clamp(Configuration.Current.CraftFromChest.range, 1, 50), !Configuration.Current.CraftFromChest.ignorePrivateAreaCheck);
                delta.Restart();
            }

            foreach (Container c in nearbyChests)
            {
                if (c.GetInventory().HaveItem(itemName))
                {
                    inventory = c.GetInventory();
                    return true;
                }
            }

            return false;
        }
    }

    public static class FireplaceExtensions
    {
        static readonly string[] torchItemNames = new[]
        {
            "piece_groundtorch_wood", // standing wood torch
            "piece_groundtorch", // standing iron torch
            "piece_groundtorch_green", // standing green torch
            "piece_groundtorch_blue", // standing blue torch
            "piece_walltorch", // sconce torch
            "piece_brazierceiling01", // ceiling brazier
            "piece_brazierfloor01", // standing brazier
            "piece_jackoturnip" // Jack-o-turnip
        };

        internal static bool IsTorch(string itemName)
        {
            return torchItemNames.Any(x => x.Equals(itemName));
        }
    }
}
