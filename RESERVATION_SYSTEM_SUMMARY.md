# PickUpAndHaul Reservation System Implementation

## Overview
This implementation adds a custom reservation system to PickUpAndHaul that allows multiple pawns to reserve partial capacity at storage locations. This solves the issue where pawns were conflicting over storage spots even when there was enough room for multiple items to stack.

## Key Features

### 1. Partial Reservations
- Multiple pawns can reserve space in the same storage location if there's enough capacity
- Example: 10 pawns can each reserve 1 spot in a cell that can hold 75 cow meat
- The system tracks reserved counts per item type to ensure accurate capacity calculations

### 2. Thread-Safe Design
- Uses locking to prevent race conditions when multiple pawns are looking for storage simultaneously
- Prevents the "Could not reserve" errors that were occurring

### 3. Automatic Cleanup
- Expired reservations are cleaned up automatically (after 10 seconds or when job changes)
- Reservations are released when:
  - Job ends
  - Pawn dies or is downed
  - Pawn is despawned
  - Reservation expires

### 4. Cache Integration
- Storage locations with no available capacity are automatically invalidated from the cache
- Prevents pawns from repeatedly trying to use full storage locations

## Implementation Details

### New Files Created

1. **ReservationSystem.cs**
   - Core reservation tracking system
   - `StorageLocation` struct to represent cells or containers
   - `StorageReservation` class to track reservations per location
   - Methods for reserving, releasing, and finding available storage

2. **HarmonyPatches_ReservationSystem.cs**
   - Harmony patches to integrate with RimWorld's job system
   - Automatically cleans up reservations on job end, pawn death, etc.
   - Periodic cleanup of expired reservations

### Modified Files

1. **WorkGiver_HaulToInventory.cs**
   - Updated `AllocateThingAtCell` to use the new reservation system
   - Now checks available capacity before allocating storage
   - Makes both PUAH and vanilla reservations for compatibility

2. **JobDriver_UnloadYourHauledInventory.cs**
   - Updated `FindTargetOrDrop` to use reservation-aware storage finding
   - Can now handle partial unloading if storage has limited capacity

3. **CacheManager.cs**
   - Added validation of cached storage locations
   - Invalidates cache entries when locations become full
   - Added `InvalidateFullStorageLocations` method

4. **PUAHHaulCaches.cs**
   - Added `InvalidateStorageLocationCache` method
   - Supports targeted cache invalidation

5. **CacheUpdater.cs**
   - Periodically invalidates full storage locations
   - Ensures cache stays accurate

6. **HarmonyPatches.cs**
   - Added patch registration for the reservation system

## Benefits

1. **Eliminates Reservation Conflicts**: Multiple pawns can now efficiently share storage spaces
2. **Maximizes Storage Efficiency**: Storage locations are used to their full capacity
3. **Reduces Job Failures**: Pawns no longer drop items due to reservation conflicts
4. **Better Performance**: Reduced job spam and failed reservation attempts

## How It Works

1. When a pawn needs storage, the system:
   - Checks available capacity (total capacity - current items - reserved items)
   - Finds the best storage location with available space
   - Creates a reservation for the exact amount needed

2. Multiple pawns can reserve the same location if:
   - The total reserved + existing items < stack limit
   - Each pawn's reservation is tracked separately

3. The system maintains compatibility with vanilla by:
   - Making vanilla reservations alongside PUAH reservations
   - Checking both systems before allowing actions

## Example Scenario

Before: 10 pawns with 1 cow meat each would conflict trying to use the same storage cell, causing 9 to drop their items.

After: All 10 pawns successfully reserve space in the same cell (which can hold 75 cow meat), and all items are stored efficiently. 