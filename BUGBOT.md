## Purpose
Static analysis checklist for PickUpAndHaul. Focus on logic contracts, data-structure invariants, reservation math, concurrency hazards, and error-prone branches. Do not run the game; read code and flag defects or high-risk patterns.

## Critical Contracts (must stay consistent)

- WorkGiver ↔ JobDriver contract
  - `WorkGiver_HaulToInventory.HasJobOnThing` must mirror early-exit logic in `JobOnThing`:
    - Same guards: `CacheUpdaterHelper.EnsureCacheUpdater`, `ValidateThingInCache`, “PUAH items already in inventory”, `OkThingToHaul`, `PawnCanAutomaticallyHaulFast`.
    - Same fallback logic: if gear capacity/encumbrance/corpse rules hit, `JobOnThing` returns `HaulAIUtility.HaulToStorageJob`; `HasJobOnThing` should reflect storability via `StoreUtility.TryFindBestBetterStorageFor(...)`.
    - Same storage pre-checks: cached storage presence and non-zero capacity for cell/container.
  - Flag any divergence between `HasJobOnThing(...)` and `JobOnThing(...)` conditions.

- Queue invariants (job serialization)
  - For `Job`: `targetQueueA.Count == countQueue.Count` at all points where job may be returned.
  - Every `targetQueueA.Add(thing)` must be paired with a corresponding `countQueue.Add(count)` in the same logical path.
  - Any removal from `targetQueueA` must be mirrored appropriately in `countQueue` (and vice versa).
  - Flag any code path where a method can return after mutating only one of the two queues.
  - Reference: `WorkGiver_HaulToInventory.AllocateThingAtCell(...)`, `JobOnThing(...)`.

- Reservation correctness (PRS)
  - All `Reserve(...)` calls must have balanced releases on failure paths or in finally blocks.
  - When computing capacity then writing a reservation, ensure both happen under the same “bucket” and lock (no TOCTOU):
    - Capacity check should use `GetAvailableCapacity(location, thing, map, reservationInstance)` while holding the destination lock.
    - The write must target the same reservation instance in that critical section.
  - Releases must happen for every early return in allocation/reallocation paths.
  - Reference: `PRS/HarmonyPatches_ReservationSystem.cs` (dest-lock + PRS bucket), `PRS/ReservationSystem.cs`.

## Concrete Static Checks (how to find issues)

- Cross-method alignment
  - Compare condition sets in:
    - `WorkGiver_HaulToInventory.HasJobOnThing` vs `JobOnThing`
  - Report any missing/extra predicate, or condition order that implies different results for identical input.

- Queue synchronization
  - Search for:
    - `.targetQueueA.Add(` occurrences without a nearby `.countQueue.Add(`.
    - `.countQueue.Pop()` or `.countQueue.Add(` followed by missing `targetQueueA` updates.
    - Early returns in `AllocateThingAtCell(...)` that might leave an item in `targetQueueA` without a count (or vice versa).
  - Verify `finally` in `AllocateThingAtCell(...)` prunes B targets and releases any stray reservations on failure.

- Lock ordering and deadlock risk
  - Ensure only one place acquires both:
    - Destination lock (local to `HarmonyPatches_ReservationSystem`) THEN `_reservationLock` (inside `PRSReservationSystem`), never reversed anywhere else.
  - Search for any new code that locks `_reservationLock` and attempts to touch dest locks (should not exist).
  - Reference: `GetDestLock(...)` usage is only in `HarmonyPatches_ReservationSystem.cs`.

- Mutable static state hazards
  - `WorkGiver_HaulToInventory.Comparer` is a static instance with mutable field `rootCell`.
  - Flag this pattern as unsafe for concurrent or interleaved calls; prefer per-call comparer instances.

- Sleep calls on game thread
  - `TryReserveSafely(...)` uses `Thread.Sleep(1)`. Flag as a design smell (pauses the main thread and hides races rather than fixing them). Recommend removing sleeps; rely on PRS to serialize capacity, not sleeping.

- Capacity math consistency
  - Cell capacity:
    - Cell: `PRSReservationSystem.GetAvailableCapacity(StorageLocation(cell,map), thing, map)` must align with count reductions applied in `AllocateThingAtCell(...)` (subtracting `count` and managing overflows).
    - Container: `ThingOwner.GetCountCanAccept(thing)` must be used consistently where the code expects container semantics.
  - Verify that “capacity over” handling always restores consistency (e.g., subtracting, relocating, or partial allocation also maintains queues and releases previous reservation).

- Cache usage and invalidation
  - Ensure storage cache invalidation is wired:
    - Look for: `StorageSettings_Priority_Postfix` calling `CacheManager.InvalidateAllStorageLocationCache(map)`.
  - Ensure `ValidateThingInCache(...)` moves items between haulable and too heavy caches based on current pawn capacities; search for calls from both `HasJobOnThing` and `JobOnThing`.

- Null and error handling
  - Check all APIs that can return null:
    - `TryGetInnerInteractableThingOwner`, `GetSlotGroup(map)`, `TryFindBestBetterStorageFor` results.
  - Ensure every branch handles nulls before dereference; flag any deref after optional retrieval without checking.

- Reflection resilience (compat)
  - `HoldMultipleThings` wrapper: confirm all reflection calls are wrapped in try/catch and that fallbacks (stackLimit paths) are correct when reflection fails.

- Pathfinding/Reachability checks
  - Ensure every path that selects a target also guards with `map.reachability.CanReach` or equivalent.

## Code Hotspots to Review

- `Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs`
  - `HasJobOnThing(...)` vs `JobOnThing(...)`
  - `AllocateThingAtCell(...)`
  - `TryReserveSafely(...)`, `ReleaseReservationSafely(...)`
  - Mutable comparer: `ThingPositionComparer` + `Comparer` static instance

- `Source/PickUpAndHaul/PRS/HarmonyPatches_ReservationSystem.cs`
  - `CanReserve_Prefix`, `Reserve_Prefix` lock/capacity critical sections
  - End-of-job reservation transfer and cleanup

- `Source/PickUpAndHaul/PRS/ReservationSystem.cs`
  - `_reservationLock` usage
  - `GetAvailableCapacity(...)` math and stackLimit enforcement
  - `TransferReservationsForPawnToJob(...)`, cleanup paths

- `Source/PickUpAndHaul/Cache/*`
  - Cache (de)classification and stale entry cleanup
  - Storage cache lifetime and invalidation

## Smell/Pattern Searches (exact strings)
- Queue integrity: `targetQueueA.Add(`, `countQueue.Add(`, `countQueue.Pop(`, `RemoveAt(job.targetQueueA.Count - 1)`
- Reservations: `.Reserve(`, `.Release(`, `ReleaseAllReservationsForJob`, `ReleaseAllReservationsForPawn`
- Locks: `lock (_reservationLock)`, `GetDestLock(`, `lock (destLock)`
- Sleep: `Thread.Sleep(`
- Mutable comparator: `ThingPositionComparer`, `Comparer.rootCell =`
- Risky deref: `.GetSlotGroup(`, `.TryGetInnerInteractableThingOwner()`, `.Thing` after checking `IsValid`
- Capacity math: `GetAvailableCapacity(`, `GetCountCanAccept(`, `stackLimit`

## Findings Format (for each issue)
- Your normal format is fine, but include the next two things as well
- Problem type: Contract mismatch, Queue desync, Locking risk, Null risk, Capacity inconsistency, Sleep on main thread, Mutable static state
- Minimal fix suggestion (one sentence)