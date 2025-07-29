using System.Collections.Generic;
using System.Linq;
using Verse;
using Verse.AI;

namespace PickUpAndHaul;

/// <summary>
/// Custom reservation manager that bypasses RimWorld's broken reservation system
/// </summary>
public static class PUAHReservationManager
{
    // Track which cells are reserved by which pawns for PUAH jobs
    private static readonly Dictionary<Map, Dictionary<IntVec3, List<PawnReservation>>> cellReservations = new Dictionary<Map, Dictionary<IntVec3, List<PawnReservation>>>();
    
    // Track which things are reserved by which pawns for PUAH jobs
    private static readonly Dictionary<Map, Dictionary<Thing, List<PawnReservation>>> thingReservations = new Dictionary<Map, Dictionary<Thing, List<PawnReservation>>>();
    
    private class PawnReservation
    {
        public Pawn Pawn { get; set; }
        public Job Job { get; set; }
        public int StackCount { get; set; }
    }
    
    /// <summary>
    /// Initialize reservation tracking for a map
    /// </summary>
    public static void InitializeMap(Map map)
    {
        if (!cellReservations.ContainsKey(map))
        {
            cellReservations[map] = new Dictionary<IntVec3, List<PawnReservation>>();
        }
        if (!thingReservations.ContainsKey(map))
        {
            thingReservations[map] = new Dictionary<Thing, List<PawnReservation>>();
        }
    }
    
    /// <summary>
    /// Check if a cell can be reserved by a pawn for PUAH
    /// </summary>
    public static bool CanReserve(IntVec3 cell, Map map, Pawn pawn, int stackCount = -1)
    {
        InitializeMap(map);
        
        // Check if cell is valid
        if (!cell.IsValid || !cell.InBounds(map))
        {
            Log.Message($"[PUAH] {pawn} cannot reserve {cell} - invalid cell");
            return false;
        }
        
        // Check if we already have reservations for this cell
        if (cellReservations[map].TryGetValue(cell, out var reservations))
        {
            // Check if this pawn already has a reservation
            if (reservations.Any(r => r.Pawn == pawn))
            {
                Log.Message($"[PUAH] {pawn} already has reservation for {cell}");
                return true; // Already reserved by this pawn
            }
            
            // Check if there's capacity for more reservations
            var totalReserved = reservations.Sum(r => r.StackCount > 0 ? r.StackCount : 1);
            var cellCapacity = GetCellCapacity(cell, map);
            
            Log.Message($"[PUAH] Cell {cell} has {totalReserved} reserved, capacity {cellCapacity}");
            
            if (stackCount > 0)
            {
                return totalReserved + stackCount <= cellCapacity;
            }
            
            // For unstackable items, check if cell has space
            return totalReserved < cellCapacity;
        }
        
        // No reservations yet, can reserve
        return true;
    }
    
    /// <summary>
    /// Reserve a cell for PUAH hauling
    /// </summary>
    public static bool Reserve(IntVec3 cell, Map map, Pawn pawn, Job job, int stackCount = -1)
    {
        InitializeMap(map);
        
        if (!CanReserve(cell, map, pawn, stackCount))
        {
            Log.Message($"[PUAH] {pawn} FAILED to reserve {cell} - CanReserve returned false");
            return false;
        }
        
        // Add reservation
        if (!cellReservations[map].ContainsKey(cell))
        {
            cellReservations[map][cell] = new List<PawnReservation>();
        }
        
        // Check if pawn already has reservation and update it
        var existing = cellReservations[map][cell].FirstOrDefault(r => r.Pawn == pawn);
        if (existing != null)
        {
            existing.Job = job;
            existing.StackCount = stackCount;
            Log.Message($"[PUAH] {pawn} updated reservation for {cell}");
        }
        else
        {
            cellReservations[map][cell].Add(new PawnReservation
            {
                Pawn = pawn,
                Job = job,
                StackCount = stackCount
            });
            Log.Message($"[PUAH] {pawn} successfully reserved {cell} (stackCount: {stackCount})");
        }
        
        // CRITICAL: Also make a vanilla reservation to prevent conflicts
        // This prevents vanilla jobs from trying to use the same cell
        try
        {
            if (pawn.Map.reservationManager.CanReserve(pawn, cell, 1, -1, null, true))
            {
                pawn.Map.reservationManager.Reserve(pawn, job, cell, 1, -1, null, false, true);
                Log.Message($"[PUAH] Also made vanilla reservation for {cell} to prevent conflicts");
            }
        }
        catch
        {
            // Ignore vanilla reservation failures - our system is authoritative
            Log.Message($"[PUAH] Vanilla reservation failed for {cell}, but PUAH reservation stands");
        }
        
        return true;
    }
    
    /// <summary>
    /// Release a cell reservation
    /// </summary>
    public static void Release(IntVec3 cell, Map map, Pawn pawn, Job job)
    {
        InitializeMap(map);
        
        if (cellReservations[map].TryGetValue(cell, out var reservations))
        {
            var removed = reservations.RemoveAll(r => r.Pawn == pawn && (job == null || r.Job == job));
            if (removed > 0)
            {
                Log.Message($"[PUAH] {pawn} released reservation for {cell}");
                
                // If no more PUAH reservations, clean up
                if (reservations.Count == 0)
                {
                    cellReservations[map].Remove(cell);
                    
                    // Release vanilla reservation only if no other PUAH pawns need it
                    try
                    {
                        if (pawn.Map.reservationManager.ReservedBy(cell, pawn, job))
                        {
                            pawn.Map.reservationManager.Release(cell, pawn, job);
                            Log.Message($"[PUAH] Also released vanilla reservation for {cell}");
                        }
                    }
                    catch
                    {
                        // Ignore vanilla release failures
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Release all reservations for a pawn
    /// </summary>
    public static void ReleaseAllForPawn(Pawn pawn, Job job = null)
    {
        if (pawn?.Map == null) return;
        
        InitializeMap(pawn.Map);
        
        // Release cell reservations
        var cellsToRelease = cellReservations[pawn.Map]
            .Where(kvp => kvp.Value.Any(r => r.Pawn == pawn && (job == null || r.Job == job)))
            .Select(kvp => kvp.Key)
            .ToList();
            
        foreach (var cell in cellsToRelease)
        {
            Release(cell, pawn.Map, pawn, job);
        }
        
        // Release thing reservations
        var thingsToRelease = thingReservations[pawn.Map]
            .Where(kvp => kvp.Value.Any(r => r.Pawn == pawn && (job == null || r.Job == job)))
            .Select(kvp => kvp.Key)
            .ToList();
            
        foreach (var thing in thingsToRelease)
        {
            ReleaseThing(thing, pawn.Map, pawn, job);
        }
    }
    
    /// <summary>
    /// Reserve a thing for PUAH hauling
    /// </summary>
    public static bool ReserveThing(Thing thing, Map map, Pawn pawn, Job job, int stackCount = -1)
    {
        InitializeMap(map);
        
        if (!thingReservations[map].ContainsKey(thing))
        {
            thingReservations[map][thing] = new List<PawnReservation>();
        }
        
        // Check if pawn already has reservation
        var existing = thingReservations[map][thing].FirstOrDefault(r => r.Pawn == pawn);
        if (existing != null)
        {
            existing.Job = job;
            existing.StackCount = stackCount;
            Log.Message($"[PUAH] {pawn} updated thing reservation for {thing}");
        }
        else
        {
            thingReservations[map][thing].Add(new PawnReservation
            {
                Pawn = pawn,
                Job = job,
                StackCount = stackCount
            });
            Log.Message($"[PUAH] {pawn} successfully reserved thing {thing}");
        }
        
        // Also make vanilla reservation
        try
        {
            if (pawn.Map.reservationManager.CanReserve(pawn, thing, 1, stackCount, null, true))
            {
                pawn.Map.reservationManager.Reserve(pawn, job, thing, 1, stackCount, null, false, true);
                Log.Message($"[PUAH] Also made vanilla reservation for thing {thing}");
            }
        }
        catch
        {
            // Ignore vanilla failures
        }
        
        return true;
    }
    
    /// <summary>
    /// Release a thing reservation
    /// </summary>
    public static void ReleaseThing(Thing thing, Map map, Pawn pawn, Job job)
    {
        InitializeMap(map);
        
        if (thingReservations[map].TryGetValue(thing, out var reservations))
        {
            var removed = reservations.RemoveAll(r => r.Pawn == pawn && (job == null || r.Job == job));
            if (removed > 0)
            {
                Log.Message($"[PUAH] {pawn} released thing reservation for {thing}");
                
                if (reservations.Count == 0)
                {
                    thingReservations[map].Remove(thing);
                    
                    // Release vanilla reservation
                    try
                    {
                        if (pawn.Map.reservationManager.ReservedBy(thing, pawn, job))
                        {
                            pawn.Map.reservationManager.Release(thing, pawn, job);
                        }
                    }
                    catch
                    {
                        // Ignore
                    }
                }
            }
        }
    }
    
    /// <summary>
    /// Get cell capacity for storage
    /// </summary>
    private static int GetCellCapacity(IntVec3 cell, Map map)
    {
        var slotGroup = cell.GetSlotGroup(map);
        if (slotGroup == null) return 0;
        
        // Check current contents
        var things = map.thingGrid.ThingsListAt(cell);
        var currentCount = 0;
        var maxStackSize = 1;
        
        foreach (var thing in things)
        {
            if (thing.def.EverStorable(false))
            {
                currentCount += thing.stackCount;
                if (thing.def.stackLimit > maxStackSize)
                {
                    maxStackSize = thing.def.stackLimit;
                }
            }
        }
        
        // For stackable items, capacity is based on stack limit
        if (maxStackSize > 1)
        {
            return maxStackSize - currentCount;
        }
        
        // For unstackable items, typically 1 per cell
        return things.Any(t => t.def.EverStorable(false)) ? 0 : 1;
    }
    
    /// <summary>
    /// Clear all reservations (for cleanup/debugging)
    /// </summary>
    public static void ClearAllReservations()
    {
        cellReservations.Clear();
        thingReservations.Clear();
        Log.Message("[PUAH] Cleared all PUAH reservations");
    }
}