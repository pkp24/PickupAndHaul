using System.Collections.Generic;
using System.Linq;

namespace PickUpAndHaul;
public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	//Thanks to AlexTD for the more dynamic search range
	//And queueing
	//And optimizing
	private const float SEARCH_FOR_OTHERS_RANGE_FRACTION = 0.5f;

	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		PerformanceProfiler.StartTimer("ShouldSkip");
		
		var result = base.ShouldSkip(pawn, forced)
                || pawn.InMentalState
                || pawn.Faction != Faction.OfPlayerSilentFail
                || !Settings.IsAllowedRace(pawn.RaceProps)
                || pawn.GetComp<CompHauledToInventory>() == null
		|| pawn.IsQuestLodger()
		|| OverAllowedGearCapacity(pawn)
		|| PickupAndHaulSaveLoadLogger.IsSaveInProgress()
		|| !PickupAndHaulSaveLoadLogger.IsModActive(); // Skip if mod is not active
		
		PerformanceProfiler.EndTimer("ShouldSkip");
		return result;
	}

	public static bool GoodThingToHaul(Thing t, Pawn pawn)
	{
		PerformanceProfiler.StartTimer("GoodThingToHaul");
		
		var result = OkThingToHaul(t, pawn)
		&& IsNotCorpseOrAllowed(t)
		&& !t.IsInValidBestStorage();
		
		PerformanceProfiler.EndTimer("GoodThingToHaul");
		return result;
	}

	public static bool OkThingToHaul(Thing t, Pawn pawn)
	{
		PerformanceProfiler.StartTimer("OkThingToHaul");
		
		var result = t.Spawned
		&& pawn.CanReserve(t)
		&& !t.IsForbidden(pawn);
		
		PerformanceProfiler.EndTimer("OkThingToHaul");
		return result;
	}

	public static bool IsNotCorpseOrAllowed(Thing t) => Settings.AllowCorpses || t is not Corpse;

        private static readonly Dictionary<Pawn, (int tick, List<Thing> list)> _potentialWorkCache = new();
        private const int CacheDuration = 30; // ticks

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
                PerformanceProfiler.StartTimer("PotentialWorkThingsGlobal");

                var currentTick = Find.TickManager.TicksGame;
                if (_potentialWorkCache.TryGetValue(pawn, out var cached) && currentTick - cached.tick <= CacheDuration)
                {
                        PerformanceProfiler.EndTimer("PotentialWorkThingsGlobal");
                        return cached.list;
                }

                var list = new List<Thing>(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
                Comparer.rootCell = pawn.Position;
                list.Sort(Comparer);

                _potentialWorkCache[pawn] = (currentTick, list);

                PerformanceProfiler.EndTimer("PotentialWorkThingsGlobal");
                return list;
        }

	private static ThingPositionComparer Comparer { get; } = new();
	public class ThingPositionComparer : IComparer<Thing>
	{
		public IntVec3 rootCell;
		public int Compare(Thing x, Thing y) => (x.Position - rootCell).LengthHorizontalSquared.CompareTo((y.Position - rootCell).LengthHorizontalSquared);
	}

        public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		PerformanceProfiler.StartTimer("HasJobOnThing");
		
		var result = !pawn.InMentalState
                && OkThingToHaul(thing, pawn)
                && IsNotCorpseOrAllowed(thing)
		&& HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced)
		&& !OverAllowedGearCapacity(pawn)
		&& !MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1);
		
		// Check for storage availability with more thorough capacity verification
		if (result)
		{
			var foundCell = IntVec3.Invalid;
			var haulDestination = (IHaulDestination)null;
			var hasStorage = StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out foundCell, out haulDestination, false);
			
			if (!hasStorage)
			{
				result = false;
			}
			else
			{
				// Create storage location for tracking
				var storageLocation = haulDestination is Thing destinationAsThing 
					? new StorageAllocationTracker.StorageLocation(destinationAsThing)
					: new StorageAllocationTracker.StorageLocation(foundCell);
				
				                // Check available capacity using the storage allocation tracker
                // Use actual carriable amount instead of full stack count to avoid blocking valid partial-stack hauling
                var currentMass = MassUtility.GearAndInventoryMass(pawn);
                var capacity = MassUtility.Capacity(pawn);
                var actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, capacity);
                if (actualCarriableAmount <= 0)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: HasJobOnThing: Pawn {pawn} cannot carry any of {thing} due to encumbrance, returning false");
                        result = false;
                }
                else if (!StorageAllocationTracker.HasAvailableCapacity(storageLocation, thing.def, actualCarriableAmount, pawn.Map))
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: HasJobOnThing: Insufficient available capacity for {actualCarriableAmount} of {thing} at {storageLocation}, returning false");
                        result = false;
                }
			}
		}
		
		PerformanceProfiler.EndTimer("HasJobOnThing");
		return result;
	}

	//bulky gear (power armor + minigun) so don't bother.
	//Updated to include inventory mass, not just gear mass

        private static readonly Dictionary<Pawn, (int tick, bool result)> _encumbranceCache = new();

        public static bool OverAllowedGearCapacity(Pawn pawn)
        {
                PerformanceProfiler.StartTimer("OverAllowedGearCapacity");
                
                var currentTick = Find.TickManager.TicksGame;

                if (_encumbranceCache.TryGetValue(pawn, out var cache) && cache.tick == currentTick)
                {
                        PerformanceProfiler.EndTimer("OverAllowedGearCapacity");
                        return cache.result;
                }

                var totalMass = MassUtility.GearAndInventoryMass(pawn);
                var capacity = MassUtility.Capacity(pawn);
                var result = totalMass / capacity >= Settings.MaximumOccupiedCapacityToConsiderHauling;

                _encumbranceCache[pawn] = (currentTick, result);
                
                PerformanceProfiler.EndTimer("OverAllowedGearCapacity");
                return result;
        }

	//pick up stuff until you can't anymore,
	//while you're up and about, pick up something and haul it
	//before you go out, empty your pockets
        public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
        {
		PerformanceProfiler.StartTimer("JobOnThing");
		
                // Check if save operation is in progress
                if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
                {
                        Log.Message($"[PickUpAndHaul] Skipping job creation during save operation for {pawn}");
			PerformanceProfiler.EndTimer("JobOnThing");
                        return null;
                }

                // Do not create hauling jobs for pawns in a mental state
                if (pawn.InMentalState)
                {
			PerformanceProfiler.EndTimer("JobOnThing");
                        return null;
                }

		// Check if mod is active
		if (!PickupAndHaulSaveLoadLogger.IsModActive())
		{
			Log.Message($"[PickUpAndHaul] Skipping job creation - mod not active for {pawn}");
			PerformanceProfiler.EndTimer("JobOnThing");
			return null;
		}

		if (!OkThingToHaul(thing, pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced))
		{
			PerformanceProfiler.EndTimer("JobOnThing");
			return null;
		}

		if (pawn.GetComp<CompHauledToInventory>() is null) // Misc. Robots compatibility
														   // See https://github.com/catgirlfighter/RimWorld_CommonSense/blob/master/Source/CommonSense11/CommonSense/OpportunisticTasks.cs#L129-L140
		{
			PerformanceProfiler.EndTimer("JobOnThing");
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
		}

                var map = pawn.Map;
                var designationManager = map.designationManager;
                var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
                var traverseParms = TraverseParms.For(pawn);
                var capacity = MassUtility.Capacity(pawn);
                float currentMass = MassUtility.GearAndInventoryMass(pawn);
                var encumberance = currentMass / capacity;
                ThingOwner nonSlotGroupThingOwner = null;
                StoreTarget storeTarget;
		if (StoreUtility.TryFindBestBetterStorageFor(thing, pawn, map, currentPriority, pawn.Faction, out var targetCell, out var haulDestination, true))
		{
			if (haulDestination is ISlotGroupParent)
			{
				//since we've gone through all the effort of getting the loc, might as well use it.
				//Don't multi-haul food to hoppers.
				if (HaulToHopperJob(thing, targetCell, map))
				{
					PerformanceProfiler.EndTimer("JobOnThing");
					return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
				}
				else
				{
					storeTarget = new(targetCell);
				}
			}
			else if (haulDestination is Thing destinationAsThing && (nonSlotGroupThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner()) != null)
			{
				storeTarget = new(destinationAsThing);
			}
			else
			{
				Log.Error("Don't know how to handle HaulToStorageJob for storage " + haulDestination.ToStringSafe() + ". thing=" + thing.ToStringSafe());
				PerformanceProfiler.EndTimer("JobOnThing");
				return null;
			}
		}
		else
		{
			JobFailReason.Is("NoEmptyPlaceLower".Translate());
			PerformanceProfiler.EndTimer("JobOnThing");
			return null;
		}

		//credit to Dingo
		var capacityStoreCell
			= storeTarget.container is null ? CapacityAt(thing, storeTarget.cell, map)
			: nonSlotGroupThingOwner.GetCountCanAccept(thing);

		if (capacityStoreCell == 0)
		{
			PerformanceProfiler.EndTimer("JobOnThing");
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
		}

		var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.HaulToInventory, null, storeTarget);   //Things will be in queues
		Log.Message($"[PickUpAndHaul] DEBUG: ===================================================================");
		Log.Message($"[PickUpAndHaul] DEBUG: ==================================================================");//different size so the log doesn't count it 2x
		Log.Message($"[PickUpAndHaul] DEBUG: {pawn} job found to haul: {thing} to {storeTarget}:{capacityStoreCell}, looking for more now");
		Log.Message($"[PickUpAndHaul] DEBUG: Initial job state - targetQueueA: {job.targetQueueA?.Count ?? 0}, targetQueueB: {job.targetQueueB?.Count ?? 0}, countQueue: {job.countQueue?.Count ?? 0}");
		
		// Always initialize queues to empty lists
		job.targetQueueA = new List<LocalTargetInfo>();
		job.targetQueueB = new List<LocalTargetInfo>();
		job.countQueue = new List<int>();
		
		Log.Message($"[PickUpAndHaul] DEBUG: After initialization - targetQueueA: {job.targetQueueA?.Count ?? 0}, targetQueueB: {job.targetQueueB?.Count ?? 0}, countQueue: {job.countQueue?.Count ?? 0}");
		
		// Validate job after creation
		ValidateJobQueues(job, pawn, "Job Creation");

		//Find what fits in inventory, set nextThingLeftOverCount to be 
                var nextThingLeftOverCount = 0;

		var ceOverweight = false;

		if (ModCompatibilityCheck.CombatExtendedIsActive)
		{
			ceOverweight = CompatHelper.CeOverweight(pawn);
		}

		var distanceToHaul = (storeTarget.Position - thing.Position).LengthHorizontal * SEARCH_FOR_OTHERS_RANGE_FRACTION;
		var distanceToSearchMore = Math.Max(12f, distanceToHaul);

		//Find extra things than can be hauled to inventory, queue to reserve them
		var haulUrgentlyDesignation = PickUpAndHaulDesignationDefOf.haulUrgently;
		var isUrgent = ModCompatibilityCheck.AllowToolIsActive && designationManager.DesignationOn(thing)?.def == haulUrgentlyDesignation;

		var haulables = new List<Thing>(map.listerHaulables.ThingsPotentiallyNeedingHauling());
		Comparer.rootCell = thing.Position;
		haulables.Sort(Comparer);
		
		Log.Message($"[PickUpAndHaul] DEBUG: Found {haulables.Count} haulable items to consider");

		// Pre-filter items that have available storage to avoid allocation failures
		var itemsWithStorage = new List<Thing>();
		itemsWithStorage.Add(thing); // Always include the initial item
		
		foreach (var haulable in haulables)
		{
			if (haulable == thing) continue; // Skip the initial item
			
			// Check if this item has available storage
			var itemPriority = StoreUtility.CurrentStoragePriorityOf(haulable);
			if (StoreUtility.TryFindBestBetterStorageFor(haulable, pawn, map, itemPriority, pawn.Faction, out _, out _, false))
			{
				itemsWithStorage.Add(haulable);
				Log.Message($"[PickUpAndHaul] DEBUG: {haulable} has available storage, adding to consideration");
			}
			else
			{
				Log.Message($"[PickUpAndHaul] DEBUG: {haulable} has no available storage, skipping");
			}
		}
		
		Log.Message($"[PickUpAndHaul] DEBUG: After storage filtering: {itemsWithStorage.Count} items have available storage");

		var nextThing = thing;
		var lastThing = thing;

		var storeCellCapacity = new Dictionary<StoreTarget, CellAllocation>()
		{
			[storeTarget] = new(nextThing, capacityStoreCell)
		};
		//skipTargets = new() { storeTarget };
		skipCells = new();
		skipThings = new();
		if (storeTarget.container != null)
		{
			skipThings.Add(storeTarget.container);
		}
		else
		{
			skipCells.Add(storeTarget.cell);
		}

		bool Validator(Thing t)
			=> (!isUrgent || designationManager.DesignationOn(t)?.def == haulUrgentlyDesignation)
			&& GoodThingToHaul(t, pawn) && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false) //forced is false, may differ from first thing
			&& itemsWithStorage.Contains(t); // Only consider items that have available storage

		haulables.Remove(thing);

		// Always allocate the initial item first to ensure the job succeeds
		Log.Message($"[PickUpAndHaul] DEBUG: Allocating initial item {thing} to ensure job success");
		
		// Create storage location for tracking
		var storageLocation = storeTarget.container != null 
			? new StorageAllocationTracker.StorageLocation(storeTarget.container)
			: new StorageAllocationTracker.StorageLocation(storeTarget.cell);
		
		                // Reserve capacity for the initial item
                // Use actual carriable amount instead of full stack count to avoid over-reservation
                var actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, capacity);
                if (actualCarriableAmount <= 0)
                {
                        Log.Warning($"[PickUpAndHaul] WARNING: Pawn {pawn} cannot carry any of {thing} due to encumbrance");
                        skipCells = null;
                        skipThings = null;
                        PerformanceProfiler.EndTimer("JobOnThing");
                        return null;
                }
                
                if (!StorageAllocationTracker.ReserveCapacity(storageLocation, thing.def, actualCarriableAmount, pawn))
                {
                        Log.Warning($"[PickUpAndHaul] WARNING: Cannot reserve capacity for {actualCarriableAmount} of {thing} at {storageLocation} - insufficient available capacity");
                        skipCells = null;
                        skipThings = null;
                        PerformanceProfiler.EndTimer("JobOnThing");
                        return null;
                }
                
                Log.Message($"[PickUpAndHaul] DEBUG: Reserved capacity for {actualCarriableAmount} of {thing} at {storageLocation}");

                                if (!AllocateThingAtCell(storeCellCapacity, pawn, thing, job, ref currentMass, capacity))
                {
                        Log.Error($"[PickUpAndHaul] ERROR: Failed to allocate initial item {thing} - this should not happen since storage was verified");
                        // Release reserved capacity
                        StorageAllocationTracker.ReleaseCapacity(storageLocation, thing.def, actualCarriableAmount, pawn);
                        skipCells = null;
                        skipThings = null;
                        PerformanceProfiler.EndTimer("JobOnThing");
                        return null;
                }
		Log.Message($"[PickUpAndHaul] DEBUG: Initial item {thing} allocated successfully");

		// Skip the initial allocation loop since we already allocated the initial item
		// Just calculate encumbrance for the initial item
                currentMass += thing.GetStatValue(StatDefOf.Mass) * thing.stackCount;
                encumberance = currentMass / capacity;
                Log.Message($"[PickUpAndHaul] DEBUG: Initial item encumbrance: {encumberance}");

		// Now try to allocate additional items
		do
		{
                        nextThing = GetClosestAndRemove(lastThing.Position, map, haulables, PathEndMode.ClosestTouch,
                                traverseParms, distanceToSearchMore, Validator);
                        if (nextThing == null) break;

                        Log.Message($"[PickUpAndHaul] DEBUG: Attempting to allocate additional item {nextThing} to job");
                        if (AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job, ref currentMass, capacity))
                        {
                                lastThing = nextThing;
                                encumberance = currentMass / capacity;

                                if (encumberance > 1 || ceOverweight)
                                {
                                        //can't CountToPickUpUntilOverEncumbered here, pawn doesn't actually hold these things yet
                                        nextThingLeftOverCount = CountPastCapacity(pawn, nextThing, encumberance, capacity);
                                        Log.Message($"[PickUpAndHaul] DEBUG: Inventory allocated, will carry {nextThing}:{nextThingLeftOverCount}");
                                        Log.Message($"[PickUpAndHaul] DEBUG: Job queues after allocation - targetQueueA: {job.targetQueueA.Count}, targetQueueB: {job.targetQueueB.Count}, countQueue: {job.countQueue.Count}");
                                        break;
                                }
                        }
                        else
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Failed to allocate additional item {nextThing} to job - continuing with next item");
                                // For additional items, just continue to the next one - don't fail the job
                        }
                }
                while (true);
		
		if (nextThing == null)
		{
			Log.Message($"[PickUpAndHaul] DEBUG: No more things to allocate, final job state:");
			Log.Message($"[PickUpAndHaul] DEBUG: targetQueueA: {job.targetQueueA.Count}, targetQueueB: {job.targetQueueB.Count}, countQueue: {job.countQueue.Count}");
			// At this point, we always have at least the initial item allocated since it was validated earlier
			// No need to check if targetQueueA is empty - that's logically impossible here
			skipCells = null;
			skipThings = null;
			//skipTargets = null;
			PerformanceProfiler.EndTimer("JobOnThing");
			return job;
		}

		//Find what can be carried
		//this doesn't actually get pickupandhauled, but will hold the reservation so others don't grab what this pawn can carry
		haulables.RemoveAll(t => !t.CanStackWith(nextThing));

		var carryCapacity = pawn.carryTracker.MaxStackSpaceEver(nextThing.def) - nextThingLeftOverCount;
		if (carryCapacity == 0)
		{
			Log.Message($"[PickUpAndHaul] DEBUG: Can't carry more, nevermind!");
			Log.Message($"[PickUpAndHaul] DEBUG: Final job state - targetQueueA: {job.targetQueueA.Count}, targetQueueB: {job.targetQueueB.Count}, countQueue: {job.countQueue.Count}");
			skipCells = null;
			skipThings = null;
			//skipTargets = null;
			PerformanceProfiler.EndTimer("JobOnThing");
			return job;
		}
		Log.Message($"[PickUpAndHaul] DEBUG: Looking for more like {nextThing}, carryCapacity: {carryCapacity}");

                while ((nextThing = GetClosestAndRemove(nextThing.Position, map, haulables,
                           PathEndMode.ClosestTouch, traverseParms, 8f, Validator)) != null)
                {
                        carryCapacity -= nextThing.stackCount;
                        Log.Message($"[PickUpAndHaul] DEBUG: Found similar thing {nextThing}, carryCapacity now: {carryCapacity}");

                        if (AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job, ref currentMass, capacity))
			{
                                Log.Message($"[PickUpAndHaul] DEBUG: Successfully allocated similar thing {nextThing}");
				break;
			}

			if (carryCapacity <= 0)
			{
				var lastCount = job.countQueue.Pop() + carryCapacity;
				job.countQueue.Add(lastCount);
				Log.Message($"[PickUpAndHaul] DEBUG: Nevermind, last count is {lastCount}");
				break;
			}
		}

		Log.Message($"[PickUpAndHaul] DEBUG: Final job state before return:");
		Log.Message($"[PickUpAndHaul] DEBUG: targetQueueA: {job.targetQueueA.Count}, targetQueueB: {job.targetQueueB.Count}, countQueue: {job.countQueue.Count}");
		
		// Validate job before returning
		ValidateJobQueues(job, pawn, "Job Return");
		
		skipCells = null;
		skipThings = null;
		//skipTargets = null;
		PerformanceProfiler.EndTimer("JobOnThing");
		return job;
	}

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

	public struct StoreTarget : IEquatable<StoreTarget>
	{
		public IntVec3 cell;
		public Thing container;
		public IntVec3 Position => container?.Position ?? cell;

		public StoreTarget(IntVec3 cell)
		{
			this.cell = cell;
			container = null;
		}
		public StoreTarget(Thing container)
		{
			cell = default;
			this.container = container;
		}

		public bool Equals(StoreTarget other) => container is null ? other.container is null && cell == other.cell : container == other.container;
		public override int GetHashCode() => container?.GetHashCode() ?? cell.GetHashCode();
		public override string ToString() => container?.ToString() ?? cell.ToString();
		public override bool Equals(object obj) => obj is StoreTarget target ? Equals(target) : obj is Thing thing ? container == thing : obj is IntVec3 intVec && cell == intVec;
		public static bool operator ==(StoreTarget left, StoreTarget right) => left.Equals(right);
		public static bool operator !=(StoreTarget left, StoreTarget right) => !left.Equals(right);
		public static implicit operator LocalTargetInfo(StoreTarget target) => target.container != null ? target.container : target.cell;
	}

	public static Thing GetClosestAndRemove(IntVec3 center, Map map, List<Thing> searchSet, PathEndMode peMode, TraverseParms traverseParams, float maxDistance = 9999f, Predicate<Thing> validator = null)
	{
		PerformanceProfiler.StartTimer("GetClosestAndRemove");
		
		if (searchSet == null || !searchSet.Any())
		{
			PerformanceProfiler.EndTimer("GetClosestAndRemove");
			return null;
		}

                var maxDistanceSquared = maxDistance * maxDistance;
                for (var i = 0; i < searchSet.Count; i++)
                {
                        var thing = searchSet[i];

                        if (!thing.Spawned)
                        {
                                searchSet.RemoveAt(i--);
                                continue;
                        }

                        if ((center - thing.Position).LengthHorizontalSquared > maxDistanceSquared)
                        {
                                // list is distance sorted; everything beyond this will also be too far
                                searchSet.RemoveRange(i, searchSet.Count - i);
                                break;
                        }

                        if (!map.reachability.CanReach(center, thing, peMode, traverseParams))
                        {
                                continue;
                        }

                        if (validator == null || validator(thing))
                        {
                                searchSet.RemoveAt(i);
				PerformanceProfiler.EndTimer("GetClosestAndRemove");
                                return thing;
                        }
                }

		PerformanceProfiler.EndTimer("GetClosestAndRemove");
                return null;
        }

        public static Thing FindClosestThing(List<Thing> searchSet, IntVec3 center, out int index)
        {
                if (searchSet.Count == 0)
                {
                        index = -1;
                        return null;
                }

		var closestThing = searchSet[0];
		index = 0;
		var closestThingSquaredLength = (center - closestThing.Position).LengthHorizontalSquared;
		var count = searchSet.Count;
		for (var i = 1; i < count; i++)
		{
			if (closestThingSquaredLength > (center - searchSet[i].Position).LengthHorizontalSquared)
			{
				closestThing = searchSet[i];
				index = i;
				closestThingSquaredLength = (center - closestThing.Position).LengthHorizontalSquared;
			}
		}
		return closestThing;
	}

	public class CellAllocation
	{
		public Thing allocated;
		public int capacity;

		public CellAllocation(Thing a, int c)
		{
			allocated = a;
			capacity = c;
		}
	}

	public static int CapacityAt(Thing thing, IntVec3 storeCell, Map map)
	{
		if (HoldMultipleThings_Support.CapacityAt(thing, storeCell, map, out var capacity))
		{
			Log.Message($"Found external capacity of {capacity}");
			return capacity;
		}

		capacity = thing.def.stackLimit;

		var preExistingThing = map.thingGrid.ThingAt(storeCell, thing.def);
		if (preExistingThing != null)
		{
			capacity = thing.def.stackLimit - preExistingThing.stackCount;
		}

		return capacity;
	}

	public static bool Stackable(Thing nextThing, KeyValuePair<StoreTarget, CellAllocation> allocation)
		=> nextThing == allocation.Value.allocated
		|| allocation.Value.allocated.CanStackWith(nextThing)
		|| HoldMultipleThings_Support.StackableAt(nextThing, allocation.Key.cell, nextThing.Map);

        public static bool AllocateThingAtCell(Dictionary<StoreTarget, CellAllocation> storeCellCapacity, Pawn pawn, Thing nextThing, Job job, ref float currentMass, float capacity)
	{
		PerformanceProfiler.StartTimer("AllocateThingAtCell");
		
		// DEBUG: Log initial state
		Log.Message($"[PickUpAndHaul] DEBUG: AllocateThingAtCell called for {pawn} with {nextThing}");
		Log.Message($"[PickUpAndHaul] DEBUG: Initial job queues - targetQueueA: {job.targetQueueA?.Count ?? 0}, targetQueueB: {job.targetQueueB?.Count ?? 0}, countQueue: {job.countQueue?.Count ?? 0}");
		
                var map = pawn.Map;
                var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);
                var allocation = storeCellCapacity.FirstOrDefault(kvp =>
                        kvp.Key is var storeTarget
                        && (storeTarget.container?.TryGetInnerInteractableThingOwner().CanAcceptAnyOf(nextThing)
                        ?? storeTarget.cell.GetSlotGroup(map).parent.Accepts(nextThing))
                        && Stackable(nextThing, kvp));
                var storeCell = allocation.Key;
                
                // Track reservations and targets added during this method call
                var reservationsMade = new List<(StorageAllocationTracker.StorageLocation location, ThingDef def, int count)>();
                var targetsAdded = new List<LocalTargetInfo>();

                // Pre-validate storage capacity for existing allocations
                if (storeCell != default)
                {
                        var currentCapacity = storeCellCapacity[storeCell].capacity;
                        if (currentCapacity <= 0)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Pre-validation failed - storage {storeCell} has capacity {currentCapacity} <= 0, removing from allocation");
                                storeCellCapacity.Remove(storeCell);
                                storeCell = default;
                        }
                }

                //Can't stack with allocated cells, find a new cell:
                if (storeCell == default)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: No existing cell found for {nextThing}, searching for new storage");
                        if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var haulDestination, out var innerInteractableThingOwner))
                        {
                                if (innerInteractableThingOwner is null)
                                {
                                                                storeCell = new(nextStoreCell);
                        job.targetQueueB.Add(nextStoreCell);
                        targetsAdded.Add(nextStoreCell);

                        var newCapacity = CapacityAt(nextThing, nextStoreCell, map);
                        if (newCapacity <= 0)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: New cell {nextStoreCell} has capacity {newCapacity} <= 0, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                                        
                                                                // Check if we can reserve capacity for this item
                        var storageLocation = new StorageAllocationTracker.StorageLocation(nextStoreCell);
                        var actualCarriableAmount = CalculateActualCarriableAmount(nextThing, currentMass, capacity);
                        if (actualCarriableAmount <= 0)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Pawn {pawn} cannot carry any of {nextThing} due to encumbrance, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        if (!StorageAllocationTracker.HasAvailableCapacity(storageLocation, nextThing.def, actualCarriableAmount, map))
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Cannot reserve capacity for {actualCarriableAmount} of {nextThing} at {storageLocation}, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Reserve capacity
                        if (!StorageAllocationTracker.ReserveCapacity(storageLocation, nextThing.def, actualCarriableAmount, pawn))
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Failed to reserve capacity for {actualCarriableAmount} of {nextThing} at {storageLocation}, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Track this reservation
                        reservationsMade.Add((storageLocation, nextThing.def, actualCarriableAmount));
                                        
                                        storeCellCapacity[storeCell] = new(nextThing, newCapacity);

                                                                Log.Message($"[PickUpAndHaul] DEBUG: New cell for unstackable {nextThing} = {nextStoreCell}, targetsAdded = {targetsAdded.Count}");
                        Log.Message($"[PickUpAndHaul] DEBUG: targetQueueB count after adding: {job.targetQueueB.Count}");
                                }
                                else
                                {
                                                                var destinationAsThing = (Thing)haulDestination;
                        storeCell = new(destinationAsThing);
                        job.targetQueueB.Add(destinationAsThing);
                        targetsAdded.Add(destinationAsThing);

                        var newCapacity = innerInteractableThingOwner.GetCountCanAccept(nextThing);
                        if (newCapacity <= 0)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: New haulDestination {haulDestination} has capacity {newCapacity} <= 0, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Check if we can reserve capacity for this item
                        var storageLocation = new StorageAllocationTracker.StorageLocation(destinationAsThing);
                        var actualCarriableAmount = CalculateActualCarriableAmount(nextThing, currentMass, capacity);
                        if (actualCarriableAmount <= 0)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Pawn {pawn} cannot carry any of {nextThing} due to encumbrance, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        if (!StorageAllocationTracker.HasAvailableCapacity(storageLocation, nextThing.def, actualCarriableAmount, map))
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Cannot reserve capacity for {actualCarriableAmount} of {nextThing} at {storageLocation}, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Reserve capacity
                        if (!StorageAllocationTracker.ReserveCapacity(storageLocation, nextThing.def, actualCarriableAmount, pawn))
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Failed to reserve capacity for {actualCarriableAmount} of {nextThing} at {storageLocation}, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Track this reservation
                        reservationsMade.Add((storageLocation, nextThing.def, actualCarriableAmount));
                                        
                                        storeCellCapacity[storeCell] = new(nextThing, newCapacity);

                                                                Log.Message($"[PickUpAndHaul] DEBUG: New haulDestination for unstackable {nextThing} = {haulDestination}, targetsAdded = {targetsAdded.Count}");
                        Log.Message($"[PickUpAndHaul] DEBUG: targetQueueB count after adding: {job.targetQueueB.Count}");
                                }
                        }
                        else
                        {
                                                        Log.Message($"[PickUpAndHaul] DEBUG: {nextThing} can't stack with allocated cells and no new storage found");

                        // Clean up targets and reservations
                        CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                        PerformanceProfiler.EndTimer("AllocateThingAtCell");
                        return false;
                        }
                }

                var count = nextThing.stackCount;
                storeCellCapacity[storeCell].capacity -= count;
                Log.Message($"{pawn} allocating {nextThing}:{count}, now {storeCell}:{storeCellCapacity[storeCell].capacity}");

				while (storeCellCapacity[storeCell].capacity <= 0)
		{
			var capacityOver = -storeCellCapacity[storeCell].capacity;
			storeCellCapacity.Remove(storeCell);

			Log.Message($"[PickUpAndHaul] DEBUG: {pawn} overdone {storeCell} by {capacityOver}");

			if (capacityOver == 0)
			{
				break;  //don't find new cell, might not have more of this thing to haul
			}

                        if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var nextHaulDestination, out var innerInteractableThingOwner))
			{
				if (innerInteractableThingOwner is null)
				{
					                        storeCell = new(nextStoreCell);
                        job.targetQueueB.Add(nextStoreCell);
                        targetsAdded.Add(nextStoreCell);

                        var newCapacity = CapacityAt(nextThing, nextStoreCell, map) - capacityOver;
                        if (newCapacity <= 0)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: New overflow cell {nextStoreCell} has capacity {newCapacity} <= 0 after overflow, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Check if we can reserve capacity for the overflow amount
                        var storageLocation = new StorageAllocationTracker.StorageLocation(nextStoreCell);
                        var actualCarriableAmount = CalculateActualCarriableAmount(nextThing, currentMass, capacity);
                        var actualOverflowAmount = Math.Min(capacityOver, actualCarriableAmount);
                        
                        if (actualOverflowAmount <= 0)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Pawn {pawn} cannot carry any overflow of {nextThing} due to encumbrance, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        if (!StorageAllocationTracker.HasAvailableCapacity(storageLocation, nextThing.def, actualOverflowAmount, map))
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Cannot reserve capacity for overflow {actualOverflowAmount} of {nextThing} at {storageLocation}, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Reserve capacity for the overflow
                        if (!StorageAllocationTracker.ReserveCapacity(storageLocation, nextThing.def, actualOverflowAmount, pawn))
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Failed to reserve capacity for overflow {actualOverflowAmount} of {nextThing} at {storageLocation}, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Track this reservation
                        reservationsMade.Add((storageLocation, nextThing.def, actualOverflowAmount));
					
                                        storeCellCapacity[storeCell] = new(nextThing, newCapacity);

                                                                Log.Message($"[PickUpAndHaul] DEBUG: New cell {nextStoreCell}:{newCapacity}, allocated extra {actualOverflowAmount}, targetsAdded = {targetsAdded.Count}");
                        Log.Message($"[PickUpAndHaul] DEBUG: targetQueueB count after adding: {job.targetQueueB.Count}");
				}
				else
				{
					                        var destinationAsThing = (Thing)nextHaulDestination;
                        storeCell = new(destinationAsThing);
                        job.targetQueueB.Add(destinationAsThing);
                        targetsAdded.Add(destinationAsThing);

                        var newCapacity = innerInteractableThingOwner.GetCountCanAccept(nextThing) - capacityOver;
                        if (newCapacity <= 0)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: New overflow haulDestination {nextHaulDestination} has capacity {newCapacity} <= 0 after overflow, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Check if we can reserve capacity for the overflow amount
                        var storageLocation = new StorageAllocationTracker.StorageLocation(destinationAsThing);
                        var actualCarriableAmount = CalculateActualCarriableAmount(nextThing, currentMass, capacity);
                        var actualOverflowAmount = Math.Min(capacityOver, actualCarriableAmount);
                        
                        if (actualOverflowAmount <= 0)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Pawn {pawn} cannot carry any overflow of {nextThing} due to encumbrance, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        if (!StorageAllocationTracker.HasAvailableCapacity(storageLocation, nextThing.def, actualOverflowAmount, map))
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Cannot reserve capacity for overflow {actualOverflowAmount} of {nextThing} at {storageLocation}, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Reserve capacity for the overflow
                        if (!StorageAllocationTracker.ReserveCapacity(storageLocation, nextThing.def, actualOverflowAmount, pawn))
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Failed to reserve capacity for overflow {actualOverflowAmount} of {nextThing} at {storageLocation}, skipping");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                        
                        // Track this reservation
                        reservationsMade.Add((storageLocation, nextThing.def, actualOverflowAmount));
					
                                        storeCellCapacity[storeCell] = new(nextThing, newCapacity);

                                                                Log.Message($"[PickUpAndHaul] DEBUG: New haulDestination {nextHaulDestination}:{newCapacity}, allocated extra {actualOverflowAmount}, targetsAdded = {targetsAdded.Count}");
                        Log.Message($"[PickUpAndHaul] DEBUG: targetQueueB count after adding: {job.targetQueueB.Count}");
				}
			}
                        else
                        {
                                count -= capacityOver;
                                                        if (count <= 0)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Cleaning up {targetsAdded.Count} targets from targetQueueB due to zero capacity");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                Log.Message($"[PickUpAndHaul] DEBUG: Nowhere else to store, skipping {nextThing} due to zero capacity");
                                return false;
                        }
                        // Don't add to countQueue here - this would desynchronize the queues
                        Log.Message($"[PickUpAndHaul] DEBUG: Nowhere else to store, skipping {nextThing}:{count}");
                        Log.Message($"[PickUpAndHaul] DEBUG: Cleaning up {targetsAdded.Count} targets from targetQueueB");
                        // Clean up targets and reservations
                        CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                        PerformanceProfiler.EndTimer("AllocateThingAtCell");
                        return false;
                        }
		}

                if (count <= 0)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: Final count is <= 0, cleaning up {targetsAdded.Count} targets from targetQueueB");
                        // Clean up targets and reservations
                        CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                        PerformanceProfiler.EndTimer("AllocateThingAtCell");
                        Log.Message($"[PickUpAndHaul] DEBUG: Skipping {nextThing} due to zero capacity");
                        return false;
                }

                job.targetQueueA.Add(nextThing);
                job.countQueue.Add(count);
                currentMass += nextThing.GetStatValue(StatDefOf.Mass) * count;
                Log.Message($"[PickUpAndHaul] DEBUG: {nextThing}:{count} allocated successfully");
                Log.Message($"[PickUpAndHaul] DEBUG: Final job queues - targetQueueA: {job.targetQueueA.Count}, targetQueueB: {job.targetQueueB.Count}, countQueue: {job.countQueue.Count}");
                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                return true;
        }

        /// <summary>
        /// Cleans up targets and reservations made during AllocateThingAtCell execution
        /// </summary>
        private static void CleanupAllocateThingAtCell(Job job, List<LocalTargetInfo> targetsAdded, List<(StorageAllocationTracker.StorageLocation location, ThingDef def, int count)> reservationsMade, Pawn pawn)
        {
                // Release all reservations made during this method execution
                foreach (var (location, def, count) in reservationsMade)
                {
                        StorageAllocationTracker.ReleaseCapacity(location, def, count, pawn);
                        Log.Message($"[PickUpAndHaul] DEBUG: Released reservation for {def} x{count} at {location}");
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
                                Log.Message($"[PickUpAndHaul] DEBUG: Removed target {targetToRemove} from targetQueueB at index {index}");
                        }
                }
        }

	//public static HashSet<StoreTarget> skipTargets;
	public static HashSet<IntVec3> skipCells;
	public static HashSet<Thing> skipThings;

	public static bool TryFindBestBetterStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, out IHaulDestination haulDestination, out ThingOwner innerInteractableThingOwner)
	{
		var storagePriority = StoragePriority.Unstored;
		innerInteractableThingOwner = null;
		if (TryFindBestBetterStoreCellFor(t, carrier, map, currentPriority, faction, out var foundCell2))
		{
			storagePriority = foundCell2.GetSlotGroup(map).Settings.Priority;
		}

		if (!TryFindBestBetterNonSlotGroupStorageFor(t, carrier, map, currentPriority, faction, out var haulDestination2))
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

	public static bool TryFindBestBetterStoreCellFor(Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell)
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
				if (skipCells.Contains(cell))
				{
					continue;
				}

				if (StoreUtility.IsGoodStoreCell(cell, map, thing, carrier, faction) && cell != default)
				{
					foundCell = cell;

					skipCells.Add(cell);

					return true;
				}
			}
		}
		foundCell = IntVec3.Invalid;
		return false;
	}

        public static float AddedEncumberance(Pawn pawn, Thing thing, float capacity)
                => thing.stackCount * thing.GetStatValue(StatDefOf.Mass) / capacity;

        public static int CountPastCapacity(Pawn pawn, Thing thing, float encumberance, float capacity)
                => (int)Math.Ceiling((encumberance - 1) * capacity / thing.GetStatValue(StatDefOf.Mass));

        /// <summary>
        /// Calculates the actual amount of a thing that a pawn can carry considering encumbrance limits
        /// </summary>
        public static int CalculateActualCarriableAmount(Thing thing, float currentMass, float capacity)
        {
                if (currentMass >= capacity)
                {
                        return 0;
                }

                var remainingCapacity = capacity - currentMass;
                var thingMass = thing.GetStatValue(StatDefOf.Mass);

                if (thingMass <= 0)
                {
                        return thing.stackCount;
                }

                var maxCarriable = (int)Math.Floor(remainingCapacity / thingMass);
                return Math.Min(maxCarriable, thing.stackCount);
        }

	public static bool TryFindBestBetterNonSlotGroupStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IHaulDestination haulDestination, bool acceptSamePriority = false)
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
				if (skipThings.Contains(thing) || thing.Faction != faction)
				{
					continue;
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

				skipThings.Add(thing);
			}
			else
			{
				//not supported. Seems dumb
				continue;

				//if (carrier != null && !carrier.Map.reachability.CanReach(intVec, iHaulDestination.Position, PathEndMode.ClosestTouch, TraverseParms.For(carrier)))
				//	continue;
			}

			num = num2;
			storagePriority = priority;
			haulDestination = iHaulDestination;
		}

		return haulDestination != null;
	}

	/// <summary>
	/// Validates job queue integrity to help debug ArgumentOutOfRangeException
	/// </summary>
	public static void ValidateJobQueues(Job job, Pawn pawn, string context)
	{
		if (job == null)
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Job is null in {context} for {pawn}");
			return;
		}

		var targetQueueACount = job.targetQueueA?.Count ?? 0;
		var targetQueueBCount = job.targetQueueB?.Count ?? 0;
		var countQueueCount = job.countQueue?.Count ?? 0;

		Log.Message($"[PickUpAndHaul] VALIDATION [{context}]: {pawn} - targetQueueA: {targetQueueACount}, targetQueueB: {targetQueueBCount}, countQueue: {countQueueCount}");

		// Check for null queues
		if (job.targetQueueA == null)
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: targetQueueA is null in {context} for {pawn}");
		}
		if (job.targetQueueB == null)
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: targetQueueB is null in {context} for {pawn}");
		}
		if (job.countQueue == null)
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: countQueue is null in {context} for {pawn}");
		}

		// Check for queue synchronization issues
		if (targetQueueACount != countQueueCount)
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Queue synchronization issue in {context} for {pawn} - targetQueueA.Count ({targetQueueACount}) != countQueue.Count ({countQueueCount})");
		}

		// Check for empty targetQueueA (this would cause the ArgumentOutOfRangeException)
		// But only flag as error if it's not during job creation (where empty is expected)
		if (targetQueueACount == 0 && context != "Job Creation")
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: targetQueueA is empty in {context} for {pawn} - this will cause ArgumentOutOfRangeException!");
		}

		// Log queue contents for debugging
		if (job.targetQueueA != null && job.targetQueueA.Count > 0)
		{
			Log.Message($"[PickUpAndHaul] VALIDATION [{context}]: targetQueueA contents: {string.Join(", ", job.targetQueueA.Select(t => t.ToStringSafe()))}");
		}
		if (job.countQueue != null && job.countQueue.Count > 0)
		{
			Log.Message($"[PickUpAndHaul] VALIDATION [{context}]: countQueue contents: {string.Join(", ", job.countQueue)}");
		}
	}
}

public static class PickUpAndHaulDesignationDefOf
{
	public static DesignationDef haulUrgently = DefDatabase<DesignationDef>.GetNamedSilentFail("HaulUrgentlyDesignation");
}