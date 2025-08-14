using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace PartialReservationSystem
{
    /// <summary>
    /// Tracks pending haul jobs that were interrupted (e.g., by drafting)
    /// to prevent over-allocation when multiple pawns target the same destination
    /// </summary>
    public static class PendingHaulTracker
    {
        private static readonly Dictionary<Map, Dictionary<PRSReservationSystem.StorageLocation, List<PendingHaul>>> _pendingHauls = new();
        private static readonly object _lock = new();
        
        // Expire pending hauls after 2500 ticks (about 1 in-game hour)
        private const int ExpirationTicks = 2500;
        
        public class PendingHaul
        {
            public Pawn Pawn { get; }
            public ThingDef ThingDef { get; }
            public int Count { get; }
            public int CreatedTick { get; }
            public Job OriginalJob { get; }
            
            public PendingHaul(Pawn pawn, ThingDef thingDef, int count, Job job)
            {
                Pawn = pawn;
                ThingDef = thingDef;
                Count = count;
                CreatedTick = Find.TickManager.TicksGame;
                OriginalJob = job;
            }
            
            public bool IsExpired => Find.TickManager.TicksGame - CreatedTick > ExpirationTicks;
            
            public bool IsValid => Pawn != null && !Pawn.Dead && !Pawn.Destroyed && Pawn.Spawned;
        }
        
        /// <summary>
        /// Record a pending haul when a job is interrupted
        /// </summary>
        public static void RecordPendingHaul(Pawn pawn, Job job, Map map)
        {
            if (pawn == null || job == null || map == null)
                return;
                
            // Only track HaulToCell jobs
            if (job.def != JobDefOf.HaulToCell)
                return;
                
            // Extract haul details from the job
            Thing sourceThing = job.targetA.Thing;
            if (sourceThing == null || sourceThing.def == null)
                return;
                
            // Determine destination
            PRSReservationSystem.StorageLocation destination;
            if (job.targetB.IsValid)
            {
                destination = job.targetB.HasThing
                    ? new PRSReservationSystem.StorageLocation(job.targetB.Thing)
                    : new PRSReservationSystem.StorageLocation(job.targetB.Cell, map);
            }
            else if (job.targetQueueB != null && job.targetQueueB.Count > 0)
            {
                var ltB = job.targetQueueB[0];
                destination = ltB.HasThing
                    ? new PRSReservationSystem.StorageLocation(ltB.Thing)
                    : new PRSReservationSystem.StorageLocation(ltB.Cell, map);
            }
            else
            {
                return; // No valid destination
            }
            
            // Get the actual reserved amount for this pawn/job/destination
            int haulAmount = PRSReservationSystem.GetReservedCountForPawnJob(pawn, job, destination, sourceThing.def, map);
            if (haulAmount <= 0)
                return;
                
            lock (_lock)
            {
                // Get or create map dictionary
                if (!_pendingHauls.TryGetValue(map, out var mapPending))
                {
                    mapPending = new Dictionary<PRSReservationSystem.StorageLocation, List<PendingHaul>>();
                    _pendingHauls[map] = mapPending;
                }
                
                // Get or create location list
                if (!mapPending.TryGetValue(destination, out var locationPending))
                {
                    locationPending = new List<PendingHaul>();
                    mapPending[destination] = locationPending;
                }
                
                // Remove any existing pending haul for this pawn at this location
                locationPending.RemoveAll(ph => ph.Pawn == pawn);
                
                // Add new pending haul
                var pending = new PendingHaul(pawn, sourceThing.def, haulAmount, job);
                locationPending.Add(pending);
                
				if (Settings.EnableDebugLogging)
				{
					Log.Message($"[PRS] Recorded pending haul: {pawn.LabelShort} -> {destination} for {sourceThing.def.defName} x{haulAmount}", pawn, job, sourceThing);
				}
            }
        }
        
        /// <summary>
        /// Clear pending hauls for a pawn when they start a new job
        /// </summary>
        public static void ClearPendingHaulsForPawn(Pawn pawn, Map map)
        {
            if (pawn == null || map == null)
                return;
                
            lock (_lock)
            {
                if (!_pendingHauls.TryGetValue(map, out var mapPending))
                    return;
                    
                var locationsToClean = new List<PRSReservationSystem.StorageLocation>();
                
                foreach (var kvp in mapPending)
                {
                    kvp.Value.RemoveAll(ph => ph.Pawn == pawn);
                    if (kvp.Value.Count == 0)
                        locationsToClean.Add(kvp.Key);
                }
                
                // Clean up empty locations
                foreach (var loc in locationsToClean)
                {
                    mapPending.Remove(loc);
                }
                
				if (Settings.EnableDebugLogging)
				{
					Log.Message($"[PRS] Cleared pending hauls for {pawn.LabelShort}", pawn);
				}
            }
        }
        
        /// <summary>
        /// Get total pending amount for a specific thing def at a location
        /// </summary>
        public static int GetPendingAmount(PRSReservationSystem.StorageLocation location, ThingDef thingDef, Map map)
        {
            if (thingDef == null || map == null)
                return 0;
                
            lock (_lock)
            {
                if (!_pendingHauls.TryGetValue(map, out var mapPending))
                    return 0;
                    
                if (!mapPending.TryGetValue(location, out var locationPending))
                    return 0;
                    
                // Clean up expired and invalid entries
                locationPending.RemoveAll(ph => ph.IsExpired || !ph.IsValid);
                
                // Sum pending amounts for this thing def
                return locationPending
                    .Where(ph => ph.ThingDef == thingDef)
                    .Sum(ph => ph.Count);
            }
        }
        
        /// <summary>
        /// Clean up expired pending hauls for a map
        /// </summary>
        public static void CleanupExpiredPending(Map map)
        {
            if (map == null)
                return;
                
            lock (_lock)
            {
                if (!_pendingHauls.TryGetValue(map, out var mapPending))
                    return;
                    
                var locationsToRemove = new List<PRSReservationSystem.StorageLocation>();
                
                foreach (var kvp in mapPending)
                {
                    // Remove expired and invalid entries
                    kvp.Value.RemoveAll(ph => ph.IsExpired || !ph.IsValid);
                    
                    if (kvp.Value.Count == 0)
                        locationsToRemove.Add(kvp.Key);
                }
                
                // Remove empty locations
                foreach (var loc in locationsToRemove)
                {
                    mapPending.Remove(loc);
                }
                
                // Remove map entry if empty
                if (mapPending.Count == 0)
                    _pendingHauls.Remove(map);
            }
        }
        
        /// <summary>
        /// Clear all pending hauls for a map
        /// </summary>
        public static void ClearAllPending(Map map)
        {
            if (map == null)
                return;
                
            lock (_lock)
            {
                _pendingHauls.Remove(map);
            }
        }
    }
}


