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
		var result = base.ShouldSkip(pawn, forced)
                || pawn.InMentalState
                || pawn.Faction != Faction.OfPlayerSilentFail
                || !Settings.IsAllowedRace(pawn.RaceProps)
                || pawn.GetComp<CompHauledToInventory>() == null
		|| pawn.IsQuestLodger()
		|| OverAllowedGearCapacity(pawn)
		|| PickupAndHaulSaveLoadLogger.IsSaveInProgress()
		|| !PickupAndHaulSaveLoadLogger.IsModActive(); // Skip if mod is not active
		
		return result;
	}

	public static bool GoodThingToHaul(Thing t, Pawn pawn)
	{
		var result = OkThingToHaul(t, pawn)
		&& IsNotCorpseOrAllowed(t)
		&& !t.IsInValidBestStorage();
		
		return result;
	}

	public static bool OkThingToHaul(Thing t, Pawn pawn)
	{
		var result = t.Spawned
		&& pawn.CanReserve(t)
		&& !t.IsForbidden(pawn);
		
		return result;
	}

	public static bool IsNotCorpseOrAllowed(Thing t) => Settings.AllowCorpses || t is not Corpse;

        private static readonly PawnCache<(int tick, List<Thing> list)> _potentialWorkCache = new();
        private static readonly PawnCache<int> _nextUpdateTick = new();

        private const int CacheDuration = 30; // ticks
        private const int UpdateInterval = 60; // stagger expensive searches

        static WorkGiver_HaulToInventory()
        {
            // Register caches for automatic cleanup
            CacheManager.RegisterCache(_potentialWorkCache);
            CacheManager.RegisterCache(_nextUpdateTick);
            CacheManager.RegisterCache(_encumbranceCache);
            
            // Register cleanup method for pawn skip lists
            CacheManager.RegisterCache(new PawnSkipListCache());
        }

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
        {
                var currentTick = Find.TickManager.TicksGame;
                
                // Determine if this pawn should refresh its cache this tick
                if (!_nextUpdateTick.TryGet(pawn, out var nextTick))
                {
                        nextTick = currentTick + (pawn.thingIDNumber % UpdateInterval);
                        _nextUpdateTick.Set(pawn, nextTick);
                }

                if (currentTick < nextTick && _potentialWorkCache.TryGet(pawn, out var cached) && cached.list != null)
                {
                        return new List<Thing>(cached.list);
                }

                var list = new List<Thing>(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
                // Ensure items are sorted by distance from pawn to prioritize closest items
                Comparer.rootCell = pawn.Position;
                list.Sort(Comparer);

                _potentialWorkCache.Set(pawn, (currentTick, list));
                _nextUpdateTick.Set(pawn, currentTick + UpdateInterval);

                if (Settings.EnableDebugLogging)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: PotentialWorkThingsGlobal for {pawn} at {pawn.Position} found {list.Count} items, first item: {list.FirstOrDefault()?.Position}");
                }

                // Return a copy to prevent cache corruption
                return new List<Thing>(list);
        }



	private static ThingPositionComparer Comparer { get; } = new();
	public class ThingPositionComparer : IComparer<Thing>
	{
		public IntVec3 rootCell;
		public int Compare(Thing x, Thing y) => (x.Position - rootCell).LengthHorizontalSquared.CompareTo((y.Position - rootCell).LengthHorizontalSquared);
	}

	// Cache for sorted haulable lists to avoid repeated sorting
	private static readonly Dictionary<(Map map, IntVec3 position, int tick), List<Thing>> _sortedHaulablesCache = new();
	private static int _lastSortedHaulablesCacheCleanupTick = 0;
	
	// Cache for storage capacity checks to avoid repeated expensive calculations
	private static readonly Dictionary<(Thing thing, IntVec3 cell, int tick), int> _storageCapacityCache = new();
	private static int _lastStorageCapacityCacheCleanupTick = 0;

        public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		var result = !pawn.InMentalState
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
				var actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, capacity, pawn);
		
		// If pawn has no capacity (like animals), fall back to vanilla behavior
		if (capacity <= 0)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Message($"[PickUpAndHaul] DEBUG: HasJobOnThing: Pawn {pawn} has no capacity ({capacity}), falling back to vanilla hauling");
			}
			// Don't return false here - let the vanilla hauling system handle it
			// The JobOnThing method will handle the fallback to HaulAIUtility.HaulToStorageJob
		}
		else if (actualCarriableAmount <= 0)
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
		
		return result;
	}

	//bulky gear (power armor + minigun) so don't bother.
	//Updated to include inventory mass, not just gear mass

        private static readonly PawnCache<(int tick, bool result)> _encumbranceCache = new();

        public static bool OverAllowedGearCapacity(Pawn pawn)
        {
                var currentTick = Find.TickManager.TicksGame;

                if (_encumbranceCache.TryGet(pawn, out var cache) && cache.tick == currentTick)
                {
                        return cache.result;
                }

                var totalMass = MassUtility.GearAndInventoryMass(pawn);
                var capacity = MassUtility.Capacity(pawn);
                var ratio = totalMass / capacity;
                var result = ratio >= Settings.MaximumOccupiedCapacityToConsiderHauling;

                if (Settings.EnableDebugLogging)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: OverAllowedGearCapacity for {pawn} - mass: {totalMass}, capacity: {capacity}, ratio: {ratio:F2}, threshold: {Settings.MaximumOccupiedCapacityToConsiderHauling:F2}, result: {result}");
                }

                _encumbranceCache.Set(pawn, (currentTick, result));
                
                return result;
        }



	//pick up stuff until you can't anymore,
	//while you're up and about, pick up something and haul it
	//before you go out, empty your pockets
        public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
        {
                // Check if save operation is in progress
                if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
                {
                        Log.Message($"[PickUpAndHaul] Skipping job creation during save operation for {pawn}");
                        return null;
                }

                // Do not create hauling jobs for pawns in a mental state
                if (pawn.InMentalState)
                {
                        return null;
                }

		// Check if mod is active
		if (!PickupAndHaulSaveLoadLogger.IsModActive())
		{
			Log.Message($"[PickUpAndHaul] Skipping job creation - mod not active for {pawn}");
			return null;
		}

		if (!OkThingToHaul(thing, pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced))
		{
			return null;
		}

		if (pawn.GetComp<CompHauledToInventory>() is null) // Misc. Robots compatibility
														   // See https://github.com/catgirlfighter/RimWorld_CommonSense/blob/master/Source/CommonSense11/CommonSense/OpportunisticTasks.cs#L129-L140
		{
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
				return null;
			}
		}
		else
		{
			JobFailReason.Is("NoEmptyPlaceLower".Translate());
			return null;
		}

		//credit to Dingo
		var capacityStoreCell
			= storeTarget.container is null ? CapacityAt(thing, storeTarget.cell, map)
			: nonSlotGroupThingOwner.GetCountCanAccept(thing);

		if (capacityStoreCell == 0)
		{
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
		
		// Validate job after creation using JobQueueManager
		if (!JobQueueManager.ValidateJobQueues(job, pawn))
		{
			Log.Error($"[PickUpAndHaul] CRITICAL ERROR: Job queue validation failed after creation for {pawn}!");
			return null;
		}

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

		// Use cached sorted haulables to avoid repeated expensive sorting operations
		var currentTick = Find.TickManager.TicksGame;
		var cacheKey = (map, pawn.Position, currentTick);
		
		// Clean up old cache entries periodically
		if (currentTick - _lastSortedHaulablesCacheCleanupTick > 100) // Clean up every 100 ticks
		{
			var keysToRemove = _sortedHaulablesCache.Keys.Where(k => currentTick - k.tick > 5).ToList();
			foreach (var key in keysToRemove)
			{
				_sortedHaulablesCache.Remove(key);
			}
			_lastSortedHaulablesCacheCleanupTick = currentTick;
		}
		
		List<Thing> haulables;
		if (!_sortedHaulablesCache.TryGetValue(cacheKey, out haulables))
		{
			haulables = new List<Thing>(map.listerHaulables.ThingsPotentiallyNeedingHauling());
			// Sort by distance from pawn, not from the initial thing, to ensure closest items are picked first
			Comparer.rootCell = pawn.Position;
			haulables.Sort(Comparer);
			_sortedHaulablesCache[cacheKey] = new List<Thing>(haulables); // Store a copy
		}
		else
		{
			haulables = new List<Thing>(haulables); // Use a copy to avoid modifying the cached list
		}
		
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
		
		// Initialize pawn-specific skip lists
		if (!_pawnSkipCells.ContainsKey(pawn))
		{
			_pawnSkipCells[pawn] = new HashSet<IntVec3>();
		}
		if (!_pawnSkipThings.ContainsKey(pawn))
		{
			_pawnSkipThings[pawn] = new HashSet<Thing>();
		}
		
		// Use pawn-specific skip lists for this job
		skipCells = _pawnSkipCells[pawn];
		skipThings = _pawnSkipThings[pawn];
		
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
		var actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, capacity, pawn);
		var effectiveAmount = Math.Min(actualCarriableAmount, capacityStoreCell);
		
		// If pawn has no capacity (like animals), fall back to vanilla hauling
		if (capacity <= 0)
		{
			if (Settings.EnableDebugLogging)
			{
				Log.Message($"[PickUpAndHaul] DEBUG: JobOnThing: Pawn {pawn} has no capacity ({capacity}), falling back to vanilla hauling");
			}
			skipCells = null;
			skipThings = null;
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
		}
		
		if (effectiveAmount <= 0)
		{
			Log.Warning($"[PickUpAndHaul] WARNING: Pawn {pawn} cannot effectively haul {thing} - carriable: {actualCarriableAmount}, storage capacity: {capacityStoreCell}");
			return null;
		}
		
		// Try to reserve capacity for the initial item
		if (!StorageAllocationTracker.Instance.ReserveCapacity(storageLocation, thing.def, effectiveAmount, pawn))
		{
			Log.Warning($"[PickUpAndHaul] WARNING: Cannot reserve capacity for {effectiveAmount} of {thing} at {storageLocation} - insufficient available capacity");
			
			// Try to find alternative storage instead of giving up
			Log.Message($"[PickUpAndHaul] DEBUG: Attempting to find alternative storage for {thing}");
			
			// Add the failed location to skip list to avoid trying it again
			if (storeTarget.container != null)
			{
				skipThings.Add(storeTarget.container);
			}
			else
			{
				skipCells.Add(storeTarget.cell);
			}
			
			// Try to find alternative storage, strictly avoiding the failed location
			if (TryFindBestBetterStorageFor(thing, pawn, map, currentPriority, pawn.Faction, out var alternativeCell, out var alternativeDestination, out var alternativeThingOwner, true))
			{
				Log.Message($"[PickUpAndHaul] DEBUG: Found alternative storage for {thing}: {alternativeDestination?.ToStringSafe() ?? alternativeCell.ToString()}");
				
				// Update storeTarget with the alternative
				if (alternativeDestination is Thing destinationAsThing && alternativeThingOwner != null)
				{
					storeTarget = new(destinationAsThing);
					capacityStoreCell = alternativeThingOwner.GetCountCanAccept(thing);
					storageLocation = new StorageAllocationTracker.StorageLocation(destinationAsThing);
				}
				else if (alternativeCell.IsValid)
				{
					storeTarget = new(alternativeCell);
					capacityStoreCell = CapacityAt(thing, alternativeCell, map);
					storageLocation = new StorageAllocationTracker.StorageLocation(alternativeCell);
				}
				else
				{
					Log.Warning($"[PickUpAndHaul] WARNING: Alternative storage found but invalid - cell: {alternativeCell}, destination: {alternativeDestination}");
					return null;
				}
				
				// Update effective amount based on new capacity
				effectiveAmount = Math.Min(actualCarriableAmount, capacityStoreCell);
				
				// Try to reserve capacity at the alternative location
				if (!StorageAllocationTracker.Instance.ReserveCapacity(storageLocation, thing.def, effectiveAmount, pawn))
				{
					Log.Warning($"[PickUpAndHaul] WARNING: Cannot reserve capacity for {effectiveAmount} of {thing} at alternative location {storageLocation} - insufficient available capacity");
					return null;
				}
				
				Log.Message($"[PickUpAndHaul] DEBUG: Successfully reserved capacity for {effectiveAmount} of {thing} at alternative location {storageLocation}");
				
				// Update the storeCellCapacity dictionary with the new target
				storeCellCapacity.Clear();
				storeCellCapacity[storeTarget] = new(thing, capacityStoreCell);
			}
			else
			{
				Log.Warning($"[PickUpAndHaul] WARNING: No alternative storage found for {thing}");
				return null;
			}
		}
		
		Log.Message($"[PickUpAndHaul] DEBUG: Reserved capacity for {effectiveAmount} of {thing} at {storageLocation} (carriable: {actualCarriableAmount}, storage: {capacityStoreCell})");

                var bCountTracker = new List<int>();

                if (!AllocateThingAtCell(storeCellCapacity, pawn, thing, job, ref currentMass, capacity, bCountTracker))
                {
			Log.Error($"[PickUpAndHaul] ERROR: Failed to allocate initial item {thing} - this should not happen since storage was verified");
			// Release reserved capacity
			StorageAllocationTracker.Instance.ReleaseCapacity(storageLocation, thing.def, effectiveAmount, pawn);
			return null;
		}
		Log.Message($"[PickUpAndHaul] DEBUG: Initial item {thing} allocated successfully");

		// CRITICAL FIX: Validate that we have at least one item allocated before proceeding
		if (job.targetQueueA == null || job.targetQueueA.Count == 0)
		{
			Log.Error($"[PickUpAndHaul] CRITICAL ERROR: Initial item allocation failed - targetQueueA is empty for {pawn}");
			Log.Error($"[PickUpAndHaul] CRITICAL ERROR: Releasing storage reservation and returning null");
			
			// Release the storage reservation we made earlier
			StorageAllocationTracker.Instance.ReleaseCapacity(storageLocation, thing.def, effectiveAmount, pawn);
			
			return null;
		}

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
                        if (AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job, ref currentMass, capacity, bCountTracker))
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
			//skipTargets = null;
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
			//skipTargets = null;
			return job;
		}
		Log.Message($"[PickUpAndHaul] DEBUG: Looking for more like {nextThing}, carryCapacity: {carryCapacity}");

                while ((nextThing = GetClosestAndRemove(nextThing.Position, map, haulables,
                           PathEndMode.ClosestTouch, traverseParms, 8f, Validator)) != null)
                {
                        carryCapacity -= nextThing.stackCount;
                        Log.Message($"[PickUpAndHaul] DEBUG: Found similar thing {nextThing}, carryCapacity now: {carryCapacity}");

                        if (AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job, ref currentMass, capacity, bCountTracker))
			{
                                Log.Message($"[PickUpAndHaul] DEBUG: Successfully allocated similar thing {nextThing}");
				break;
			}

			if (carryCapacity <= 0)
			{
				// Get the last item count safely - check if countQueue has items first
				if (job.countQueue == null || job.countQueue.Count == 0)
				{
					Log.Error($"[PickUpAndHaul] CRITICAL ERROR: countQueue is null or empty when trying to adjust count for {pawn}");
					CleanupInvalidJob(job, storeCellCapacity, thing, pawn);
					return null;
				}
				
				var originalCount = job.countQueue[job.countQueue.Count - 1];
				var adjustedCount = originalCount + carryCapacity;
				
				if (adjustedCount <= 0)
				{
					// Use JobQueueManager to atomically remove the last item from all queues
					if (!JobQueueManager.RemoveItemsFromJob(job, 1, pawn))
					{
						Log.Error($"[PickUpAndHaul] CRITICAL ERROR: Failed to atomically remove last item from job queues for {pawn}");
						// Clean up and return null to prevent crash
						CleanupInvalidJob(job, storeCellCapacity, thing, pawn);
						return null;
					}
					
					// Also remove the corresponding entry from bCountTracker
					if (bCountTracker.Count > 0)
					{
						bCountTracker.RemoveAt(bCountTracker.Count - 1);
					}

					Log.Message($"[PickUpAndHaul] DEBUG: Atomically removed last item from job - adjusted count would be {adjustedCount} (original: {originalCount}, carryCapacity: {carryCapacity})");
				}
				else
				{
					// Use JobQueueManager to atomically update the count
					if (!JobQueueManager.UpdateLastItemCount(job, adjustedCount, pawn))
					{
						Log.Error($"[PickUpAndHaul] CRITICAL ERROR: Failed to atomically update last item count for {pawn}");
						// Clean up and return null to prevent crash
						CleanupInvalidJob(job, storeCellCapacity, thing, pawn);
						return null;
					}
					
					Log.Message($"[PickUpAndHaul] DEBUG: Atomically adjusted count from {originalCount} to {adjustedCount} (carryCapacity: {carryCapacity})");
				}
				break;
			}
		}

		Log.Message($"[PickUpAndHaul] DEBUG: Final job state before return:");
		Log.Message($"[PickUpAndHaul] DEBUG: targetQueueA: {job.targetQueueA.Count}, targetQueueB: {job.targetQueueB.Count}, countQueue: {job.countQueue.Count}");
		
		// CRITICAL FIX: Ensure job is never returned with empty targetQueueA
		// This prevents ArgumentOutOfRangeException in JobDriver_HaulToInventory
		if (job.targetQueueA == null || job.targetQueueA.Count == 0)
		{
			Log.Error($"[PickUpAndHaul] CRITICAL ERROR: Job has empty targetQueueA for {pawn} - this would cause ArgumentOutOfRangeException!");
			Log.Error($"[PickUpAndHaul] CRITICAL ERROR: Releasing all reservations and returning null to prevent crash");
			
			// Use CleanupInvalidJob to properly release storage capacity based on actual allocated amounts
			CleanupInvalidJob(job, storeCellCapacity, thing, pawn);
			
			return null;
		}
		
		// Validate job before returning
		ValidateJobQueues(job, pawn, "Job Return");
		
		// CRITICAL FIX: Final validation to ensure job is completely valid
		if (!IsJobValid(job, pawn))
		{
			Log.Error($"[PickUpAndHaul] CRITICAL ERROR: Job failed final validation for {pawn} - cleaning up and returning null");
			CleanupInvalidJob(job, storeCellCapacity, thing, pawn);
			return null;
		}
		
		//skipTargets = null;
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
		if (searchSet == null || !searchSet.Any())
		{
			Log.Message($"[PickUpAndHaul] DEBUG: GetClosestAndRemove - searchSet is null or empty");
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
                                return thing;
                        }
                        else
                        {
                                itemsFiltered++;
                        }
                }

                Log.Message($"[PickUpAndHaul] DEBUG: GetClosestAndRemove - no valid items found. Checked: {itemsChecked}, Unspawned: {itemsUnspawned}, Too far: {itemsTooFar}, Unreachable: {itemsUnreachable}, Filtered: {itemsFiltered}");
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
		// Use cached capacity to avoid repeated expensive calculations
		var currentTick = Find.TickManager.TicksGame;
		var cacheKey = (thing, storeCell, currentTick);
		
		// Clean up old cache entries periodically
		if (currentTick - _lastStorageCapacityCacheCleanupTick > 50) // Clean up every 50 ticks
		{
			var keysToRemove = _storageCapacityCache.Keys.Where(k => currentTick - k.tick > 3).ToList();
			foreach (var key in keysToRemove)
			{
				_storageCapacityCache.Remove(key);
			}
			_lastStorageCapacityCacheCleanupTick = currentTick;
		}
		
		if (_storageCapacityCache.TryGetValue(cacheKey, out var cachedCapacity))
		{
			return cachedCapacity;
		}

		int capacity;
		if (HoldMultipleThings_Support.CapacityAt(thing, storeCell, map, out capacity))
		{
			Log.Message($"Found external capacity of {capacity}");
			_storageCapacityCache[cacheKey] = capacity;
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
				_storageCapacityCache[cacheKey] = containerCapacity;
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
		_storageCapacityCache[cacheKey] = capacity;
		return capacity;
	}

	public static bool Stackable(Thing nextThing, KeyValuePair<StoreTarget, CellAllocation> allocation)
		=> nextThing == allocation.Value.allocated
		|| allocation.Value.allocated.CanStackWith(nextThing)
		|| HoldMultipleThings_Support.StackableAt(nextThing, allocation.Key.cell, nextThing.Map);

        public static bool AllocateThingAtCell(Dictionary<StoreTarget, CellAllocation> storeCellCapacity, Pawn pawn, Thing nextThing, Job job, ref float currentMass, float capacity, List<int> bCountTracker)
        {
                var bCountBefore = job.targetQueueB.Count;
		
				// DEBUG: Log initial state
				Log.Message($"[PickUpAndHaul] DEBUG: AllocateThingAtCell called for {pawn} with {nextThing}");
				Log.Message($"[PickUpAndHaul] DEBUG: Initial job queues - targetQueueA: {job.targetQueueA?.Count ?? 0}, targetQueueB: {job.targetQueueB?.Count ?? 0}, countQueue: {job.countQueue?.Count ?? 0}");
				Log.Message($"[PickUpAndHaul] DEBUG: Current mass: {currentMass}, capacity: {capacity}, encumbrance: {currentMass / capacity}");
				
                var map = pawn.Map;
                var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);
                
                // First, check if the pawn can carry this item at all
                var actualCarriableAmount = CalculateActualCarriableAmount(nextThing, currentMass, capacity, pawn);
                if (actualCarriableAmount <= 0)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: Pawn {pawn} cannot carry any of {nextThing} due to encumbrance (current: {currentMass}/{capacity}), skipping");
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
                                        // Don't add to job.targetQueueB here - let AddItemsToJob handle it
                                        targetsAdded.Add(nextStoreCell);
                                        
                                        var newCapacity = CapacityAt(nextThing, nextStoreCell, map);
                                        Log.Message($"[PickUpAndHaul] DEBUG: New cell {nextStoreCell} has capacity {newCapacity}");
                                        
                                        if (newCapacity <= 0)
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: New cell {nextStoreCell} has capacity {newCapacity} <= 0, skipping this item");
                                                // Clean up targets and reservations
                                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                                return false;
                                        }
                                        
                                        // For new storage, be more flexible with capacity reservation
                                        var storageLocation = new StorageAllocationTracker.StorageLocation(nextStoreCell);
                                        var reservationAmount = Math.Min(actualCarriableAmount, newCapacity);
                                        
                                        // Try to reserve capacity, but don't fail if we can't
                                        if (StorageAllocationTracker.Instance.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
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
                                        targetsAdded.Add(destinationAsThing);

                                        var newCapacity = innerInteractableThingOwner.GetCountCanAccept(nextThing);
                                        Log.Message($"[PickUpAndHaul] DEBUG: New haulDestination {haulDestination} has capacity {newCapacity}");
                                        
                                        if (newCapacity <= 0)
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: New haulDestination {haulDestination} has capacity {newCapacity} <= 0, skipping this item");
                                                // Clean up targets and reservations
                                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                                return false;
                                        }
                                        
                                        						// For new storage, be more flexible with capacity reservation
						var storageLocation = new StorageAllocationTracker.StorageLocation(destinationAsThing);
						var reservationAmount = Math.Min(actualCarriableAmount, newCapacity);
						
						// Try to reserve capacity, but don't fail if we can't
						if (StorageAllocationTracker.Instance.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
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
                                        targetsAdded.Add(nextStoreCell);

                                        var newCapacity = CapacityAt(nextThing, nextStoreCell, map) - capacityOver;
                                        Log.Message($"[PickUpAndHaul] DEBUG: New overflow cell {nextStoreCell} has capacity {newCapacity}");
                                        
                                        if (newCapacity <= 0)
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: New overflow cell {nextStoreCell} has insufficient capacity, skipping this item");
                                                // Clean up targets and reservations
                                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                                return false;
                                        }
                                        
                                        						// Try to reserve capacity for the overflow
						var storageLocation = new StorageAllocationTracker.StorageLocation(nextStoreCell);
						var reservationAmount = Math.Min(capacityOver, actualCarriableAmount);
						
						if (StorageAllocationTracker.Instance.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
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
                                        targetsAdded.Add(destinationAsThing);

                                        var newCapacity = innerInteractableThingOwner.GetCountCanAccept(nextThing) - capacityOver;
                                        Log.Message($"[PickUpAndHaul] DEBUG: New overflow haulDestination {nextHaulDestination} has capacity {newCapacity}");
                                        
                                        if (newCapacity <= 0)
                                        {
                                                Log.Message($"[PickUpAndHaul] DEBUG: New overflow haulDestination {nextHaulDestination} has insufficient capacity, skipping this item");
                                                // Clean up targets and reservations
                                                CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                                                return false;
                                        }
                                        
                                        						// Try to reserve capacity for the overflow
						var storageLocation = new StorageAllocationTracker.StorageLocation(destinationAsThing);
						var reservationAmount = Math.Min(capacityOver, actualCarriableAmount);
						
						if (StorageAllocationTracker.Instance.ReserveCapacity(storageLocation, nextThing.def, reservationAmount, pawn))
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
                        return false;
                }

                // Use JobQueueManager for atomic queue operations
                var things = new List<Thing> { nextThing };
                var counts = new List<int> { count };
                
                // Get the target from the storeCell, not from the queue
                LocalTargetInfo target;
                if (storeCell.container != null)
                {
                    target = new LocalTargetInfo(storeCell.container);
                }
                else
                {
                    target = new LocalTargetInfo(storeCell.cell);
                }
                var targets = new List<LocalTargetInfo> { target };
                
                if (!JobQueueManager.AddItemsToJob(job, things, counts, targets, pawn))
                {
                    Log.Error($"[PickUpAndHaul] CRITICAL ERROR: Failed to add items to job queues for {pawn}!");
                    // Clean up any targets we added
                    CleanupAllocateThingAtCell(job, targetsAdded, reservationsMade, pawn);
                    return false;
                }
                
                currentMass += nextThing.GetStatValue(StatDefOf.Mass) * count;

                var bEntriesAdded = job.targetQueueB.Count - bCountBefore;
                bCountTracker?.Add(bEntriesAdded);
                
                Log.Message($"[PickUpAndHaul] DEBUG: Successfully allocated {nextThing}:{count} to job");
                Log.Message($"[PickUpAndHaul] DEBUG: Updated mass: {currentMass}, new encumbrance: {currentMass / capacity}");
                Log.Message($"[PickUpAndHaul] DEBUG: Final job queues - targetQueueA: {job.targetQueueA.Count}, targetQueueB: {job.targetQueueB.Count}, countQueue: {job.countQueue.Count}");
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
                        StorageAllocationTracker.Instance.ReleaseCapacity(location, def, count, pawn);
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
	
	// Track failed storage locations per pawn to avoid retrying them
	private static readonly Dictionary<Pawn, HashSet<IntVec3>> _pawnSkipCells = new();
	private static readonly Dictionary<Pawn, HashSet<Thing>> _pawnSkipThings = new();
	
	/// <summary>
	/// Cleans up pawn-specific skip lists for dead or invalid pawns
	/// </summary>
	public static void CleanupPawnSkipLists()
	{
		var pawnsToRemove = new List<Pawn>();
		
		foreach (var pawn in _pawnSkipCells.Keys)
		{
			if (pawn == null || pawn.Dead || pawn.Destroyed)
			{
				pawnsToRemove.Add(pawn);
			}
		}
		
		foreach (var pawn in pawnsToRemove)
		{
			_pawnSkipCells.Remove(pawn);
			_pawnSkipThings.Remove(pawn);
		}
		
		if (Settings.EnableDebugLogging && pawnsToRemove.Count > 0)
		{
			Log.Message($"[PickUpAndHaul] Cleaned up skip lists for {pawnsToRemove.Count} invalid pawns");
		}
	}

	public static bool TryFindBestBetterStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, out IHaulDestination haulDestination, out ThingOwner innerInteractableThingOwner, bool strictSkip = false)
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

	public static bool TryFindBestBetterStoreCellFor(Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, bool strictSkip = false)
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
				if (skipCells.Contains(cell))
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
						var remainingCapacity = CapacityAt(thing, cell, map);
						if (remainingCapacity <= 0)
						{
							continue; // Skip if no capacity
						}
						Log.Message($"[PickUpAndHaul] DEBUG: Reusing previously allocated cell {cell} with remaining capacity {remainingCapacity} for {thing}");
					}
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
        {
                var thingMass = thing.GetStatValue(StatDefOf.Mass);
                if (thingMass <= 0 || encumberance <= 1)
                {
                        return 0; // No items past capacity if no mass or not overencumbered
                }
                
                var result = (int)Math.Ceiling((encumberance - 1) * capacity / thingMass);
                return Math.Max(0, result); // Ensure result is never negative
        }

        /// <summary>
        /// Calculates the actual amount of a thing that a pawn can carry considering encumbrance limits
        /// </summary>
        public static int CalculateActualCarriableAmount(Thing thing, float currentMass, float capacity, Pawn pawn = null)
        {
                // Add debug logging to understand the issue
                if (Settings.EnableDebugLogging)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: CalculateActualCarriableAmount - thing: {thing}, currentMass: {currentMass}, capacity: {capacity}, ratio: {currentMass / capacity:F2}");
                }

                // For animals, use 100% capacity. For humans, use the settings threshold
                var maxAllowedMass = capacity;
                if (pawn != null && !pawn.RaceProps.Animal)
                {
                        maxAllowedMass = capacity * Settings.MaximumOccupiedCapacityToConsiderHauling;
                }
                
                if (currentMass >= maxAllowedMass)
                {
                        if (Settings.EnableDebugLogging)
                        {
                                Log.Message($"[PickUpAndHaul] DEBUG: Pawn at max allowed mass ({currentMass}/{maxAllowedMass}), cannot carry more");
                        }
                        return 0;
                }

                var remainingCapacity = maxAllowedMass - currentMass;
                var thingMass = thing.GetStatValue(StatDefOf.Mass);

                if (thingMass <= 0)
                {
                        return thing.stackCount;
                }

                var maxCarriable = (int)Math.Floor(remainingCapacity / thingMass);
                var result = Math.Min(maxCarriable, thing.stackCount);
                
                if (Settings.EnableDebugLogging)
                {
                        Log.Message($"[PickUpAndHaul] DEBUG: Can carry {result} of {thing} (mass: {thingMass}, remaining capacity: {remainingCapacity})");
                }
                
                return result;
        }

	public static bool TryFindBestBetterNonSlotGroupStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IHaulDestination haulDestination, bool acceptSamePriority = false, bool strictSkip = false)
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
				if (skipThings.Contains(thing))
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
							Log.Message($"[PickUpAndHaul] DEBUG: Reusing previously allocated container {thing} with remaining capacity {remainingCapacity} for {t}");
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

		// Check for negative or zero counts
		if (job.countQueue != null)
		{
			for (int i = 0; i < job.countQueue.Count; i++)
			{
				if (job.countQueue[i] <= 0)
				{
					Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Found negative/zero count {job.countQueue[i]} at index {i} in {context} for {pawn}");
				}
			}
		}

		// CRITICAL FIX: Additional validation for job integrity
		if (job.targetQueueA != null && job.countQueue != null)
		{
			// Check if any targets in targetQueueA are null or invalid
			for (int i = 0; i < job.targetQueueA.Count; i++)
			{
				var target = job.targetQueueA[i];
				if (target == null || target.Thing == null)
				{
					Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Found null target at index {i} in targetQueueA in {context} for {pawn}");
				}
				else if (target.Thing.Destroyed || !target.Thing.Spawned)
				{
					Log.Warning($"[PickUpAndHaul] VALIDATION WARNING: Found destroyed/unspawned target {target.Thing} at index {i} in targetQueueA in {context} for {pawn}");
				}
			}
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

	private static bool IsJobValid(Job job, Pawn pawn)
	{
		if (job == null)
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Job is null in IsJobValid for {pawn}");
			return false;
		}

		if (job.targetQueueA == null || job.targetQueueA.Count == 0)
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Job has empty targetQueueA in IsJobValid for {pawn}");
			return false;
		}

		if (job.targetQueueB == null || job.targetQueueB.Count == 0)
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Job has empty targetQueueB in IsJobValid for {pawn}");
			return false;
		}

		if (job.countQueue == null || job.countQueue.Count == 0)
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Job has empty countQueue in IsJobValid for {pawn}");
			return false;
		}

		if (job.targetQueueA.Count != job.countQueue.Count)
		{
			Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Queue synchronization issue in IsJobValid for {pawn} - targetQueueA.Count ({job.targetQueueA.Count}) != countQueue.Count ({job.countQueue.Count})");
			return false;
		}

		for (int i = 0; i < job.targetQueueA.Count; i++)
		{
			var target = job.targetQueueA[i];
			if (target == null || target.Thing == null)
			{
				Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Found null target at index {i} in targetQueueA in IsJobValid for {pawn}");
				return false;
			}
			if (target.Thing.Destroyed || !target.Thing.Spawned)
			{
				Log.Warning($"[PickUpAndHaul] VALIDATION WARNING: Found destroyed/unspawned target {target.Thing} at index {i} in targetQueueA in IsJobValid for {pawn}");
				return false;
			}
		}

		for (int i = 0; i < job.countQueue.Count; i++)
		{
			if (job.countQueue[i] <= 0)
			{
				Log.Error($"[PickUpAndHaul] VALIDATION ERROR: Found negative/zero count {job.countQueue[i]} at index {i} in IsJobValid for {pawn}");
				return false;
			}
		}

		return true;
	}

	private static void CleanupInvalidJob(Job job, Dictionary<StoreTarget, CellAllocation> storeCellCapacity, Thing thing, Pawn pawn)
	{
		if (job == null) return;

		// Use the actual allocated amounts from job queues instead of remaining capacity
		if (job.countQueue != null && job.targetQueueB != null && job.countQueue.Count == job.targetQueueB.Count)
		{
			for (int i = 0; i < job.countQueue.Count; i++)
			{
				var allocatedCount = job.countQueue[i];
				var target = job.targetQueueB[i];
				
				if (allocatedCount > 0 && target != null)
				{
					StorageAllocationTracker.StorageLocation releaseLocation;
					
					// Determine the storage location from the target
					if (target.Thing != null)
					{
						// Container storage
						releaseLocation = new StorageAllocationTracker.StorageLocation(target.Thing);
					}
					else if (target.Cell.IsValid)
					{
						// Cell storage
						releaseLocation = new StorageAllocationTracker.StorageLocation(target.Cell);
					}
					else
					{
						Log.Warning($"[PickUpAndHaul] WARNING: Invalid target at index {i} in CleanupInvalidJob for {pawn}");
						continue;
					}
					
					// Release the actual allocated amount
					StorageAllocationTracker.Instance.ReleaseCapacity(releaseLocation, thing.def, allocatedCount, pawn);
					Log.Message($"[PickUpAndHaul] DEBUG: CleanupInvalidJob: Released {allocatedCount} of {thing.def} at {releaseLocation} for {pawn}");
				}
			}
		}
		else
		{
			// Fallback: if job queues are invalid, use the storeCellCapacity (less accurate but safer)
			Log.Warning($"[PickUpAndHaul] WARNING: Job queues are invalid in CleanupInvalidJob for {pawn}, using fallback cleanup method");
			foreach (var kvp in storeCellCapacity)
			{
				var currentStoreTarget = kvp.Key;
				var releaseLocation = currentStoreTarget.container != null 
					? new StorageAllocationTracker.StorageLocation(currentStoreTarget.container)
					: new StorageAllocationTracker.StorageLocation(currentStoreTarget.cell);
				
				// Note: This is less accurate as it uses remaining capacity instead of allocated amount
				// But it's safer than doing nothing if job queues are corrupted
				StorageAllocationTracker.Instance.ReleaseCapacity(releaseLocation, thing.def, kvp.Value.capacity, pawn);
				Log.Message($"[PickUpAndHaul] DEBUG: CleanupInvalidJob fallback: Released {kvp.Value.capacity} of {thing.def} at {releaseLocation} for {pawn}");
			}
		}
	}

	/// <summary>
	/// Cache wrapper for pawn skip lists to integrate with CacheManager
	/// </summary>
	internal class PawnSkipListCache : ICache
	{
		public void ForceCleanup()
		{
			CleanupPawnSkipLists();
		}
		
		public string GetDebugInfo()
		{
			return $"Pawn skip lists: {_pawnSkipCells.Count} pawns tracked";
		}
	}

	public static class PickUpAndHaulDesignationDefOf
	{
		public static DesignationDef haulUrgently = DefDatabase<DesignationDef>.GetNamedSilentFail("HaulUrgentlyDesignation");
	}
}
