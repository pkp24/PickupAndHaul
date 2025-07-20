using PickUpAndHaul.Cache;

namespace PickUpAndHaul;
public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	private readonly object _lockObject = new();

	public override bool ShouldSkip(Pawn pawn, bool forced = false) => base.ShouldSkip(pawn, forced)
		|| pawn.InMentalState
		|| pawn.Faction != Faction.OfPlayerSilentFail
		|| !Settings.IsAllowedRace(pawn.RaceProps)
		|| pawn.GetComp<CompHauledToInventory>() == null
		|| pawn.IsQuestLodger()
		|| pawn.IsOverAllowedGearCapacity()
		|| !PickupAndHaulSaveLoadLogger.IsModActive();

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		WorkCache.Instance.CalculatePotentialWork(pawn);
		return WorkCache.Cache;
	}

	public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		// Check for basic storage availability and encumbrance, but be less restrictive about storage capacity
		// Let the allocation phase handle sophisticated storage finding and capacity management
		if (thing.OkThingToHaul(pawn))
		{
			if (!StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var _, out var _, false))
				return false;
			// Check if pawn can physically carry the item and if storage has meaningful capacity
			var currentMass = MassUtility.GearAndInventoryMass(pawn);
			var capacity = MassUtility.Capacity(pawn);
			var actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, capacity);

			return capacity > 0 && actualCarriableAmount > 0;
		}

		return false;
	}

	//pick up stuff until you can't anymore,
	//while you're up and about, pick up something and haul it
	//before you go out, empty your pockets
	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		lock (_lockObject)
		{
			if (!thing.OkThingToHaul(pawn))
			{
				Log.Error($"{pawn} cannot haul {thing}");
				return null;
			}

			var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.HaulToInventory);
			job.targetQueueA = [];
			job.targetQueueB = [];
			job.countQueue = [];

			var capacity = MassUtility.Capacity(pawn);
			var currentMass = MassUtility.GearAndInventoryMass(pawn);
			int actualCarriableAmount;
			do
			{
				// First, check if the pawn can carry this item at all
				actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, capacity);
				if (actualCarriableAmount <= 0 || job.targetQueueA.Count >= 20)
					break;
				if (TryFindBestBetterStorageFor(pawn, thing, job, ref currentMass, actualCarriableAmount))
					continue;
			} while ((thing = GetClosestSimilarAndRemove(thing, pawn)) != null);

			Log.Message($"Remaining {WorkCache.Cache.Count} items to haul");
			return job;
		}
	}

	private static Thing GetClosestSimilarAndRemove(Thing thing, Pawn pawn)
	{
		if (WorkCache.Cache == null || !WorkCache.Cache.Any())
		{
			Log.Message($"searchSet is null or empty");
			return null;
		}
		var maxDistanceSquared = 50f;
		for (var i = 0; i < WorkCache.Cache.Count; i++)
		{
			var nextThing = WorkCache.Cache[i];

			if (!nextThing.Spawned)
			{
				WorkCache.Cache.RemoveAt(i--);
				continue;
			}

			if (!thing.CanStackWith(nextThing))
				continue;

			var distanceSquared = (thing.Position - nextThing.Position).LengthHorizontalSquared;
			while (distanceSquared > maxDistanceSquared)
				return null;

			if (!pawn.Map.reachability.CanReach(thing.Position, nextThing, PathEndMode.ClosestTouch, TraverseParms.For(pawn)))
				continue;
			WorkCache.Cache.RemoveAt(i);
			return nextThing;
		}

		return null;
	}

	private static bool TryFindBestBetterStorageFor(Pawn pawn, Thing nextThing, Job job, ref float currentMass, int actualCarriableAmount)
	{
		var currentPriority = StoreUtility.CurrentStoragePriorityOf(nextThing);

		if (StoreUtility.TryFindBestBetterStorageFor(nextThing, pawn, pawn.Map, currentPriority, pawn.Faction, out var targetCell, out var haulDestination, true))
		{
			// Calculate the effective amount considering storage capacity, carriable amount, and item stack size
			var count = Math.Min(nextThing.stackCount, actualCarriableAmount);

			// Handle capacity overflow more gracefully
			if (count <= 0)
			{
				Log.Message($"Final count is {count}, cannot allocate {nextThing}");
				return false;
			}

			// Get the target from the storeCell, not from the queue
			if (!JobQueueManager.AddItemsToJob(pawn, job, nextThing, count, targetCell, haulDestination))
			{
				Log.Error($"Failed to add items to job queues for {pawn}!");
				return false;
			}
			currentMass += nextThing.GetStatValue(StatDefOf.Mass) * count;
			return true;
		}

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
}