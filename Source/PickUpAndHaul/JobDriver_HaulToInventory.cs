using System.Linq;

namespace PickUpAndHaul;
public class JobDriver_HaulToInventory : JobDriver
{
	public override void ExposeData()
	{
		// Don't save any data for this job driver to prevent save corruption
		// when the mod is removed
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			Log.Message("[PickUpAndHaul] Skipping save data for HaulToInventory job driver");
			return;
		}
		
		// Only load data if we're in loading mode and the mod is active
		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			Log.Message("[PickUpAndHaul] Skipping load data for HaulToInventory job driver");
			return;
		}
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed)
	{
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"[PickUpAndHaul] Skipping HaulToInventory job reservations during save operation for {pawn}");
			return false;
		}

		Log.Message($"{pawn} starting HaulToInventory job: {job.targetQueueA.ToStringSafeEnumerable()}:{job.countQueue.ToStringSafeEnumerable()}");
		pawn.ReserveAsManyAsPossible(job.targetQueueA, job);
		pawn.ReserveAsManyAsPossible(job.targetQueueB, job);
		return pawn.Reserve(job.targetQueueA[0], job) && pawn.Reserve(job.targetB, job);
	}

	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		// Check if save operation is in progress at the start
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"[PickUpAndHaul] Ending HaulToInventory job during save operation for {pawn}");
			EndJobWith(JobCondition.InterruptForced);
			yield break;
		}

		var takenToInventory = pawn.TryGetComp<CompHauledToInventory>();

		var wait = Toils_General.Wait(2);

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

		var takeThing = new Toil
		{
			initAction = () =>
			{
				// Check for save operation before taking action
				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					EndJobWith(JobCondition.InterruptForced);
					return;
				}

				var actor = pawn;
				var thing = actor.CurJob.GetTarget(TargetIndex.A).Thing;
				Toils_Haul.ErrorCheckForCarry(actor, thing);

				//get max we can pick up
				var countToPickUp = Mathf.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(actor, thing));
				Log.Message($"{actor} is hauling to inventory {thing}:{countToPickUp}");

				if (ModCompatibilityCheck.CombatExtendedIsActive)
				{
					countToPickUp = CompatHelper.CanFitInInventory(pawn, thing);
				}

				if (countToPickUp > 0)
				{
					var splitThing = thing.SplitOff(countToPickUp);
					var shouldMerge = takenToInventory.GetHashSet().Any(x => x.def == thing.def);
					actor.inventory.GetDirectlyHeldThings().TryAdd(splitThing, shouldMerge);
					takenToInventory.RegisterHauledItem(splitThing);

					if (ModCompatibilityCheck.CombatExtendedIsActive)
					{
						CompatHelper.UpdateInventory(pawn);
					}
				}

				//thing still remains, so queue up hauling if we can + end the current job (smooth/instant transition)
				//This will technically release the reservations in the queue, but what can you do
				if (thing.Spawned)
				{
					var haul = HaulAIUtility.HaulToStorageJob(actor, thing, false);
					if (haul?.TryMakePreToilReservations(actor, false) ?? false)
					{
						actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);
					}
					actor.jobs.curDriver.JumpToToil(wait);
				}
			}
		};
		yield return takeThing;
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());

		//Find more to haul, in case things spawned while this was in progess
		yield return new Toil
		{
			initAction = () =>
			{
				// Check for save operation before finding more work
				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					EndJobWith(JobCondition.InterruptForced);
					return;
				}

				var haulables = TempListForThings;
				haulables.Clear();
				haulables.AddRange(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
				var haulMoreWork = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(wg => wg.Worker is WorkGiver_HaulToInventory).Worker as WorkGiver_HaulToInventory;
				Job haulMoreJob = null;
				var haulMoreThing = WorkGiver_HaulToInventory.GetClosestAndRemove(pawn.Position, pawn.Map, haulables, PathEndMode.ClosestTouch,
					   TraverseParms.For(pawn), 12, t => (haulMoreJob = haulMoreWork.JobOnThing(pawn, t)) != null);

				//WorkGiver_HaulToInventory found more work nearby
				if (haulMoreThing != null)
				{
					Log.Message($"{pawn} hauling again : {haulMoreThing}");
					if (haulMoreJob.TryMakePreToilReservations(pawn, false))
					{
						pawn.jobs.jobQueue.EnqueueFirst(haulMoreJob, JobTag.Misc);
						EndJobWith(JobCondition.Succeeded);
					}
				}
			}
		};

		//maintain cell reservations on the trip back
		//TODO: do that when we carry things
		//I guess that means TODO: implement carrying the rest of the items in this job instead of falling back on HaulToStorageJob
		yield return TargetB.HasThing ? Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
			: Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.ClosestTouch);

		yield return new Toil //Queue next job
		{
			initAction = () =>
			{
				// Check for save operation before queuing next job
				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					EndJobWith(JobCondition.InterruptForced);
					return;
				}

				var actor = pawn;
				var curJob = actor.jobs.curJob;
				var storeCell = curJob.targetB;

				var unloadJob = JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, storeCell);
				if (unloadJob.TryMakePreToilReservations(actor, false))
				{
					actor.jobs.jobQueue.EnqueueFirst(unloadJob, JobTag.Misc);
					EndJobWith(JobCondition.Succeeded);
					//This will technically release the cell reservations in the queue, but what can you do
				}
			}
		};
		yield return wait;
	}

	private static List<Thing> TempListForThings { get; } = new();

	/// <summary>
	/// the workgiver checks for encumbered, this is purely extra for CE
	/// </summary>
	/// <returns></returns>
	public Toil CheckForOverencumberedForCombatExtended()
	{
		var toil = new Toil();

		if (!ModCompatibilityCheck.CombatExtendedIsActive)
		{
			return toil;
		}

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

			var ceOverweight = CompatHelper.CeOverweight(pawn);

			if (!(MassUtility.EncumbrancePercent(actor) <= 0.9f && !ceOverweight))
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