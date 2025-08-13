using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace PartialReservationSystem
{
    /// <summary>
    /// Harmony patches to integrate the custom reservation system
    /// </summary>
    [HarmonyPatch]
    public static class HarmonyPatches_ReservationSystem
    {
        // Per-destination lock map to serialize capacity probes and reservation writes for the same destination
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<PRSReservationSystem.StorageLocation, object> _destLocks
            = new System.Collections.Concurrent.ConcurrentDictionary<PRSReservationSystem.StorageLocation, object>();
        private static object GetDestLock(PRSReservationSystem.StorageLocation loc)
        {
            return _destLocks.GetOrAdd(loc, _ => new object());
        }
        /// <summary>
        /// Clean up reservations when a job ends
        /// </summary>
        [HarmonyPatch(typeof(Pawn_JobTracker), "EndCurrentJob")]
        [HarmonyPostfix]
        public static void EndCurrentJob_Postfix(Pawn_JobTracker __instance, JobCondition condition, bool startNewJob, bool canReturnToPool)
        {
            // Safely access pawn via reflection (Pawn_JobTracker.pawn is protected)
            Pawn pawn = null;
            var pawnField = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");
            if (pawnField != null)
                pawn = pawnField.GetValue(__instance) as Pawn;

            // Access curJob via reflection as well
            Job curJob = null;
            var curJobProp = AccessTools.Property(typeof(Pawn_JobTracker), "curJob");
            if (curJobProp != null)
                curJob = curJobProp.GetValue(__instance) as Job;
            if (curJob == null)
            {
                var curJobField = AccessTools.Field(typeof(Pawn_JobTracker), "curJob");
                if (curJobField != null)
                    curJob = curJobField.GetValue(__instance) as Job;
            }

            if (pawn?.Map == null || curJob == null)
                return;

            // If the job was interrupted (not completed normally), record it as pending
            if (condition != JobCondition.Succeeded && curJob.def == JobDefOf.HaulToCell)
            {
                PendingHaulTracker.RecordPendingHaul(pawn, curJob, pawn.Map);
            }

            PRSReservationSystem.ReleaseAllReservationsForJob(pawn, curJob, pawn.Map);
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

            PRSReservationSystem.ReleaseAllReservationsForPawn(__instance, __instance.Map);
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

            PRSReservationSystem.ReleaseAllReservationsForPawn(__instance, __instance.Map);
        }

        /// <summary>
        /// Periodic cleanup of expired reservations via MapComponent in PRS
        /// </summary>
        [HarmonyPatch(typeof(Map), "FinalizeInit")]
        [HarmonyPostfix]
        public static void FinalizeInit_Postfix(Map __instance)
        {
            PRSReservationSystem.ClearAllReservations(__instance);
        }

        /// <summary>
        /// Intercept CanReserve to allow multiple pawns to reserve the same storage target for the same item
        /// up to partial capacity tracked by PRS.
        /// </summary>
        [HarmonyPatch(typeof(ReservationManager), nameof(ReservationManager.CanReserve))]
        [HarmonyPrefix]
        public static bool CanReserve_Prefix(ref bool __result, Pawn claimant, LocalTargetInfo target, int maxPawns, int stackCount, ReservationLayerDef layer, bool ignoreOtherReservations)
        {
            if (claimant == null || !target.IsValid) return true;
            var map = claimant.Map;
            if (map == null) return true;

            try
            {
                // Identify storage destination candidate
                PRSReservationSystem.StorageLocation location;
                bool isStorageTarget;
                if (target.HasThing)
                {
                    var t = target.Thing;
                    isStorageTarget = t is ISlotGroupParent || t is IHaulDestination;
                    location = new PRSReservationSystem.StorageLocation(t);
                }
                else if (target.Cell.IsValid)
                {
                    isStorageTarget = true;
                    location = new PRSReservationSystem.StorageLocation(target.Cell);
                }
                else
                {
                    return true;
                }

                if (!isStorageTarget)
                    return true;

                // Determine the def being hauled by this claimant to avoid 0-capacity on empty cells.
                Thing probeThing = claimant.carryTracker?.CarriedThing
                                   ?? claimant.CurJob?.targetA.Thing
                                   ?? claimant.CurJob?.targetB.Thing;

                // If still no probe, try queued targets (store destinations frequently live in targetQueueB)
                if (probeThing == null)
                {
                    var qB = claimant.CurJob?.targetQueueB;
                    if (qB != null)
                    {
                        for (int i = 0; i < qB.Count; i++)
                        {
                            var lt = qB[i];
                            if (!lt.IsValid) continue;
                            if (lt.HasThing) { probeThing = lt.Thing; break; }
                        }
                    }
                }

                // Per-destination lock: ensure capacity probes see latest reservations for this location.
                // IMPORTANT: Derive the effective store destination from the current job (B / QueueB) if available,
                // because CanReserve(target=cell) can be invoked on A or other intermediate targets.
                PRSReservationSystem.StorageLocation effectiveDest = location;
                var job = claimant.CurJob;
                if (job != null)
                {
                    if (job.targetB.IsValid)
                    {
                        effectiveDest = job.targetB.HasThing
                            ? new PRSReservationSystem.StorageLocation(job.targetB.Thing)
                            : new PRSReservationSystem.StorageLocation(job.targetB.Cell);
                    }
                    else if (job.targetQueueB != null && job.targetQueueB.Count > 0)
                    {
                        var ltB = job.targetQueueB[0];
                        effectiveDest = ltB.HasThing
                            ? new PRSReservationSystem.StorageLocation(ltB.Thing)
                            : new PRSReservationSystem.StorageLocation(ltB.Cell);
                    }
                }

                int cap;
                var destLock = GetDestLock(effectiveDest);
                if (Settings.EnableDebugLogging)
                {
                    Log.Message($"[DEST-KEY] CanReserve effectiveDest={effectiveDest} hash={effectiveDest.GetHashCode()} [THREAD id={System.Threading.Thread.CurrentThread.ManagedThreadId} ticks={Find.TickManager.TicksGame}]");
                }
                lock (destLock)
                {
                    // new – one canonical bucket for this map+location
                    PRSReservationSystem.StorageReservation reservation =
                        PRSReservationSystem.GetReservationForLocation(effectiveDest, map, createIfMissing: true);

                    // Clean then compute capacity strictly using this exact instance
                    reservation.CleanupExpiredReservations();

                    // Re-resolve probe on-demand to avoid stale nulls
                    var probeForCap = probeThing ?? claimant.carryTracker?.CarriedThing ?? claimant.CurJob?.targetA.Thing ?? claimant.CurJob?.targetB.Thing;

                    // Force GetAvailableCapacity to use only this provided bucket (no fallback)
                    cap = PRSReservationSystem.GetAvailableCapacity(effectiveDest, probeForCap, map, reservation);
                }

                if (Settings.EnableDebugLogging)
                {
                    var locStr = location.IsContainer ? location.Container?.ToString() ?? "null" : location.Cell.ToString();
                    Log.Message($"pawn={claimant?.LabelShort ?? "null"} target={locStr} PRS_cap={cap} pawnHas={PRSReservationSystem.PawnHasReservation(claimant, location, map)}");
                }
                // If this pawn already holds PRS reservation here, allow immediately.
                if (PRSReservationSystem.PawnHasReservation(claimant, location, map))
                {
                    __result = true;
                    return false;
                }

                // If capacity remains, allow another pawn to reserve as well.
                if (cap > 0)
                {
                    __result = true;
                    return false;
                }

                // No capacity: let vanilla decide (likely false)
                return true;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Intercept Reserve to install PRS partial reservations and optionally block vanilla when over capacity.
        /// </summary>
        [HarmonyPatch(typeof(ReservationManager), nameof(ReservationManager.Reserve))]
        [HarmonyPrefix]
        public static bool Reserve_Prefix(ref bool __result, Pawn claimant, LocalTargetInfo target, Job job, int maxPawns = 1, int stackCount = -1, ReservationLayerDef layer = null, bool errorOnFailed = true)
        {
            // Only augment; never block vanilla reservation here to avoid breaking hauling.
            if (claimant?.Map == null || job == null || !target.IsValid)
                return true;

            var map = claimant.Map;

            // IMPORTANT: Always treat 'location' here as the destination bucket (B/QueueB) so all accounting is keyed to the store cell.
            PRSReservationSystem.StorageLocation location;
            if (job.targetB.IsValid)
            {
                location = job.targetB.HasThing
                    ? new PRSReservationSystem.StorageLocation(job.targetB.Thing)
                    : new PRSReservationSystem.StorageLocation(job.targetB.Cell);
            }
            else if (job.targetQueueB != null && job.targetQueueB.Count > 0)
            {
                var ltB = job.targetQueueB[0];
                location = ltB.HasThing
                    ? new PRSReservationSystem.StorageLocation(ltB.Thing)
                    : new PRSReservationSystem.StorageLocation(ltB.Cell);
            }
            else if (target.Cell.IsValid)
            {
                location = new PRSReservationSystem.StorageLocation(target.Cell);
            }
            else if (target.HasThing)
            {
                location = new PRSReservationSystem.StorageLocation(target.Thing);
            }
            else
                return true;

            // Determine source thing from job.targetA first; fallback to carriedThing or targetB
            Thing sourceThing = job.targetA.Thing
                               ?? claimant.carryTracker?.CarriedThing
                               ?? job.targetB.Thing;

            // If job.targetA.Thing is null, probe the cell for a plausible stack
            if (sourceThing == null && job.targetA.Cell.IsValid && claimant.Map != null)
            {
                var cell = job.targetA.Cell;
                var list = cell.GetThingList(claimant.Map);
                for (int i = 0; i < list.Count; i++)
                {
                    var th = list[i];
                    if (th != null && th.def != null && th.stackCount > 0)
                    {
                        sourceThing = th;
                        break;
                    }
                }
            }

            if (sourceThing == null)
                return true; // nothing to account; let vanilla proceed

            // Acquire and use the SAME reservation instance for capacity computation and mutation to ensure immediate consistency
            int available = 0;

            // Compute pawn's immediate pickup intent using vanilla-correct sources
            int jobCount = job.count;
            var carryTracker = claimant.carryTracker;
            var carriedThing = carryTracker?.CarriedThing;
            int stackLimit = sourceThing.def?.stackLimit ?? 0;

            // Available stack space from carry tracker for this def (when available)
            int availableStackSpace = 0;
            if (carryTracker != null && sourceThing.def != null)
            {
                try { availableStackSpace = carryTracker.AvailableStackSpace(sourceThing.def); }
                catch { availableStackSpace = 0; }
            }

            int intendedCount;
            if (carriedThing != null && carriedThing.def == sourceThing.def)
            {
                // Top-off carried stack
                int remainingInCarried = System.Math.Max(0, stackLimit - carriedThing.stackCount);
                intendedCount = System.Math.Min(sourceThing.stackCount, remainingInCarried);
            }
            else
            {
                // Empty-handed: immediate pickup is clamped by AvailableStackSpace (vanilla)
                int cap = availableStackSpace > 0 ? availableStackSpace : stackLimit;
                intendedCount = System.Math.Min(sourceThing.stackCount, cap);
            }

            // Cap by job.count only after carry-cap to reflect immediate pickup semantics
            if (jobCount > 0)
            {
                intendedCount = System.Math.Min(intendedCount, jobCount);
            }

            // If the Reserve() call provided an explicit stackCount arg, treat it as an upper bound.
            if (stackCount > 0)
            {
                intendedCount = System.Math.Min(intendedCount, stackCount);
            }

            // Destination used for all capacity checks and reservation recording
            PRSReservationSystem.StorageLocation effectiveDest = location;

            // We'll compute any already-reserved amount for this pawn+job to avoid duplicate bookings
            int alreadyReservedByPawnJobForDef = 0;

            var destLock = GetDestLock(effectiveDest);
            if (Settings.EnableDebugLogging)
            {
                Log.Message($"[LOCK] Reserve acquiring for effectiveDest={effectiveDest} hash={effectiveDest.GetHashCode()} [THREAD id={System.Threading.Thread.CurrentThread.ManagedThreadId} ticks={Find.TickManager.TicksGame}]");
            }
            lock (destLock)
            {
                // new – one canonical bucket for this map+location
                PRSReservationSystem.StorageReservation reservation =
                    PRSReservationSystem.GetReservationForLocation(effectiveDest, map, createIfMissing: true);

                // Clean old entries then compute available using this exact instance to instantly see our own writes
                reservation.CleanupExpiredReservations();

                // Compute remaining capacity strictly for the destination using this bucket
                int totalAvail = PRSReservationSystem.GetAvailableCapacity(effectiveDest, sourceThing, map, reservation);
                available = System.Math.Max(0, totalAvail); // already subtracts PRS reservations

                // Estimate already-reserved by this pawn+job for same def to avoid double-book by same pawn/job
                var entriesField = AccessTools.Field(typeof(PRSReservationSystem.StorageReservation), "_reservations");
                var dict = (System.Collections.IDictionary)entriesField.GetValue(reservation);
                if (dict != null && claimant != null && dict.Contains(claimant))
                {
                    var list = (System.Collections.IEnumerable)dict[claimant];
                    foreach (var entry in list)
                    {
                        var entryThing = (Thing)AccessTools.Property(typeof(PRSReservationSystem.StorageReservation.ReservationEntry), "Thing").GetValue(entry);
                        var entryJob = (Job)AccessTools.Property(typeof(PRSReservationSystem.StorageReservation.ReservationEntry), "Job").GetValue(entry);
                        if (entryJob == job && entryThing?.def == sourceThing.def)
                        {
                            var entryCount = (int)AccessTools.Property(typeof(PRSReservationSystem.StorageReservation.ReservationEntry), "Count").GetValue(entry);
                            alreadyReservedByPawnJobForDef += entryCount;
                        }
                    }
                }

                // Compute how much more we intend to reserve beyond what's already reserved (avoid double-book by same pawn/job)
                int remainingIntended = System.Math.Max(0, intendedCount - alreadyReservedByPawnJobForDef);

                // Enforce single-stack destination: capacity cannot exceed remaining to fill to one stack of this def at the destination
                int stackLimitForDef = sourceThing.def?.stackLimit ?? stackCount;
                if (stackLimitForDef > 0)
                {
                    // remainingToFillStack based on current bucket's def total
                    int bucketReservedForDef = reservation.GetReservedCountForThingDef(sourceThing.def);
                    int remainingToFillStack = System.Math.Max(0, stackLimitForDef - bucketReservedForDef);
                    if (available > remainingToFillStack)
                        available = remainingToFillStack;
                }

                // Final amount to record this call, computed strictly from values derived under this same lock
                int toReserve = System.Math.Max(0, System.Math.Min(remainingIntended, available));

                if (Settings.EnableDebugLogging)
                {
                    var locStrDbg = location.IsContainer ? location.Container?.ToString() ?? "null" : location.Cell.ToString();
                    var destStr = effectiveDest.IsContainer ? effectiveDest.Container?.ToString() ?? "null" : effectiveDest.Cell.ToString();
                    int reservedBefore = reservation.GetReservedCountForThingDef(sourceThing.def);
                    Log.Message($"pawn={claimant?.LabelShort ?? "null"} target={locStrDbg} dest={destStr} thing={sourceThing.def.defName} intended={intendedCount} alreadyByPawnJob={alreadyReservedByPawnJobForDef} availableAtDest={available} toReserve={toReserve} reservedBefore={reservedBefore}");
                }

                if (toReserve > 0)
                {
                    // Enforce at most one outstanding destination reservation per pawn+job+def at the effective destination.
                    bool alreadyHasOutstanding = false;
                    var entriesField2 = AccessTools.Field(typeof(PRSReservationSystem.StorageReservation), "_reservations");
                    var dict2 = (System.Collections.IDictionary)entriesField2.GetValue(reservation);
                    if (dict2 != null && claimant != null && dict2.Contains(claimant))
                    {
                        var list2 = (System.Collections.IEnumerable)dict2[claimant];
                        foreach (var entry in list2)
                        {
                            var entryThing = (Thing)AccessTools.Property(typeof(PRSReservationSystem.StorageReservation.ReservationEntry), "Thing").GetValue(entry);
                            var entryJob = (Job)AccessTools.Property(typeof(PRSReservationSystem.StorageReservation.ReservationEntry), "Job").GetValue(entry);
                            if (entryJob == job && entryThing?.def == sourceThing.def)
                            {
                                alreadyHasOutstanding = true;
                                break;
                            }
                        }
                    }

                    if (!alreadyHasOutstanding)
                    {
                        // Recompute available immediately before commit as a last-line defense against cross-call drift,
                        // still within the same lock and same bucket.
                        int sanityAvail = PRSReservationSystem.GetAvailableCapacity(effectiveDest, sourceThing, map, reservation);
                        int sanityBucketForDef = reservation.GetReservedCountForThingDef(sourceThing.def);
                        int sanityRemainingToFill = stackLimit > 0 ? System.Math.Max(0, stackLimit - sanityBucketForDef) : sanityAvail;
                        int finalToReserve = System.Math.Max(0, System.Math.Min(toReserve, System.Math.Min(sanityAvail, sanityRemainingToFill)));

                        if (finalToReserve > 0)
                        {
                            Log.Warning($"[PRS DEBUG] BEFORE ADD  pawn={claimant.LabelShort} dest={effectiveDest} alreadyByPawn={alreadyReservedByPawnJobForDef} totalBefore={reservation.GetReservedCountForThingDef(sourceThing.def)}");
                            var added = reservation.TryAddReservation(claimant, sourceThing, finalToReserve, job);
                            if (Settings.EnableDebugLogging)
                            {
                                var locStr = effectiveDest.IsContainer ? effectiveDest.Container?.ToString() ?? "null" : effectiveDest.Cell.ToString();
                                int reservedAfter = reservation.GetReservedCountForThingDef(sourceThing.def);
                                Log.Message(added
                                    ? $"Recorded partial reservation at {locStr} for {sourceThing.def.defName} x{finalToReserve} (reservedAfter={reservedAfter})"
                                    : $"Failed to record partial reservation at {locStr} for {sourceThing.def.defName} x{finalToReserve}");
                            }
                        }
                    }
                }
            }

            // Always allow vanilla to proceed
            return true;
        }
        
        /// <summary>
        /// Track release to clean PRS state when vanilla releases reservations for a target.
        /// </summary>
        [HarmonyPatch(typeof(ReservationManager), nameof(ReservationManager.Release))]
        [HarmonyPostfix]
        public static void Release_Postfix(ReservationManager __instance, LocalTargetInfo target, Pawn claimant, Job job)
        {
            try
            {
                // Access ReservationManager.map non-public field safely
                var mapField = AccessTools.Field(typeof(ReservationManager), "map");
                var map = mapField != null ? (Map)mapField.GetValue(__instance) : null;
                if (map == null || claimant == null) return;

                PRSReservationSystem.StorageLocation location;
                if (target.Cell.IsValid)
                    location = new PRSReservationSystem.StorageLocation(target.Cell);
                else if (target.HasThing)
                    location = new PRSReservationSystem.StorageLocation(target.Thing);
                else
                    return;

                PRSReservationSystem.ReleaseAllReservationsForJob(claimant, job, map);
            }
            catch { /* defensive */ }
        }
    }
}


