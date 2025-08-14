# PUAH × PRS: Planned/Implemented Changes and Safe Approaches

This note captures the full set of changes we'd apply (and why), plus multiple safe approaches for allowing concurrent storage access without overfills. Use this as a temporary plan/checklist.

---

## Current goals
- Never pick up more than can be dropped later.
- Avoid "false capacity" races between pawns that share a destination.
- Maintain vanilla compatibility while enabling capacity‑accurate multi‑pawn sharing.
- Provide high‑signal diagnostics in `PUAHForked.log` tied to pawn and thing.

---

## Changes implemented (and reasons)

### 1) Hard pre‑reserve at pickup (guarantee)
- Where: `JobDriver_HaulToInventory` pickup toil.
- What: Before splitting the source stack, compute PRS capacity across valid destinations and pre‑reserve the exact pickup amount; if we can't lock it all, reduce pickup to the reserved sum.
- Why: Guarantees we don't pick more than can be dropped, even if other jobs exist.
- Logs: "Pickup clamp scan …" + "PreReserve at pickup …" + "PreReserve shortfall …".

### 2) Optional pre‑reserve while enqueueing unload
- Where: `JobDriver_HaulToInventory` when queuing the unload job.
- What: Opportunistically reserves capacity for each PUAH‑tracked item carried.
- Why: Further reduces races if other jobs probe the same spots between pickup and unload.
- Logs: "Pre-reserved unload …".

### 3) Storage relocation and cache invalidation
- Where: `WorkGiver_HaulToInventory.AllocateThingAtCell`.
- What: Invalidate cache and relocate when a cell is full; avoid repeating the same cell; reserve the new destination and adjust counts.
- Why: Prevents the "partial 1 and stop" behavior; allows multi‑cell distribution.
- Logs: "overdone by X", "New cell … allocated extra …".

### 4) Transfer PRS reservations to unload job
- Where: `JobDriver_UnloadYourHauledInventory.MakeNewToils` (start of unload).
- What: Retag PRS entries from the pickup job to the current unload job for this pawn (optionally filtered by `ThingDef`).
- Why: Unload now "sees" its own reserved capacity (reservedByThisJob), avoids premature cleanup and prioritizes owned spots.
- API: `PRSReservationSystem.TransferReservationsForPawnToJob(pawn, pawn.CurJob, pawn.Map, defFilter)`.
- Logs: "Unload: transferred N PRS reservation entries …".

### 5) Unload prefers own reservations and retries before dropping
- Where: `JobDriver_UnloadYourHauledInventory.FindTargetOrDrop`.
- What:
  - Effective capacity = reservedByThisJob, else "reserved by this pawn (any job)", else raw available.
  - If vanilla can't reserve any destination this tick but PRS shows this pawn still owns capacity somewhere, jump back and retry instead of dropping.
- Why: Handles momentary vanilla reservations (another pawn still at the cell) without spilling.
- Logs: "Unload pick dest …", "Unload will place …", and retry line: "could not reserve any cell this tick … waiting and retrying".

### 6) Capacity checks for vanilla job creation
- Where: `HarmonyPatches_HaulAI` postfixes.
- What: Cancel or downscale vanilla `HaulToStorageJob` based on PRS capacity.
- Why: Prevents late failures and overfills from vanilla paths.
- Logs: "PRS.Postfix HaulToStorageJob CANCEL/ADJUST".

### 7) Debug and capacity diagnostics
- Where: `PRSReservationSystem.GetAvailableCapacity` and across toils.
- What: Second log line per capacity probe, tied to pawn/thing where possible.
- Why: End‑to‑end transparency in `PUAHForked.log`.

---

## Additional safe approaches (for concurrent storage access)

Below are staged options to let multiple pawns "share" a cell concurrently. Each builds on PRS as the truth for partial capacity, while retaining vanilla safety.

### Approach A: PRS‑gated multi‑reserve (cells and/or containers)
- Patch: Harmony prefix on `ReservationManager.CanReserve` and `ReservationManager.Reserve`; postfix on `ReservationManager.Release`.
- Logic:
  - Compute the effective store destination from the job (prefer `job.targetB`, else first of `job.targetQueueB`).
  - If destination is a storage cell (slot group) or storage container and `PRS.GetAvailableCapacity(effectiveDest, thing, map) > 0`, allow additional reservations even if vanilla shows occupied.
  - During `Reserve`, record partial reservations in PRS against the effective destination using the same bucket used for capacity.
  - Revalidate capacity right before placement; PRS and vanilla toils remain the final gate.
- Guardrails:
  - Scope to storage destinations only (slot groups and/or containers) and to hauling/unload jobs.
  - Optional container‑only mode (see D) for lower risk.
  - Feature toggle (e.g., `Settings.EnableSharedReservations`) and keep debug logging path for diagnostics.
  - Deny if PRS capacity is 0.
- Pros: Minimal change with PRS as admission control; matches current implementation direction.
- Cons: Must ensure no unrelated systems assume exclusive single reservation per destination.

### Approach B: PRS "virtual layer" (no custom `ReservationLayerDef`)
- Patch: None beyond Approach A hooks; do not introduce a custom reservation layer asset.
- Logic: Treat PRS bookkeeping as a virtual shared layer on top of vanilla's single reservation layer; rely on PRS capacity to selectively relax `CanReserve` and on PRS entries recorded during `Reserve` to avoid overbooking.
- Guardrails: Same as Approach A; feature toggle controls activation.
- Pros: Avoids incompatibility since vanilla/other mods won’t use a custom layer automatically; leverages the PRS ledger already in place.
- Cons: Still requires careful gating in `CanReserve` to avoid lifting reservations outside storage/hauling contexts.

### Approach C: Opportunistic convergence with small wait windows
- Patch: Leave vanilla reservations intact; in `JobDriver_UnloadYourHauledInventory`, keep a brief wait/retry when a pawn has PRS‑reserved capacity but can't reserve the cell this tick.
- Logic: Already implemented as a single‑tick retry; can extend to a bounded window (e.g., 30–90 ticks) that exits early when reserve succeeds or when PRS shows this pawn no longer owns capacity.
- Pros: Safe; no changes to `ReservationManager` required.
- Cons: Serialized (not truly concurrent), but eliminates drops and reduces conflicts.

### Approach D: Container‑only concurrency
- Patch: Apply Approach A only when the effective destination is a storage container (e.g., `IHaulDestination` with `TryGetInnerInteractableThingOwner() != null`).
- Logic: Many containers already manage capacity internally; multi‑reserve here is lower risk than for floor cells.
- Pros: Narrow blast radius; aligns with mods that already support multi‑stack placement.
- Cons: Does not help for plain floor cells.

### Approach E: Strict PRS‑gated multi‑reserve with fallback
- Patch: Combine A (or D) with defensive fallbacks:
  - If placement fails at runtime (e.g., filter changed), re‑probe/re‑queue or retry instead of dropping immediately.
  - Release PRS reservations immediately on failure so capacity is restored.
- Pros: Encourages progress without spills; robust to dynamic changes.
- Cons: Slightly more complexity in job state handling.

---

## Concrete hooks to add if we enable A/D
- Prefix on `ReservationManager.CanReserve(…, Pawn pawn, LocalTargetInfo target, …)`:
  - Derive the effective store destination from the job (`job.targetB` or first of `job.targetQueueB`).
  - If destination is a storage cell or (for D) a container and PRS reports capacity for the hauled `thing.def`, set result true.
- Prefix on `ReservationManager.Reserve(…, Pawn pawn, Job job, …)`:
  - Under the same conditions, record a PRS partial reservation for the effective destination, limited by PRS capacity and the immediate pickup intent.
- Postfix on `ReservationManager.Release(…)`:
  - Release PRS entries for the `pawn+job` at that destination to keep PRS in sync.
- Safety:
  - Gate behind a feature toggle (e.g., `Settings.EnableSharedReservations`), keep debug logging; scope to hauling/unload jobs only.

### Viability summary
- A: Viable with the guardrails above; recommended when toggle is enabled.
- B: Replace with PRS "virtual layer" (no custom `ReservationLayerDef`).
- C: Viable and already implemented (single‑tick); can extend to short bounded retry.
- D: Viable and lower risk; recommended as a first step if enabling concurrency.
- E: Viable; complements A/D with robust fallbacks.

---

## Test plan
- Single pawn with multiple partial cells: verify full delivery, no drops.
- Two pawns, same destination set: verify no drops, both unload fully; check retry logs when cell is briefly reserved.
- Capacity changes mid‑haul (filters/priorities): ensure pre‑reserve shortfalls clamp pickup; unload retries and then re‑probes.
- Containers (if present): verify container capacity accounting via reflection hook remains valid.

---

## Rollback plan
- All changes are localized:
  - Disable pre‑reserve features via a flag if needed.
  - Remove/disable `ReservationManager` patches if concurrency causes regressions.
  - Keep PRS logging on to diagnose.

---

## Files touched/added
- `Source/PickUpAndHaul/JobDriver_HaulToInventory.cs`: pre‑reserve at pickup; optional pre‑reserve at unload enqueue; clamp & logs.
- `Source/PickUpAndHaul/WorkGiver_HaulToInventory.cs`: relocation, cache invalidation, capacity checks, logs.
- `Source/PickUpAndHaul/JobDriver_UnloadYourHauledInventory.cs`: transfer reservations to unload, prefer own reservations, retry instead of drop, logs.
- `Source/PickUpAndHaul/PRS/ReservationSystem.cs`: new APIs for transfer/get reserved counts, stronger cleanup rules, detailed capacity logs.
- `Source/PickUpAndHaul/PRS/HarmonyPatches_HaulAI.cs`: capacity cancellations/adjustments for vanilla hauling.

---

## Notes
- True concurrent access requires careful, minimal hooks into `ReservationManager`. Start with Approach C (already in), then consider D (containers only), then A (PRS‑gated) with a feature toggle; E adds robust fallbacks.
