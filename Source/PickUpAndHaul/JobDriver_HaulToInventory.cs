using PickUpAndHaul.Cache;

namespace PickUpAndHaul;
public class JobDriver_HaulToInventory : JobDriver
{
	private readonly int WAIT_DURATION = 3;

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
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"Skipping HaulToInventory job reservations during save operation for {pawn}");
			return false;
		}

		// Validate job before making reservations
		if (!ValidateJobBeforeExecution())
		{
			Log.Error($"Job failed for {pawn}, cannot make reservations");
			return false;
		}

		// EXTENSIVE DEBUGGING - Track job state
		Log.Message($"{pawn} starting HaulToInventory job reservations");
		Log.Message($"Job targetA: {job.targetA.ToStringSafe()}");
		Log.Message($"Job targetB: {job.targetB.ToStringSafe()}");
		Log.Message($"Job targetQueueA count: {job.targetQueueA?.Count ?? 0}");
		Log.Message($"Job targetQueueB count: {job.targetQueueB?.Count ?? 0}");
		Log.Message($"Job countQueue count: {job.countQueue?.Count ?? 0}");

		if (job.targetQueueA != null)
			Log.Message($"targetQueueA contents: {string.Join(", ", job.targetQueueA.Select(t => t.ToStringSafe()))}");
		if (job.targetQueueB != null)
			Log.Message($"targetQueueB contents: {string.Join(", ", job.targetQueueB.Select(t => t.ToStringSafe()))}");
		if (job.countQueue != null)
			Log.Message($"countQueue contents: {string.Join(", ", job.countQueue)}");

		// Validate queue synchronization
		if (job.targetQueueA != null && job.countQueue != null && job.targetQueueA.Count != job.countQueue.Count)
		{
			Log.Error($"Queue synchronization error! targetQueueA.Count ({job.targetQueueA.Count}) != countQueue.Count ({job.countQueue.Count}) for {pawn}");
			Log.Error($"This indicates a bug in AllocateThingAtCell or job creation logic");
		}

		// Validate job integrity before proceeding
		job.ValidateJobQueues(pawn, "PreToilReservations");

		// Reserve as many as possible from queues
		if (job.targetQueueA != null && job.targetQueueA.Count > 0)
		{
			Log.Message($"Reserving {job.targetQueueA.Count} items from targetQueueA");
			pawn.ReserveAsManyAsPossible(job.targetQueueA, job);
		}
		else
			Log.Warning($"targetQueueA is null or empty for {pawn}");

		if (job.targetQueueB != null && job.targetQueueB.Count > 0)
		{
			Log.Message($"Reserving {job.targetQueueB.Count} items from targetQueueB");
			pawn.ReserveAsManyAsPossible(job.targetQueueB, job);
		}
		else
			Log.Warning($"targetQueueB is null or empty for {pawn}");

		// FIXED: Add bounds checking before accessing targetQueueA[0]
		var targetAReserved = false;
		if (job.targetQueueA != null && job.targetQueueA.Count > 0)
		{
			Log.Message($"Reserving targetQueueA[0]: {job.targetQueueA[0]}");
			targetAReserved = pawn.Reserve(job.targetQueueA[0], job);
		}
		else
		{
			Log.Error($"Cannot reserve targetQueueA[0] - queue is null or empty for {pawn}");
			Log.Error($"This job should not have been created with empty targetQueueA");
			Log.Error($"Job state - targetQueueA: {job.targetQueueA?.Count ?? 0}, targetQueueB: {job.targetQueueB?.Count ?? 0}, countQueue: {job.countQueue?.Count ?? 0}");

			// End the job gracefully instead of crashing
			Log.Error($"Ending job gracefully to prevent ArgumentOutOfRangeException");
			return false;
		}

		var targetBReserved = false;
		if (job.targetB != null)
		{
			Log.Message($"Reserving targetB: {job.targetB}");
			targetBReserved = pawn.Reserve(job.targetB, job);
		}
		else
		{
			Log.Error($"targetB is null for {pawn}");
			return false;
		}

		var result = targetAReserved && targetBReserved;
		Log.Message($"Reservation result: {result} (targetA: {targetAReserved}, targetB: {targetBReserved})");

		return result;
	}

	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		// Validate job integrity before proceeding
		if (!ValidateJobBeforeExecution())
		{
			Log.Error($"Job failed for {pawn} in MakeNewToils");
			EndJobWith(JobCondition.Incompletable);
			yield break;
		}

		// Check if save operation is in progress at the start
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"Ending HaulToInventory job during save operation for {pawn}");
			EndJobWith(JobCondition.InterruptForced);
			yield break;
		}

		var wait = Toils_General.Wait(WAIT_DURATION);

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
		yield return CheckIfPawnShouldLoadInventory(pawn, wait);
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());

		//maintain cell reservations on the trip back
		yield return ShouldReturn(pawn, wait);
		yield return CheckIfPawnShouldUnloadInventory(pawn);
		yield return wait;
	}

	private Toil ShouldReturn(Pawn pawn, Toil wait) =>
		!pawn.IsOverAllowedGearCapacity()
				? wait
				: TargetB.HasThing ? Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
				: Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.ClosestTouch);

	private Toil CheckIfPawnShouldLoadInventory(Pawn pawn, Toil wait) => new()
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
			Log.Message($"{actor} is hauling to inventory {thing}:{countToPickUp}");

			if (takenToInventory.HashSet.Count == 0 && actor.inventory.GetDirectlyHeldThings().Count != 0)
				foreach (var item in actor.inventory.GetDirectlyHeldThings())
					takenToInventory.RegisterHauledItem(item);

			if (countToPickUp > 0)
			{
				var splitThing = thing.SplitOff(countToPickUp);
				var shouldMerge = takenToInventory.HashSet.Any(x => x.def == thing.def);
				actor.inventory.GetDirectlyHeldThings().TryAdd(splitThing, shouldMerge);
				takenToInventory.RegisterHauledItem(splitThing);
			}

			//thing still remains, so queue up hauling if we can + end the current job (smooth/instant transition)
			//This will technically release the reservations in the queue, but what can you do
			if (thing.Spawned)
			{
				var haul = HaulAIUtility.HaulToStorageJob(actor, thing, false);
				if (haul?.TryMakePreToilReservations(actor, false) ?? false)
					actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);
				actor.jobs.curDriver.JumpToToil(wait);
			}
		}
	};

	private static Toil CheckIfPawnShouldUnloadInventory(Pawn pawn) => new()
	{
		initAction = () =>
		{
			// Check if save operation is in progress
			if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				return; // Skip unload checking during save operations

			// Ignore pawns that are currently in a mental state
			if (pawn == null || pawn.InMentalState)
				return;

			var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, pawn);
			var itemsTakenToInventory = pawn.GetComp<CompHauledToInventory>();

			if (itemsTakenToInventory == null)
				return;

			// Clean up nulls at a safe point before accessing the collection
			itemsTakenToInventory.CleanupNulls();

			var carriedThing = itemsTakenToInventory.HashSet;

			if (pawn.Faction != Faction.OfPlayerSilentFail || !Settings.IsAllowedRace(pawn.RaceProps)
				|| carriedThing == null || carriedThing.Count == 0
				|| pawn.inventory.innerContainer is not { } inventoryContainer || inventoryContainer.Count == 0)
				return;

			if ((pawn.IsOverAllowedGearCapacity() && job.TryMakePreToilReservations(pawn, false))
				|| (WorkCache.Cache.Count == 0 && job.TryMakePreToilReservations(pawn, false)))
			{
				pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
				return;
			}

			if (inventoryContainer.Count >= 1)
				for (var i = 0; i < inventoryContainer.Count; i++)
				{
					var compRottable = inventoryContainer[i].TryGetComp<CompRottable>();

					if (compRottable?.TicksUntilRotAtCurrentTemp < 30000)
					{
						pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
						return;
					}
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

	/// <summary>
	/// Validates the job before execution to prevent desync issues
	/// </summary>
	private bool ValidateJobBeforeExecution()
	{
		if (job == null)
		{
			Log.Error($"Job is null for {pawn}");
			return false;
		}

		if (pawn == null)
		{
			Log.Error($"Pawn is null");
			return false;
		}

		// For regular jobs, validate queue synchronization
		if (job.targetQueueA == null || job.targetQueueA.Count == 0)
		{
			Log.Error($"Job has empty targetQueueA for {pawn}");
			return false;
		}

		if (job.countQueue == null || job.countQueue.Count == 0)
		{
			Log.Error($"Job has empty countQueue for {pawn}");
			return false;
		}

		if (job.targetQueueA.Count != job.countQueue.Count)
		{
			Log.Error($"Queue synchronization failure for {pawn} - targetQueueA.Count ({job.targetQueueA.Count}) != countQueue.Count ({job.countQueue.Count})");
			return false;
		}

		// Validate that all targets are still valid
		for (var i = 0; i < job.targetQueueA.Count; i++)
		{
			var target = job.targetQueueA[i];
			if (target == null || target.Thing == null)
			{
				Log.Error($"Found null target at index {i} for {pawn}");
				return false;
			}

			if (target.Thing.Destroyed || !target.Thing.Spawned)
			{
				Log.Warning($"Found destroyed/unspawned target {target.Thing} at index {i} for {pawn}");
				return false;
			}
		}

		// Validate that all counts are positive
		for (var i = 0; i < job.countQueue.Count; i++)
			if (job.countQueue[i] <= 0)
			{
				Log.Error($"Found non-positive count {job.countQueue[i]} at index {i} for {pawn}");
				return false;
			}

		return true;
	}
}