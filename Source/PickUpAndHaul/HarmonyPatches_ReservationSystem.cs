using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PickUpAndHaul
{
    /// <summary>
    /// Harmony patches to integrate the custom reservation system
    /// </summary>
    [HarmonyPatch]
    public static class HarmonyPatches_ReservationSystem
    {
        /// <summary>
        /// Clean up reservations when a job ends
        /// </summary>
        [HarmonyPatch(typeof(Pawn_JobTracker), "EndCurrentJob")]
        [HarmonyPostfix]
        public static void EndCurrentJob_Postfix(Pawn_JobTracker __instance, JobCondition condition, bool startNewJob, bool canReturnToPool)
        {
            var pawn = __instance.pawn;
            if (pawn?.Map == null || __instance.curJob == null)
                return;

            // Release all PUAH reservations for this job
            PUAHReservationSystem.ReleaseAllReservationsForJob(pawn, __instance.curJob, pawn.Map);
        }

        /// <summary>
        /// Clean up reservations when a pawn is despawned
        /// </summary>
        [HarmonyPatch(typeof(Pawn), "DeSpawn")]
        [HarmonyPrefix]
        public static void DeSpawn_Prefix(Pawn __instance)
        {
            if (__instance?.Map == null)
                return;

            // Release all PUAH reservations for this pawn
            PUAHReservationSystem.ReleaseAllReservationsForPawn(__instance, __instance.Map);
        }

        /// <summary>
        /// Clean up reservations when a pawn dies
        /// </summary>
        [HarmonyPatch(typeof(Pawn), "Kill")]
        [HarmonyPostfix]
        public static void Kill_Postfix(Pawn __instance)
        {
            if (__instance?.Map == null)
                return;

            // Release all PUAH reservations for this pawn
            PUAHReservationSystem.ReleaseAllReservationsForPawn(__instance, __instance.Map);
        }

        /// <summary>
        /// Periodic cleanup of expired reservations
        /// </summary>
        [HarmonyPatch(typeof(Map), "MapPostTick")]
        [HarmonyPostfix]
        public static void MapPostTick_Postfix(Map __instance)
        {
            // Clean up expired reservations every 5 seconds
            if (Find.TickManager.TicksGame % 300 == 0)
            {
                PUAHReservationSystem.CleanupExpiredReservations(__instance);
            }
        }

        /// <summary>
        /// Clear all reservations when a map is removed
        /// </summary>
        [HarmonyPatch(typeof(Map), "FinalizeInit")]
        [HarmonyPostfix]
        public static void FinalizeInit_Postfix(Map __instance)
        {
            // Clear any stale reservations when map is initialized
            PUAHReservationSystem.ClearAllReservations(__instance);
        }

        /// <summary>
        /// Prevent vanilla reservation conflicts by checking our system first
        /// </summary>
        [HarmonyPatch(typeof(ReservationManager), "CanReserve")]
        [HarmonyPostfix]
        public static void CanReserve_Postfix(ref bool __result, Pawn claimant, LocalTargetInfo target, int maxPawns, int stackCount, ReservationLayerDef layer, bool ignoreOtherReservations)
        {
            // If vanilla says we can't reserve, respect that
            if (!__result)
                return;

            // If this is a PUAH job, check our reservation system too
            if (claimant?.CurJob?.def == PickUpAndHaulJobDefOf.HaulToInventory ||
                claimant?.CurJob?.def == PickUpAndHaulJobDefOf.UnloadYourHauledInventory)
            {
                // Check if the target is a cell or thing
                if (target.HasThing && target.Thing is ISlotGroupParent)
                {
                    // This is a storage building, let vanilla handle it
                    return;
                }

                PUAHReservationSystem.StorageLocation location;
                if (target.Cell.IsValid)
                {
                    location = new PUAHReservationSystem.StorageLocation(target.Cell);
                }
                else if (target.HasThing)
                {
                    location = new PUAHReservationSystem.StorageLocation(target.Thing);
                }
                else
                {
                    return;
                }

                // Check available capacity in our system
                var availableCapacity = PUAHReservationSystem.GetAvailableCapacity(location, null, claimant.Map);
                
                // If no capacity available, we can't reserve
                if (availableCapacity <= 0)
                {
                    __result = false;
                }
            }
        }
    }
} 