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
		|| !PickupAndHaulSaveLoadLogger.IsModActive();

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		WorkCache.Instance.CalculatePotentialWork(pawn);
		return WorkCache.Cache.Select(x => x.Thing);
	}

	public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		if (thing.OkThingToHaul(pawn))
		{
			if (!StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thing), pawn.Faction, out var _, out var _, false))
				return false;
			var currentMass = MassUtility.GearAndInventoryMass(pawn);
			var capacity = MassUtility.Capacity(pawn);
			return CalculateActualCarriableAmount(thing, currentMass, capacity) > 0;
		}

		return false;
	}

	//pick up stuff until you can't anymore,
	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		try
		{
			lock (_lockObject)
			{
				if (!thing.OkThingToHaul(pawn))
				{
					Log.Error($"{pawn} cannot haul {thing}");
					return null;
				}

				var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.HaulToInventory);
				var currentMass = MassUtility.GearAndInventoryMass(pawn);
				do
				{
					thing = GetClosestSimilarAndRemove(thing, pawn, job, ref currentMass);
				} while (thing != null);

				//Log.Message($"Remaining {WorkCache.Cache.Count} items to haul");
				return job;
			}
		}
		catch (Exception ex)
		{
			Log.Error(ex.ToString());
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced); //fallback
		}
	}

	private static Thing GetClosestSimilarAndRemove(Thing thing, Pawn pawn, Job job, ref float currentMass)
	{
		if (!TryEnqueue(thing, pawn, job, ref currentMass))
			return null;

		if (WorkCache.Cache == null || !WorkCache.Cache.Any())
			return null;
		var maxDistanceSquared = 144f;
		for (var i = 0; i < WorkCache.Cache.Count; i++)
		{
			var nextThing = WorkCache.Cache[i].Thing;

			if (!nextThing.Spawned || nextThing.Destroyed)
			{
				WorkCache.Cache.RemoveAt(i--);
				continue;
			}

			if (!thing.CanStackWith(nextThing))
				continue;

			var distanceSquared = (thing.Position - nextThing.Position).LengthHorizontalSquared;
			if (distanceSquared > maxDistanceSquared)
				return null;

			if (!pawn.Map.reachability.CanReach(thing.Position, nextThing, PathEndMode.ClosestTouch, TraverseParms.For(pawn)))
				continue;
			WorkCache.Cache.RemoveAt(i);
			return nextThing;
		}

		return null;
	}

	private static bool TryEnqueue(Thing thing, Pawn pawn, Job job, ref float currentMass)
	{
		var actualCarriableAmount = CalculateActualCarriableAmount(thing, currentMass, MassUtility.Capacity(pawn));
		if (actualCarriableAmount <= 0)
			return false;

		var count = Math.Min(thing.stackCount, actualCarriableAmount);

		if (count <= 0)
		{
			Log.Message($"Final count is {count}, cannot allocate {thing}");
			return false;
		}

		job.targetQueueA ??= [];
		job.countQueue ??= [];

		job.targetQueueA.Add(new LocalTargetInfo(thing));
		job.countQueue.Add(count);

		currentMass += thing.GetStatValue(StatDefOf.Mass) * count;

		return true;
	}

	/// <summary>
	/// Calculates the actual amount of a thing that a pawn can carry considering encumbrance limits
	/// </summary>
	private static int CalculateActualCarriableAmount(Thing thing, float currentMass, float capacity)
	{
		if (currentMass >= capacity)
		{
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