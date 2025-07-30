using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace PickUpAndHaul
{
    /// <summary>
    /// Custom reservation system that allows partial reservations of storage locations
    /// </summary>
    public static class PUAHReservationSystem
    {
        // Storage reservations by map
        private static readonly Dictionary<int, Dictionary<StorageLocation, StorageReservation>> _storageReservations = new();
        
        // Lock object for thread safety
        private static readonly object _reservationLock = new();

        /// <summary>
        /// Represents a storage location (either a cell or a container)
        /// </summary>
        public struct StorageLocation : IEquatable<StorageLocation>
        {
            public IntVec3 Cell { get; }
            public Thing Container { get; }
            public bool IsContainer => Container != null;
            
            public StorageLocation(IntVec3 cell)
            {
                Cell = cell;
                Container = null;
            }
            
            public StorageLocation(Thing container)
            {
                Cell = IntVec3.Invalid;
                Container = container;
            }

            public bool Equals(StorageLocation other)
            {
                return IsContainer 
                    ? Container == other.Container 
                    : Cell == other.Cell && !other.IsContainer;
            }

            public override int GetHashCode()
            {
                return IsContainer ? Container.GetHashCode() : Cell.GetHashCode();
            }

            public override string ToString()
            {
                return IsContainer ? $"Container:{Container}" : $"Cell:{Cell}";
            }
        }

        /// <summary>
        /// Tracks reservations for a specific storage location
        /// </summary>
        public class StorageReservation
        {
            private readonly Dictionary<Pawn, List<ReservationEntry>> _reservations = new();
            private int _totalReservedCount = 0;
            
            public int TotalReservedCount => _totalReservedCount;
            
            /// <summary>
            /// Individual reservation entry
            /// </summary>
            public class ReservationEntry
            {
                public Thing Thing { get; }
                public int Count { get; }
                public Job Job { get; }
                public int TickCreated { get; }
                
                public ReservationEntry(Thing thing, int count, Job job)
                {
                    Thing = thing;
                    Count = count;
                    Job = job;
                    TickCreated = Find.TickManager.TicksGame;
                }
            }
            
            /// <summary>
            /// Try to add a reservation
            /// </summary>
            public bool TryAddReservation(Pawn pawn, Thing thing, int count, Job job)
            {
                if (!_reservations.TryGetValue(pawn, out var entries))
                {
                    entries = new List<ReservationEntry>();
                    _reservations[pawn] = entries;
                }
                
                entries.Add(new ReservationEntry(thing, count, job));
                _totalReservedCount += count;
                return true;
            }
            
            /// <summary>
            /// Remove all reservations for a pawn
            /// </summary>
            public void RemoveReservationsForPawn(Pawn pawn)
            {
                if (_reservations.TryGetValue(pawn, out var entries))
                {
                    foreach (var entry in entries)
                    {
                        _totalReservedCount -= entry.Count;
                    }
                    _reservations.Remove(pawn);
                }
            }
            
            /// <summary>
            /// Remove reservations for a specific job
            /// </summary>
            public void RemoveReservationsForJob(Pawn pawn, Job job)
            {
                if (_reservations.TryGetValue(pawn, out var entries))
                {
                    var toRemove = entries.Where(e => e.Job == job).ToList();
                    foreach (var entry in toRemove)
                    {
                        _totalReservedCount -= entry.Count;
                        entries.Remove(entry);
                    }
                    
                    if (entries.Count == 0)
                    {
                        _reservations.Remove(pawn);
                    }
                }
            }
            
            /// <summary>
            /// Get reserved count for a specific thing type
            /// </summary>
            public int GetReservedCountForThingDef(ThingDef def)
            {
                var count = 0;
                foreach (var entries in _reservations.Values)
                {
                    foreach (var entry in entries)
                    {
                        if (entry.Thing.def == def)
                        {
                            count += entry.Count;
                        }
                    }
                }
                return count;
            }
            
            /// <summary>
            /// Check if this storage has any reservations
            /// </summary>
            public bool HasAnyReservations => _reservations.Count > 0;
            
            /// <summary>
            /// Clean up expired reservations
            /// </summary>
            public void CleanupExpiredReservations()
            {
                var currentTick = Find.TickManager.TicksGame;
                var pawnsToRemove = new List<Pawn>();
                
                foreach (var kvp in _reservations)
                {
                    var pawn = kvp.Key;
                    var entries = kvp.Value;
                    
                    // Remove if pawn is dead, downed, or job is no longer valid
                    if (pawn.Dead || pawn.Downed || pawn.CurJob == null)
                    {
                        pawnsToRemove.Add(pawn);
                        continue;
                    }
                    
                    // Remove old reservations (older than 10 seconds)
                    var expiredEntries = entries.Where(e => 
                        currentTick - e.TickCreated > 600 || 
                        e.Job != pawn.CurJob).ToList();
                        
                    foreach (var entry in expiredEntries)
                    {
                        _totalReservedCount -= entry.Count;
                        entries.Remove(entry);
                    }
                    
                    if (entries.Count == 0)
                    {
                        pawnsToRemove.Add(pawn);
                    }
                }
                
                foreach (var pawn in pawnsToRemove)
                {
                    _reservations.Remove(pawn);
                }
            }
        }

        /// <summary>
        /// Get or create storage reservations for a map
        /// </summary>
        private static Dictionary<StorageLocation, StorageReservation> GetMapReservations(Map map)
        {
            lock (_reservationLock)
            {
                if (!_storageReservations.TryGetValue(map.uniqueID, out var reservations))
                {
                    reservations = new Dictionary<StorageLocation, StorageReservation>();
                    _storageReservations[map.uniqueID] = reservations;
                }
                return reservations;
            }
        }

        /// <summary>
        /// Try to reserve storage space for a thing
        /// </summary>
        public static bool TryReserveStorage(Pawn pawn, Thing thing, StorageLocation location, Job job, Map map)
        {
            if (pawn == null || thing == null || job == null || map == null)
                return false;

            lock (_reservationLock)
            {
                var reservations = GetMapReservations(map);
                
                // Get or create reservation for this location
                if (!reservations.TryGetValue(location, out var reservation))
                {
                    reservation = new StorageReservation();
                    reservations[location] = reservation;
                }
                
                // Clean up expired reservations first
                reservation.CleanupExpiredReservations();
                
                // Check if we have capacity - allow partial reservations
                var capacity = GetAvailableCapacity(location, thing, map, reservation);
                if (capacity <= 0)
                {
                    return false;
                }
                
                // Reserve what we can (up to the full stack count)
                var countToReserve = Math.Min(capacity, thing.stackCount);
                
                // Add the reservation
                return reservation.TryAddReservation(pawn, thing, countToReserve, job);
            }
        }

        /// <summary>
        /// Try to reserve partial storage space
        /// </summary>
        public static bool TryReservePartialStorage(Pawn pawn, Thing thing, int count, StorageLocation location, Job job, Map map)
        {
            if (pawn == null || thing == null || job == null || map == null || count <= 0)
                return false;

            lock (_reservationLock)
            {
                var reservations = GetMapReservations(map);
                
                // Get or create reservation for this location
                if (!reservations.TryGetValue(location, out var reservation))
                {
                    reservation = new StorageReservation();
                    reservations[location] = reservation;
                }
                
                // Clean up expired reservations first
                reservation.CleanupExpiredReservations();
                
                // Check if we have capacity - use the reservation object we already have to avoid redundant cleanup
                var capacity = GetAvailableCapacity(location, thing, map, reservation);
                if (capacity < count)
                {
                    return false;
                }
                
                // Add the reservation
                return reservation.TryAddReservation(pawn, thing, count, job);
            }
        }

        /// <summary>
        /// Release all reservations for a pawn's job
        /// </summary>
        public static void ReleaseAllReservationsForJob(Pawn pawn, Job job, Map map)
        {
            if (pawn == null || job == null || map == null)
                return;

            lock (_reservationLock)
            {
                var reservations = GetMapReservations(map);
                var locationsToRemove = new List<StorageLocation>();
                
                foreach (var kvp in reservations)
                {
                    kvp.Value.RemoveReservationsForJob(pawn, job);
                    
                    // Remove empty reservations
                    if (!kvp.Value.HasAnyReservations)
                    {
                        locationsToRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var location in locationsToRemove)
                {
                    reservations.Remove(location);
                }
            }
        }

        /// <summary>
        /// Release all reservations for a pawn
        /// </summary>
        public static void ReleaseAllReservationsForPawn(Pawn pawn, Map map)
        {
            if (pawn == null || map == null)
                return;

            lock (_reservationLock)
            {
                var reservations = GetMapReservations(map);
                var locationsToRemove = new List<StorageLocation>();
                
                foreach (var kvp in reservations)
                {
                    kvp.Value.RemoveReservationsForPawn(pawn);
                    
                    // Remove empty reservations
                    if (!kvp.Value.HasAnyReservations)
                    {
                        locationsToRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var location in locationsToRemove)
                {
                    reservations.Remove(location);
                }
            }
        }

        /// <summary>
        /// Get available capacity at a storage location
        /// </summary>
        public static int GetAvailableCapacity(StorageLocation location, Thing thing, Map map, StorageReservation reservation = null)
        {
            if (thing == null || map == null)
                return 0;

            int totalCapacity;
            int currentCount;
            
            if (location.IsContainer)
            {
                var container = location.Container;
                if (container == null || container.Destroyed)
                    return 0;
                    
                var thingOwner = container.TryGetInnerInteractableThingOwner();
                if (thingOwner == null)
                    return 0;
                    
                totalCapacity = thingOwner.GetCountCanAccept(thing);
                currentCount = 0; // ThingOwner already accounts for current items
            }
            else
            {
                // Check if cell is valid
                if (!location.Cell.InBounds(map))
                    return 0;
                    
                // Get stack limit for this location
                totalCapacity = thing.def.stackLimit;
                
                // Check for existing items of same type
                var existingThing = map.thingGrid.ThingAt(location.Cell, thing.def);
                currentCount = existingThing?.stackCount ?? 0;
                
                // Check if HoldMultipleThings mod allows more
                if (HoldMultipleThings_Support.CapacityAt(thing, location.Cell, map, out var modCapacity))
                {
                    totalCapacity = modCapacity;
                    currentCount = 0; // Mod handles current count
                }
            }
            
            // Subtract reserved count
            var reservedCount = 0;
            if (reservation == null)
            {
                lock (_reservationLock)
                {
                    var reservations = GetMapReservations(map);
                    if (reservations.TryGetValue(location, out reservation))
                    {
                        // Only cleanup if we don't already have a reservation object (to avoid redundant calls)
                        reservation.CleanupExpiredReservations();
                        reservedCount = reservation.GetReservedCountForThingDef(thing.def);
                    }
                }
            }
            else
            {
                // Use the provided reservation object without cleanup (caller should handle cleanup)
                reservedCount = reservation.GetReservedCountForThingDef(thing.def);
            }
            
            return Math.Max(0, totalCapacity - currentCount - reservedCount);
        }

        /// <summary>
        /// Find best storage location with partial reservation support
        /// </summary>
        public static bool TryFindBestStorageWithReservation(Thing thing, Pawn pawn, Map map, 
            StoragePriority currentPriority, Faction faction, out StorageLocation location, 
            out int availableCapacity)
        {
            location = default;
            availableCapacity = 0;
            
            if (thing == null || pawn == null || map == null)
                return false;

            var bestPriority = currentPriority;
            var bestLocation = default(StorageLocation);
            var bestCapacity = 0;
            var closestDistance = float.MaxValue;

            lock (_reservationLock)
            {
                var reservations = GetMapReservations(map);
                
                // Check storage cells
                var haulDestinations = map.haulDestinationManager.AllGroupsListInPriorityOrder;
                foreach (var slotGroup in haulDestinations)
                {
                    if (slotGroup.Settings.Priority <= currentPriority || !slotGroup.parent.Accepts(thing))
                        continue;
                        
                    foreach (var cell in slotGroup.CellsList)
                    {
                        if (!StoreUtility.IsGoodStoreCell(cell, map, thing, pawn, faction))
                            continue;
                            
                        var storageLocation = new StorageLocation(cell);
                        var capacity = GetAvailableCapacity(storageLocation, thing, map);
                        
                        if (capacity > 0)
                        {
                            var distance = (cell - pawn.Position).LengthHorizontalSquared;
                            
                            // Prefer higher priority or closer distance
                            if (slotGroup.Settings.Priority > bestPriority || 
                                (slotGroup.Settings.Priority == bestPriority && distance < closestDistance))
                            {
                                bestLocation = storageLocation;
                                bestCapacity = capacity;
                                bestPriority = slotGroup.Settings.Priority;
                                closestDistance = distance;
                            }
                        }
                    }
                }
                
                // Check storage containers
                var allHaulDestinations = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
                foreach (var haulDest in allHaulDestinations)
                {
                    if (haulDest is not Thing container || haulDest is ISlotGroupParent)
                        continue;
                        
                    var settings = haulDest.GetStoreSettings();
                    if (settings.Priority <= currentPriority || !haulDest.Accepts(thing))
                        continue;
                        
                    if (!pawn.CanReserveNew(container) || container.IsForbidden(pawn))
                        continue;
                        
                    var storageLocation = new StorageLocation(container);
                    var capacity = GetAvailableCapacity(storageLocation, thing, map);
                    
                    if (capacity > 0)
                    {
                        var distance = (container.Position - pawn.Position).LengthHorizontalSquared;
                        
                        if (settings.Priority > bestPriority || 
                            (settings.Priority == bestPriority && distance < closestDistance))
                        {
                            bestLocation = storageLocation;
                            bestCapacity = capacity;
                            bestPriority = settings.Priority;
                            closestDistance = distance;
                        }
                    }
                }
            }
            
            if (bestCapacity > 0)
            {
                location = bestLocation;
                availableCapacity = bestCapacity;
                return true;
            }
            
            return false;
        }

        /// <summary>
        /// Clean up all expired reservations for a map
        /// </summary>
        public static void CleanupExpiredReservations(Map map)
        {
            if (map == null)
                return;

            lock (_reservationLock)
            {
                var reservations = GetMapReservations(map);
                var locationsToRemove = new List<StorageLocation>();
                
                foreach (var kvp in reservations)
                {
                    kvp.Value.CleanupExpiredReservations();
                    
                    if (!kvp.Value.HasAnyReservations)
                    {
                        locationsToRemove.Add(kvp.Key);
                    }
                }
                
                foreach (var location in locationsToRemove)
                {
                    reservations.Remove(location);
                }
            }
        }

        /// <summary>
        /// Clear all reservations for a map
        /// </summary>
        public static void ClearAllReservations(Map map)
        {
            if (map == null)
                return;

            lock (_reservationLock)
            {
                _storageReservations.Remove(map.uniqueID);
            }
        }

        /// <summary>
        /// Get the actual reserved count for a specific thing type at a location
        /// </summary>
        public static int GetReservedCount(StorageLocation location, ThingDef thingDef, Map map)
        {
            lock (_reservationLock)
            {
                var reservations = GetMapReservations(map);
                if (reservations.TryGetValue(location, out var reservation))
                {
                    reservation.CleanupExpiredReservations();
                    return reservation.GetReservedCountForThingDef(thingDef);
                }
                return 0;
            }
        }

        /// <summary>
        /// Debug: Get reservation info for a location
        /// </summary>
        public static string GetReservationDebugInfo(StorageLocation location, Map map)
        {
            lock (_reservationLock)
            {
                var reservations = GetMapReservations(map);
                if (reservations.TryGetValue(location, out var reservation))
                {
                    return $"Total reserved: {reservation.TotalReservedCount}";
                }
                return "No reservations";
            }
        }
    }
}
