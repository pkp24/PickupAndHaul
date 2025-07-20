using PickUpAndHaul.Cache;

namespace PickUpAndHaul;
public class JobDriver_HaulToInventory : JobDriver
{
	private readonly object _lockObject = new();

	public override void ExposeData()
	{
		// Don't save any data for this job driver to prevent save corruption
		// when the mod is removed
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			Log.Message("Skipping save data for HaulToInventory job driver");
			return;
		}

		// Only load data if we're in loading mode and the mod is active
		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			Log.Message("Skipping load data for HaulToInventory job driver");
			return;
		}
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed)
	{
		lock (_lockObject)
		{
			// Check if save operation is in progress
			if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
			{
				Log.Message($"Skipping HaulToInventory job reservations during save operation for {pawn}");
				return false;
			}

			Log.Message($"{pawn} job reservations targetA: {job.targetA.ToStringSafe()} targetB: {job.targetB.ToStringSafe()}");

			// Reserve as many as possible from queues
			if (job.targetQueueA != null && job.targetQueueA.Count > 0)
				pawn.ReserveAsManyAsPossible(job.targetQueueA, job);
			else
				Log.Warning($"targetQueueA is null or empty for {pawn}");

			if (job.targetQueueB != null && job.targetQueueB.Count > 0)
				pawn.ReserveAsManyAsPossible(job.targetQueueB, job);
			else
				Log.Warning($"targetQueueB is null or empty for {pawn}");

			bool targetAReserved;
			if (job.targetQueueA != null && job.targetQueueA.Count > 0)
				targetAReserved = pawn.Reserve(job.targetQueueA[0], job);
			else
			{
				Log.Error($"Cannot reserve targetQueueA[0] - queue is null or empty for {pawn}");
				Log.Error($"Job state - targetQueueA: {job.targetQueueA?.Count ?? 0}, targetQueueB: {job.targetQueueB?.Count ?? 0}, countQueue: {job.countQueue?.Count ?? 0}");

				return false;
			}

			bool targetBReserved;
			if (job.targetB != null)
				targetBReserved = pawn.Reserve(job.targetB, job);
			else
			{
				Log.Error($"targetB is null for {pawn}");
				return false;
			}

			var result = targetAReserved && targetBReserved;
			Log.Message($"Reservation result: {result} (targetA: {targetAReserved}, targetB: {targetBReserved})");

			return result;
		}
	}

	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		// Check if save operation is in progress at the start
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"Ending HaulToInventory job during save operation for {pawn}");
			EndJobWith(JobCondition.InterruptForced);
			yield break;
		}

		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A); //also does count
		yield return nextTarget;

		yield return CheckForOverencumberedForCombatExtended();

		var gotoThing = new Toil
		{
			initAction = () =>
			{
				// Check for save operation before pathing
				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					EndJobWith(JobCondition.InterruptForced);
					return;
				}
				pawn.pather.StartPath(TargetThingA, PathEndMode.ClosestTouch);
			},
			defaultCompleteMode = ToilCompleteMode.PatherArrival
		};
		gotoThing.FailOnDespawnedNullOrForbidden(TargetIndex.A);
		yield return gotoThing;
		yield return CheckIfPawnShouldLoadInventory(pawn);
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());
		yield return UnloadInventory(pawn);

		//maintain cell reservations on the trip back
		yield return TargetB.HasThing ? Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
				: Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.ClosestTouch);
	}

	private Toil CheckIfPawnShouldLoadInventory(Pawn pawn) => new()
	{
		initAction = () =>
		{
			// Check for save operation before taking action
			if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
			{
				EndJobWith(JobCondition.InterruptForced);
				return;
			}
			var takenToInventory = pawn.GetComp<CompHauledToInventory>();

			if (takenToInventory == null)
				return;

			// Clean up nulls at a safe point before accessing the collection
			takenToInventory.CleanupNulls();

			var actor = pawn;
			var thing = actor.CurJob.GetTarget(TargetIndex.A).Thing;
			Toils_Haul.ErrorCheckForCarry(actor, thing);

			//get max we can pick up
			var countToPickUp = Mathf.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(actor, thing));

			if (countToPickUp > 0)
			{
				var splitThing = thing.SplitOff(countToPickUp);
				var shouldMerge = takenToInventory.HashSet.Any(x => x.def == thing.def);
				actor.inventory.GetDirectlyHeldThings().TryAdd(splitThing, shouldMerge);
				takenToInventory.RegisterHauledItem(splitThing);
			}
		}
	};

	private Toil UnloadInventory(Pawn pawn) => new()
	{
		initAction = () =>
		{
			// Check if save operation is in progress
			if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
			{
				EndJobWith(JobCondition.InterruptForced);
				return;
			}

			if (pawn == null || pawn.InMentalState)
				return;

			var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, pawn);
			var itemsTakenToInventory = pawn.GetComp<CompHauledToInventory>();

			if (itemsTakenToInventory == null)
				return;

			itemsTakenToInventory.CleanupNulls();

			var carriedThing = itemsTakenToInventory.HashSet;

			if (pawn.Faction != Faction.OfPlayerSilentFail || !Settings.IsAllowedRace(pawn.RaceProps)
				|| carriedThing == null || carriedThing.Count == 0
				|| pawn.inventory.innerContainer is not { } inventoryContainer || inventoryContainer.Count == 0)
				return;

			if ((pawn.IsOverAllowedGearCapacity() || WorkCache.Cache.Count == 0 || carriedThing.Count >= 5) && job.TryMakePreToilReservations(pawn, false))
			{
				pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
				return;
			}
		}
	};

	/// <summary>
	/// the workgiver checks for encumbered, this is purely extra for CE
	/// </summary>
	/// <returns></returns>
	private Toil CheckForOverencumberedForCombatExtended()
	{
		var toil = new Toil();

		if (!ModCompatibilityCheck.CombatExtendedIsActive)
			return toil;

		toil.initAction = () =>
		{
			// Check for save operation before checking encumbrance
			if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
			{
				EndJobWith(JobCondition.InterruptForced);
				return;
			}

			var actor = toil.actor;
			var curJob = actor.jobs.curJob;
			var nextThing = curJob.targetA.Thing;

			if (pawn.IsOverAllowedGearCapacity())
			{
				var haul = HaulAIUtility.HaulToStorageJob(actor, nextThing, false);
				if (haul?.TryMakePreToilReservations(actor, false) ?? false)
				{
					//note that HaulToStorageJob etc doesn't do opportunistic duplicate hauling for items in valid storage. REEEE
					actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);
					EndJobWith(JobCondition.Succeeded);
				}
			}
		};

		return toil;
	}
}