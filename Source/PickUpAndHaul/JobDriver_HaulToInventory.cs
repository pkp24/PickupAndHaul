using System.Linq;
using System.Collections.Generic;

namespace PickUpAndHaul;

public class JobDriver_HaulToInventory : JobDriver
{
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
                Log.Message($"{pawn} attempting reservations for job with {job.targetQueueA?.Count ?? 0} items in queueA, {job.targetQueueB?.Count ?? 0} items in queueB");

                bool success = true;
                var successfulReservations = new List<LocalTargetInfo>();

                if (job.targetQueueB != null && job.targetQueueA != null)
                {
                        int count = System.Math.Min(job.targetQueueA.Count, job.targetQueueB.Count);
                        for (int i = 0; i < count; i++)
                        {
                                var storageTarget = job.targetQueueB[i];
                                var thingTarget = job.targetQueueA[i].Thing;
                                int itemCount = job.countQueue?.ElementAtOrDefault(i) ?? thingTarget.stackCount;

                                // convert to PUAH location
                                PUAHReservationSystem.StorageLocation puahLoc = storageTarget.HasThing
                                        ? new PUAHReservationSystem.StorageLocation(storageTarget.Thing)
                                        : new PUAHReservationSystem.StorageLocation(storageTarget.Cell);

                                Log.Message($"{pawn} attempting to reserve storage {storageTarget} for {thingTarget} x{itemCount}");

                                if (!PUAHReservationSystem.TryReservePartialStorage(pawn, thingTarget, itemCount, puahLoc, job, pawn.Map))
                                {
                                        Log.Message($"{pawn} FAILED PUAH reservation {storageTarget}");
                                        success = false;
                                        break;
                                }

                                if (pawn.Reserve(storageTarget, job, 1, -1, null, false))
                                {
                                        Log.Message($"{pawn} successfully reserved storage {storageTarget}");
                                        successfulReservations.Add(storageTarget);
                                }
                                else
                                {
                                        Log.Message($"{pawn} FAILED vanilla reserve {storageTarget}");
                                        success = false;
                                        break;
                                }
                        }
                }

                if (success && job.targetB != null)
                {
                        Log.Message($"{pawn} attempting to reserve targetB: {job.targetB}");
                        if (pawn.Reserve(job.targetB, job, 1, -1, null, false))
                        {
                                Log.Message($"{pawn} successfully reserved targetB: {job.targetB}");
                                successfulReservations.Add(job.targetB);
                        }
                        else
                        {
                                Log.Message($"{pawn} FAILED to reserve targetB: {job.targetB}");
                                success = false;
                        }
                }

                if (!success)
                {
                        Log.Message($"{pawn} reservation FAILED, releasing {successfulReservations.Count} successful reservations");
                        foreach (var target in successfulReservations)
                        {
                                pawn.Map.reservationManager.Release(target, pawn, job);
                        }
                        PUAHReservationSystem.ReleaseAllReservationsForJob(pawn, job, pawn.Map);
                }
                else
                {
                        Log.Message($"{pawn} ALL storage reservations SUCCESSFUL (items will be reserved when picked up)");
                }

                return success;
        }

	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		var takenToInventory = pawn.TryGetComp<CompHauledToInventory>();
		
		Log.Message($"{pawn} starting HaulToInventory job with {job.targetQueueA?.Count ?? 0} items in queue");

		var wait = Toils_General.Wait(2);

		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A); //also does count
		yield return nextTarget;

		yield return CheckForOverencumberedForCombatExtended();

		var gotoThing = new Toil
		{
			initAction = () => 
			{
				var currentTarget = TargetThingA;
				Log.Message($"{pawn} starting path to {currentTarget}");
				
				// Reserve the current target item just before going to pick it up
				if (!pawn.Reserve(currentTarget, job, 1, -1, null, false))
				{
					Log.Message($"{pawn} FAILED to reserve {currentTarget}, ending job");
					EndJobWith(JobCondition.Incompletable);
					return;
				}
				
				Log.Message($"{pawn} successfully reserved {currentTarget}, starting path");
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
				var actor = pawn;
				var thing = actor.CurJob.GetTarget(TargetIndex.A).Thing;
				
				Log.Message($"{actor} attempting to pick up {thing}");

				// no more job was 0 errors
				if (job.count <= 0)
				{
					Log.Warning($"Job count was {job.count}, setting to 1 for thing {thing}");
					job.count = 1;
				}

				Toils_Haul.ErrorCheckForCarry(actor, thing);

				//get max we can pick up
				var countToPickUp = Mathf.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(actor, thing));
				Log.Message($"{actor} calculated countToPickUp: {countToPickUp} (job.count: {job.count}, maxUntilOverEncumbered: {MassUtility.CountToPickUpUntilOverEncumbered(actor, thing)})");

				if (ModCompatibilityCheck.CombatExtendedIsActive)
				{
					var ceCount = CompatHelper.CanFitInInventory(pawn, thing);
					Log.Message($"Combat Extended active, CanFitInInventory returned: {ceCount}");
					countToPickUp = ceCount;
				}

				if (countToPickUp > 0)
				{
					Log.Message($"{actor} will pick up {countToPickUp} of {thing}");
					var splitThing = thing.SplitOff(countToPickUp);
					var shouldMerge = takenToInventory.GetHashSet().Any(x => x.def == thing.def);
					actor.inventory.GetDirectlyHeldThings().TryAdd(splitThing, shouldMerge);
					takenToInventory.RegisterHauledItem(splitThing);

					if (ModCompatibilityCheck.CombatExtendedIsActive)
					{
						CompatHelper.UpdateInventory(pawn);
					}
				}
				else
				{
					// If we can't pick up anything, end the job to prevent getting stuck
					Log.Message($"{actor} cannot carry any {thing} (countToPickUp: {countToPickUp}). Ending job.");
					EndJobWith(JobCondition.Incompletable);
					return;
				}

				//thing still remains, so queue up hauling if we can + end the current job (smooth/instant transition)
				//This will technically release the reservations in the queue, but what can you do
				if (thing.Spawned)
				{
					Log.Message($"{thing} still spawned, creating HaulToStorageJob for remaining items");
					var haul = HaulAIUtility.HaulToStorageJob(actor, thing, job.playerForced);
					if (haul?.TryMakePreToilReservations(actor, false) ?? false)
					{
						actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);
						Log.Message($"Enqueued HaulToStorageJob for {actor}");
					}
					else
					{
						Log.Message($"Failed to make reservations for HaulToStorageJob for {actor}");
					}
					actor.jobs.curDriver.JumpToToil(wait);
				}
				else
				{
					Log.Message($"{thing} no longer spawned, continuing to next target");
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
				Log.Message($"{pawn} looking for more haulable items");
				var haulables = TempListForThings;
				haulables.Clear();
				haulables.AddRange(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()
					.Where(t => t != null && t.Spawned && !t.Destroyed)); // Filter out null, unspawned, or destroyed things
				Log.Message($"Found {haulables.Count} potential haulable items");
				var haulMoreWork = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(wg => wg.Worker is WorkGiver_HaulToInventory).Worker as WorkGiver_HaulToInventory;
				Job haulMoreJob = null;
				var haulMoreThing = WorkGiver_HaulToInventory.GetClosestAndRemove(pawn.Position, pawn.Map, haulables, PathEndMode.ClosestTouch,
					   TraverseParms.For(pawn), 12, t => (haulMoreJob = haulMoreWork.JobOnThing(pawn, t)) != null);
				//WorkGiver_HaulToInventory found more work nearby
				if (haulMoreThing != null)
				{
					Log.Message($"Found more work: {haulMoreThing}, enqueueing job");
					pawn.jobs.jobQueue.EnqueueFirst(haulMoreJob, JobTag.Misc);
				}
				else
				{
					Log.Message($"No more work found for {pawn}");
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

	private static List<Thing> TempListForThings { get; } = [];

	/// <summary>
	/// the workgiver checks for encumbered, this is purely extra for CE
	/// </summary>
	/// <returns></returns>
	public Toil CheckForOverencumberedForCombatExtended()
	{
		var toil = new Toil();

		if (!ModCompatibilityCheck.CombatExtendedIsActive)
		{
			Log.Message($"Combat Extended not active for {pawn}");
			return toil;
		}

		toil.initAction = () =>
		{
			var actor = toil.actor;
			var curJob = actor.jobs.curJob;
			var nextThing = curJob.targetA.Thing;

			Log.Message($"{actor} checking encumbrance for {nextThing}");
			var ceOverweight = CompatHelper.CeOverweight(pawn);
			var encumbrancePercent = MassUtility.EncumbrancePercent(actor);
			Log.Message($"{actor} encumbrance: {encumbrancePercent:F2}, ceOverweight: {ceOverweight}");

			if (!(MassUtility.EncumbrancePercent(actor) <= 0.9f && !ceOverweight))
			{
				Log.Message($"{actor} is overencumbered, switching to HaulToStorageJob");
				var haul = HaulAIUtility.HaulToStorageJob(actor, nextThing, job.playerForced);
				if (haul?.TryMakePreToilReservations(actor, false) ?? false)
				{
					//note that HaulToStorageJob etc doesn't do opportunistic duplicate hauling for items in valid storage. REEEE
					actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);
					EndJobWith(JobCondition.Succeeded);
				}
				else
				{
					Log.Message($"Failed to make reservations for HaulToStorageJob for {actor}");
				}
			}
			else
			{
				Log.Message($"{actor} encumbrance OK, continuing with HaulToInventory");
			}
		};

		return toil;
	}
}