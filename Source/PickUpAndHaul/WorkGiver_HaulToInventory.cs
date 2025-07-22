using PickUpAndHaul.Cache;

namespace PickUpAndHaul;
public class WorkGiver_HaulToInventory : WorkGiver_HaulGeneral
{
	public override bool ShouldSkip(Pawn pawn, bool forced = false) => base.ShouldSkip(pawn, forced)
		|| pawn.Faction != Faction.OfPlayerSilentFail
		|| !Settings.IsAllowedRace(pawn.RaceProps)
		|| pawn.GetComp<CompHauledToInventory>() == null
		|| pawn.IsQuestLodger()
		|| !PickupAndHaulSaveLoadLogger.IsModActive();

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		WorkCache.Instance.CalculatePotentialWork(pawn);
		return WorkCache.Cache;
	}

	//pick up stuff until you can't anymore,
	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		try
		{
			if (ShouldUnloadInventory(pawn, thing))
				pawn.jobs.jobQueue.EnqueueFirst(JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory), JobTag.Misc);

			var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.HaulToInventory);
			GetClosestAndEnqueue(thing, pawn, job, MassUtility.GearAndInventoryMass(pawn));

			return job.targetQueueA.Count > 0 ? job : null;
		}
		catch (Exception ex)
		{
			Log.Error(ex.ToString());
			return HaulAIUtility.HaulToStorageJob(pawn, thing, forced); //fallback
		}
	}

	private static void GetClosestAndEnqueue(Thing previousThing, Pawn pawn, Job job, float currentMass)
	{
		while (!WorkCache.Cache.IsEmpty)
		{
			if (!WorkCache.Cache.TryDequeue(out var candidate))
				break;

			if (candidate == null
				|| !candidate.Spawned
				|| !pawn.CanReserve(candidate)
				|| candidate.IsForbidden(pawn)
				|| candidate.IsInValidBestStorage()
				|| candidate.Destroyed)
				continue;

			var thingInSight = (previousThing.Position - candidate.Position).LengthHorizontalSquared <= 400f;
			if (!thingInSight)
				continue;
			if (!pawn.Map.reachability.CanReach(previousThing.Position, candidate, PathEndMode.ClosestTouch, TraverseParms.For(pawn)))
				continue;
			var count = Math.Min(candidate.stackCount, CalculateActualCarriableAmount(candidate, currentMass, MassUtility.Capacity(pawn)));
			if (count <= 0)
				break;

			var thingToCarry = new LocalTargetInfo(candidate);
			pawn.Reserve(thingToCarry, job);
			currentMass += candidate.GetStatValue(StatDefOf.Mass) * count;
			job.targetQueueA ??= [];
			job.countQueue ??= [];
			job.targetQueueA.Add(thingToCarry);
			job.countQueue.Add(count);
			GetClosestAndEnqueue(candidate, pawn, job, currentMass);
		}
	}

	private static int CalculateActualCarriableAmount(Thing thing, float currentMass, float capacity)
	{
		if (currentMass >= capacity)
			return 0;

		var thingMass = thing.GetStatValue(StatDefOf.Mass);

		if (thingMass <= 0)
			return thing.stackCount;

		var remainingCapacity = capacity - currentMass;
		var maxCarriable = (int)Math.Floor(remainingCapacity / thingMass);

		return Math.Min(maxCarriable, thing.stackCount);
	}

	private static bool ShouldUnloadInventory(Pawn pawn, Thing thing)
	{
		if (pawn.jobs != null &&
			((pawn.jobs.jobQueue != null && pawn.jobs.jobQueue.Any(x => x.job.def == PickUpAndHaulJobDefOf.UnloadYourHauledInventory)) ||
			(pawn.jobs.curJob != null && pawn.jobs.curJob.def == PickUpAndHaulJobDefOf.UnloadYourHauledInventory)))
			return false;

		var isOverThreashold = (MassUtility.GearAndInventoryMass(pawn) / MassUtility.Capacity(pawn)) >= 0.9f;
		return (isOverThreashold || WorkCache.Cache.IsEmpty || MassUtility.WillBeOverEncumberedAfterPickingUp(pawn, thing, 1)) && pawn.inventory.innerContainer.Count != 0;
	}
}