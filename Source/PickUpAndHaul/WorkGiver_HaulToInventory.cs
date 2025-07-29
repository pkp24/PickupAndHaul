using System.Linq;
using PickUpAndHaul.Cache;
using System.Threading;

namespace PickUpAndHaul;

/// <summary>
/// Tracks storage locations that recently failed reservation to prevent immediate retry loops
/// </summary>
public static class StorageFailureTracker
{
	private static readonly Dictionary<(Pawn pawn, IntVec3 cell), int> _failedStorageAttempts = new();
	private static readonly Dictionary<(Pawn pawn, IntVec3 cell), int> _lastFailureTick = new();
	private const int COOLDOWN_TICKS = 300; // 5 seconds at 60 FPS
	private const int MAX_FAILURES = 3; // After 3 failures, longer cooldown

	public static bool ShouldSkipStorage(Pawn pawn, IntVec3 cell)
	{
		var key = (pawn, cell);
		
		if (!_lastFailureTick.TryGetValue(key, out var lastTick))
			return false; // Never failed before
			
		var currentTick = Find.TickManager.TicksGame;
		var ticksSinceFailure = currentTick - lastTick;
		
		// Get failure count
		_failedStorageAttempts.TryGetValue(key, out var failures);
		
		// Progressive cooldown: more failures = longer cooldown
		var requiredCooldown = COOLDOWN_TICKS * (failures >= MAX_FAILURES ? failures : 1);
		
		return ticksSinceFailure < requiredCooldown;
	}
	
	public static void RecordStorageFailure(Pawn pawn, IntVec3 cell)
	{
		var key = (pawn, cell);
		var currentTick = Find.TickManager.TicksGame;
		
		_lastFailureTick[key] = currentTick;
		_failedStorageAttempts[key] = _failedStorageAttempts.GetValueOrDefault(key) + 1;
		
		Log.Message($"[StorageFailureTracker] Recorded failure for {pawn} at {cell} (total failures: {_failedStorageAttempts[key]})");
	}
	
	public static void ClearOldEntries()
	{
		if (Find.TickManager.TicksGame % 2500 != 0) // Clean every ~40 seconds
			return;
			
		var currentTick = Find.TickManager.TicksGame;
		var keysToRemove = new List<(Pawn, IntVec3)>();
		
		foreach (var kvp in _lastFailureTick)
		{
			if (currentTick - kvp.Value > COOLDOWN_TICKS * 10) // Remove entries older than 10x cooldown
				keysToRemove.Add(kvp.Key);
		}
		
		foreach (var key in keysToRemove)
		{
			_lastFailureTick.Remove(key);
			_failedStorageAttempts.Remove(key);
		}
	}
}

public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	//Thanks to AlexTD for the more dynamic search range
	//And queueing
	//And optimizing
	private const float SEARCH_FOR_OTHERS_RANGE_FRACTION = 0.5f;

	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		// Periodically clean up old failure tracking entries
		StorageFailureTracker.ClearOldEntries();
		
		return base.ShouldSkip(pawn, forced)
		|| pawn.Faction != Faction.OfPlayerSilentFail
		|| !Settings.IsAllowedRace(pawn.RaceProps)
		|| pawn.GetComp<CompHauledToInventory>() == null
		|| pawn.IsQuestLodger()
		|| OverAllowedGearCapacity(pawn);
	}

	public static bool GoodThingToHaul(Thing t, Pawn pawn)
		=> OkThingToHaul(t, pawn)
		&& IsNotCorpseOrAllowed(t)
		&& !t.IsInValidBestStorage();

	public static bool OkThingToHaul(Thing t, Pawn pawn)
		=> t.Spawned
		&& pawn.CanReserve(t)
		&& !t.IsForbidden(pawn);

	public static bool IsNotCorpseOrAllowed(Thing t) => Settings.AllowCorpses || t is not Corpse;

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		// Use the cache system instead of directly accessing listerHaulables
		var cachedHaulables = CacheManager.GetAccessibleHaulables(pawn.Map)
			.Where(t => t != null && t.Spawned && !t.Destroyed) // Additional safety filter
			.ToList();
		
		Comparer.rootCell = pawn.Position;
		cachedHaulables.Sort(Comparer);
		return cachedHaulables;
	}

	private static ThingPositionComparer Comparer { get; } = new();
	public class ThingPositionComparer : IComparer<Thing>
	{
		public IntVec3 rootCell;
		public int Compare(Thing x, Thing y)
		{
			// Handle null cases
			if (x == null && y == null)
				return 0;
			if (x == null)
				return -1;
			if (y == null)
				return 1;

			// Handle invalid things
			return !x.Spawned && !y.Spawned
				? 0
				: !x.Spawned
				? -1
				: !y.Spawned ? 1 : (x.Position - rootCell).LengthHorizontalSquared.CompareTo((y.Position - rootCell).LengthHorizontalSquared);
		}
	}

	public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		Log.Message($"Checking pawn {pawn} (Drafted: {pawn.Drafted}, Downed: {pawn.Downed}, Dead: {pawn.Dead}, Forced: {forced}, CurrentJob: {pawn.jobs?.curJob?.def?.defName ?? "null"}) for thing {thing}");
		
		// Add detailed weight logging
		var thingMass = thing.GetStatValue(StatDefOf.Mass);
		var pawnCapacity = MassUtility.Capacity(pawn);
		var pawnGearMass = MassUtility.GearMass(pawn);
		var pawnCurrentMass = pawnGearMass;
		var willBeOverEncumbered = MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1);
		var countUntilOverEncumbered = MassUtility.CountToPickUpUntilOverEncumbered(pawn, thing);
		var maxOccupiedCapacity = Settings.MaximumOccupiedCapacityToConsiderHauling;
		var currentOccupiedCapacity = pawnGearMass / pawnCapacity;
		
		Log.Message($"{pawn} WEIGHT ANALYSIS - Thing mass: {thingMass}, Pawn capacity: {pawnCapacity}, Gear mass: {pawnGearMass}, Current occupied: {currentOccupiedCapacity:F2}, Max allowed: {maxOccupiedCapacity:F2}, Will be over encumbered: {willBeOverEncumbered}, Count until over encumbered: {countUntilOverEncumbered}");
		
		// Ensure cache is up to date for this map (same as JobOnThing)
		CacheUpdaterHelper.EnsureCacheUpdater(pawn.Map);
		
		// Validate that the thing is in the correct cache (same as JobOnThing)
		ValidateThingInCache(pawn.Map, thing);
		
		// Check basic conditions that JobOnThing checks first
		if (!OkThingToHaul(thing, pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced))
		{
			Log.Message($"{pawn} failed basic checks - OkThingToHaul: {OkThingToHaul(thing, pawn)}, PawnCanAutomaticallyHaulFast: {HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced)}");
			return false;
		}
		
		// Check if conditions would force JobOnThing to use fallback HaulToStorageJob
		var overGearCapacity = OverAllowedGearCapacity(pawn);
		var hasComp = pawn.GetComp<CompHauledToInventory>() != null;
		var isNotCorpse = IsNotCorpseOrAllowed(thing);
		var canPickUpAny = MassUtility.CountToPickUpUntilOverEncumbered(pawn, thing) > 0;
		
		Log.Message($"{pawn} condition checks - overGearCapacity: {overGearCapacity}, hasComp: {hasComp}, isNotCorpse: {isNotCorpse}, willBeOverEncumbered: {willBeOverEncumbered}, canPickUpAny: {canPickUpAny}");
		
		if (overGearCapacity
			|| !hasComp
			|| !isNotCorpse
			|| willBeOverEncumbered
			|| !canPickUpAny)
		{
			Log.Message($"{pawn} will use fallback HaulToStorageJob, checking if that would succeed");
			// JobOnThing will try HaulToStorageJob - check if that would succeed
			var fallbackSuccess = StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out _, out _, true);
			Log.Message($"{pawn} fallback HaulToStorageJob would succeed: {fallbackSuccess}");
			return fallbackSuccess;
		}
		
		// If we get here, JobOnThing will try to create an inventory hauling job
		// Check if storage location can be found for inventory hauling AND has capacity
		Log.Message($"{pawn} attempting to find storage location for inventory hauling");
		if (!CacheManager.TryGetCachedStorageLocation(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var targetCell, out var haulDestination, out var nonSlotGroupThingOwner, this))
		{
			Log.Message($"{pawn} failed to find cached storage location");
			return false;
		}

		// Check capacity at the storage location (same logic as JobOnThing)
		StoreTarget storeTarget;
		if (haulDestination is ISlotGroupParent)
		{
			storeTarget = new(targetCell);
		}
		else if (haulDestination is Thing destinationAsThing && (nonSlotGroupThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner()) != null)
		{
			storeTarget = new(destinationAsThing);
		}
		else
		{
			Log.Message($"{pawn} failed to create storeTarget");
			return false;
		}

		var capacityStoreCell = storeTarget.container is null 
			? CapacityAt(thing, storeTarget.cell, pawn.Map)
			: nonSlotGroupThingOwner.GetCountCanAccept(thing);

		Log.Message($"{pawn} storage capacity at {storeTarget}: {capacityStoreCell}");

		if (capacityStoreCell == 0)
		{
			Log.Message($"{pawn} no capacity at storage, checking fallback");
			// JobOnThing will try HaulToStorageJob when no capacity - check if that would succeed
			var fallbackSuccess = StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out _, out _, true);
			Log.Message($"{pawn} fallback HaulToStorageJob would succeed: {fallbackSuccess}");
			return fallbackSuccess;
		}

		// Check cooldown to avoid repeatedly trying failed storage locations
		if (StorageFailureTracker.ShouldSkipStorage(pawn, targetCell))
		{
			Log.Message($"{pawn} SKIPPING storage at {storeTarget} due to recent reservation failures");
			
			// Try fallback to regular hauling job
			var fallbackSuccess = StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out _, out _, true);
			Log.Message($"{pawn} fallback due to storage cooldown would succeed: {fallbackSuccess}");
			return fallbackSuccess;
		}

		// CRITICAL FIX: Check if the storage can actually be reserved before approving the job
		// Test multiple parameter combinations to match what JobDriver might be using
		var targetThing = storeTarget.container;
		var storageCell = storeTarget.cell;
		
		// Use our custom PUAH reservation system for cells
		bool canReserveStorage1 = targetThing != null 
			? pawn.CanReserve(targetThing, 1, -1, null, false)
			: PUAHReservationManager.CanReserve(storageCell, pawn.Map, pawn, capacityStoreCell);
			
		bool canReserveStorage2 = targetThing != null 
			? pawn.CanReserve(targetThing, 1, -1, null, true)
			: PUAHReservationManager.CanReserve(storageCell, pawn.Map, pawn, capacityStoreCell);
			
		bool canReserveStorage3 = targetThing != null 
			? pawn.CanReserve(targetThing, 1, 1, null, false)
			: PUAHReservationManager.CanReserve(storageCell, pawn.Map, pawn, 1);
			
		bool canReserveStorage4 = targetThing != null 
			? pawn.CanReserve(targetThing)
			: PUAHReservationManager.CanReserve(storageCell, pawn.Map, pawn, -1);
			
		// Check existing reservations using same method as JobDriver
		var targetForReservation = targetThing != null ? (LocalTargetInfo)targetThing : (LocalTargetInfo)storageCell;
		var existingReservations = pawn.Map.reservationManager.ReservationsReadOnly
			.Where(r => r.Target == targetForReservation)
			.ToList();
			
		// Additional debugging for reservation system state
		var reservationManager = pawn.Map.reservationManager;
		Log.Message($"{pawn} WORKGIVER RESERVATION SYSTEM STATE:");
		Log.Message($"  Total active reservations: {reservationManager.ReservationsReadOnly.Count()}");
		Log.Message($"  Pawn's current reservations: {reservationManager.ReservationsReadOnly.Where(r => r.Claimant == pawn).Count()}");
		Log.Message($"  Reservation manager type: {reservationManager.GetType().Name}");
			
		Log.Message($"{pawn} WORKGIVER DETAILED RESERVATION TEST:");
		Log.Message($"  Target Thing: {targetThing?.def?.defName ?? "null"} at {targetThing?.Position ?? IntVec3.Invalid}");
		Log.Message($"  Target Cell: {storageCell}");
		Log.Message($"  CanReserve(target, 1, -1, null, false): {canReserveStorage1}");
		Log.Message($"  CanReserve(target, 1, -1, null, true): {canReserveStorage2}");
		Log.Message($"  CanReserve(target, 1, 1, null, false): {canReserveStorage3}");
		Log.Message($"  CanReserve(target): {canReserveStorage4}");
		Log.Message($"  Existing reservations for target: {existingReservations.Count}");
		
		// Use the same logic as JobDriver for consistency
		bool canReserveStorage = canReserveStorage1;
			
		if (!canReserveStorage)
		{
			Log.Message($"{pawn} VALIDATION FAILED - Storage at {storeTarget} cannot be reserved despite having capacity");
			
			// CRITICAL: Check if this is the RimWorld engine bug
			if (existingReservations.Count == 0)
			{
				Log.Message($"{pawn} DETECTED RIMWORLD ENGINE BUG - CanReserve fails but no reservations exist");
				Log.Message($"{pawn} BLOCKING job creation to prevent infinite job loop");
				
				// Record this failure to prevent immediate retries
				StorageFailureTracker.RecordStorageFailure(pawn, targetCell);
				return false; // BLOCK the job completely
			}
			
			// Record this failure to prevent immediate retries
			StorageFailureTracker.RecordStorageFailure(pawn, targetCell);
			
			// Check detailed reservation state for debugging
			var cellValid = targetCell.IsValid && targetCell.InBounds(pawn.Map);
			var cellWalkable = cellValid && targetCell.Walkable(pawn.Map);
			var pathReachable = cellValid && pawn.CanReach(targetCell, PathEndMode.Touch, Danger.Deadly);
			var slotGroup = cellValid ? targetCell.GetSlotGroup(pawn.Map) : null;
			
			Log.Message($"{pawn} WORKGIVER VALIDATION - Cell valid: {cellValid}, Walkable: {cellWalkable}, Reachable: {pathReachable}, SlotGroup: {slotGroup != null}");
			
			// Try fallback to regular hauling job
			var fallbackSuccess = StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out _, out _, true);
			Log.Message($"{pawn} fallback due to unreservable storage would succeed: {fallbackSuccess}");
			return fallbackSuccess;
		}

		// CRITICAL: Test reservation state again just before returning true
		bool finalReservationTest = targetThing != null 
			? pawn.CanReserve(targetThing, 1, -1, null, false)
			: pawn.CanReserve(storageCell, 1, -1, null, false);
			
		Log.Message($"{pawn} FINAL RESERVATION CHECK before returning true: {finalReservationTest}");
		if (!finalReservationTest)
		{
			Log.Message($"{pawn} CRITICAL: Storage became unreservable between initial check and final check!");
		}
		
		Log.Message($"{pawn} can haul {thing} to inventory (storage validated as reservable)");
		return true;
	}

	//bulky gear (power armor + minigun) so don't bother.
	public static bool OverAllowedGearCapacity(Pawn pawn) => MassUtility.GearMass(pawn) / MassUtility.Capacity(pawn) >= Settings.MaximumOccupiedCapacityToConsiderHauling;

	/// <summary>
	/// Validates and fixes job count, no more job was 0 errors
	/// </summary>
	private static void ValidateJobCount(Job job, Thing thing)
	{
		// Ensure the count queue exists and has entries
		if (job.countQueue == null || job.countQueue.Count == 0)
		{
			Log.Warning($"Job for {thing} has no count queue; this should not happen");
			return;
		}

		// Iterate backwards so removal does not affect subsequent indices
		for (int i = job.countQueue.Count - 1; i >= 0; i--)
		{
			int cnt = job.countQueue[i];
			if (cnt <= 0)
			{
				Log.Error($"Job count at index {i} was {cnt} for {thing}; removing this item from the job queues to prevent invalid hauling.");
				// Remove from targetQueueA if present
				if (job.targetQueueA != null && i < job.targetQueueA.Count)
				{
					job.targetQueueA.RemoveAt(i);
				}
				// Remove the corresponding storage cell/container from targetQueueB if present
				if (job.targetQueueB != null && i < job.targetQueueB.Count)
				{
					job.targetQueueB.RemoveAt(i);
				}
				// Remove from countQueue
				job.countQueue.RemoveAt(i);
			}
		}
		// Do not set job.count here; the JobDriver will set it when dequeuing
	}


	//pick up stuff until you can't anymore,
	//while you're up and about, pick up something and haul it
	//before you go out, empty your pockets
	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		Log.Message($"{pawn} creating job for {thing} (forced: {forced})");
		
		// CRITICAL: Test if we can still reserve the storage at the start of job creation
		if (CacheManager.TryGetCachedStorageLocation(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var verifyTargetCell, out var verifyHaulDestination, out var verifyNonSlotGroupThingOwner, this))
		{
			var verifyStoreTarget = new StoreTarget(verifyTargetCell);
			bool canStillReserve = verifyStoreTarget.container != null 
				? pawn.CanReserve(verifyStoreTarget.container, 1, -1, null, false)
				: pawn.CanReserve(verifyStoreTarget.cell, 1, -1, null, false);
			Log.Message($"{pawn} JOBONTHING START - Can still reserve {verifyStoreTarget}: {canStillReserve}");
		}
		
		// Ensure cache is up to date for this map
		CacheUpdaterHelper.EnsureCacheUpdater(pawn.Map);
		
		// Validate that the thing is in the correct cache
		ValidateThingInCache(pawn.Map, thing);
		
		// Check basic conditions first
		if (!OkThingToHaul(thing, pawn) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, forced))
		{
			Log.Message($"{pawn} failed basic checks for {thing}");
			return null;
		}
		
		// Check if conditions would force us to use fallback HaulToStorageJob
		if (OverAllowedGearCapacity(pawn)
			|| pawn.GetComp<CompHauledToInventory>() == null
			|| !IsNotCorpseOrAllowed(thing)
			|| MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1)
			|| MassUtility.CountToPickUpUntilOverEncumbered(pawn, thing) <= 0)
		{
			Log.Message($"{pawn} using fallback HaulToStorageJob for {thing}");
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced);
		}

		// Create job-local skip collections to avoid race conditions
		var skipContext = new JobSkipContext();

		var map = pawn.Map;
		var designationManager = map.designationManager;
		var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
		ThingOwner nonSlotGroupThingOwner = null;
		StoreTarget storeTarget;
		
		// Use cached storage location if available
		if (CacheManager.TryGetCachedStorageLocation(thing, pawn, map, currentPriority, pawn.Faction, out var targetCell, out var haulDestination, out nonSlotGroupThingOwner, this))
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
				Verse.Log.Error("Don't know how to handle HaulToStorageJob for storage " + haulDestination.ToStringSafe() + ". thing=" + thing.ToStringSafe());
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



		var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.HaulToInventory, thing);   //Things will be in queues
		
		Log.Message($"{pawn} WORKGIVER JOB CREATION:");
		Log.Message($"  Initial job - TargetA: {job.targetA}, TargetB: {job.targetB}");
		Log.Message($"  StoreTarget - Container: {storeTarget.container?.def?.defName ?? "null"}, Cell: {storeTarget.cell}");
		
		// Set targetB to the storage destination for the JobDriver
		if (storeTarget.container != null)
		{
			job.SetTarget(TargetIndex.B, storeTarget.container);
			Log.Message($"  Set TargetB to container: {storeTarget.container}");
		}
		else
		{
			job.SetTarget(TargetIndex.B, storeTarget.cell);
			Log.Message($"  Set TargetB to cell: {storeTarget.cell}");
		}
		
		Log.Message($"  After setting targets - TargetA: {job.targetA}, TargetB: {job.targetB}");
		
		// Initialize job queues
		job.targetQueueA = []; //more things
		job.targetQueueB = []; //more storage; keep in mind the job doesn't use it, but reserve it so you don't over-haul
		job.countQueue = [];//thing counts

		// Add the initial storage location to targetQueueB
		if (storeTarget.container != null)
		{
			job.targetQueueB.Add(storeTarget.container);
			Log.Message($"  Added container to queueB: {storeTarget.container}");
		}
		else
		{
			job.targetQueueB.Add(storeTarget.cell);
			Log.Message($"  Added cell to queueB: {storeTarget.cell}");
		}

		// Add the first item to the queue with its count
		job.targetQueueA.Add(thing);
		job.countQueue.Add(thing.stackCount);
		
		Log.Message($"  Final queueA[0]: {job.targetQueueA[0]}");
		Log.Message($"  Final queueB[0]: {job.targetQueueB[0]}");
		Log.Message($"  Final countQueue[0]: {job.countQueue[0]}");
		
		Log.Message($"-------------------------------------------------------------------");
		Log.Message($"------------------------------------------------------------------");//different size so the log doesn't count it 2x
		// NEW RADIAL HAUL ALGORITHM
		var storeCellCapacity = new Dictionary<StoreTarget, CellAllocation>()
		{
			[storeTarget] = new(thing, capacityStoreCell)
		};
		
		skipContext.ClearSkipCollections();
		if (storeTarget.container != null)
		{
			skipContext.AddSkipThing(storeTarget.container);
		}
		else
		{
			skipContext.AddSkipCell(storeTarget.cell);
		}

		var radialCandidates = BuildRadialHaulGroup(pawn, thing, 10);
		foreach (var candidate in radialCandidates)
		{
			// Skip if picking up this would over-encumber the pawn
			if (MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, candidate, 1))
				continue;
			// Allocate storage for the candidate (no reservation made here)
			this.AllocateThingAtCell(storeCellCapacity, pawn, candidate, job, skipContext);
		}
		Log.Message($"{pawn} returning NEW radial job with {job.targetQueueA?.Count ?? 0} items allocated");
		
		// CRITICAL: Final reservation test before returning the job
		Log.Message($"{pawn} FINAL JOB RESERVATION TEST - Testing all storage locations:");
		for (int i = 0; i < job.targetQueueB.Count; i++)
		{
			var storageTarget = job.targetQueueB[i];
			bool canReserveStorage = storageTarget.Thing != null 
				? pawn.CanReserve(storageTarget.Thing, 1, -1, null, false)
				: pawn.CanReserve(storageTarget.Cell, 1, -1, null, false);
			Log.Message($"  QueueB[{i}]: {storageTarget} - CanReserve: {canReserveStorage}");
		}
		
		// Validate that the job is properly set up
		ValidateJobCount(job, thing);
		
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
			return null;
		}

		var maxDistanceSquared = maxDistance * maxDistance;

		while (FindClosestThing(searchSet, center, out var i) is { } closestThing)
		{
			searchSet.RemoveAt(i);
			if (!closestThing.Spawned)
			{
				continue;
			}

			if ((center - closestThing.Position).LengthHorizontalSquared > maxDistanceSquared)
			{
				break;
			}

			if (!map.reachability.CanReach(center, closestThing, peMode, traverseParams))
			{
				continue;
			}

			if (validator == null || validator(closestThing))
			{
				return closestThing;
			}
		}

		return null;
	}

	public static Thing FindClosestThing(List<Thing> searchSet, IntVec3 center, out int index)
	{
		if (!searchSet.Any())
		{
			index = -1;
			return null;
		}

		// Find the first valid thing
		Thing closestThing = null;
		index = -1;
		for (var i = 0; i < searchSet.Count; i++)
		{
			if (searchSet[i] != null && searchSet[i].Spawned && !searchSet[i].Destroyed)
			{
				closestThing = searchSet[i];
				index = i;
				break;
			}
		}

		// If no valid thing found, return null
		if (closestThing == null)
		{
			index = -1;
			return null;
		}

		var closestThingSquaredLength = (center - closestThing.Position).LengthHorizontalSquared;
		var count = searchSet.Count;
		for (var i = index + 1; i < count; i++)
		{
			var currentThing = searchSet[i];
			if (currentThing != null && currentThing.Spawned && !currentThing.Destroyed)
			{
				if (closestThingSquaredLength > (center - currentThing.Position).LengthHorizontalSquared)
				{
					closestThing = currentThing;
					index = i;
					closestThingSquaredLength = (center - closestThing.Position).LengthHorizontalSquared;
				}
			}
		}
		return closestThing;
	}

	public class CellAllocation(Thing a, int c)
	{
		public Thing allocated = a;
		public int capacity = c;
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

	public bool AllocateThingAtCell(Dictionary<StoreTarget, CellAllocation> storeCellCapacity, Pawn pawn, Thing nextThing, Job job, JobSkipContext skipContext)
	{
		var map = pawn.Map;
		var allocation = storeCellCapacity.FirstOrDefault(kvp =>
			kvp.Key is var storeTarget
			&& (storeTarget.container?.TryGetInnerInteractableThingOwner().CanAcceptAnyOf(nextThing)
			?? storeTarget.cell.GetSlotGroup(map).parent.Accepts(nextThing))
			&& Stackable(nextThing, kvp));
		var storeCell = allocation.Key;

		//Can't stack with allocated cells, find a new cell:
		if (storeCell == default)
		{
			var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);
			
			// Use our new reservation system to find storage with available capacity
			PUAHReservationSystem.StorageLocation storageLocation;
			int availableCapacity;
			
			if (PUAHReservationSystem.TryFindBestStorageWithReservation(nextThing, pawn, map, currentPriority, pawn.Faction, out storageLocation, out availableCapacity))
			{
				if (storageLocation.IsContainer)
				{
					storeCell = new(storageLocation.Container);
					job.targetQueueB.Add(storageLocation.Container);
					storeCellCapacity[storeCell] = new(nextThing, availableCapacity);
					Log.Message($"New container for unstackable {nextThing} = {storageLocation.Container}");
				}
				else
				{
					storeCell = new(storageLocation.Cell);
					job.targetQueueB.Add(storageLocation.Cell);
					storeCellCapacity[storeCell] = new(nextThing, availableCapacity);
					Log.Message($"New cell for unstackable {nextThing} = {storageLocation.Cell}");
				}
			}
			else
			{
				Log.Message($"{nextThing} can't find storage with available capacity");

				if (job.targetQueueA.NullOrEmpty())
				{
					job.targetQueueA.Add(nextThing);
				}

				return false;
			}
		}

		// Convert StoreTarget to our StorageLocation
		PUAHReservationSystem.StorageLocation puahLocation;
		if (storeCell.container != null)
		{
			puahLocation = new PUAHReservationSystem.StorageLocation(storeCell.container);
		}
		else
		{
			puahLocation = new PUAHReservationSystem.StorageLocation(storeCell.cell);
		}

		// Track only what we need for queue building
		bool itemAddedToQueue = false;
		
		// WorkGiver should NOT make any reservations - only find storage and build job queues
		// All reservations will be handled by the JobDriver to prevent conflicts
		Log.Message($"WorkGiver found storage for {nextThing} at {storeCell}, building job queue (no reservations)");
		
		try
		{
			// Add to queue and immediately mark as successful to prevent race conditions
			job.targetQueueA.Add(nextThing);
			itemAddedToQueue = true; // Set immediately after successful addition to prevent cleanup on success
			
			var count = nextThing.stackCount;
			storeCellCapacity[storeCell].capacity -= count;
			Log.Message($"{pawn} allocating {nextThing}:{count}, now {storeCell}:{storeCellCapacity[storeCell].capacity}");

			while (storeCellCapacity[storeCell].capacity <= 0)
			{
				var capacityOver = -storeCellCapacity[storeCell].capacity;
				storeCellCapacity.Remove(storeCell);

				// Prevent cycling on the same exhausted cell/container again
				if (storeCell.container != null)
				{
					skipContext.AddSkipThing(storeCell.container);
				}
				else
				{
					skipContext.AddSkipCell(storeCell.cell);
				}

				Log.Message($"{pawn} overdone {storeCell} by {capacityOver}");

				// Fix for infinite loop: when capacity is exactly met, break out
				if (capacityOver == 0)
				{
					job.countQueue.Add(count);
					Log.Message($"{nextThing}:{count} allocated (capacityOver was 0)");
					return true;
				}

				var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);
				if (CacheManager.TryGetCachedStorageLocation(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var nextHaulDestination, out var innerInteractableThingOwner, this))
				{
					// Fix for unreliable repeated storage detection: properly compare storage types
					bool isRepeatedStorage = false;
					if (innerInteractableThingOwner is null && storeCell.container is null)
					{
						// Both are cell-based storage
						isRepeatedStorage = nextStoreCell == storeCell.cell;
					}
					else if (innerInteractableThingOwner is not null && storeCell.container is not null)
					{
						// Both are container-based storage
						isRepeatedStorage = nextHaulDestination == storeCell.container;
					}
					// If storage types don't match, they're not the same storage

					if (isRepeatedStorage)
					{
						// Fix for incorrect item count reduction: ensure count doesn't go negative
						var adjustedCount = Math.Max(0, count - capacityOver);
						if (adjustedCount > 0)
						{
							job.countQueue.Add(adjustedCount);
							Log.Message($"Repeated storage detected, allocating partial {adjustedCount} and aborting further allocation.");
							return true;
						}
						else
						{
							// Remove from targetQueueA since we can't allocate any count
							if (itemAddedToQueue && job.targetQueueA.Count > 0)
							{
								job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);
								// Clean up reservations for the removed item
								CleanupReservationsForRemovedItem(pawn, nextThing, storeCell, job, map);
							}
							Log.Message($"Repeated storage detected but no capacity remaining, removing from queue.");
							return false;
						}
					}

					if (innerInteractableThingOwner is null)
					{
						storeCell = new(nextStoreCell);
						job.targetQueueB.Add(nextStoreCell);

						var capacity = CapacityAt(nextThing, nextStoreCell, map) - capacityOver;
						storeCellCapacity[storeCell] = new(nextThing, capacity);

						Log.Message($"New cell {nextStoreCell}:{capacity}, allocated extra {capacityOver}");
					}
					else
					{
						var destinationAsThing = (Thing)nextHaulDestination;
						storeCell = new(destinationAsThing);
						job.targetQueueB.Add(destinationAsThing);

						var capacity = innerInteractableThingOwner.GetCountCanAccept(nextThing) - capacityOver;

						storeCellCapacity[storeCell] = new(nextThing, capacity);

						Log.Message($"New haulDestination {nextHaulDestination}:{capacity}, allocated extra {capacityOver}");
					}
				}
				else
				{
					// Fix for incorrect item count reduction: ensure count doesn't go negative
					var adjustedCount = Math.Max(0, count - capacityOver);
					if (adjustedCount > 0)
					{
						job.countQueue.Add(adjustedCount);
						Log.Message($"Nowhere else to store, allocated {nextThing}:{adjustedCount}");
						return true;
					}
					else
					{
						// Remove from targetQueueA since we can't allocate any count
						if (itemAddedToQueue && job.targetQueueA.Count > 0)
						{
							job.targetQueueA.RemoveAt(job.targetQueueA.Count - 1);
							// Clean up reservations for the removed item
							CleanupReservationsForRemovedItem(pawn, nextThing, storeCell, job, map);
						}
						Log.Message($"Nowhere else to store and no capacity remaining, removing {nextThing} from queue.");
						return false;
					}
				}
			}
			job.countQueue.Add(count);
			Log.Message($"{nextThing}:{count} allocated");
			return true;
		}
		catch (System.Exception ex)
		{
			// Log the exception but don't change reservation tracking
			Log.Warning($"Exception during allocation for {nextThing} by {pawn}: {ex.Message}");
			throw;
		}
		finally
		{
			// No reservation cleanup needed since WorkGiver doesn't make any reservations
			// The JobDriver will handle all reservations and their cleanup
		}
	}

	/// <summary>
	/// Context object to hold skip collections for a single job, preventing race conditions
	/// </summary>
	public class JobSkipContext
	{
		private readonly HashSet<IntVec3> skipCells = new();
		private readonly HashSet<Thing> skipThings = new();

		/// <summary>
		/// Add a cell to the skip list
		/// </summary>
		public void AddSkipCell(IntVec3 cell)
		{
			skipCells.Add(cell);
		}

		/// <summary>
		/// Add a thing to the skip list
		/// </summary>
		public void AddSkipThing(Thing thing)
		{
			skipThings.Add(thing);
		}

		/// <summary>
		/// Check if a cell is in the skip list
		/// </summary>
		public bool ContainsSkipCell(IntVec3 cell)
		{
			return skipCells.Contains(cell);
		}

		/// <summary>
		/// Check if a thing is in the skip list
		/// </summary>
		public bool ContainsSkipThing(Thing thing)
		{
			return skipThings.Contains(thing);
		}

		/// <summary>
		/// Clear skip collections
		/// </summary>
		public void ClearSkipCollections()
		{
			skipCells.Clear();
			skipThings.Clear();
		}
	}

	/// <summary>
	/// Thread-safe method to attempt reservation with retry logic to prevent race conditions
	/// </summary>
	private static bool TryReserveSafely(Pawn pawn, object target, Job job, int maxPawns = 1, int stackCount = -1, ReservationLayerDef layer = null, bool ignoreOtherReservations = false)
	{
		if (target == null || pawn == null || job == null) return false;

		// First check if we can reserve
		bool canReserve = target is Thing thing 
			? pawn.CanReserve(thing, maxPawns, stackCount, layer, ignoreOtherReservations)
			: pawn.CanReserve((IntVec3)target, maxPawns, stackCount, layer, ignoreOtherReservations);

		if (!canReserve) return false;

		// Attempt the actual reservation with a small retry loop to handle race conditions
		for (int attempt = 0; attempt < 3; attempt++)
		{
			try
			{
				bool reserved = target is Thing thingTarget
					? pawn.Reserve(thingTarget, job, maxPawns, stackCount, layer, ignoreOtherReservations)
					: pawn.Reserve((IntVec3)target, job, maxPawns, stackCount, layer, ignoreOtherReservations);

				if (reserved) return true;

				// If reservation failed, check if it's because another pawn got it first
				if (attempt < 2) // Don't sleep on the last attempt
				{
					// Small delay to let other reservations complete
					System.Threading.Thread.Sleep(1);
				}
			}
			catch (System.Exception ex)
			{
				// Log the exception but continue trying
				Verse.Log.Warning($"Reservation attempt {attempt + 1} failed for {pawn} on {target}: {ex.Message}");
				if (attempt < 2)
				{
					System.Threading.Thread.Sleep(1);
				}
			}
		}

		return false;
	}

	/// <summary>
	/// Safely release a reservation for a storage target
	/// </summary>
	private static void ReleaseReservationSafely(Pawn pawn, StoreTarget storeTarget, Job job)
	{
		if (pawn?.Map?.reservationManager == null) return;

		try
		{
			if (storeTarget.container != null)
			{
				pawn.Map.reservationManager.Release(storeTarget.container, pawn, job);
			}
			else
			{
				pawn.Map.reservationManager.Release(storeTarget.cell, pawn, job);
			}
		}
		catch (System.Exception ex)
		{
			Log.Warning($"Failed to release reservation for {storeTarget}: {ex.Message}");
		}
	}

	/// <summary>
	/// Clean up reservations when an item is removed from the job queue
	/// </summary>
	private static void CleanupReservationsForRemovedItem(Pawn pawn, Thing thing, StoreTarget storeTarget, Job job, Map map)
	{
		// WorkGiver doesn't make any reservations anymore, so no cleanup needed
		// All reservations are handled by the JobDriver
		Log.Message($"WorkGiver removed item {thing} from queue - no reservations to clean up");
	}

	/// <summary>
	/// Validate that both mod and vanilla reservations are still active
	/// </summary>
	private static bool ValidateReservationsStillActive(Pawn pawn, StoreTarget storeTarget, Job job, Map map)
	{
		try
		{
			// Check vanilla reservation
			bool vanillaValid = false;
			if (storeTarget.container != null)
			{
				vanillaValid = map.reservationManager.ReservedBy(storeTarget.container, pawn, job);
			}
			else
			{
				vanillaValid = map.reservationManager.ReservedBy(storeTarget.cell, pawn, job);
			}

			// Note: We could also check mod reservations here if the PUAHReservationSystem
			// provides a method to validate existing reservations
			
			return vanillaValid;
		}
		catch (System.Exception ex)
		{
			Log.Warning($"Failed to validate reservations: {ex.Message}");
			return false;
		}
	}

	public bool TryFindBestBetterStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, out IHaulDestination haulDestination, out ThingOwner innerInteractableThingOwner)
	{
		// Create a temporary skip context for storage lookup
		var skipContext = new JobSkipContext();
		
		var storagePriority = StoragePriority.Unstored;
		innerInteractableThingOwner = null;
		if (TryFindBestBetterStoreCellFor(t, carrier, map, currentPriority, faction, out var foundCell2, skipContext))
		{
			storagePriority = foundCell2.GetSlotGroup(map).Settings.Priority;
		}

		if (!TryFindBestBetterNonSlotGroupStorageFor(t, carrier, map, currentPriority, faction, out var haulDestination2, false, skipContext))
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
				Verse.Log.Error($"{haulDestination2} is not a valid Thing. Pick Up And Haul can't work with this");
			}
			else
			{
				innerInteractableThingOwner = destinationAsThing.TryGetInnerInteractableThingOwner();
			}

			if (innerInteractableThingOwner is null)
			{
				Verse.Log.Error($"{haulDestination2} gave null ThingOwner during lookup in Pick Up And Haul's WorkGiver_HaulToInventory");
			}

			return true;
		}

		foundCell = foundCell2;
		haulDestination = foundCell2.GetSlotGroup(map).parent;
		return true;
	}

	public bool TryFindBestBetterStoreCellFor(Thing thing, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IntVec3 foundCell, JobSkipContext skipContext)
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
				if (skipContext.ContainsSkipCell(cell))
				{
					continue;
				}

				if (StoreUtility.IsGoodStoreCell(cell, map, thing, carrier, faction) && cell != default)
				{
					foundCell = cell;

					skipContext.AddSkipCell(cell);

					return true;
				}
			}
		}
		foundCell = IntVec3.Invalid;
		return false;
	}

	public static float AddedEncumberance(Pawn pawn, Thing thing)
		=> thing.stackCount * thing.GetStatValue(StatDefOf.Mass) / MassUtility.Capacity(pawn);

	public static int CountPastCapacity(Pawn pawn, Thing thing, float encumberance)
		=> (int)Math.Ceiling((encumberance - 1) * MassUtility.Capacity(pawn) / thing.GetStatValue(StatDefOf.Mass));

	/// <summary>
	/// Validate that a thing is in the correct cache and move it if necessary
	/// </summary>
	private static void ValidateThingInCache(Map map, Thing thing)
	{
		if (map == null || thing == null || thing.Destroyed) return;

		// Check if the thing is too heavy for any pawn
		bool isTooHeavy = true;
		foreach (var pawn in map.mapPawns.FreeColonistsSpawned)
		{
			if (pawn == null || pawn.Dead || pawn.Downed) continue;

			if (CanPawnCarryThing(pawn, thing))
			{
				isTooHeavy = false;
				break;
			}
		}

		// Move thing to appropriate cache
		if (isTooHeavy)
		{
			// Remove from haulable cache and add to too heavy cache
			PUAHHaulCaches.RemoveFromHaulableCache(map, thing);
			PUAHHaulCaches.AddToTooHeavyCache(map, thing);
		}
		else
		{
			// Remove from too heavy cache and add to haulable cache
			PUAHHaulCaches.RemoveFromTooHeavyCache(map, thing);
			PUAHHaulCaches.AddToHaulableCache(map, thing);
		}
	}

	/// <summary>
	/// Check if a pawn can carry a specific thing
	/// </summary>
	private static bool CanPawnCarryThing(Pawn pawn, Thing thing)
	{
		if (pawn == null || thing == null) return false;

		// Check if the thing is too heavy for the pawn
		float thingMass = thing.GetStatValue(StatDefOf.Mass);
		float maxCarryMass = pawn.GetStatValue(StatDefOf.CarryingCapacity);

		return thingMass <= maxCarryMass;
	}

	/// <summary>
	/// Build a list of nearby haulable things using a radial search and simple weight/path checks.
	/// </summary>
	private static List<Thing> BuildRadialHaulGroup(Pawn pawn, Thing rootThing, int maxCandidates = 10)
	{
		var map = pawn.Map;
		if (map == null || rootThing == null)
			return new List<Thing>();

		// Pull haulables from our cache to save work
		var haulables = CacheManager.GetAccessibleHaulables(map);

		var candidates = new List<(Thing thing, float distSq)>();

		foreach (var thing in haulables)
		{
			if (thing == null || thing == rootThing || !thing.Spawned || thing.Destroyed)
				continue;

			// Quick weight guard so we don't even consider impossibly heavy items
			if (MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1))
				continue;

			float distSq = (thing.Position - rootThing.Position).LengthHorizontalSquared;
			candidates.Add((thing, distSq));
		}

		// Order by proximity to the root object
		var ordered = candidates.OrderBy(c => c.distSq).Select(c => c.thing).ToList();

		// Trim to the closest N
		if (ordered.Count > maxCandidates)
			ordered = ordered.GetRange(0, maxCandidates);

		// Remove unreachable ones
		ordered = ordered.Where(t => pawn.CanReach(t, PathEndMode.ClosestTouch, Danger.Some)).ToList();

		// Greedy nearest-neighbour ordering starting from rootThing’s cell
		var optimised = new List<Thing>();
		var currentPos = rootThing.Position;
		while (ordered.Count > 0)
		{
			Thing closest = null;
			float best = float.MaxValue;
			foreach (var t in ordered)
			{
				float d = (t.Position - currentPos).LengthHorizontalSquared;
				if (d < best)
				{
					best = d;
					closest = t;
				}
			}
			optimised.Add(closest);
			currentPos = closest.Position;
			ordered.Remove(closest);
		}

		return optimised;
	}




	public bool TryFindBestBetterNonSlotGroupStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IHaulDestination haulDestination, bool acceptSamePriority = false)
	{
		var skipContext = new JobSkipContext();
		return TryFindBestBetterNonSlotGroupStorageFor(t, carrier, map, currentPriority, faction, out haulDestination, acceptSamePriority, skipContext);
	}

	public bool TryFindBestBetterNonSlotGroupStorageFor(Thing t, Pawn carrier, Map map, StoragePriority currentPriority, Faction faction, out IHaulDestination haulDestination, bool acceptSamePriority, JobSkipContext skipContext)
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
				if (skipContext.ContainsSkipThing(thing) || thing.Faction != faction)
				{
					continue;
				}

				if (carrier != null)
				{
					// Use a more robust reservation check to prevent race conditions
					bool canReserve = false;
					try
					{
						canReserve = carrier.CanReserveNew(thing);
					}
					catch (System.Exception ex)
					{
						// Log the exception but continue with other destinations
						Verse.Log.Warning($"Reservation check failed for {carrier} on {thing}: {ex.Message}");
						continue;
					}

					if (thing.IsForbidden(carrier)
						|| !canReserve
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

				skipContext.AddSkipThing(thing);
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
}

public static class PickUpAndHaulDesignationDefOf
{
	public static DesignationDef haulUrgently = DefDatabase<DesignationDef>.GetNamedSilentFail("HaulUrgentlyDesignation");
}