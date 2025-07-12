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
		|| !PickupAndHaulSaveLoadLogger.IsModActive() // Skip if mod is not active
		|| pawn.RaceProps.Animal; // Skip animals - they can't use inventory systems
		
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
        private static int _lastCacheCleanupTick = 0;
        private const int CacheCleanupInterval = 2500; // Clean up every ~1 minute

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
                PerformanceProfiler.StartTimer("PotentialWorkThingsGlobal");

                var currentTick = Find.TickManager.TicksGame;
                
                // Periodic cleanup to prevent memory leaks
                if (currentTick - _lastCacheCleanupTick > CacheCleanupInterval)
                {
                        CleanupPotentialWorkCache(currentTick);
                        _lastCacheCleanupTick = currentTick;
                }

                if (_potentialWorkCache.TryGetValue(pawn, out var cached) && currentTick - cached.tick <= CacheDuration)
                {
                        PerformanceProfiler.EndTimer("PotentialWorkThingsGlobal");
                        // Return a copy to prevent cache corruption
                        return new List<Thing>(cached.list);
                }

                var list = new List<Thing>(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
                // Ensure items are sorted by distance from pawn to prioritize closest items
                Comparer.rootCell = pawn.Position;
                list.Sort(Comparer);

                _potentialWorkCache[pawn] = (currentTick, list);

                Log.Message($"[PickUpAndHaul] DEBUG: PotentialWorkThingsGlobal for {pawn} at {pawn.Position} found {list.Count} items, first item: {list.FirstOrDefault()?.Position}");

                PerformanceProfiler.EndTimer("PotentialWorkThingsGlobal");
                // Return a copy to prevent cache corruption
                return new List<Thing>(list);
        }

        /// <summary>
        /// Cleans up the potential work cache to prevent memory leaks
        /// </summary>
        private static void CleanupPotentialWorkCache(int currentTick)
        {
                var keysToRemove = new List<Pawn>();
                
                foreach (var kvp in _potentialWorkCache)
                {
                        var pawn = kvp.Key;
                        var (tick, _) = kvp.Value;
                        
                        // Remove entries for dead/destroyed pawns or expired entries
                        if (pawn == null || pawn.Destroyed || !pawn.Spawned || currentTick - tick > CacheDuration * 2)
                        {
                                keysToRemove.Add(pawn);
                        }
                }
                
                foreach (var key in keysToRemove)
                {
                        _potentialWorkCache.Remove(key);
                }
                
                if (keysToRemove.Count > 0)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: Cleaned up {keysToRemove.Count} entries from potential work cache");
                }
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
                && !pawn.RaceProps.Animal // Skip animals - they can't use inventory systems
                && OkThingToHaul(thing, pawn)
                && IsNotCorpseOrAllowed(thing)
		&& HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced)
		&& !OverAllowedGearCapacity(pawn)
		&& !MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1);
		
		// Check for basic storage availability and encumbrance, but be less restrictive about storage capacity
		// Let the allocation phase handle sophisticated storage finding and capacity management
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
				// Check if pawn can physically carry the item and if storage has meaningful capacity
				var currentMass = MassUtility.GearAndInventoryMass(pawn);
				var capacity = MassUtility.Capacity(pawn);
				var actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, capacity);
				if (actualCarriableAmount <= 0)
				{
					Log.Message($"[PickUpAndHaul] DEBUG: HasJobOnThing: Pawn {pawn} cannot carry any of {thing} due to encumbrance, returning false");
					result = false;
				}
				else
				{
					// Check if storage has any meaningful capacity for this item
					// This prevents the HasJobOnThing/JobOnThing synchronization issue
					var storageCapacity = 0;
					if (haulDestination is ISlotGroupParent)
					{
						storageCapacity = CapacityAt(thing, foundCell, pawn.Map);
					}
					else if (haulDestination is Thing destinationThing)
					{
						var thingOwner = destinationThing.TryGetInnerInteractableThingOwner();
						if (thingOwner != null)
						{
							storageCapacity = thingOwner.GetCountCanAccept(thing);
						}
					}
					
					if (storageCapacity <= 0)
					{
						Log.Message($"[PickUpAndHaul] DEBUG: HasJobOnThing: Storage for {thing} has no capacity, returning false");
						result = false;
					}
					else
					{
						// At least some capacity exists, let JobOnThing handle the detailed allocation
						Log.Message($"[PickUpAndHaul] DEBUG: HasJobOnThing: {pawn} can carry {actualCarriableAmount} of {thing}, storage has {storageCapacity} capacity");
					}
				}
			}
		}
		
		PerformanceProfiler.EndTimer("HasJobOnThing");
		return result;
	}

	//bulky gear (power armor + minigun) so don't bother.
	//Updated to include inventory mass, not just gear mass

        private static readonly Dictionary<Pawn, (int tick, bool result)> _encumbranceCache = new();
        private static int _lastEncumbranceCacheCleanupTick = 0;

        public static bool OverAllowedGearCapacity(Pawn pawn)
        {
                PerformanceProfiler.StartTimer("OverAllowedGearCapacity");
                
                var currentTick = Find.TickManager.TicksGame;

                // Periodic cleanup to prevent memory leaks
                if (currentTick - _lastEncumbranceCacheCleanupTick > CacheCleanupInterval)
                {
                        CleanupEncumbranceCache(currentTick);
                        _lastEncumbranceCacheCleanupTick = currentTick;
                }

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

        /// <summary>
        /// Cleans up the encumbrance cache to prevent memory leaks
        /// </summary>
        private static void CleanupEncumbranceCache(int currentTick)
        {
                var keysToRemove = new List<Pawn>();
                
                foreach (var kvp in _encumbranceCache)
                {
                        var pawn = kvp.Key;
                        var (tick, _) = kvp.Value;
                        
                        // Remove entries for dead/destroyed pawns or stale entries (older than 1 tick)
                        if (pawn == null || pawn.Destroyed || !pawn.Spawned || currentTick - tick > 1)
                        {
                                keysToRemove.Add(pawn);
                        }
                }
                
                foreach (var key in keysToRemove)
                {
                        _encumbranceCache.Remove(key);
                }
                
                if (keysToRemove.Count > 0)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: Cleaned up {keysToRemove.Count} entries from encumbrance cache");
                }
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

                // Do not create hauling jobs for pawns in a mental state or animals
                if (pawn.InMentalState || pawn.RaceProps.Animal)
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

		// Use a more reasonable and consistent search distance
		// The original calculation was too restrictive and inconsistent
		var distanceToSearchMore = 20f; // Increased from variable calculation to fixed reasonable distance

		//Find extra things than can be hauled to inventory, queue to reserve them
		var haulUrgentlyDesignation = PickUpAndHaulDesignationDefOf.haulUrgently;
		var isUrgent = ModCompatibilityCheck.AllowToolIsActive && designationManager.DesignationOn(thing)?.def == haulUrgentlyDesignation;

		var haulables = new List<Thing>(map.listerHaulables.ThingsPotentiallyNeedingHauling());
		// Sort by distance from pawn, not from the initial thing, to ensure closest items are picked first
		Comparer.rootCell = pawn.Position;
		haulables.Sort(Comparer);
		
		Log.Message($"[PickUpAndHaul] DEBUG: Found {haulables.Count} haulable items to consider, sorted by distance from pawn at {pawn.Position}");

		// Don't pre-filter items - check storage availability dynamically during allocation
		Log.Message($"[PickUpAndHaul] DEBUG: Will check storage availability dynamically during allocation for {haulables.Count} items");

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
		{
			var urgentCheck = !isUrgent || designationManager.DesignationOn(t)?.def == haulUrgentlyDesignation;
			var goodToHaul = GoodThingToHaul(t, pawn);
			var canHaulFast = HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false);
			var notOverEncumbered = !MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, t, 1);
			
			var result = urgentCheck && goodToHaul && canHaulFast && notOverEncumbered;
			
			if (!result)
			{
				Log.Message($"[PickUpAndHaul] DEBUG: Validator failed for {t} - urgent: {urgentCheck}, good: {goodToHaul}, fast: {canHaulFast}, encumbered: {notOverEncumbered}");
			}
			
			return result;
		}

		haulables.Remove(thing);

		// Always allocate the initial item first to ensure the job succeeds
		Log.Message($"[PickUpAndHaul] DEBUG: Allocating initial item {thing} to ensure job success");
		
		// Create storage location for tracking
		var storageLocation = storeTarget.container != null 
			? new StorageAllocationTracker.StorageLocation(storeTarget.container)
			: new StorageAllocationTracker.StorageLocation(storeTarget.cell);
		
		// Reserve capacity for the initial item
		// Use minimum of what pawn can carry and what storage can hold to prevent reservation failures
		var actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, capacity);
		var effectiveAmount = Math.Min(actualCarriableAmount, capacityStoreCell);
		
		if (effectiveAmount <= 0)
		{
			Log.Warning($"[PickUpAndHaul] WARNING: Pawn {pawn} cannot effectively haul {thing} - carriable: {actualCarriableAmount}, storage capacity: {capacityStoreCell}");
			skipCells = null;
			skipThings = null;
			PerformanceProfiler.EndTimer("JobOnThing");
			return null;
		}
		
		if (!StorageAllocationTracker.ReserveCapacity(storageLocation, thing.def, effectiveAmount, pawn))
		{
			Log.Warning($"[PickUpAndHaul] WARNING: Cannot reserve capacity for {effectiveAmount} of {thing} at {storageLocation} - insufficient available capacity");
			skipCells = null;
			skipThings = null;
			PerformanceProfiler.EndTimer("JobOnThing");
			return null;
		}
		
		Log.Message($"[PickUpAndHaul] DEBUG: Reserved capacity for {effectiveAmount} of {thing} at {storageLocation} (carriable: {actualCarriableAmount}, storage: {capacityStoreCell})");

		if (!AllocateThingAtCell(storeCellCapacity, pawn, thing, job, ref currentMass, capacity))
		{
			Log.Error($"[PickUpAndHaul] ERROR: Failed to allocate initial item {thing} - this should not happen since storage was verified");
			// Release reserved capacity
			StorageAllocationTracker.ReleaseCapacity(storageLocation, thing.def, effectiveAmount, pawn);
			skipCells = null;
			skipThings = null;
			PerformanceProfiler.EndTimer("JobOnThing");
			return null;
		}
		Log.Message($"[PickUpAndHaul] DEBUG: Initial item {thing} allocated successfully");

		// Skip the initial allocation loop since we already allocated the initial item
		// Note: currentMass is already updated by AllocateThingAtCell, so no need to add it again
		encumberance = currentMass / capacity;
		Log.Message($"[PickUpAndHaul] DEBUG: Initial item encumbrance: {encumberance}");

		// Now try to allocate additional items
		Log.Message($"[PickUpAndHaul] DEBUG: Starting additional item allocation loop with {haulables.Count} remaining haulables");
		Log.Message($"[PickUpAndHaul] DEBUG: Pawn position: {pawn.Position}, Initial item position: {thing.Position}, Last item position: {lastThing.Position}");
		Log.Message($"[PickUpAndHaul] DEBUG: Searching from position {lastThing.Position} with max distance {distanceToSearchMore}");
	
	do
	{
		// Early exit if pawn is at max encumbrance - they can't carry anything more
		if (encumberance >= 1.0f)
		{
			Log.Message($"[PickUpAndHaul] DEBUG: Pawn {pawn} at max encumbrance ({currentMass}/{capacity}), stopping additional item search");
			break;
		}
		
		nextThing = GetClosestAndRemove(lastThing.Position, map, haulables, PathEndMode.ClosestTouch,
			traverseParms, distanceToSearchMore, Validator);
		if (nextThing == null) 
		{
			Log.Message($"[PickUpAndHaul] DEBUG: GetClosestAndRemove returned null - no more valid items found");
			Log.Message($"[PickUpAndHaul] DEBUG: Remaining haulables count: {haulables.Count}");
			break;
		}

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
			Log.Message($"[PickUpAndHaul] DEBUG: GetClosestAndRemove - searchSet is null or empty");
			PerformanceProfiler.EndTimer("GetClosestAndRemove");
			return null;
		}

		Log.Message($"[PickUpAndHaul] DEBUG: GetClosestAndRemove - searching {searchSet.Count} items from {center} with max distance {maxDistance}");
                var maxDistanceSquared = maxDistance * maxDistance;
                var itemsChecked = 0;
                var itemsFiltered = 0;
                var itemsUnreachable = 0;
                var itemsUnspawned = 0;
                var itemsTooFar = 0;
                
                for (var i = 0; i < searchSet.Count; i++)
                {
                        var thing = searchSet[i];
                        itemsChecked++;

                        if (!thing.Spawned)
                        {
                                itemsUnspawned++;
                                searchSet.RemoveAt(i--);
                                continue;
                        }

                        var distanceSquared = (center - thing.Position).LengthHorizontalSquared;
                        if (distanceSquared > maxDistanceSquared)
                        {
                                itemsTooFar++;
                                // list is distance sorted; everything beyond this will also be too far
                                searchSet.RemoveRange(i, searchSet.Count - i);
                                break;
                        }

                        if (!map.reachability.CanReach(center, thing, peMode, traverseParams))
                        {
                                itemsUnreachable++;
                                continue;
                        }

                        if (validator == null || validator(thing))
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: GetClosestAndRemove - found valid item {thing} at distance {Math.Sqrt(distanceSquared):F1}");
                                searchSet.RemoveAt(i);
				PerformanceProfiler.EndTimer("GetClosestAndRemove");
                                return thing;
                        }
                        else
                        {
                                itemsFiltered++;
                        }
                }

                Log.Message($"[PickUpAndHaul] DEBUG: GetClosestAndRemove - no valid items found. Checked: {itemsChecked}, Unspawned: {itemsUnspawned}, Too far: {itemsTooFar}, Unreachable: {itemsUnreachable}, Filtered: {itemsFiltered}");
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

		// Check if there's a container at this cell that can hold multiple items
		var thingsAtCell = map.thingGrid.ThingsListAt(storeCell);
		for (int i = 0; i < thingsAtCell.Count; i++)
		{
			var thingAtCell = thingsAtCell[i];
			var thingOwner = thingAtCell.TryGetInnerInteractableThingOwner();
			if (thingOwner != null)
			{
				// This is a container (like a crate) - use its proper capacity calculation
				var containerCapacity = thingOwner.GetCountCanAccept(thing);
				Log.Message($"[PickUpAndHaul] DEBUG: Found container {thingAtCell} at {storeCell} with capacity {containerCapacity} for {thing}");
				return containerCapacity;
			}
		}

		// Fallback to original logic for simple storage cells
		capacity = thing.def.stackLimit;

		var preExistingThing = map.thingGrid.ThingAt(storeCell, thing.def);
		if (preExistingThing != null)
		{
			capacity = thing.def.stackLimit - preExistingThing.stackCount;
		}

		Log.Message($"[PickUpAndHaul] DEBUG: Using fallback capacity calculation for {thing} at {storeCell}: {capacity}");
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
		Log.Message($"[PickUpAndHaul] DEBUG: Current mass: {currentMass}, capacity: {capacity}, encumbrance: {currentMass / capacity}");
		
                var map = pawn.Map;
                var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);
                
                // First, check if the pawn can carry this item at all
                var actualCarriableAmount = CalculateActualCarriableAmount(nextThing, currentMass, capacity);
                if (actualCarriableAmount <= 0)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: Pawn {pawn} cannot carry any of {nextThing} due to encumbrance (current: {currentMass}/{capacity}), skipping");
                        PerformanceProfiler.EndTimer("AllocateThingAtCell");
                        return false;
                }
                
                Log.Message($"[PickUpAndHaul] DEBUG: Pawn {pawn} can carry {actualCarriableAmount} of {nextThing}");
                
                // Find existing compatible storage, but safely check for valid cells
                var allocation = storeCellCapacity.FirstOrDefault(kvp => {
                        var storeTarget = kvp.Key;
                        try
                        {
                                // Handle container storage
                                if (storeTarget.container != null)
                                {
                                        var thingOwner = storeTarget.container.TryGetInnerInteractableThingOwner();
                                        return thingOwner != null && thingOwner.CanAcceptAnyOf(nextThing) && Stackable(nextThing, kvp);
                                }
                                // Handle cell storage - check if cell is valid before accessing slot group
                                else if (storeTarget.cell.IsValid && storeTarget.cell.InBounds(map))
                                {
                                        var slotGroup = storeTarget.cell.GetSlotGroup(map);
                                        return slotGroup != null && slotGroup.parent.Accepts(nextThing) && Stackable(nextThing, kvp);
                                }
                                return false;
                        }
                        catch (System.Exception ex)
                        {
                                Log.Warning($"[PickUpAndHaul] Exception checking storage compatibility for {nextThing}: {ex.Message}");
                                return false;
                        }
                });
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
                        else
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Found existing compatible storage {storeCell} with capacity {currentCapacity}");
                        }
                }

                //Can't stack with allocated cells, find a new cell:
                if (storeCell == default)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: No existing cell found for {nextThing}, searching for new storage");
                        if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var haulDestination, out var innerInteractableThingOwner))
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Found new storage for {nextThing}: {haulDestination?.ToStringSafe() ?? nextStoreCell.ToString()}");
                                
                                if (innerInteractableThingOwner is null)
                                {
                                        storeCell = new(nextStoreCell);
                                        job.targetQueueB.Add(nextStoreCell);
                                        targetsAdded.Add(nextStoreCell);

                                        var newCapacity = CapacityAt(nextThing, nextStoreCell, map);
                                        Log.Message($"[PickUpAndHaul] DEBUG: New cell {nextStoreCell} has capacity {newCapacity}");
                                        
                                        if (newCapacity <= 0)
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: New cell {nextStoreCell} has capacity {newCapacity} <= 0, skipping this item");
                                                // Clean up targets and reservations
                                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                                return false;
                                        }
                                        
                                        						// For new storage, be more flexible with capacity reservation
						var storageLocation = new StorageAllocationTracker.StorageLocation(nextStoreCell);
						var reservationAmount = Math.Min(actualCarriableAmount, newCapacity);
						
						// Try to reserve capacity, but don't fail if we can't
						if (StorageAllocationTracker.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
                                        {
                                                reservationsMade.Add((storageLocation, nextThing.def, reservationAmount));
                                                Log.Message($"[PickUpAndHaul] DEBUG: Reserved {reservationAmount} capacity for {nextThing} at {storageLocation}");
                                        }
                                        else
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: Could not reserve capacity for {nextThing} at {storageLocation}, but continuing anyway");
                                        }
                                        
                                        storeCellCapacity[storeCell] = new(nextThing, newCapacity);
                                        Log.Message($"[PickUpAndHaul] DEBUG: New cell for {nextThing} = {nextStoreCell}, capacity = {newCapacity}");
                                }
                                else
                                {
                                        var destinationAsThing = (Thing)haulDestination;
                                        storeCell = new(destinationAsThing);
                                        job.targetQueueB.Add(destinationAsThing);
                                        targetsAdded.Add(destinationAsThing);

                                        var newCapacity = innerInteractableThingOwner.GetCountCanAccept(nextThing);
                                        Log.Message($"[PickUpAndHaul] DEBUG: New haulDestination {haulDestination} has capacity {newCapacity}");
                                        
                                        if (newCapacity <= 0)
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: New haulDestination {haulDestination} has capacity {newCapacity} <= 0, skipping this item");
                                                // Clean up targets and reservations
                                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                                return false;
                                        }
                                        
                                        						// For new storage, be more flexible with capacity reservation
						var storageLocation = new StorageAllocationTracker.StorageLocation(destinationAsThing);
						var reservationAmount = Math.Min(actualCarriableAmount, newCapacity);
						
						// Try to reserve capacity, but don't fail if we can't
						if (StorageAllocationTracker.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
                                        {
                                                reservationsMade.Add((storageLocation, nextThing.def, reservationAmount));
                                                Log.Message($"[PickUpAndHaul] DEBUG: Reserved {reservationAmount} capacity for {nextThing} at {storageLocation}");
                                        }
                                        else
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: Could not reserve capacity for {nextThing} at {storageLocation}, but continuing anyway");
                                        }
                                        
                                        storeCellCapacity[storeCell] = new(nextThing, newCapacity);
                                        Log.Message($"[PickUpAndHaul] DEBUG: New haulDestination for {nextThing} = {haulDestination}, capacity = {newCapacity}");
                                }
                        }
                        else
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: {nextThing} can't stack with allocated cells and no new storage found, skipping this item");
                                // Clean up targets and reservations
                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                return false;
                        }
                }

                // Calculate the effective amount considering storage capacity, carriable amount, and item stack size
                var availableStorageCapacity = Math.Max(0, storeCellCapacity[storeCell].capacity);
                var count = Math.Min(nextThing.stackCount, Math.Min(actualCarriableAmount, availableStorageCapacity));
                storeCellCapacity[storeCell].capacity -= count;
                Log.Message($"[PickUpAndHaul] DEBUG: {pawn} allocating {nextThing}:{count} (carriable: {actualCarriableAmount}, storage: {availableStorageCapacity}), storage capacity remaining: {storeCellCapacity[storeCell].capacity}");

                // Handle capacity overflow more gracefully
                while (storeCellCapacity[storeCell].capacity < 0)
                {
                        var capacityOver = -storeCellCapacity[storeCell].capacity;
                        storeCellCapacity.Remove(storeCell);

                        Log.Message($"[PickUpAndHaul] DEBUG: {pawn} overdone {storeCell} by {capacityOver}");

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
                                        job.targetQueueB.Add(nextStoreCell);
                                        targetsAdded.Add(nextStoreCell);

                                        var newCapacity = CapacityAt(nextThing, nextStoreCell, map) - capacityOver;
                                        Log.Message($"[PickUpAndHaul] DEBUG: New overflow cell {nextStoreCell} has capacity {newCapacity}");
                                        
                                        if (newCapacity <= 0)
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: New overflow cell {nextStoreCell} has insufficient capacity, skipping this item");
                                                // Clean up targets and reservations
                                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                                return false;
                                        }
                                        
                                        						// Try to reserve capacity for the overflow
						var storageLocation = new StorageAllocationTracker.StorageLocation(nextStoreCell);
						var reservationAmount = Math.Min(capacityOver, actualCarriableAmount);
						
						if (StorageAllocationTracker.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
                                        {
                                                reservationsMade.Add((storageLocation, nextThing.def, reservationAmount));
                                                Log.Message($"[PickUpAndHaul] DEBUG: Reserved {reservationAmount} overflow capacity for {nextThing} at {storageLocation}");
                                        }
                                        else
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: Could not reserve overflow capacity for {nextThing} at {storageLocation}, but continuing anyway");
                                        }
                                        
                                        storeCellCapacity[storeCell] = new(nextThing, newCapacity);
                                        Log.Message($"[PickUpAndHaul] DEBUG: New overflow cell {nextStoreCell}:{newCapacity} for {nextThing}");
                                }
                                else
                                {
                                        var destinationAsThing = (Thing)nextHaulDestination;
                                        storeCell = new(destinationAsThing);
                                        job.targetQueueB.Add(destinationAsThing);
                                        targetsAdded.Add(destinationAsThing);

                                        var newCapacity = innerInteractableThingOwner.GetCountCanAccept(nextThing) - capacityOver;
                                        Log.Message($"[PickUpAndHaul] DEBUG: New overflow haulDestination {nextHaulDestination} has capacity {newCapacity}");
                                        
                                        if (newCapacity <= 0)
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: New overflow haulDestination {nextHaulDestination} has insufficient capacity, skipping this item");
                                                // Clean up targets and reservations
                                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                                return false;
                                        }
                                        
                                        						// Try to reserve capacity for the overflow
						var storageLocation = new StorageAllocationTracker.StorageLocation(destinationAsThing);
						var reservationAmount = Math.Min(capacityOver, actualCarriableAmount);
						
						if (StorageAllocationTracker.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
                                        {
                                                reservationsMade.Add((storageLocation, nextThing.def, reservationAmount));
                                                Log.Message($"[PickUpAndHaul] DEBUG: Reserved {reservationAmount} overflow capacity for {nextThing} at {storageLocation}");
                                        }
                                        else
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: Could not reserve overflow capacity for {nextThing} at {storageLocation}, but continuing anyway");
                                        }
                                        
                                        storeCellCapacity[storeCell] = new(nextThing, newCapacity);
                                        Log.Message($"[PickUpAndHaul] DEBUG: New overflow haulDestination {nextHaulDestination}:{newCapacity} for {nextThing}");
                                }
                        }
                        else
                        {
                                count -= capacityOver;
                                Log.Message($"[PickUpAndHaul] DEBUG: No additional storage found for overflow, reducing count to {count}");
                                
                                if (count <= 0)
                                {
                                        Log.Message($"[PickUpAndHaul] DEBUG: Count reduced to {count}, cannot allocate {nextThing}");
                                        // Clean up targets and reservations
                                        CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                        PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                        return false;
                                }
                                break;
                        }
                }

                if (count <= 0)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: Final count is {count}, cannot allocate {nextThing}");
                        // Clean up targets and reservations
                        CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                        PerformanceProfiler.EndTimer("AllocateThingAtCell");
                        return false;
                }

                job.targetQueueA.Add(nextThing);
                job.countQueue.Add(count);
                currentMass += nextThing.GetStatValue(StatDefOf.Mass) * count;
                Log.Message($"[PickUpAndHaul] DEBUG: Successfully allocated {nextThing}:{count} to job");
                Log.Message($"[PickUpAndHaul] DEBUG: Updated mass: {currentMass}, new encumbrance: {currentMass / capacity}");
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
				
				// For multi-item hauling, allow reuse of cells that have remaining capacity
				// Only skip cells if they have no remaining capacity
				if (skipCells.Contains(cell))
				{
					// Check if this cell still has capacity for more items
					var remainingCapacity = CapacityAt(thing, cell, map);
					if (remainingCapacity <= 0)
					{
						continue; // Skip if no capacity
					}
					Log.Message($"[PickUpAndHaul] DEBUG: Reusing previously allocated cell {cell} with remaining capacity {remainingCapacity} for {thing}");
				}

				if (StoreUtility.IsGoodStoreCell(cell, map, thing, carrier, faction) && cell != default)
				{
					foundCell = cell;

					// Only add to skip list if this completely fills the cell
					var capacity = CapacityAt(thing, cell, map);
					if (capacity <= thing.stackCount)
					{
						skipCells.Add(cell);
						Log.Message($"[PickUpAndHaul] DEBUG: Cell {cell} will be full after {thing}, adding to skip list");
					}
					else
					{
						Log.Message($"[PickUpAndHaul] DEBUG: Cell {cell} will have remaining capacity after {thing}, not adding to skip list");
					}

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
				if (thing.Faction != faction)
				{
					continue;
				}
				
				// For multi-item hauling, allow reuse of containers that have remaining capacity
				if (skipThings.Contains(thing))
				{
					// Check if this container still has capacity for more items
					var thingOwner = thing.TryGetInnerInteractableThingOwner();
					if (thingOwner != null)
					{
						var remainingCapacity = thingOwner.GetCountCanAccept(t);
						if (remainingCapacity <= 0)
						{
							continue; // Skip if no capacity
						}
						Log.Message($"[PickUpAndHaul] DEBUG: Reusing previously allocated container {thing} with remaining capacity {remainingCapacity} for {t}");
					}
					else
					{
						continue; // Skip if can't determine capacity
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
						skipThings.Add(thing);
						Log.Message($"[PickUpAndHaul] DEBUG: Container {thing} will be full after {t}, adding to skip list");
					}
					else
					{
						Log.Message($"[PickUpAndHaul] DEBUG: Container {thing} will have remaining capacity after {t}, not adding to skip list");
					}
				}
				else
				{
					skipThings.Add(thing); // Add to skip list if we can't determine capacity
				}
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