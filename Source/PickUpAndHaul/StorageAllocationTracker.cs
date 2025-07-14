using System.Collections.Generic;
using UnityEngine;

namespace PickUpAndHaul
{
    /// <summary>
    /// Tracks pending hauling jobs and their storage allocations to prevent race conditions
    /// between multiple pawns hauling to the same storage location.
    /// </summary>
    public class StorageAllocationTracker : ICache
    {
        /// <summary>
        /// Singleton instance
        /// </summary>
        private static readonly StorageAllocationTracker _instance = new();
        
        /// <summary>
        /// Gets the singleton instance
        /// </summary>
        public static StorageAllocationTracker Instance => _instance;
        
        /// <summary>
        /// Tracks storage allocations by storage location and item type
        /// Key: Storage location identifier (cell position or container thing)
        /// Value: Dictionary of item def to allocated count
        /// </summary>
        private readonly Dictionary<StorageLocation, Dictionary<ThingDef, int>> _pendingAllocations = new();
        
        /// <summary>
        /// Tracks which pawns have pending allocations to clean up when they die or jobs fail
        /// </summary>
        private readonly Dictionary<Pawn, HashSet<StorageLocation>> _pawnAllocations = new();
        
        /// <summary>
        /// Lock object for thread safety
        /// </summary>
        private readonly object _lockObject = new object();

        static StorageAllocationTracker()
        {
            // Register with cache manager for automatic cleanup
            CacheManager.RegisterCache(_instance);
        }

        /// <summary>
        /// Represents a storage location (either a cell or a container thing)
        /// </summary>
        public struct StorageLocation : IEquatable<StorageLocation>
        {
            public readonly IntVec3 Cell;
            public readonly Thing Container;
            
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
                if (Container != null)
                    return Container == other.Container;
                return Cell == other.Cell;
            }
            
            public override bool Equals(object obj)
            {
                return obj is StorageLocation other && Equals(other);
            }
            
            public override int GetHashCode()
            {
                return Container?.GetHashCode() ?? Cell.GetHashCode();
            }
            
            public override string ToString()
            {
                return Container?.ToString() ?? Cell.ToString();
            }
        }

        /// <summary>
        /// Check if there's enough available capacity at a storage location for a given item
        /// </summary>
        public bool HasAvailableCapacity(StorageLocation location, ThingDef itemDef, int requestedAmount, Map map)
        {
            lock (_lockObject)
            {
                // Get actual storage capacity
                int actualCapacity = GetActualCapacity(location, itemDef, map);
                
                // Subtract pending allocations
                int pendingAmount = GetPendingAllocation(location, itemDef);
                
                int availableCapacity = actualCapacity - pendingAmount;
                
                Log.Message($"[StorageAllocationTracker] Location {location}: actual={actualCapacity}, pending={pendingAmount}, available={availableCapacity}, requested={requestedAmount}");
                
                return availableCapacity >= requestedAmount;
            }
        }



        /// <summary>
        /// Reserve storage capacity for a pending hauling job (backward compatibility)
        /// </summary>
        public bool ReserveCapacity(StorageLocation location, ThingDef itemDef, int amount, Pawn pawn)
        {
            lock (_lockObject)
            {
                // Check if we can reserve this amount
                if (!HasAvailableCapacity(location, itemDef, amount, pawn.Map))
                {
                    Log.Message($"[StorageAllocationTracker] Cannot reserve {amount} of {itemDef} at {location} - insufficient capacity");
                    return false;
                }
                
                // Add to pending allocations
                if (!_pendingAllocations.ContainsKey(location))
                {
                    _pendingAllocations[location] = new Dictionary<ThingDef, int>();
                }
                
                if (!_pendingAllocations[location].ContainsKey(itemDef))
                {
                    _pendingAllocations[location][itemDef] = 0;
                }
                
                _pendingAllocations[location][itemDef] += amount;
                
                // Track this allocation for the pawn
                if (!_pawnAllocations.ContainsKey(pawn))
                {
                    _pawnAllocations[pawn] = new HashSet<StorageLocation>();
                }
                _pawnAllocations[pawn].Add(location);
                
                Log.Message($"[StorageAllocationTracker] Reserved {amount} of {itemDef} at {location} for {pawn}");
                return true;
            }
        }

        /// <summary>
        /// Check if a pawn currently has any pending allocations
        /// </summary>
        public bool HasAllocations(Pawn pawn)
        {
            lock (_lockObject)
            {
                return _pawnAllocations.ContainsKey(pawn) && _pawnAllocations[pawn].Count > 0;
            }
        }

        /// <summary>
        /// Release storage capacity when a hauling job is completed or cancelled
        /// </summary>
        public void ReleaseCapacity(StorageLocation location, ThingDef itemDef, int amount, Pawn pawn)
        {
            lock (_lockObject)
            {
                if (_pendingAllocations.ContainsKey(location) && _pendingAllocations[location].ContainsKey(itemDef))
                {
                    _pendingAllocations[location][itemDef] = Math.Max(0, _pendingAllocations[location][itemDef] - amount);
                    
                    // Remove location from pawn's allocations if no more pending allocations
                    if (_pendingAllocations[location][itemDef] == 0)
                    {
                        _pendingAllocations[location].Remove(itemDef);
                    }
                    
                    if (_pendingAllocations[location].Count == 0)
                    {
                        _pendingAllocations.Remove(location);
                    }
                }
                
                // Remove from pawn's allocation tracking
                if (_pawnAllocations.ContainsKey(pawn))
                {
                    _pawnAllocations[pawn].Remove(location);
                    if (_pawnAllocations[pawn].Count == 0)
                    {
                        _pawnAllocations.Remove(pawn);
                    }
                }
                
                Log.Message($"[StorageAllocationTracker] Released {amount} of {itemDef} at {location} for {pawn}");
            }
        }

        /// <summary>
        /// Clean up all allocations for a pawn (when they die or job fails)
        /// </summary>
        public void CleanupPawnAllocations(Pawn pawn)
        {
            lock (_lockObject)
            {
                if (!_pawnAllocations.ContainsKey(pawn))
                    return;
                
                var locations = new List<StorageLocation>(_pawnAllocations[pawn]);
                foreach (var location in locations)
                {
                    if (_pendingAllocations.ContainsKey(location))
                    {
                        var itemDefs = new List<ThingDef>(_pendingAllocations[location].Keys);
                        foreach (var itemDef in itemDefs)
                        {
                            var amount = _pendingAllocations[location][itemDef];
                            ReleaseCapacity(location, itemDef, amount, pawn);
                        }
                    }
                }
                
                Log.Message($"[StorageAllocationTracker] Cleaned up all allocations for {pawn}");
            }
        }

        /// <summary>
        /// Get the actual storage capacity at a location
        /// </summary>
        private int GetActualCapacity(StorageLocation location, ThingDef itemDef, Map map)
        {
            if (location.Container != null)
            {
                var thingOwner = location.Container.TryGetInnerInteractableThingOwner();
                if (thingOwner != null)
                {
                    // Create a temporary thing to check capacity
                    var tempThing = CreateTempThing(itemDef);
                    if (tempThing != null)
                    {
                        try
                        {
                            tempThing.stackCount = itemDef.stackLimit;
                            var capacity = thingOwner.GetCountCanAccept(tempThing);
                            return capacity;
                        }
                        catch (Exception ex)
                        {
                            Log.Error($"[StorageAllocationTracker] Error getting capacity for {itemDef} at container {location.Container}: {ex.Message}");
                            // Return stack limit as fallback
                            return itemDef.stackLimit;
                        }
                        // Note: No need to call Destroy() on unspawned temporary objects - they will be garbage collected
                    }
                    else
                    {
                        // Fallback: use stack limit as approximation
                        return itemDef.stackLimit;
                    }
                }
            }
            else if (location.Cell.IsValid)
            {
                // Use the existing CapacityAt method
                var tempThing = CreateTempThing(itemDef);
                if (tempThing != null)
                {
                    try
                    {
                        tempThing.stackCount = itemDef.stackLimit;
                        var capacity = WorkGiver_HaulToInventory.CapacityAt(tempThing, location.Cell, map);
                        return capacity;
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"[StorageAllocationTracker] Error getting capacity for {itemDef} at cell {location.Cell}: {ex.Message}");
                        // Fallback: check if there's already an item at the location and calculate remaining capacity
                        var existingThing = map.thingGrid.ThingAt(location.Cell, itemDef);
                        if (existingThing != null)
                        {
                            return itemDef.stackLimit - existingThing.stackCount;
                        }
                        else
                        {
                            return itemDef.stackLimit;
                        }
                    }
                    // Note: No need to call Destroy() on unspawned temporary objects - they will be garbage collected
                }
                else
                {
                    // Fallback: check if there's already an item at the location and calculate remaining capacity
                    var existingThing = map.thingGrid.ThingAt(location.Cell, itemDef);
                    if (existingThing != null)
                    {
                        return itemDef.stackLimit - existingThing.stackCount;
                    }
                    else
                    {
                        return itemDef.stackLimit;
                    }
                }
            }
            
            return 0;
        }

        /// <summary>
        /// Safely create a temporary thing for capacity checking
        /// </summary>
        private Thing CreateTempThing(ThingDef itemDef)
        {
            try
            {
                if (itemDef.MadeFromStuff)
                {
                    // For stuff-based items, use the default stuff or find a suitable one
                    var stuff = itemDef.defaultStuff ?? GenStuff.DefaultStuffFor(itemDef);
                    if (stuff != null)
                    {
                        var thing = ThingMaker.MakeThing(itemDef, stuff);
                        thing.stackCount = itemDef.stackLimit;
                        return thing;
                    }
                    else
                    {
                        Log.Warning($"[StorageAllocationTracker] Could not find suitable stuff for {itemDef}, using stackCount for capacity estimation");
                        return null;
                    }
                }
                else
                {
                    var thing = ThingMaker.MakeThing(itemDef);
                    thing.stackCount = itemDef.stackLimit;
                    return thing;
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[StorageAllocationTracker] Error creating temp thing for {itemDef}: {ex.Message}");
                return null;
            }
        }



        /// <summary>
        /// Get the amount of pending allocations for a specific item at a location
        /// </summary>
        private int GetPendingAllocation(StorageLocation location, ThingDef itemDef)
        {
            if (_pendingAllocations.ContainsKey(location) && _pendingAllocations[location].ContainsKey(itemDef))
            {
                return _pendingAllocations[location][itemDef];
            }
            return 0;
        }



        /// <summary>
        /// Clear all allocations (for testing or when save is loaded)
        /// </summary>
        public void ClearAllAllocations()
        {
            lock (_lockObject)
            {
                _pendingAllocations.Clear();
                _pawnAllocations.Clear();
                Log.Message("[StorageAllocationTracker] Cleared all allocations");
            }
        }

        /// <summary>
        /// Forces a cleanup of the storage allocation tracker
        /// </summary>
        public void ForceCleanup()
        {
            lock (_lockObject)
            {
                // Clean up allocations for dead pawns
                var deadPawns = new List<Pawn>();
                
                foreach (var kvp in _pawnAllocations)
                {
                    var pawn = kvp.Key;
                    if (pawn == null || pawn.Destroyed || !pawn.Spawned)
                    {
                        deadPawns.Add(pawn);
                    }
                }
                
                foreach (var deadPawn in deadPawns)
                {
                    CleanupPawnAllocations(deadPawn);
                }
                
                if (deadPawns.Count > 0 && Settings.EnableDebugLogging)
                {
                    Log.Message($"[StorageAllocationTracker] Cleaned up allocations for {deadPawns.Count} dead pawns");
                }
            }
        }

        /// <summary>
        /// Gets debug information about the storage allocation tracker
        /// </summary>
        public string GetDebugInfo()
        {
            lock (_lockObject)
            {
                return $"StorageAllocationTracker: {_pendingAllocations.Count} pending allocations, {_pawnAllocations.Count} pawn allocations";
            }
        }
    }
} 