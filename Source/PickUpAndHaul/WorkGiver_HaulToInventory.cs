﻿using System.Linq;

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

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		PerformanceProfiler.StartTimer("PotentialWorkThingsGlobal");
		
		var list = new List<Thing>(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
		Comparer.rootCell = pawn.Position;
		list.Sort(Comparer);
		
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
		&& StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out _, out _, false)
		&& !OverAllowedGearCapacity(pawn)
		&& !MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1);
		
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
                var encumberance = MassUtility.GearAndInventoryMass(pawn) / capacity;
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
		Log.Message($"-------------------------------------------------------------------");
		Log.Message($"------------------------------------------------------------------");//different size so the log doesn't count it 2x
		Log.Message($"{pawn} job found to haul: {thing} to {storeTarget}:{capacityStoreCell}, looking for more now");

		//Find what fits in inventory, set nextThingLeftOverCount to be 
                var nextThingLeftOverCount = 0;
		job.targetQueueA = new List<LocalTargetInfo>(); //more things
		job.targetQueueB = new List<LocalTargetInfo>(); //more storage; keep in mind the job doesn't use it, but reserve it so you don't over-haul
		job.countQueue = new List<int>();//thing counts

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
			&& GoodThingToHaul(t, pawn) && HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false); //forced is false, may differ from first thing

		haulables.Remove(thing);

		do
		{
                        if (AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job))
                        {
                                lastThing = nextThing;
                                encumberance += AddedEncumberance(pawn, nextThing, capacity);

                                if (encumberance > 1 || ceOverweight)
                                {
                                        //can't CountToPickUpUntilOverEncumbered here, pawn doesn't actually hold these things yet
                                        nextThingLeftOverCount = CountPastCapacity(pawn, nextThing, encumberance, capacity);
                                        Log.Message($"Inventory allocated, will carry {nextThing}:{nextThingLeftOverCount}");
                                        break;
                                }
                        }
                }
                while ((nextThing = GetClosestAndRemove(lastThing.Position, map, haulables, PathEndMode.ClosestTouch,
                        traverseParms, distanceToSearchMore, Validator)) != null);
		
		if (nextThing == null)
		{
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
			Log.Message("Can't carry more, nevermind!");
			skipCells = null;
			skipThings = null;
			//skipTargets = null;
			PerformanceProfiler.EndTimer("JobOnThing");
			return job;
		}
		Log.Message($"Looking for more like {nextThing}");

                while ((nextThing = GetClosestAndRemove(nextThing.Position, map, haulables,
                           PathEndMode.ClosestTouch, traverseParms, 8f, Validator)) != null)
                {
                        carryCapacity -= nextThing.stackCount;

			if (AllocateThingAtCell(storeCellCapacity, pawn, nextThing, job))
			{
				break;
			}

			if (carryCapacity <= 0)
			{
				var lastCount = job.countQueue.Pop() + carryCapacity;
				job.countQueue.Add(lastCount);
				Log.Message($"Nevermind, last count is {lastCount}");
				break;
			}
		}

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

	public static bool AllocateThingAtCell(Dictionary<StoreTarget, CellAllocation> storeCellCapacity, Pawn pawn, Thing nextThing, Job job)
	{
		PerformanceProfiler.StartTimer("AllocateThingAtCell");
		
                var map = pawn.Map;
                var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);
                var allocation = storeCellCapacity.FirstOrDefault(kvp =>
                        kvp.Key is var storeTarget
                        && (storeTarget.container?.TryGetInnerInteractableThingOwner().CanAcceptAnyOf(nextThing)
                        ?? storeTarget.cell.GetSlotGroup(map).parent.Accepts(nextThing))
                        && Stackable(nextThing, kvp));
                var storeCell = allocation.Key;
                var targetsAddedCount = 0;

                //Can't stack with allocated cells, find a new cell:
                if (storeCell == default)
                {
                        if (TryFindBestBetterStorageFor(nextThing, pawn, map, currentPriority, pawn.Faction, out var nextStoreCell, out var haulDestination, out var innerInteractableThingOwner))
                        {
                                if (innerInteractableThingOwner is null)
                                {
                                        storeCell = new(nextStoreCell);
                                        job.targetQueueB.Add(nextStoreCell);
                                        targetsAddedCount++;

                                        storeCellCapacity[storeCell] = new(nextThing, CapacityAt(nextThing, nextStoreCell, map));

                                        Log.Message($"New cell for unstackable {nextThing} = {nextStoreCell}");
                                }
                                else
                                {
                                        var destinationAsThing = (Thing)haulDestination;
                                        storeCell = new(destinationAsThing);
                                        job.targetQueueB.Add(destinationAsThing);
                                        targetsAddedCount++;

                                        storeCellCapacity[storeCell] = new(nextThing, innerInteractableThingOwner.GetCountCanAccept(nextThing));

                                        Log.Message($"New haulDestination for unstackable {nextThing} = {haulDestination}");
                                }
                        }
                        else
                        {
                                Log.Message($"{nextThing} can't stack with allocated cells");

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

			Log.Message($"{pawn} overdone {storeCell} by {capacityOver}");

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
					targetsAddedCount++;

					var capacity = CapacityAt(nextThing, nextStoreCell, map) - capacityOver;
					storeCellCapacity[storeCell] = new(nextThing, capacity);

					Log.Message($"New cell {nextStoreCell}:{capacity}, allocated extra {capacityOver}");
				}
				else
				{
					var destinationAsThing = (Thing)nextHaulDestination;
					storeCell = new(destinationAsThing);
					job.targetQueueB.Add(destinationAsThing);
					targetsAddedCount++;

					var capacity = innerInteractableThingOwner.GetCountCanAccept(nextThing) - capacityOver;

					storeCellCapacity[storeCell] = new(nextThing, capacity);

					Log.Message($"New haulDestination {nextHaulDestination}:{capacity}, allocated extra {capacityOver}");
				}
			}
                        else
                        {
                                count -= capacityOver;
                                if (count <= 0)
                                {
                                        // Remove all targets added by this execution
                                        if (targetsAddedCount > 0 && job.targetQueueB.Count >= targetsAddedCount)
                                                job.targetQueueB.RemoveRange(job.targetQueueB.Count - targetsAddedCount, targetsAddedCount);
                                        PerformanceProfiler.EndTimer("AllocateThingAtCell");
                                        Log.Message($"Nowhere else to store, skipping {nextThing} due to zero capacity");
                                        return false;
                                }
                                // Don't add to countQueue here - only add when we successfully add to targetQueueA
                                Log.Message($"Nowhere else to store, will try to allocate {nextThing}:{count}");
                                break;
                        }
                }

                if (count <= 0)
                {
                        // Remove all targets added by this execution
                        if (targetsAddedCount > 0 && job.targetQueueB.Count >= targetsAddedCount)
                                job.targetQueueB.RemoveRange(job.targetQueueB.Count - targetsAddedCount, targetsAddedCount);
                        PerformanceProfiler.EndTimer("AllocateThingAtCell");
                        Log.Message($"Skipping {nextThing} due to zero capacity");
                        return false;
                }

                job.targetQueueA.Add(nextThing);
                job.countQueue.Add(count);
                Log.Message($"{nextThing}:{count} allocated");
                PerformanceProfiler.EndTimer("AllocateThingAtCell");
                return true;
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
}

public static class PickUpAndHaulDesignationDefOf
{
	public static DesignationDef haulUrgently = DefDatabase<DesignationDef>.GetNamedSilentFail("HaulUrgentlyDesignation");
}