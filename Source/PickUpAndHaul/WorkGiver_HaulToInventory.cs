using PickUpAndHaul.Cache;
using PickUpAndHaul.Structs;

namespace PickUpAndHaul;
public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	private readonly object _lockObject = new();

	public override bool ShouldSkip(Pawn pawn, bool forced = false) =>
		base.ShouldSkip(pawn, forced)
				|| pawn.InMentalState
				|| pawn.Faction != Faction.OfPlayerSilentFail
				|| !Settings.IsAllowedRace(pawn.RaceProps)
				|| pawn.GetComp<CompHauledToInventory>() == null
				|| pawn.IsQuestLodger()
				|| pawn.IsOverAllowedGearCapacity()
				|| PickupAndHaulSaveLoadLogger.IsSaveInProgress()
				|| !PickupAndHaulSaveLoadLogger.IsModActive();

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		WorkCache.Instance.CalculatePotentialWork(pawn);
		return WorkCache.Cache;
	}

	public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		var result = thing.OkThingToHaul(pawn, forced);

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
			if (!thing.OkThingToHaul(pawn, forced))
				return null;

			var traverseParams = TraverseParms.For(pawn);
			var capacity = MassUtility.Capacity(pawn);
			var currentMass = MassUtility.GearAndInventoryMass(pawn);

			var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.HaulToInventory);

			job.targetQueueA = [];
			job.targetQueueB = [];
			job.countQueue = [];

			// Validate job after creation using JobQueueManager
			if (!JobQueueManager.ValidateJobQueues(job, pawn))
			{
				Log.Error($"Job queue validation failed after creation for {pawn}!");
				return null;
			}

			int actualCarriableAmount;
			Log.Message($"Searching {WorkCache.Cache.Count} not urgent items and {WorkCache.Cache.Count} urgent items");
			do
			{
				// First, check if the pawn can carry this item at all
				actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, capacity);
				if (actualCarriableAmount <= 0 || job.targetQueueA.Count >= 20)
					break;
				if (AllocateThingAtCell(pawn, thing, job, ref currentMass, actualCarriableAmount))
					continue;
			} while ((thing = GetClosestAndRemove(thing.Position, pawn.Map, WorkCache.UrgentCache.Any() ? WorkCache.UrgentCache : WorkCache.Cache, PathEndMode.ClosestTouch, traverseParams)) != null);

			Log.Message($"Remaining {WorkCache.Cache.Count} not urgent items and {WorkCache.Cache.Count} urgent items");
			return job;
		}
	}

	private static Thing GetClosestAndRemove(IntVec3 center, Map map, List<Thing> searchSet, PathEndMode peMode, TraverseParms traverseParams)
	{
		if (searchSet == null || !searchSet.Any())
		{
			Log.Message($"searchSet is null or empty");
			return null;
		}
		var maxDistanceSquared = 100f;
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
				maxDistanceSquared += 10f;

			if (!map.reachability.CanReach(center, thing, peMode, traverseParams))
				continue;

			searchSet.RemoveAt(i);
			return thing;
		}

		return null;
	}

	private static bool AllocateThingAtCell(Pawn pawn, Thing nextThing, Job job, ref float currentMass, int actualCarriableAmount)
	{
		Dictionary<StoreTarget, CellAllocation> storeCellCapacity = [];
		var map = pawn.Map;
		var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);

		if (actualCarriableAmount <= 0)
			return false;

		// Track reservations and targets added during this method call
		var reservationsMade = new List<(StorageLocation location, ThingDef def, int count)>();
		var targetsAdded = new List<LocalTargetInfo>();

		// Find existing compatible storage, but safely check for valid cells
		StoreTarget storeCell;
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
			if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out nextStoreCell, out var nextHaulDestination, out innerInteractableThingOwner))
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
		if (!JobQueueManager.ValidateJobQueues(job, pawn))
		{
			Log.Error($"Job failed final validation for {pawn} - cleaning up and returning null");
			CleanupInvalidJob(job, storeCellCapacity, nextThing, pawn);
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
						cells.Add(cell);
						PawnSkipListCache.PawnSkipCells.AddOrUpdate(carrier, cells, (pawn, existingCells) =>
						{
							existingCells = cells;
							return existingCells;
						});
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
						things.Add(thing);
						PawnSkipListCache.PawnSkipThings.AddOrUpdate(carrier, things, (pawn, existingThings) =>
						{
							existingThings = things;
							return existingThings;
						});
						Log.Message($"Container {thing} will be full after {t}, adding to skip list");
					}
					else
					{
						Log.Message($"Container {thing} will have remaining capacity after {t}, not adding to skip list");
					}
				}
				else
				{
					things.Add(thing);
					PawnSkipListCache.PawnSkipThings.AddOrUpdate(carrier, things, (pawn, existingThings) =>
					{
						existingThings = things;
						return existingThings;
					});
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