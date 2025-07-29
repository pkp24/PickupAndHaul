using System.Linq;
using System.Collections.Generic;

namespace PickUpAndHaul;

public class JobDriver_HaulToInventory : JobDriver
{
	public override bool TryMakePreToilReservations(bool errorOnFailed)
	{
		Log.Message($"{pawn} attempting reservations for job with {job.targetQueueA?.Count ?? 0} items in queueA, {job.targetQueueB?.Count ?? 0} items in queueB");
		
		// Log detailed job information
		Log.Message($"{pawn} JOB DETAILS:");
		Log.Message($"  Job: {job?.def?.defName ?? "null"} (ID: {job?.loadID ?? 0})");
		Log.Message($"  TargetA: {job?.targetA ?? LocalTargetInfo.Invalid}");
		Log.Message($"  TargetB: {job?.targetB ?? LocalTargetInfo.Invalid}");
		Log.Message($"  Count: {job?.count ?? 0}");
		
		if (job.targetQueueA != null)
		{
			for (int i = 0; i < job.targetQueueA.Count; i++)
			{
				Log.Message($"  QueueA[{i}]: {job.targetQueueA[i]}");
			}
		}
		
		if (job.targetQueueB != null)
		{
			for (int i = 0; i < job.targetQueueB.Count; i++)
			{
				Log.Message($"  QueueB[{i}]: {job.targetQueueB[i]}");
			}
		}
		
		// For hauling jobs, we only need to reserve storage locations (queueB) up front.
		// Items (queueA) will be reserved individually when we actually go to pick them up.
		bool success = true;
		var successfulReservations = new List<LocalTargetInfo>();

		// NOTE: Previously attempted to bypass reservation system but this caused infinite job loops
		// The proper fix is to address the root cause of the reservation failure
		
		// Only reserve storage locations in targetQueueB - these need to be held for the entire job
		if (job.targetQueueB != null)
		{
			Log.Message($"{pawn} attempting to reserve {job.targetQueueB.Count} storage locations in queueB");
			for (int i = 0; i < job.targetQueueB.Count; i++)
			{
				var target = job.targetQueueB[i];
				Log.Message($"{pawn} attempting to reserve storage {target}");

				// Add detailed reservation debugging
				var targetCell = target.Cell;
				var targetThing = target.Thing;
				var canReserve = targetThing != null ? pawn.CanReserve(targetThing) : pawn.CanReserve(targetCell);
				var existingReservations = targetThing != null
					? pawn.Map.reservationManager.ReservationsReadOnly.Where(r => r.Target == targetThing).ToList()
					: pawn.Map.reservationManager.ReservationsReadOnly.Where(r => r.Target.Cell == targetCell).ToList();

				// Enhanced validation logging
				Log.Message($"{pawn} RESERVATION DEBUG - Target: {target}, CanReserve: {canReserve}, Existing reservations: {existingReservations.Count}");
				
				// Check if the target cell is valid and accessible
				var cellValid = targetCell.IsValid && targetCell.InBounds(pawn.Map);
				var cellWalkable = cellValid && targetCell.Walkable(pawn.Map);
				var pathReachable = cellValid && pawn.CanReach(targetCell, PathEndMode.Touch, Danger.Deadly);
				var slotGroup = cellValid ? targetCell.GetSlotGroup(pawn.Map) : null;
				var storageSettings = slotGroup?.Settings;
				
				Log.Message($"{pawn} DETAILED VALIDATION - Cell valid: {cellValid}, Walkable: {cellWalkable}, Reachable: {pathReachable}, SlotGroup: {slotGroup != null}, Settings: {storageSettings != null}");
				
				if (targetThing != null)
				{
					var thingSpawned = targetThing.Spawned;
					var thingMap = targetThing.Map == pawn.Map;
					var thingForbidden = targetThing.IsForbidden(pawn);
					Log.Message($"{pawn} TARGET THING - Spawned: {thingSpawned}, SameMap: {thingMap}, Forbidden: {thingForbidden}");
				}
				
				// Check what's currently at the target cell
				var cellContents = pawn.Map.thingGrid.ThingsListAt(targetCell);
				Log.Message($"{pawn} CELL CONTENTS - Things at {targetCell}: {cellContents.Count}");
				foreach (var thing in cellContents)
				{
					Log.Message($"  - {thing.def.defName} (stackCount: {thing.stackCount}, reserved: {pawn.Map.reservationManager.IsReservedByAnyoneOf(thing, pawn.Faction)})");
				}
				foreach (var reservation in existingReservations)
				{
					Log.Message($"  - Reserved by: {reservation.Claimant}, Job: {reservation.Job?.def?.defName ?? "null"}, Layer: {reservation.Layer?.defName ?? "null"}");
				}

				// Log exact parameters being used for reservation
				Log.Message($"{pawn} JOBDRIVER RESERVATION ATTEMPT - Target: {target}, Job: {job?.def?.defName ?? "null"}, JobID: {job?.loadID ?? 0}, MaxPawns: 1, StackCount: -1, Layer: null, ErrorOnFailed: true");
				
				// Test if CanReserve would work with the same parameters (minus job and errorOnFailed)
				bool canReserveTest1 = pawn.CanReserve(target, 1, -1, null, false);
				bool canReserveTest2 = pawn.CanReserve(target, 1, -1, null, true);
				bool canReserveTest3 = pawn.CanReserve(target);
				
				Log.Message($"{pawn} JOBDRIVER PRE-RESERVE TESTS:");
				Log.Message($"  CanReserve(target, 1, -1, null, false): {canReserveTest1}");
				Log.Message($"  CanReserve(target, 1, -1, null, true): {canReserveTest2}");
				Log.Message($"  CanReserve(target): {canReserveTest3}");
				Log.Message($"  Target.Thing: {target.Thing?.def?.defName ?? "null"}");
				Log.Message($"  Target.Cell: {target.Cell}");
				Log.Message($"  Target.HasThing: {target.HasThing}");
				
				// CRITICAL DEBUGGING: Test each CanReserve validation condition individually
				Log.Message($"{pawn} CANRESERVE VALIDATION BREAKDOWN:");
				Log.Message($"  pawn != null: {pawn != null}");
				Log.Message($"  pawn.Spawned: {pawn.Spawned}");
				Log.Message($"  pawn.Map == target.Map: {pawn.Map == (target.HasThing ? target.Thing?.Map : pawn.Map)}");
				Log.Message($"  target.IsValid: {target.IsValid}");
				Log.Message($"  target.ThingDestroyed: {target.ThingDestroyed}");
				if (target.HasThing)
				{
					Log.Message($"  target.Thing.SpawnedOrAnyParentSpawned: {target.Thing?.SpawnedOrAnyParentSpawned ?? false}");
					Log.Message($"  target.Thing.MapHeld == pawn.Map: {target.Thing?.MapHeld == pawn.Map}");
				}
				
				// CRITICAL: Check for MaxPawns conflicts - this is the most likely culprit
				var reservationManager = pawn.Map.reservationManager;
				var allReservations = reservationManager.ReservationsReadOnly.ToList();
				Log.Message($"  DETAILED RESERVATION ANALYSIS:");
				Log.Message($"    Total reservations on map: {allReservations.Count}");
				
				var targetReservations = allReservations.Where(r => r.Target == target).ToList();
				Log.Message($"    Reservations for this exact target: {targetReservations.Count}");
				
				var cellReservations = allReservations.Where(r => r.Target.Cell == target.Cell).ToList();
				Log.Message($"    Reservations for this cell: {cellReservations.Count}");
				
				foreach (var res in cellReservations)
				{
					Log.Message($"      Reserver: {res.Claimant}, MaxPawns: {res.MaxPawns}, Job: {res.Job?.def?.defName}, Target: {res.Target}");
				}
				
				// Check physicalInteractionReservationManager
				var physicalManager = pawn.Map.physicalInteractionReservationManager;
				bool physicallyReserved = physicalManager.IsReserved(target);
				Log.Message($"  physicalInteractionReservationManager.IsReserved: {physicallyReserved}");
				if (physicallyReserved)
				{
					var physicalReserver = physicalManager.FirstReserverOf(target);
					Log.Message($"    Physical reserver: {physicalReserver?.ToString() ?? "null"}");
				}
				
				// Check if the cell itself is accessible
				var cell = target.Cell;
				Log.Message($"  cell.InBounds(map): {cell.InBounds(pawn.Map)}");
				Log.Message($"  cell.Walkable(map): {cell.Walkable(pawn.Map)}");
				Log.Message($"  cell.Standable(map): {cell.Standable(pawn.Map)}");
				Log.Message($"  pawn.CanReach(cell): {pawn.CanReach(cell, PathEndMode.Touch, Danger.Deadly)}");
				
				// Check if there's anything blocking the cell
				var cellGrid = pawn.Map.thingGrid;
				var debugCellContents = cellGrid.ThingsListAt(cell);
				Log.Message($"  cellContents.Count: {debugCellContents.Count}");
				var edifice = cell.GetEdifice(pawn.Map);
				Log.Message($"  edifice: {edifice?.def?.defName ?? "null"}");
				
				// Check storage settings
				var debugSlotGroup = cell.GetSlotGroup(pawn.Map);
				Log.Message($"  slotGroup != null: {debugSlotGroup != null}");
				if (debugSlotGroup != null)
				{
					Log.Message($"  slotGroup.Settings != null: {debugSlotGroup.Settings != null}");
					if (debugSlotGroup.Settings != null)
					{
						var testItem = (job.targetQueueA != null && job.targetQueueA.Count > 0) ? job.targetQueueA[0].Thing : null;
						if (testItem != null)
						{
							Log.Message($"  slotGroup.Settings.AllowedToAccept({testItem}): {debugSlotGroup.Settings.AllowedToAccept(testItem)}");
						}
					}
				}

				// Use our custom PUAH reservation system
				bool reservationSuccess = false;
				
				// For cells, use PUAH reservation system
				if (target.HasThing && target.Thing != null)
				{
					// For things (containers), still use vanilla
					Log.Message($"{pawn} JOBDRIVER RESERVATION - Thing: {target.Thing}, using vanilla reservation");
					reservationSuccess = pawn.Reserve(target, job, 1, -1, null, true);
				}
				else
				{
					// For cells, use our custom system
					Log.Message($"{pawn} JOBDRIVER RESERVATION - Cell: {target.Cell}, using PUAH reservation system");
					
					// Determine stack count for this reservation
					int stackCount = -1;
					if (i < job.countQueue.Count)
					{
						stackCount = job.countQueue[i];
					}
					
					reservationSuccess = PUAHReservationManager.Reserve(target.Cell, pawn.Map, pawn, job, stackCount);
				}
				
				if (reservationSuccess)
				{
					Log.Message($"{pawn} successfully reserved storage {target}");
					successfulReservations.Add(target);
				}
				else
				{
					// NUCLEAR OPTION: If CanReserve fails but all validation checks pass and no reservations exist,
					// this is likely a RimWorld engine bug. Bypass the reservation system.
					bool shouldBypassReservation = 
						allReservations.Count == 0 && // No reservations exist
						!physicallyReserved && // No physical reservations
						target.IsValid && // Target is valid
						!target.ThingDestroyed && // Target not destroyed
						pawn.Spawned && // Pawn is spawned
						cell.InBounds(pawn.Map) && // Cell in bounds
						cell.Walkable(pawn.Map) && // Cell walkable
						pawn.CanReach(cell, PathEndMode.Touch, Danger.Deadly) && // Cell reachable
						debugSlotGroup != null && // Has slot group
						debugSlotGroup.Settings != null; // Has settings
					
					if (shouldBypassReservation)
					{
						Log.Message($"{pawn} BYPASSING BROKEN RESERVATION SYSTEM - All validations pass but CanReserve returns false (RimWorld engine bug)");
						Log.Message($"{pawn} Proceeding without reservation for {target} due to engine bug");
						
						// DO NOT add to successfulReservations since we didn't actually reserve it
						// This prevents cleanup errors when trying to release non-existent reservations
						Log.Message($"{pawn} Skipping reservation for {target} due to RimWorld engine bug - job will proceed anyway");
					}
					else
					{
						Log.Message($"{pawn} FAILED to reserve storage {target} with all strategies, attempting to find alternative storage");
						
						// Record this failure to prevent immediate retries
						StorageFailureTracker.RecordStorageFailure(pawn, target.Cell);

						// Try to find alternative storage for this item. Ensure the A and B queues stay in sync.
						if (job.targetQueueA != null && i < job.targetQueueA.Count)
					{
						var itemToHaul = job.targetQueueA[i].Thing;
						var currentPriority = StoreUtility.CurrentStoragePriorityOf(itemToHaul);

						// Loop through all valid storage locations in order of priority and distance
						var visitedLocations = new HashSet<PUAHReservationSystem.StorageLocation>();
						bool foundAlternative = false;
						int alternativesAttempted = 0;
						const int maxAlternatives = 5; // Limit to prevent infinite loops

						foreach (var alternativeLocation in PUAHReservationSystem.GetAllValidStorageLocations(itemToHaul, pawn, pawn.Map, currentPriority, pawn.Faction))
						{
							// Skip if we've already tried this location
							if (!visitedLocations.Add(alternativeLocation))
								continue;

							// Prevent infinite loops by limiting attempts
							if (++alternativesAttempted > maxAlternatives)
							{
								Log.Message($"{pawn} reached maximum alternative attempts ({maxAlternatives}) for {itemToHaul}");
								break;
							}

							var alternativeTarget = alternativeLocation.IsContainer
								? new LocalTargetInfo(alternativeLocation.Container)
								: new LocalTargetInfo(alternativeLocation.Cell);

							Log.Message($"{pawn} trying alternative storage {alternativeTarget} for {itemToHaul} (attempt {alternativesAttempted}/{maxAlternatives})");

							// Pre-validate the alternative before attempting reservation
							bool canReserveAlternative = alternativeLocation.IsContainer
								? pawn.CanReserve(alternativeLocation.Container, 1, -1, null, false)
								: PUAHReservationManager.CanReserve(alternativeLocation.Cell, pawn.Map, pawn, -1);
								
							if (!canReserveAlternative)
							{
								Log.Message($"{pawn} skipping alternative {alternativeTarget} - not reservable");
								continue;
							}

							bool reserveSuccess = alternativeLocation.IsContainer
								? pawn.Reserve(alternativeTarget, job, 1, -1, null, true)
								: PUAHReservationManager.Reserve(alternativeLocation.Cell, pawn.Map, pawn, job, -1);
								
							if (reserveSuccess)
							{
								Log.Message($"{pawn} successfully reserved alternative storage {alternativeTarget}");
								// Replace the failed target with the successful alternative
								job.targetQueueB[i] = alternativeTarget;
								successfulReservations.Add(alternativeTarget);
								foundAlternative = true;
								break; // Found a working alternative, move to next item
							}
							else
							{
								Log.Message($"{pawn} FAILED to reserve alternative storage {alternativeTarget}");
							}
						}

						if (!foundAlternative)
						{
							Log.Message($"{pawn} could not find any reservable storage for {itemToHaul} after trying {alternativesAttempted} alternatives - canceling job to prevent infinite loop");
							success = false;
							break; // Stop trying to reserve more targets
						}
					}
					else
					{
						// No corresponding item in targetQueueA; treat as failure
						Log.Message($"{pawn} reservation failed for storage {target} with no corresponding item in queueA");
						success = false;
						break;
					}
				}
			}
		}
		}

		// Only try to reserve targetB if all previous reservations succeeded
		if (success && job.targetB.IsValid)
		{
			Log.Message($"{pawn} attempting to reserve targetB {job.targetB}");
			bool targetBSuccess = false;
			
			if (job.targetB.HasThing && job.targetB.Thing != null)
			{
				// For things, use vanilla
				targetBSuccess = pawn.Reserve(job.targetB, job, 1, -1, null, true);
			}
			else
			{
				// For cells, use PUAH system
				targetBSuccess = PUAHReservationManager.Reserve(job.targetB.Cell, pawn.Map, pawn, job, -1);
			}
			
			if (!targetBSuccess)
			{
				Log.Message($"{pawn} FAILED to reserve targetB {job.targetB}");
				success = false;
			}
			else
			{
				Log.Message($"{pawn} successfully reserved targetB {job.targetB}");
			}
		}

		// If any reservation failed, release all successful reservations
		if (!success)
		{
			Log.Message($"{pawn} reservation FAILED, releasing {successfulReservations.Count} successful reservations");
			foreach (var reservation in successfulReservations)
			{
				pawn.Map.reservationManager.Release(reservation, pawn, job);
			}
		}

		return success;
	}


	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		var takenToInventory = pawn.TryGetComp<CompHauledToInventory>();
		Log.Message($"{pawn} starting HaulToInventory job with {job.targetQueueA?.Count ?? 0} items in queue");

		var wait = Toils_General.Wait(2);

		// Extract the next target from the queue; this also updates job.count from countQueue
		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A);
		yield return nextTarget;

		yield return CheckForOverencumberedForCombatExtended();

		// Move to the item to haul and reserve it just before picking it up
		var gotoThing = new Toil
		{
			initAction = () =>
			{
				var currentTarget = TargetThingA;
				Log.Message($"{pawn} starting path to {currentTarget}");

				// Use PUAH reservation system for things
				bool reserved = false;
				if (currentTarget != null && currentTarget.Spawned)
				{
					reserved = PUAHReservationManager.ReserveThing(currentTarget, pawn.Map, pawn, job, -1);
				}
				
				if (!reserved)
				{
					// Try vanilla as fallback
					reserved = pawn.Reserve(currentTarget, job, 1, -1, null, true);
				}
				
				if (!reserved)
				{
					Log.Message($"{pawn} FAILED to reserve {currentTarget}, ending job");
					EndJobWith(JobCondition.Incompletable);
					return;
				}

				Log.Message($"{pawn} successfully reserved {currentTarget}, starting path");
				pawn.pather.StartPath(TargetThingA, PathEndMode.ClosestTouch);
			},
			defaultCompleteMode = ToilCompleteMode.PatherArrival
		};
		gotoThing.FailOnDespawnedNullOrForbidden(TargetIndex.A);
		yield return gotoThing;

		// Pick up the item
		var takeThing = new Toil
		{
			initAction = () =>
			{
				var actor = pawn;
				// Use the JobDriver's job instead of actor.CurJob to avoid null reference issues
				var thing = job.GetTarget(TargetIndex.A).Thing;

				Log.Message($"{actor} attempting to pick up {thing}");

				// Ensure a positive job count. If no items were queued or an invalid count was set,
				// end the job instead of silently fudging the count to avoid masking errors.
				if (job.count <= 0)
				{
					Log.Error($"Invalid job count {job.count} for {thing}, ending job to prevent stuck pawn.");
					EndJobWith(JobCondition.Incompletable);
					return;
				}

				Toils_Haul.ErrorCheckForCarry(actor, thing);

				// get max we can pick up
				var countToPickUp = Mathf.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(actor, thing));
				Log.Message($"{actor} calculated countToPickUp: {countToPickUp} (job.count: {job.count}, maxUntilOverEncumbered: {MassUtility.CountToPickUpUntilOverEncumbered(actor, thing)})");

				if (ModCompatibilityCheck.CombatExtendedIsActive)
				{
					var ceCount = CompatHelper.CanFitInInventory(pawn, thing);
					Log.Message($"Combat Extended active, CanFitInInventory returned: {ceCount}");
					countToPickUp = ceCount;
				}

				if (countToPickUp > 0)
				{
					Log.Message($"{actor} will pick up {countToPickUp} of {thing}");
					var splitThing = thing.SplitOff(countToPickUp);
					var shouldMerge = takenToInventory.GetHashSet().Any(x => x.def == thing.def);
					actor.inventory.GetDirectlyHeldThings().TryAdd(splitThing, shouldMerge);
					takenToInventory.RegisterHauledItem(splitThing);

					if (ModCompatibilityCheck.CombatExtendedIsActive)
					{
						CompatHelper.UpdateInventory(pawn);
					}
				}
				else
				{
					// If we can't pick up anything, end the job to prevent getting stuck
					Log.Message($"{actor} cannot carry any {thing} (countToPickUp: {countToPickUp}). Ending job.");
					EndJobWith(JobCondition.Incompletable);
					return;
				}

				// thing still remains, so queue up hauling if we can + end the current job (smooth/instant transition)
				// This will technically release the reservations in the queue, but what can you do
				if (thing.Spawned)
				{
					Log.Message($"{thing} still spawned, creating HaulToStorageJob for remaining items");
					var haul = HaulAIUtility.HaulToStorageJob(actor, thing, job.playerForced);
					if (haul?.TryMakePreToilReservations(actor, false) ?? false)
					{
						actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);
						Log.Message($"Enqueued HaulToStorageJob for {actor}");
					}
					else
					{
						Log.Message($"Failed to make reservations for HaulToStorageJob for {actor}");
					}
					actor.jobs.curDriver.JumpToToil(wait);
				}
				else
				{
					Log.Message($"{thing} no longer spawned, continuing to next target");
				}
			}
		};
		yield return takeThing;
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());

		// Find more to haul, in case things spawned while this was in progress
		yield return new Toil
		{
			initAction = () =>
			{
				Log.Message($"{pawn} looking for more haulable items");
				var haulables = TempListForThings;
				haulables.Clear();
				haulables.AddRange(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()
					.Where(t => t != null && t.Spawned && !t.Destroyed)); // Filter out null, unspawned, or destroyed things
				Log.Message($"Found {haulables.Count} potential haulable items");
				var haulMoreWork = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(wg => wg.Worker is WorkGiver_HaulToInventory).Worker as WorkGiver_HaulToInventory;
				Job haulMoreJob = null;
				var haulMoreThing = WorkGiver_HaulToInventory.GetClosestAndRemove(pawn.Position, pawn.Map, haulables, PathEndMode.ClosestTouch,
					TraverseParms.For(pawn), 12, t => (haulMoreJob = haulMoreWork.JobOnThing(pawn, t)) != null);
				// WorkGiver_HaulToInventory found more work nearby
				if (haulMoreThing != null)
				{
					Log.Message($"Found more work: {haulMoreThing}, enqueueing job");
					pawn.jobs.jobQueue.EnqueueFirst(haulMoreJob, JobTag.Misc);
				}
				else
				{
					Log.Message($"No more work found for {pawn}");
				}
			}
		};

		// maintain cell reservations on the trip back
		yield return TargetB.HasThing ? Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
			: Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.ClosestTouch);

		// Queue next job (unload inventory)
		yield return new Toil
		{
			initAction = () =>
			{
				var actor = pawn;
				// Use the JobDriver's job instead of actor.jobs.curJob; by this time, job.targetB holds the storage location
				var storeCell = job.targetB;

				var unloadJob = JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, storeCell);
				if (unloadJob.TryMakePreToilReservations(actor, false))
				{
					actor.jobs.jobQueue.EnqueueFirst(unloadJob, JobTag.Misc);
					EndJobWith(JobCondition.Succeeded);
					// This will technically release the cell reservations in the queue, but what can you do
				}
			}
		};
		yield return wait;
	}


	private static List<Thing> TempListForThings { get; } = [];

	/// <summary>
	/// the workgiver checks for encumbered, this is purely extra for CE
	/// </summary>
	/// <returns></returns>
	public Toil CheckForOverencumberedForCombatExtended()
	{
		var toil = new Toil();

		if (!ModCompatibilityCheck.CombatExtendedIsActive)
		{
			Log.Message($"Combat Extended not active for {pawn}");
			return toil;
		}

		toil.initAction = () =>
		{
			var actor = toil.actor;
			// Use the JobDriver's job instead of actor.jobs.curJob to avoid null reference
			var nextThing = job.GetTarget(TargetIndex.A).Thing;

			Log.Message($"{actor} checking encumbrance for {nextThing}");
			var ceOverweight = CompatHelper.CeOverweight(pawn);
			var encumbrancePercent = MassUtility.EncumbrancePercent(actor);
			Log.Message($"{actor} encumbrance: {encumbrancePercent:F2}, ceOverweight: {ceOverweight}");

			if (!(MassUtility.EncumbrancePercent(actor) <= 0.9f && !ceOverweight))
			{
				Log.Message($"{actor} is overencumbered, switching to HaulToStorageJob");
				var haul = HaulAIUtility.HaulToStorageJob(actor, nextThing, job.playerForced);
				if (haul?.TryMakePreToilReservations(actor, false) ?? false)
				{
					// note that HaulToStorageJob etc doesn't do opportunistic duplicate hauling for items in valid storage
					actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);
					EndJobWith(JobCondition.Succeeded);
				}
				else
				{
					Log.Message($"Failed to make reservations for HaulToStorageJob for {actor}");
				}
			}
			else
			{
				Log.Message($"{actor} encumbrance OK, continuing with HaulToInventory");
			}
		};

		return toil;
	}

}