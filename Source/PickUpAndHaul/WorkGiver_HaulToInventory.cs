using PickUpAndHaul.Cache;
using PickUpAndHaul.Structs;

namespace PickUpAndHaul;
public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	private readonly object _lockObject = new();

	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		var result = base.ShouldSkip(pawn, forced)
				|| pawn.InMentalState
				|| pawn.Faction != Faction.OfPlayerSilentFail
				|| !Settings.IsAllowedRace(pawn.RaceProps)
				|| pawn.GetComp<CompHauledToInventory>() == null
		|| pawn.IsQuestLodger()
		|| pawn.IsOverAllowedGearCapacity()
		|| PickupAndHaulSaveLoadLogger.IsSaveInProgress()
		|| !PickupAndHaulSaveLoadLogger.IsModActive(); // Skip if mod is not active

		return result;
	}

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		WorkCache.Instance.CalculatePotentialWork(pawn);
		return WorkCache.Cache;
	}

	public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		var result = !pawn.InMentalState
				&& OkThingToHaul(thing, pawn)
				&& IsNotCorpseOrAllowed(thing)
		&& HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced)
		&& !pawn.IsOverAllowedGearCapacity()
		&& !MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1);

		// Check for basic storage availability and encumbrance, but be less restrictive about storage capacity
		// Let the allocation phase handle sophisticated storage finding and capacity management
		if (result)
		{
			var hasStorage = StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var foundCell, out var haulDestination, false);

			if (!hasStorage)
			{
				result = false;
			}
			else
			{
				// Check if pawn can physically carry the item and if storage has meaningful capacity
				var currentMass = MassUtility.GearAndInventoryMass(pawn);
				var capacity = MassUtility.Capacity(pawn);
				var actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, capacity);

				// If pawn has no capacity (like animals), fall back to vanilla behavior
				if (capacity <= 0)
				{
					if (Settings.EnableDebugLogging)
						Log.Message($"Pawn {pawn} has no capacity ({capacity}), falling back to vanilla hauling");
					// Don't return false here - let the vanilla hauling system handle it
					// The JobOnThing method will handle the fallback to HaulAIUtility.HaulToStorageJob
				}
				else if (actualCarriableAmount <= 0)
				{
					Log.Message($"Pawn {pawn} cannot carry any of {thing} due to encumbrance, returning false");
					result = false;
				}
				else
				{
					// Check if storage has any meaningful capacity for this item
					// This prevents the HasJobOnThing/JobOnThing synchronization issue
					var storageCapacity = 0;
					if (haulDestination is ISlotGroupParent)
						storageCapacity = StorageCapacityCache.CapacityAt(thing, foundCell, pawn.Map);
					else if (haulDestination is Thing destinationThing)
					{
						var thingOwner = destinationThing.TryGetInnerInteractableThingOwner();
						if (thingOwner != null)
							storageCapacity = thingOwner.GetCountCanAccept(thing);
					}

					if (storageCapacity <= 0)
						result = false;
				}
			}
		}

		return result;
	}

	//pick up stuff until you can't anymore,
	//while you're up and about, pick up something and haul it
	//before you go out, empty your pockets
	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		lock (_lockObject)
		{
			// Check if save operation is in progress
			if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
			{
				Log.Message($"Skipping job creation during save operation for {pawn}");
				return null;
			}

			// Do not create hauling jobs for pawns in a mental state
			if (pawn.InMentalState)
				return null;

			// Check if mod is active
			if (!PickupAndHaulSaveLoadLogger.IsModActive())
			{
				Log.Message($"Skipping job creation - mod not active for {pawn}");
				return null;
			}

			if (!OkThingToHaul(thing, pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced))
				return null;

			var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
			var traverseParms = TraverseParms.For(pawn);
			var capacity = MassUtility.Capacity(pawn);
			var currentMass = MassUtility.GearAndInventoryMass(pawn);
			var encumberance = currentMass / capacity;
			ThingOwner nonSlotGroupThingOwner = null;
			StoreTarget storeTarget;
			if (StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, currentPriority, pawn.Faction, out var targetCell, out var haulDestination, true))
			{
				if (haulDestination is ISlotGroupParent)
				{
					if (HaulToHopperJob(thing, targetCell, pawn.Map))
						return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
					else
						storeTarget = new(targetCell);
				}
				else if (haulDestination is Thing destinationAsThing && (nonSlotGroupThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner()) != null)
				{
					storeTarget = new(destinationAsThing);
				}
				else
				{
					Log.Error("Don't know how to handle HaulToStorageJob for storage " + haulDestination.ToStringSafe() + ". thing=" + thing.ToStringSafe());
					return null;
				}
			}
			else
			{
				JobFailReason.Is("NoEmptyPlaceLower".Translate());
				return null;
			}

			var capacityStoreCell
				= storeTarget.Container is null ? StorageCapacityCache.CapacityAt(thing, storeTarget.Cell, pawn.Map)
				: nonSlotGroupThingOwner.GetCountCanAccept(thing);

			if (capacityStoreCell == 0)
				return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);

			var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.HaulToInventory, null, storeTarget);
			Log.Message($"{pawn} job found to haul: {thing} to {storeTarget}:{capacityStoreCell}");

			// Always initialize queues to empty lists
			job.targetQueueA = [];
			job.targetQueueB = [];
			job.countQueue = [];

			// Validate job after creation using JobQueueManager
			if (!JobQueueManager.ValidateJobQueues(job, pawn))
			{
				Log.Error($"Job queue validation failed after creation for {pawn}!");
				return null;
			}

			//Find extra things than can be hauled to inventory, queue to reserve them
			var haulUrgentlyDesignation = DefDatabase<DesignationDef>.GetNamedSilentFail("HaulUrgentlyDesignation");
			var isUrgent = ModCompatibilityCheck.AllowToolIsActive && pawn.Map.designationManager.DesignationOn(thing)?.def == haulUrgentlyDesignation;

			var nextThing = thing;
			var lastThing = thing;

			var storeCellCapacity = new Dictionary<StoreTarget, CellAllocation>()
			{
				[storeTarget] = new(nextThing, capacityStoreCell)
			};

			if (!PawnSkipListCache.PawnSkipCells.ContainsKey(pawn))
				PawnSkipListCache.PawnSkipCells.TryAdd(pawn, []);
			if (!PawnSkipListCache.PawnSkipThings.ContainsKey(pawn))
				PawnSkipListCache.PawnSkipThings.TryAdd(pawn, []);
			PawnSkipListCache.PawnSkipCells.TryGetValue(pawn, out var skipCells);
			PawnSkipListCache.PawnSkipThings.TryGetValue(pawn, out var skipThings);

			if (storeTarget.Container != null)
				skipThings.Add(storeTarget.Container);
			else
				skipCells.Add(storeTarget.Cell);

			// Create storage location for tracking
			var storageLocation = storeTarget.Container != null
				? new StorageLocation(storeTarget.Container)
				: new StorageLocation(storeTarget.Cell);

			if (nextThing == null)
			{
				Log.Message($"No more things to allocate, targetQueueA: {job.targetQueueA.Count}, targetQueueB: {job.targetQueueB.Count}, countQueue: {job.countQueue.Count}");
				return job;
			}

			// First, check if the pawn can carry this item at all
			var actualCarriableAmount = CalculateActualCarriableAmount(nextThing, currentMass, capacity);
			if (actualCarriableAmount <= 0)
			{
				Log.Message($"Pawn {pawn} cannot carry any of {nextThing} due to encumbrance (current: {currentMass}/{capacity})");
				return job;
			}

			Log.Message($"Searching {WorkCache.Cache.Count} items");
			while ((nextThing = GetClosestAndRemove(nextThing.Position, pawn.Map, WorkCache.Cache, PathEndMode.ClosestTouch, traverseParms, t => Validator(t, pawn, haulUrgentlyDesignation, isUrgent))) != null)
				if (AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job, ref currentMass, actualCarriableAmount))
					break;
			Log.Message($"Remaining {WorkCache.Cache.Count} items");

			// Ensure job is never returned with empty targetQueueA
			// This prevents ArgumentOutOfRangeException in JobDriver_HaulToInventory
			if (job.targetQueueA == null || job.targetQueueA.Count == 0)
			{
				Log.Error($"Job has empty targetQueueA for {pawn} - ArgumentOutOfRangeException! Releasing all reservations and returning null to prevent crash.");
				CleanupInvalidJob(job, storeCellCapacity, thing, pawn);
				return null;
			}

			// Validate job before returning
			job.ValidateJobQueues(pawn, "Job Return");

			// Final to ensure job is completely valid
			if (!IsJobValid(job, pawn))
			{
				Log.Error($"Job failed final validation for {pawn} - cleaning up and returning null");
				CleanupInvalidJob(job, storeCellCapacity, thing, pawn);
				return null;
			}

			return job;
		}
	}

	private static bool Validator(Thing t, Pawn pawn, DesignationDef haulUrgentlyDesignation, bool isUrgent)
	{
		var urgentCheck = !isUrgent || pawn.Map.designationManager.DesignationOn(t)?.def == haulUrgentlyDesignation;
		var goodToHaul = GoodThingToHaul(t, pawn);
		var canHaulFast = HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false);
		var notOverEncumbered = !MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, t, 1);

		return urgentCheck && goodToHaul && canHaulFast && notOverEncumbered;
	}

	private static bool GoodThingToHaul(Thing t, Pawn pawn) =>
		OkThingToHaul(t, pawn)
		&& IsNotCorpseOrAllowed(t)
		&& !t.IsInValidBestStorage();

	private static bool OkThingToHaul(Thing t, Pawn pawn) =>
		t.Spawned
		&& pawn.CanReserve(t)
		&& !t.IsForbidden(pawn);

	private static bool IsNotCorpseOrAllowed(Thing t) => Settings.AllowCorpses || t is not Corpse;
	private static bool HaulToHopperJob(Thing thing, IntVec3 targetCell, Map map)
	{
		if (thing.def.IsNutritionGivingIngestible
			&& thing.def.ingestible.preferability is FoodPreferability.RawBad or FoodPreferability.RawTasty)
		{
			var thingList = targetCell.GetThingList(map);
			for (var i = 0; i < thingList.Count; i++)
			{
				if (thingList[i].def == ThingDefOf.Hopper)
				{
					return true;
				}
			}
		}
		return false;
	}

	private static Thing GetClosestAndRemove(IntVec3 center, Map map, List<Thing> searchSet, PathEndMode peMode, TraverseParms traverseParams, Predicate<Thing> validator)
	{
		if (searchSet == null || !searchSet.Any())
		{
			Log.Message($"searchSet is null or empty");
			return null;
		}
		var maxDistanceSquared = 400f;
		for (var i = 0; i < searchSet.Count; i++)
		{
			var thing = searchSet[i];

			if (!thing.Spawned)
			{
				searchSet.RemoveAt(i--);
				continue;
			}

			var distanceSquared = (center - thing.Position).LengthHorizontalSquared;
			while (distanceSquared > maxDistanceSquared)
				maxDistanceSquared += 50f;

			if (!map.reachability.CanReach(center, thing, peMode, traverseParams))
				continue;

			if (validator == null || validator(thing))
			{
				searchSet.RemoveAt(i);
				return thing;
			}
		}

		return null;
	}

	private static bool Stackable(Thing nextThing, KeyValuePair<StoreTarget, CellAllocation> allocation)
		=> nextThing == allocation.Value.Allocated
		|| allocation.Value.Allocated.CanStackWith(nextThing)
		|| nextThing.StackableAt(allocation.Key.Cell, nextThing.Map);

	private static bool AllocateThingAtCell(Dictionary<StoreTarget, CellAllocation> storeCellCapacity, Pawn pawn, Thing nextThing, Job job, ref float currentMass, int actualCarriableAmount)
	{
		var map = pawn.Map;
		var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);

		if (actualCarriableAmount <= 0)
			return false;

		// Find existing compatible storage, but safely check for valid cells
		var allocation = storeCellCapacity.FirstOrDefault(kvp =>
		{
			var storeTarget = kvp.Key;
			try
			{
				// Handle container storage
				if (storeTarget.Container != null)
				{
					var thingOwner = storeTarget.Container.TryGetInnerInteractableThingOwner();
					return thingOwner != null && thingOwner.CanAcceptAnyOf(nextThing) && Stackable(nextThing, kvp);
				}
				// Handle cell storage - check if cell is valid before accessing slot group
				else if (storeTarget.Cell.IsValid && storeTarget.Cell.InBounds(map))
				{
					var slotGroup = storeTarget.Cell.GetSlotGroup(map);
					return slotGroup != null && slotGroup.parent.Accepts(nextThing) && Stackable(nextThing, kvp);
				}
				return false;
			}
			catch (Exception ex)
			{
				Log.Warning($"Exception checking storage compatibility for {nextThing}: {ex.Message}");
				return false;
			}
		});
		var storeCell = allocation.Key;

		// Track reservations and targets added during this method call
		var reservationsMade = new List<(StorageLocation location, ThingDef def, int count)>();
		var targetsAdded = new List<LocalTargetInfo>();

		// Pre-validate storage capacity for existing allocations
		if (storeCell != default)
		{
			var currentCapacity = storeCellCapacity[storeCell].Capacity;
			if (currentCapacity <= 0)
			{
				Log.Message($"Pre-validation failed - storage {storeCell} has capacity {currentCapacity} <= 0, removing from allocation");
				storeCellCapacity.Remove(storeCell);
				storeCell = default;
			}
		}

		//Can't stack with allocated cells, find a new cell:
		if (storeCell == default)
		{
			if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var haulDestination, out var innerInteractableThingOwner))
			{
				if (innerInteractableThingOwner is null)
				{
					storeCell = new(nextStoreCell);
					// Don't add to job.targetQueueB here - let AddItemsToJob handle it
					targetsAdded.Add(nextStoreCell);

					var newCapacity = StorageCapacityCache.CapacityAt(nextThing, nextStoreCell, map);

					if (newCapacity <= 0)
					{
						Log.Message($"New cell {nextStoreCell} has capacity {newCapacity} <= 0, skipping this item");
						// Clean up targets and reservations
						CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
						return false;
					}

					// For new storage, be more flexible with capacity reservation
					var storageLocation = new StorageLocation(nextStoreCell);
					var reservationAmount = Math.Min(actualCarriableAmount, newCapacity);

					// Try to reserve capacity, but don't fail if we can't
					if (StorageAllocationTracker.Instance.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
						reservationsMade.Add((storageLocation, nextThing.def, reservationAmount));
					else
						Log.Message($"Could not reserve capacity for {nextThing} at {storageLocation}, but continuing anyway");

					storeCellCapacity[storeCell] = new(nextThing, newCapacity);
				}
				else
				{
					var destinationAsThing = (Thing)haulDestination;
					storeCell = new(destinationAsThing);
					targetsAdded.Add(destinationAsThing);

					var newCapacity = innerInteractableThingOwner.GetCountCanAccept(nextThing);
					Log.Message($"New haulDestination {haulDestination} has capacity {newCapacity}");

					if (newCapacity <= 0)
					{
						Log.Message($"New haulDestination {haulDestination} has capacity {newCapacity} <= 0, skipping this item");
						// Clean up targets and reservations
						CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
						return false;
					}

					// For new storage, be more flexible with capacity reservation
					var storageLocation = new StorageLocation(destinationAsThing);
					var reservationAmount = Math.Min(actualCarriableAmount, newCapacity);

					// Try to reserve capacity, but don't fail if we can't
					if (StorageAllocationTracker.Instance.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
					{
						reservationsMade.Add((storageLocation, nextThing.def, reservationAmount));
						Log.Message($"Reserved {reservationAmount} capacity for {nextThing} at {storageLocation}");
					}
					else
					{
						Log.Message($"Could not reserve capacity for {nextThing} at {storageLocation}, but continuing anyway");
					}

					storeCellCapacity[storeCell] = new(nextThing, newCapacity);
					Log.Message($"New haulDestination for {nextThing} = {haulDestination}, capacity = {newCapacity}");
				}
			}
			else
			{
				// Clean up targets and reservations
				CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
				return false;
			}
		}

		// Calculate the effective amount considering storage capacity, carriable amount, and item stack size
		var availableStorageCapacity = Math.Max(0, storeCellCapacity[storeCell].Capacity);
		var count = Math.Min(nextThing.stackCount, Math.Min(actualCarriableAmount, availableStorageCapacity));
		storeCellCapacity[storeCell].Capacity -= count;

		// Handle capacity overflow more gracefully
		while (storeCellCapacity[storeCell].Capacity < 0)
		{
			var capacityOver = -storeCellCapacity[storeCell].Capacity;
			storeCellCapacity.Remove(storeCell);

			Log.Message($"{pawn} overdone {storeCell} by {capacityOver}");

			if (capacityOver == 0)
			{
				break;  //don't find new cell, might not have more of this thing to haul
			}

			// Try to find additional storage for the overflow
			if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var nextHaulDestination, out var innerInteractableThingOwner))
			{
				if (innerInteractableThingOwner is null)
				{
					storeCell = new(nextStoreCell);
					targetsAdded.Add(nextStoreCell);

					var newCapacity = StorageCapacityCache.CapacityAt(nextThing, nextStoreCell, map) - capacityOver;
					Log.Message($"New overflow cell {nextStoreCell} has capacity {newCapacity}");

					if (newCapacity <= 0)
					{
						Log.Message($"New overflow cell {nextStoreCell} has insufficient capacity, skipping this item");
						// Clean up targets and reservations
						CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
						return false;
					}

					// Try to reserve capacity for the overflow
					var storageLocation = new StorageLocation(nextStoreCell);
					var reservationAmount = Math.Min(capacityOver, actualCarriableAmount);

					if (StorageAllocationTracker.Instance.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
					{
						reservationsMade.Add((storageLocation, nextThing.def, reservationAmount));
						Log.Message($"Reserved {reservationAmount} overflow capacity for {nextThing} at {storageLocation}");
					}
					else
					{
						Log.Message($"Could not reserve overflow capacity for {nextThing} at {storageLocation}, but continuing anyway");
					}

					storeCellCapacity[storeCell] = new(nextThing, newCapacity);
					Log.Message($"New overflow cell {nextStoreCell}:{newCapacity} for {nextThing}");
				}
				else
				{
					var destinationAsThing = (Thing)nextHaulDestination;
					storeCell = new(destinationAsThing);
					targetsAdded.Add(destinationAsThing);

					var newCapacity = innerInteractableThingOwner.GetCountCanAccept(nextThing) - capacityOver;
					Log.Message($"New overflow haulDestination {nextHaulDestination} has capacity {newCapacity}");

					if (newCapacity <= 0)
					{
						Log.Message($"New overflow haulDestination {nextHaulDestination} has insufficient capacity, skipping this item");
						// Clean up targets and reservations
						CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
						return false;
					}

					// Try to reserve capacity for the overflow
					var storageLocation = new StorageLocation(destinationAsThing);
					var reservationAmount = Math.Min(capacityOver, actualCarriableAmount);

					if (StorageAllocationTracker.Instance.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
					{
						reservationsMade.Add((storageLocation, nextThing.def, reservationAmount));
						Log.Message($"Reserved {reservationAmount} overflow capacity for {nextThing} at {storageLocation}");
					}
					else
					{
						Log.Message($"Could not reserve overflow capacity for {nextThing} at {storageLocation}, but continuing anyway");
					}

					storeCellCapacity[storeCell] = new(nextThing, newCapacity);
					Log.Message($"New overflow haulDestination {nextHaulDestination}:{newCapacity} for {nextThing}");
				}
			}
			else
			{
				count -= capacityOver;
				Log.Message($"No additional storage found for overflow, reducing count to {count}");

				if (count <= 0)
				{
					Log.Message($"Count reduced to {count}, cannot allocate {nextThing}");
					// Clean up targets and reservations
					CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
					return false;
				}
				break;
			}
		}

		if (count <= 0)
		{
			Log.Message($"Final count is {count}, cannot allocate {nextThing}");
			// Clean up targets and reservations
			CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
			return false;
		}

		// Get the target from the storeCell, not from the queue
		var target = storeCell.Container != null ? new LocalTargetInfo(storeCell.Container) : new LocalTargetInfo(storeCell.Cell);

		if (!JobQueueManager.AddItemsToJob(job, [nextThing], [count], [target], pawn))
		{
			Log.Error($"Failed to add items to job queues for {pawn}!");
			// Clean up any targets we added
			CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
			return false;
		}

		currentMass += nextThing.GetStatValue(StatDefOf.Mass) * count;
		return true;
	}

	/// <summary>
	/// Cleans up targets and reservations made during AllocateThingAtCell execution
	/// </summary>
	private static void CleanupAllocateThingAtCell(Job job, List<LocalTargetInfo> targetsAdded, List<(StorageLocation location, ThingDef def, int count)> reservationsMade, Pawn pawn)
	{
		// Release all reservations made during this method execution
		foreach (var (location, def, count) in reservationsMade)
		{
			StorageAllocationTracker.Instance.ReleaseCapacity(location, def, count, pawn);
			Log.Message($"Released reservation for {def} x{count} at {location}");
		}

		// Remove all targets added during this method execution
		// Work backwards to avoid index shifting issues
		for (var i = targetsAdded.Count - 1; i >= 0; i--)
		{
			var targetToRemove = targetsAdded[i];
			var index = job.targetQueueB.LastIndexOf(targetToRemove);
			if (index >= 0)
			{
				job.targetQueueB.RemoveAt(index);
				Log.Message($"Removed target {targetToRemove} from targetQueueB at index {index}");
			}
		}
	}

	private static bool TryFindBestBetterStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, out IHaulDestination haulDestination, out ThingOwner innerInteractableThingOwner, bool strictSkip = false)
	{
		var storagePriority = StoragePriority.Unstored;
		innerInteractableThingOwner = null;
		if (TryFindBestBetterStoreCellFor(t, carrier, map, currentPriority, faction, out var foundCell2, strictSkip))
		{
			storagePriority = foundCell2.GetSlotGroup(map).Settings.Priority;
		}

		if (!TryFindBestBetterNonSlotGroupStorageFor(t, carrier, map, currentPriority, faction, out var haulDestination2, strictSkip))
		{
			haulDestination2 = null;
		}

		if (storagePriority == StoragePriority.Unstored && haulDestination2 == null)
		{
			foundCell = IntVec3.Invalid;
			haulDestination = null;
			return false;
		}

		if (haulDestination2 != null && (storagePriority == StoragePriority.Unstored || (int)haulDestination2.GetStoreSettings().Priority > (int)storagePriority))
		{
			foundCell = IntVec3.Invalid;
			haulDestination = haulDestination2;

			if (haulDestination2 is not Thing destinationAsThing)
			{
				Log.Error($"{haulDestination2} is not a valid Thing. Pick Up And Haul can't work with this");
			}
			else
			{
				innerInteractableThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner();
			}

			if (innerInteractableThingOwner is null)
			{
				Log.Error($"{haulDestination2} gave null ThingOwner during lookup in Pick Up And Haul's WorkGiver_HaulToInventory");
			}

			return true;
		}

		foundCell = foundCell2;
		haulDestination = foundCell2.GetSlotGroup(map).parent;
		return true;
	}

	private static bool TryFindBestBetterStoreCellFor(Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, bool strictSkip = false)
	{
		var haulDestinations = map.haulDestinationManager.AllGroupsListInPriorityOrder;
		for (var i = 0; i < haulDestinations.Count; i++)
		{
			var slotGroup = haulDestinations[i];
			if (slotGroup.Settings.Priority <= currentPriority || !slotGroup.parent.Accepts(thing))
			{
				continue;
			}

			var cellsList = slotGroup.CellsList;

			for (var j = 0; j < cellsList.Count; j++)
			{
				var cell = cellsList[j];

				// Handle skipped cells based on strictSkip parameter
				if (PawnSkipListCache.PawnSkipCells.TryGetValue(carrier, out var cells) && cells.Contains(cell))
				{
					if (strictSkip)
					{
						// In strict mode, completely avoid skipped cells
						continue;
					}
					else
					{
						// For multi-item hauling, allow reuse of cells that have remaining capacity
						// Only skip cells if they have no remaining capacity
						var remainingCapacity = StorageCapacityCache.CapacityAt(thing, cell, map);
						if (remainingCapacity <= 0)
							continue; // Skip if no capacity
					}
				}

				if (StoreUtility.IsGoodStoreCell(cell, map, thing, carrier, faction) && cell != default)
				{
					foundCell = cell;

					// Only add to skip list if this completely fills the cell
					var capacity = StorageCapacityCache.CapacityAt(thing, cell, map);
					if (capacity <= thing.stackCount)
					{
						PawnSkipListCache.PawnSkipCells[carrier].Add(cell);
					}
					else
					{
						Log.Message($"Cell {cell} will have remaining capacity after {thing}, not adding to skip list");
					}

					return true;
				}
			}
		}
		foundCell = IntVec3.Invalid;
		return false;
	}

	/// <summary>
	/// Calculates the actual amount of a thing that a pawn can carry considering encumbrance limits
	/// </summary>
	private static int CalculateActualCarriableAmount(Thing thing, float currentMass, float capacity)
	{
		if (currentMass >= capacity)
		{
			if (Settings.EnableDebugLogging)
				Log.Message($"Pawn at max allowed mass ({currentMass}/{capacity}), cannot carry more");
			return 0;
		}

		var thingMass = thing.GetStatValue(StatDefOf.Mass);

		if (thingMass <= 0)
			return thing.stackCount;

		var remainingCapacity = capacity - currentMass;
		var maxCarriable = (int)Math.Floor(remainingCapacity / thingMass);

		return Math.Min(maxCarriable, thing.stackCount);
	}

	private static bool TryFindBestBetterNonSlotGroupStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IHaulDestination haulDestination, bool acceptSamePriority = false, bool strictSkip = false)
	{
		var allHaulDestinationsListInPriorityOrder = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
		var intVec = t.SpawnedOrAnyParentSpawned ? t.PositionHeld : carrier.PositionHeld;
		var num = float.MaxValue;
		var storagePriority = StoragePriority.Unstored;
		haulDestination = null;
		for (var i = 0; i < allHaulDestinationsListInPriorityOrder.Count; i++)
		{
			var iHaulDestination = allHaulDestinationsListInPriorityOrder[i];

			if (iHaulDestination is ISlotGroupParent || (iHaulDestination is Building_Grave && !t.CanBeBuried()))
			{
				continue;
			}

			var priority = iHaulDestination.GetStoreSettings().Priority;
			if ((int)priority < (int)storagePriority || (acceptSamePriority && (int)priority < (int)currentPriority) || (!acceptSamePriority && (int)priority <= (int)currentPriority))
			{
				break;
			}

			float num2 = intVec.DistanceToSquared(iHaulDestination.Position);
			if (num2 > num || !iHaulDestination.Accepts(t))
			{
				continue;
			}

			if (iHaulDestination is Thing thing)
			{
				if (thing.Faction != faction)
				{
					continue;
				}

				// Handle skipped containers based on strictSkip parameter
				if (PawnSkipListCache.PawnSkipThings.TryGetValue(carrier, out var things) && things.Contains(thing))
				{
					if (strictSkip)
					{
						// In strict mode, completely avoid skipped containers
						continue;
					}
					else
					{
						// For multi-item hauling, allow reuse of containers that have remaining capacity
						var thingOwner = thing.TryGetInnerInteractableThingOwner();
						if (thingOwner != null)
						{
							var remainingCapacity = thingOwner.GetCountCanAccept(t);
							if (remainingCapacity <= 0)
							{
								continue; // Skip if no capacity
							}
							Log.Message($"Reusing previously allocated container {thing} with remaining capacity {remainingCapacity} for {t}");
						}
						else
						{
							continue; // Skip if can't determine capacity
						}
					}
				}

				if (carrier != null)
				{
					if (thing.IsForbidden(carrier)
						|| !carrier.CanReserveNew(thing)
						|| !carrier.Map.reachability.CanReach(intVec, thing, PathEndMode.ClosestTouch, TraverseParms.For(carrier)))
					{
						continue;
					}
				}
				else if (faction != null)
				{
					if (thing.IsForbidden(faction) || map.reservationManager.IsReservedByAnyoneOf(thing, faction))
					{
						continue;
					}
				}

				// Only add to skip list if this will completely fill the container
				var thingOwner2 = thing.TryGetInnerInteractableThingOwner();
				if (thingOwner2 != null)
				{
					var capacity = thingOwner2.GetCountCanAccept(t);
					if (capacity <= t.stackCount)
					{
						PawnSkipListCache.PawnSkipThings[carrier].Add(thing);
						Log.Message($"Container {thing} will be full after {t}, adding to skip list");
					}
					else
					{
						Log.Message($"Container {thing} will have remaining capacity after {t}, not adding to skip list");
					}
				}
				else
				{
					PawnSkipListCache.PawnSkipThings[carrier].Add(thing);
				}
			}
			else
			{
				continue;
			}

			num = num2;
			storagePriority = priority;
			haulDestination = iHaulDestination;
		}

		return haulDestination != null;
	}

	private static bool IsJobValid(Job job, Pawn pawn)
	{
		if (job == null)
		{
			Log.Error($"Job is null in IsJobValid for {pawn}");
			return false;
		}

		if (job.targetQueueA == null || job.targetQueueA.Count == 0)
		{
			Log.Error($"Job has empty targetQueueA in IsJobValid for {pawn}");
			return false;
		}

		if (job.targetQueueB == null || job.targetQueueB.Count == 0)
		{
			Log.Error($"Job has empty targetQueueB in IsJobValid for {pawn}");
			return false;
		}

		if (job.countQueue == null || job.countQueue.Count == 0)
		{
			Log.Error($"Job has empty countQueue in IsJobValid for {pawn}");
			return false;
		}

		if (job.targetQueueA.Count != job.countQueue.Count)
		{
			Log.Error($"Queue synchronization issue in IsJobValid for {pawn} - targetQueueA.Count ({job.targetQueueA.Count}) != countQueue.Count ({job.countQueue.Count})");
			return false;
		}

		for (var i = 0; i < job.targetQueueA.Count; i++)
		{
			var target = job.targetQueueA[i];
			if (target == null || target.Thing == null)
			{
				Log.Error($"Found null target at index {i} in targetQueueA in IsJobValid for {pawn}");
				return false;
			}
			if (target.Thing.Destroyed || !target.Thing.Spawned)
			{
				Log.Warning($"Found destroyed/unspawned target {target.Thing} at index {i} in targetQueueA in IsJobValid for {pawn}");
				return false;
			}
		}

		for (var i = 0; i < job.countQueue.Count; i++)
		{
			if (job.countQueue[i] <= 0)
			{
				Log.Error($"Found negative/zero count {job.countQueue[i]} at index {i} in IsJobValid for {pawn}");
				return false;
			}
		}

		return true;
	}

	private static void CleanupInvalidJob(Job job, Dictionary<StoreTarget, CellAllocation> storeCellCapacity, Thing thing, Pawn pawn)
	{
		if (job == null)
			return;

		// Use the actual allocated amounts from job queues instead of remaining capacity
		if (job.countQueue != null && job.targetQueueB != null && job.countQueue.Count == job.targetQueueB.Count)
		{
			for (var i = 0; i < job.countQueue.Count; i++)
			{
				var allocatedCount = job.countQueue[i];
				var target = job.targetQueueB[i];

				if (allocatedCount > 0 && target != null)
				{
					StorageLocation releaseLocation;

					// Determine the storage location from the target
					if (target.Thing != null)
					{
						// Container storage
						releaseLocation = new StorageLocation(target.Thing);
					}
					else if (target.Cell.IsValid)
					{
						// Cell storage
						releaseLocation = new StorageLocation(target.Cell);
					}
					else
					{
						Log.Warning($"Invalid target at index {i} in CleanupInvalidJob for {pawn}");
						continue;
					}

					// Release the actual allocated amount
					StorageAllocationTracker.Instance.ReleaseCapacity(releaseLocation, thing.def, allocatedCount, pawn);
					Log.Message($"CleanupInvalidJob: Released {allocatedCount} of {thing.def} at {releaseLocation} for {pawn}");
				}
			}
		}
		else
		{
			// Fallback: if job queues are invalid, use the storeCellCapacity (less accurate but safer)
			Log.Warning($"Job queues are invalid in CleanupInvalidJob for {pawn}, using fallback cleanup method");
			foreach (var kvp in storeCellCapacity)
			{
				var currentStoreTarget = kvp.Key;
				var releaseLocation = currentStoreTarget.Container != null
					? new StorageLocation(currentStoreTarget.Container)
					: new StorageLocation(currentStoreTarget.Cell);

				// Note: This is less accurate as it uses remaining capacity instead of allocated amount
				// But it's safer than doing nothing if job queues are corrupted
				StorageAllocationTracker.Instance.ReleaseCapacity(releaseLocation, thing.def, kvp.Value.Capacity, pawn);
				Log.Message($"CleanupInvalidJob fallback: Released {kvp.Value.Capacity} of {thing.def} at {releaseLocation} for {pawn}");
			}
		}
	}
}