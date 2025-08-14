using HarmonyLib;
using RimWorld;
using System;
using Verse;
using Verse.AI;

namespace PartialReservationSystem
{
    /// <summary>
    /// Patches for HaulAIUtility and StoreUtility to consider pending hauls and PRS capacity
    /// </summary>
    [HarmonyPatch]
    public static class HarmonyPatches_HaulAI
    {
        [HarmonyPatch(typeof(HaulAIUtility), nameof(HaulAIUtility.HaulToStorageJob))]
        public static class HarmonyPatches_HaulAI_HaulToStorageJob
        {
            [HarmonyPostfix]
            public static void Postfix(ref Job __result, Pawn p, Thing t)
            {
                if (__result == null || p == null || t == null || p.Map == null)
                    return;

                // Check if this is a hauling job to a cell storage
                if (__result.def != JobDefOf.HaulToCell || !__result.targetB.IsValid)
                    return;

                PRSReservationSystem.StorageLocation? destLocation = null;
                if (__result.targetB.HasThing)
                {
                    destLocation = new PRSReservationSystem.StorageLocation(__result.targetB.Thing);
                }
                else if (__result.targetB.Cell.IsValid)
                {
                    destLocation = new PRSReservationSystem.StorageLocation(__result.targetB.Cell);
                }

                if (!destLocation.HasValue)
                    return;

                // Check available capacity including pending hauls
                int availableCapacity = PRSReservationSystem.GetAvailableCapacity(destLocation.Value, t, p.Map);
                
                if (availableCapacity <= 0)
                {
                    // No space available, cancel the job
                    Log.Message($"[PRS] HaulToStorageJob: Cancelling job for {p.LabelShort} hauling {t.def.defName} to {destLocation.Value} - no available capacity (pending hauls considered)");
                    PickUpAndHaul.Log.Message($"PRS.Postfix HaulToStorageJob CANCEL: pawn={p.LabelShort} thing={t} dest={destLocation.Value}");
                    __result = null;
                    return;
                }

                // If there's limited space, adjust the job count
                int haulAmount = PRSReservationSystem.CalculateHaulAmount(p, t);
                if (availableCapacity < haulAmount)
                {
                    __result.count = Math.Min(availableCapacity, t.stackCount);
                    Log.Message($"[PRS] HaulToStorageJob: Adjusted job count for {p.LabelShort} hauling {t.def.defName} to {destLocation.Value} - count={__result.count} (was {haulAmount})");
                    PickUpAndHaul.Log.Message($"PRS.Postfix HaulToStorageJob ADJUST: pawn={p.LabelShort} thing={t} dest={destLocation.Value} count={__result.count} was={haulAmount}");
                }
            }
        }

        [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor))]
        public static class HarmonyPatches_StoreUtility_TryFindBestBetterStoreCellFor
        {
            [HarmonyPostfix]
            public static void Postfix(ref bool __result, Thing t, Pawn carrier, Map map, ref IntVec3 foundCell)
            {
                if (!__result || !foundCell.IsValid || t == null || map == null)
                    return;

                // Check if the found cell has enough capacity considering pending hauls
                var destLocation = new PRSReservationSystem.StorageLocation(foundCell);
                int availableCapacity = PRSReservationSystem.GetAvailableCapacity(destLocation, t, map);

                if (availableCapacity <= 0)
                {
                    // No space available at this cell, mark as not found
                    Log.Message($"[PRS] TryFindBestBetterStoreCellFor: Cell {foundCell} has no available capacity for {t.def.defName} (pending hauls considered)");
                    __result = false;
                    foundCell = IntVec3.Invalid;
                }
            }
        }
    }
}


