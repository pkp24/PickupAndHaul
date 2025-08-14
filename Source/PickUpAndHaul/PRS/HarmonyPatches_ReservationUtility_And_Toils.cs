using HarmonyLib;
using RimWorld;
using System;
using System.Linq;
using System.Reflection;
using Verse;
using Verse.AI;
using System.Collections.Generic;
using PartialReservationSystem;

namespace PartialReservationSystem
{
    [HarmonyPatch]
    public static class HarmonyPatches_ReservationUtility_And_Toils
    {
        // KEEP: Core clamp
        internal static void PRS_TryPrePickupClamp(Pawn pawn, Job job, TargetIndex ind)
        {
            if (pawn == null || job == null) return;
            if (pawn.Map == null) return;

            if (job.def != JobDefOf.HaulToCell && job.haulMode != HaulMode.ToCellStorage)
            {
                return;
            }

            var tgtTouch = job.GetTarget(ind);
            if (tgtTouch.IsValid && tgtTouch.HasThing)
            {
                if (!pawn.Position.InHorDistOf(tgtTouch.Thing.Position, 1.5f))
                {
                    return;
                }
            }

            var tgt = job.GetTarget(ind);
            var srcThing = tgt.Thing;
            if (srcThing == null || srcThing.Destroyed) return;

            PRSReservationSystem.StorageLocation? maybeDest = null;
            if (job.targetB.IsValid)
            {
                maybeDest = job.targetB.HasThing
                    ? new PRSReservationSystem.StorageLocation(job.targetB.Thing)
                    : new PRSReservationSystem.StorageLocation(job.targetB.Cell, pawn.Map);
            }
            else if (job.targetQueueB != null && job.targetQueueB.Count > 0)
            {
                var lt = job.targetQueueB[0];
                maybeDest = lt.HasThing
                    ? new PRSReservationSystem.StorageLocation(lt.Thing)
                    : new PRSReservationSystem.StorageLocation(lt.Cell, pawn.Map);
            }
            if (maybeDest == null) return;

            var destLocation = maybeDest.Value;

            // Always get the canonical bucket (createIfMissing = true so we never get a second one)
            var bucket = PRSReservationSystem.GetReservationForLocation(destLocation, pawn.Map, createIfMissing: true);

            // How many are still free in *this* bucket?
            int remaining = PRSReservationSystem.GetAvailableCapacity(destLocation, srcThing, pawn.Map, bucket);

            int alreadyByPawn = bucket.GetReservedCountForPawn(pawn, srcThing.def);

            Log.Message(
                $"[PRS] PrePickupClamp probe actor={pawn.LabelShort} job={job.def.defName} " +
                $"src={srcThing.def?.defName} count={srcThing.stackCount} dest={destLocation} " +
                $"remaining={remaining} reservedByPawn={alreadyByPawn}");
            if (remaining <= 0 && alreadyByPawn <= 0) return;

            int haulAmount = PRSReservationSystem.CalculateHaulAmount(pawn, srcThing);
            int cap = System.Math.Min(remaining + alreadyByPawn, haulAmount);
            if (cap <= 0) return;

            if (srcThing.stackCount > cap)
            {
                int leaveForNow = srcThing.stackCount - cap;
                if (leaveForNow > 0)
                {
                    try
                    {
                        var split = srcThing.SplitOff(leaveForNow);
                        GenPlace.TryPlaceThing(split, srcThing.Position, pawn.Map, ThingPlaceMode.Near);
                        Log.Info($"[PRS] PrePickupClamp split excess={leaveForNow} at {srcThing.Position}; cap={cap}");
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error($"PrePickupClamp split exception: {ex}");
                    }
                }
            }
            
            // NOTE: Auto-release on pickup behavior has been removed.
            // Reservations are now held until drop-off via PlaceHauledThingInCell patch.
        }

        // KEEP: TakeToInventory overloads to invoke clamp at pickup
        [HarmonyPatch]
        public static class Toils_Haul_TakeToInventory_Int_Postfix
        {
            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(Toils_Haul), "TakeToInventory", new[] { typeof(TargetIndex), typeof(int) });
            }

            [HarmonyPrepare]
            public static bool Prepare()
            {
                var m = TargetMethod();
                Log.Info($"[PRS] PATCH PREPARE: Toils_Haul.TakeToInventory(TargetIndex,int) method={(m?.DeclaringType?.FullName + "." + m?.Name ?? "null")} ");
                return m != null;
            }

            [HarmonyPostfix]
            public static void Postfix(ref Toil __result, TargetIndex ind, int count)
            {
                if (__result == null) return;
                var toil = __result;
                var originalInit = toil.initAction;
                toil.initAction = () =>
                {
                    try
                    {
                        var actor = toil.actor;
                        Log.Info($"[PRS] TakeToInventory(int) INIT actor={actor?.LabelShort ?? "null"} ind={ind} count={count}");
                        PRS_TryPrePickupClamp(actor, actor?.CurJob, ind);
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error($"PRS TakeToInventory(int) init exception: {ex}");
                    }
                    originalInit?.Invoke();
                };
            }
        }

        [HarmonyPatch]
        public static class Toils_Haul_TakeToInventory_FuncInt_Postfix
        {
            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(Toils_Haul), "TakeToInventory", new[] { typeof(TargetIndex), typeof(System.Func<int>) });
            }

            [HarmonyPrepare]
            public static bool Prepare()
            {
                var m = TargetMethod();
                Log.Info($"[PRS] PATCH PREPARE: Toils_Haul.TakeToInventory(TargetIndex,Func<int>) method={(m?.DeclaringType?.FullName + "." + m?.Name ?? "null")} ");
                return m != null;
            }

            [HarmonyPostfix]
            public static void Postfix(ref Toil __result, TargetIndex ind, System.Func<int> countGetter)
            {
                if (__result == null) return;
                var toil = __result;
                var originalInit = toil.initAction;
                toil.initAction = () =>
                {
                    try
                    {
                        var actor = toil.actor;
                        Log.Info($"[PRS] TakeToInventory(Func<int>) INIT actor={actor?.LabelShort ?? "null"} ind={ind} count={(countGetter != null ? countGetter() : -1)}");
                        PRS_TryPrePickupClamp(actor, actor?.CurJob, ind);
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error($"PRS TakeToInventory(Func<int>) init exception: {ex}");
                    }
                    originalInit?.Invoke();
                };
            }
        }
        
        [HarmonyPatch]
        public static class Toils_Haul_TakeToInventory_FuncThingInt_Postfix
        {
            static MethodBase TargetMethod()
            {
                return AccessTools.Method(typeof(Toils_Haul), "TakeToInventory", new[] { typeof(TargetIndex), typeof(System.Func<Thing,int>) });
            }
            
            [HarmonyPrepare]
            public static bool Prepare()
            {
                var m = TargetMethod();
                Log.Info($"[PRS] PATCH PREPARE: Toils_Haul.TakeToInventory(TargetIndex,Func<Thing,int>) method={(m?.DeclaringType?.FullName + "." + m?.Name ?? "null")} ");
                return m != null;
            }

            [HarmonyPostfix]
            public static void Postfix(ref Toil __result, TargetIndex ind, System.Func<Thing,int> countGetter)
            {
                if (__result == null) return;
                var toil = __result;
                var originalInit = toil.initAction;
                toil.initAction = () =>
                {
                    try
                    {
                        var actor = toil.actor;
                        Log.Info($"[PRS] TakeToInventory(Func<Thing,int>) INIT actor={actor?.LabelShort ?? "null"} ind={ind}");
                        PRS_TryPrePickupClamp(actor, actor?.CurJob, ind);
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error($"PRS TakeToInventory(Func<Thing,int>) init exception: {ex}");
                    }
                    originalInit?.Invoke();
                };
            }
        }

        // KEEP: Fallback at arrival to source
        [HarmonyPatch(typeof(Toils_Goto), nameof(Toils_Goto.GotoThing), new[] { typeof(TargetIndex), typeof(PathEndMode), typeof(bool) })]
        public static class Toils_Goto_GotoThing_PostfixInjector
        {
            [HarmonyPostfix]
            public static void Postfix(ref Toil __result, TargetIndex ind, PathEndMode peMode, bool canGotoSpawnedParent)
            {
                if (__result == null) return;

                var toil = __result;
                toil.finishActions ??= new System.Collections.Generic.List<System.Action>();
                toil.finishActions.Insert(0, () =>
                {
                    try
                    {
                        var actor = toil.actor;
                        Log.Info($"[PRS] GotoThing finishAction actor={actor?.LabelShort ?? "null"} pos={actor?.Position.ToString() ?? "null"} A={actor?.CurJob?.targetA} B={actor?.CurJob?.targetB}");
                        PRS_TryPrePickupClamp(actor, actor?.CurJob, ind);
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error($"PrePickupClamp GotoThing finishAction exception: {ex}");
                    }
                });

                toil.preTickActions ??= new System.Collections.Generic.List<System.Action>();
                toil.preTickActions.Insert(0, () =>
                {
                    try
                    {
                        var actor = toil.actor;
                        Log.Info($"[PRS] GotoThing preTick actor={actor?.LabelShort ?? "null"} pos={actor?.Position.ToString() ?? "null"} pathing={actor?.pather?.Moving.ToString() ?? "null"}");
                        PRS_TryPrePickupClamp(actor, actor?.CurJob, ind);
                    }
                    catch (System.Exception ex)
                    {
                        Log.Error($"PrePickupClamp GotoThing preTick exception: {ex}");
                    }
                });
            }
        }
    }
}


