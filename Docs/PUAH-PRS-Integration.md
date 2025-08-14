# Pick Up And Haul × Partial Reservation System

## Overview
This document explains how Pick Up And Haul (PUAH) integrates the Partial Reservation System (PRS) to enable capacity‑accurate, conflict‑free hauling where multiple pawns can share a destination. The integration keeps vanilla reservations intact while tracking partial destination capacity internally, so pawns can split stacks cleanly (e.g., one pawn hauls 60 while another hauls 15) without overfilling storage or thrashing jobs.

---

## Goals
1. Capacity‑accurate destination accounting in PUAH
   - Respect live stack contents, PRS partial reservations, and pending hauls.
2. Keep vanilla compatibility and mod interop
   - Always allow vanilla `ReservationManager` to proceed; record PRS reservations alongside.
3. Clamp pickup to what the destination will accept, with a hard pre‑reserve guarantee
   - We pre‑reserve the exact pickup amount across destinations before splitting the source stack, then reduce the pickup if we can’t lock all space.
4. Work smoothly with PUAH’s multi‑pickup/inventory job flow
   - Properly reserve/relocate across multiple candidate storage targets.
5. Auto‑cleanup
   - Periodic purge of stale PRS reservations and pending hauls.

---

## Core Pieces (PUAH)
- Capacity and reservations (integrated PRS):
  - `PickUpAndHaul/PRS/ReservationSystem.cs`
  - `PickUpAndHaul/PRS/PendingHaulTracker.cs`
  - `PickUpAndHaul/PRS/PRS_Cleanup.cs` (MapComponent)
- Harmony integration:
  - `PickUpAndHaul/PRS/HarmonyPatches_ReservationSystem.cs` (hooks `ReservationManager` and job lifecycle)
  - `PickUpAndHaul/PRS/HarmonyPatches_ReservationUtility_And_Toils.cs` (clamp at pickup; release on placement)
  - `PickUpAndHaul/PRS/HarmonyPatches_HaulAI.cs` (capacity checks for job creation and storage selection)
- PUAH job flow integration:
  - `PickUpAndHaul/WorkGiver_HaulToInventory.cs` (storage search, reservation/relocation, capacity checks)
  - `PickUpAndHaul/JobDriver_UnloadYourHauledInventory.cs` (unload flow; destination reserve and state flags)
- Support & diagnostics:
  - `PickUpAndHaul/PRS/Settings.cs` (debug toggle for PRS logs)
  - `PickUpAndHaul/PRS/DebugLog.cs`

PUAH also continues to use its cache system (see `PickUpAndHaul/Source/PickUpAndHaul/Cache/*`) unchanged.

---

## How Capacity Is Computed (PUAH)
`PRSReservationSystem.GetAvailableCapacity(location, thing, map)` returns how many of `thing.def` can be accepted now. It considers:
- Current contents at the destination cell/container (vanilla stack counts, or `IHoldMultipleThings` via reflection).
- PRS partial reservations recorded for that `StorageLocation` and `ThingDef`.
- Pending amounts from interrupted hauling (`PendingHaulTracker`).
- Per‑pickup stack limits.

This value is used by:
- `WorkGiver_HaulToInventory` when validating the chosen storage target and when relocating to an alternative target during queue building.
- `HaulAIUtility.HaulToStorageJob` postfix to null jobs with no capacity or downscale job `count` if capacity is smaller than the immediate pickup amount.
- `StoreUtility.TryFindBestBetterStoreCellFor` postfix to reject cells with no effective capacity.

---

## What Gets Reserved and When
- Pre‑reserve at pickup (hard guarantee): before the pawn splits and takes from the source stack, we pre‑reserve the exact pickup amount across valid destinations and reduce the pickup if we can’t lock it all. This guarantees we don’t pick more than can be dropped later.
- Optional pre‑reserve when queuing unload: after pickup and just before enqueueing the unload job, we also attempt to reserve capacity for each carried PUAH item. This further reduces races if other jobs try to target the same cells.
- On `ReservationManager.Reserve` (vanilla), PRS records a partial destination reservation for the pawn/job/def alongside vanilla, so multiple pawns can share a destination up to capacity.
- Reservation persistence: reservations are kept while the pawn is on the job or if that job is still queued for the pawn, and are released at drop‑off via `ReservationManager.Release` hooks.

---

## Clamping at Pickup
We now clamp with a pre‑reserve guarantee at pickup time:
- We scan PRS capacity across valid destinations, then pre‑reserve the exact pickup amount across those destinations.
- If we can’t secure all of it immediately, we reduce the pickup down to the successfully reserved total before splitting the source stack.

Example:
- Destination capacity 75; currently 20 present.
- Pawn A pre‑reserves 60, picks 60; capacity becomes 5.
- Pawn B later pre‑reserves 5, picks 5.
- Both deliver; reservations release; no overfill, no race.

---

## PUAH WorkGiver and JobDriver Integration
### WorkGiver_HaulToInventory
- Uses PRS capacity when validating the primary storage target and when relocating to another target mid‑allocation:
  - `PRSReservationSystem.GetAvailableCapacity(new StorageLocation(cellOrContainer), thing, map)`
- Attempts to reserve the destination before committing a queue entry; releases stale reservations if the allocation relocates.
- Invalidates cached storage when a location is full to avoid re‑selecting the same cell repeatedly; then relocates to an alternative cell/container.
- Avoids starting new PUAH pickup jobs for pawns that:
  - Already carry PUAH‑tracked items (must unload first), or
  - Are currently unloading, or
  - Are targeting a stack recently unloaded by the same pawn (small cooldown window).

### JobDriver_UnloadYourHauledInventory
- Marks the pawn as unloading for the duration of the job.
- Prefers a destination already reserved by this pawn/job in PRS; otherwise finds and reserves a valid alternative.
- Only unloads up to the reserved/available amount at that destination; then iterates to find the next destination.
- After pulling an item from inventory, marks it as “recently unloaded” (cooldown) to prevent immediate re‑targeting for opportunistic pickup.
- Releases the vanilla reservation directly; PRS reservations are released via the `ReservationManager.Release` hook.

---

## Pending Hauls and Cleanup
- If a hauling job is interrupted (`EndCurrentJob` not Succeeded), PUAH records a pending haul entry for the pawn/def/destination (`PendingHaulTracker.RecordPendingHaul`). Pending amounts subtract from capacity to avoid double allocation.
- `PRS_MapComponent` runs on each map every ~250 ticks to purge stale PRS reservations and expired/invalid pending entries.

---

## Interop and Compatibility
- Vanilla `ReservationManager.CanReserve`/`Reserve` are not blocked; PRS augments them and may allow multiple pawns to reserve the same destination if capacity remains.
- Capacity integrates with `IHoldMultipleThings` containers or slot parents via reflection (no hard dependency).
- PUAH caches remain the authority for finding accessible haulables and candidate storage; PRS only refines capacity and reservations.

---

## Debug and Settings
- PRS debug logging is enabled by default within PUAH (`PartialReservationSystem.Settings`).
- PUAH has its own debug logging toggle (`PickUpAndHaul.Settings`).
- Logs are written to RimWorld’s console and to mod‑specific files when available.

Key log lines to look for (in `PUAHForked.log`):
- `PRS.GetAvailableCapacity ... totalCap=.. current=.. reserved=.. pending=.. => available=..` — capacity accounting per location.
- `PRS.Postfix HaulToStorageJob ADJUST/CANCEL` — vanilla haul jobs adjusted or cancelled by PRS capacity.
- `PreReserve at pickup: ...` and `PreReserve shortfall: ...` — hard guarantee at pickup time.
- `PreReserveUnload: ...` — optional pre‑reserve while enqueueing unload.
- `Unload will place N at ...` — stepwise unload schedule.

---

## Quick Reference: Key Call Sites
- Capacity checks:
  - `WorkGiver_HaulToInventory` → `PRSReservationSystem.GetAvailableCapacity(...)`
  - `HarmonyPatches_HaulAI` (job creation and store cell search)
- Pre‑reserve at pickup (hard guarantee):
  - `JobDriver_HaulToInventory` pickup toil computes PRS capacity, reserves the exact pickup amount, then splits the source stack.
- Optional pre‑reserve for unload:
  - `JobDriver_HaulToInventory` when enqueueing unload.
- Partial reservations & lifecycle:
  - `ReservationManager.Reserve/CanReserve/Release` prefixes/postfixes (in PRS patches)
  - `ReservationManager.Release` postfix releases PRS reservations at drop‑off
  - `PendingHaulTracker` on job interruption and cleanup

---

## FAQ
- Will this break vanilla reservations?
  - No. Vanilla reservations are preserved; PRS reservations are additional accounting to allow partial, capacity‑aware sharing.
- Do I need to load the standalone PRS mod?
  - No. PUAH embeds PRS; don’t load standalone PRS alongside PUAH to avoid duplicate patches.

---

## Example Walkthrough (“60 and 15”)
1. A storage cell has space for 75 of Uranium; currently 0 there.
2. Pawn A is assigned a PUAH job. PRS capacity reports 75; A pre‑reserves 60 (based on immediate pickup) and picks 60.
3. Pawn B sees 15 capacity remaining; pre‑reserves 15 and picks 15.
4. Both deliver; drop‑off releases PRS reservations; the cell ends with 75 exactly.
