using System.Linq;

namespace PickUpAndHaul;

public class JobDriver_HaulToInventory : JobDriver
{
	public override bool TryMakePreToilReservations(bool errorOnFailed)
	{
		// Silence the usual reservation-spam by disabling error logging on failed reserves.
		// We still *try* to reserve everything we plan to touch, we just don't let Verse.Log.Error fire
		// when another pawn already owns the reservation.
		bool success = true;
		var successfulReservations = new List<LocalTargetInfo>();
		Log.Message($"Begin TryMakePreToilReservations; A={job.targetQueueA?.Count ?? 0}, B={job.targetQueueB?.Count ?? 0}, targetBValid={job.targetB.IsValid}", pawn);

		// Reserve all targets in targetQueueA
		if (job.targetQueueA != null)
		{
			foreach (var target in job.targetQueueA)
			{
				Log.Message($"Reserve A: {target}", pawn);
				if (pawn.Reserve(target, job, 1, -1, null, false))
				{
					successfulReservations.Add(target);
					Log.Message($"Reserved A OK: {target}", pawn);
				}
				else
				{
					success = false;
					Log.Message($"Reserve A FAILED: {target}", pawn);
					break; // Stop trying to reserve more targets
				}
			}
		}

		// Only continue if all targetQueueA reservations succeeded
		if (success && job.targetQueueB != null)
		{
			foreach (var target in job.targetQueueB)
			{
				Log.Message($"Reserve B: {target}", pawn);
				if (pawn.Reserve(target, job, 1, -1, null, false))
				{
					successfulReservations.Add(target);
					Log.Message($"Reserved B OK: {target}", pawn);
				}
				else
				{
					success = false;
					Log.Message($"Reserve B FAILED: {target}", pawn);
					break; // Stop trying to reserve more targets
				}
			}
		}

		// Only try to reserve targetB if all previous reservations succeeded
		if (success && job.targetB != null)
		{
			Log.Message($"Reserve single targetB: {job.targetB}", pawn);
			if (pawn.Reserve(job.targetB, job, 1, -1, null, false))
			{
				successfulReservations.Add(job.targetB);
				Log.Message($"Reserved targetB OK: {job.targetB}", pawn);
			}
			else
			{
				success = false;
				Log.Message($"Reserve targetB FAILED: {job.targetB}", pawn);
			}
		}

		// If any reservation failed, release all successful reservations to prevent leaks
		if (!success)
		{
			Log.Message($"Releasing {successfulReservations.Count} successful reservations due to earlier failure", pawn);
			foreach (var target in successfulReservations)
			{
				pawn.Map.reservationManager.Release(target, pawn, job);
			}
		}

		Log.Message($"End TryMakePreToilReservations success={success}", pawn);
		return success;
	}

	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		var takenToInventory = pawn.TryGetComp<CompHauledToInventory>();

		var wait = Toils_General.Wait(2);

		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A); //also does count
		yield return nextTarget;

		yield return CheckForOverencumberedForCombatExtended();

		var gotoThing = new Toil
		{
			initAction = () => { Log.Message($"Goto A start: {TargetThingA}", pawn); pawn.pather.StartPath(TargetThingA, PathEndMode.ClosestTouch); },
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

				// Guard against invalid or missing target/count
				if (thing == null)
				{
					Log.Warning($"HaulToInventory: TargetIndex.A Thing was null for {actor}; aborting job.", actor);
					EndJobWith(JobCondition.Incompletable);
					return;
				}
				if (job.count <= 0)
				{
					Log.Warning($"HaulToInventory: Invalid job.count={job.count} for {actor} on {thing}; aborting job.", actor);
					EndJobWith(JobCondition.Incompletable);
					return;
				}

				Toils_Haul.ErrorCheckForCarry(actor, thing);

				//get max we can pick up
				var countToPickUp = Mathf.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(actor, thing));
				Log.Message($"{actor} is hauling to inventory {thing}:{countToPickUp}", actor);

				if (ModCompatibilityCheck.CombatExtendedIsActive)
				{
					countToPickUp = CompatHelper.CanFitInInventory(pawn, thing);
				}

				// Clamp by actual storage availability to avoid picking more than can be stored
				try
				{
					var map = actor.Map;
					var fac = actor.Faction;
					var priority = StoreUtility.CurrentStoragePriorityOf(thing);

					// Take a stable snapshot of candidate locations to avoid race issues during iteration
					var candidateLocations = PartialReservationSystem.PRSReservationSystem
						.GetAllValidStorageLocations(thing, actor, map, priority, fac)
						.ToList();

					int totalAvailable = 0;
					int checkedLocations = 0;
					foreach (var loc in candidateLocations)
					{
						var cap = PartialReservationSystem.PRSReservationSystem.GetAvailableCapacity(loc, thing, map);
						totalAvailable += cap;
						checkedLocations++;
						if (totalAvailable >= countToPickUp) break;
					}
					Log.Message($"Pickup clamp scan: locationsChecked={checkedLocations}, totalAvailable={totalAvailable}, planned={countToPickUp}", actor);
					if (totalAvailable < countToPickUp)
					{
						Log.Message($"Clamping pickup for {thing} to available storage: {totalAvailable} (was {countToPickUp})", actor);
						countToPickUp = Math.Max(0, totalAvailable);
					}

					// Pre-reserve the exact amount we will pick up so fallback jobs won't consume it
					var toReserve = countToPickUp;
					foreach (var loc in candidateLocations)
					{
						if (toReserve <= 0) break;
						var cap = PartialReservationSystem.PRSReservationSystem.GetAvailableCapacity(loc, thing, map);
						if (cap <= 0) continue;
						var reserveCount = Math.Min(toReserve, cap);
						if (reserveCount > 0 && PartialReservationSystem.PRSReservationSystem.TryReservePartialStorage(actor, thing, reserveCount, loc, job, map))
						{
							Log.Message($"PreReserve at pickup: {reserveCount}x{thing.def.defName} at {(loc.IsContainer ? loc.Container.ToString() : loc.Cell.ToString())}", actor);
							toReserve -= reserveCount;
						}
					}
					if (toReserve > 0)
					{
						// Could not lock all; reduce pickup further to what we actually locked in
						var reservedOk = countToPickUp - toReserve;
						Log.Message($"PreReserve shortfall: reducing pickup from {countToPickUp} to {reservedOk}", actor);
						countToPickUp = Math.Max(0, reservedOk);
					}
				}
				catch (System.Exception ex)
				{
					// Fail safe: do NOT pick up anything if we couldn't confidently pre-reserve capacity
					Log.Warning($"Pickup clamp/reserve failed for {thing}: {ex.Message}; preventing zero-count loops and over-pickup by clamping to 0.", actor);
					countToPickUp = 0;
				}

				// If nothing can be picked up, avoid zero-count pickups and pointless unload jobs
				if (countToPickUp <= 0)
				{
					Log.Message($"No reservable storage for {thing}; skipping pickup.", actor);
					if (thing.Spawned)
					{
						var haul = HaulAIUtility.HaulToStorageJob(actor, thing, job.playerForced);
						if (haul?.TryMakePreToilReservations(actor, false) ?? false)
						{
							actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);
							Log.Message($"Enqueued fallback HaulToStorageJob for {thing} (no pickup).", actor);
						}
					}
					EndJobWith(JobCondition.Succeeded);
					return;
				}

				if (countToPickUp > 0)
				{
					var splitThing = thing.SplitOff(countToPickUp);
					var shouldMerge = takenToInventory.GetHashSet().Any(x => x.def == thing.def);
					actor.inventory.GetDirectlyHeldThings().TryAdd(splitThing, shouldMerge);
					takenToInventory.RegisterHauledItem(splitThing);
					Log.Message($"Added to inventory: {splitThing} now inventoryCount={actor.inventory.innerContainer.Count}", actor);

					if (ModCompatibilityCheck.CombatExtendedIsActive)
					{
						CompatHelper.UpdateInventory(pawn);
					}
				}

				//thing still remains, so queue up hauling if we can + end the current job (smooth/instant transition)
				//This will technically release the reservations in the queue, but what can you do
				if (thing.Spawned)
				{
					var haul = HaulAIUtility.HaulToStorageJob(actor, thing, job.playerForced);
					if (haul?.TryMakePreToilReservations(actor, false) ?? false)
					{
						actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);
						Log.Message($"Enqueued fallback HaulToStorageJob for remaining {thing}", actor);
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
				var haulables = TempListForThings;
				haulables.Clear();
				haulables.AddRange(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()
					.Where(t => t != null && t.Spawned && !t.Destroyed)); // Filter out null, unspawned, or destroyed things
				var haulMoreWork = DefDatabase<WorkGiverDef>.AllDefsListForReading.First(wg => wg.Worker is WorkGiver_HaulToInventory).Worker as WorkGiver_HaulToInventory;
				Job haulMoreJob = null;
				var haulMoreThing = WorkGiver_HaulToInventory.GetClosestAndRemove(pawn.Position, pawn.Map, haulables, PathEndMode.ClosestTouch,
					   TraverseParms.For(pawn), 12, t => (haulMoreJob = haulMoreWork.JobOnThing(pawn, t)) != null);

				//WorkGiver_HaulToInventory found more work nearby
				if (haulMoreThing != null)
				{
					Log.Message($"{pawn} hauling again : {haulMoreThing}", pawn);
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
				var actor = pawn;
				var curJob = actor.jobs.curJob;
				var storeCell = curJob.targetB;

				var unloadJob = JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, storeCell);
				// Pre-reserve PRS capacity for everything we plan to unload with this unload job
				try
				{
					var carriedSet = actor.TryGetComp<CompHauledToInventory>()?.GetHashSet();
					if (carriedSet != null && carriedSet.Count > 0)
					{
						var map = actor.Map;
						var fac = actor.Faction;
						foreach (var invThing in carriedSet.ToList())
						{
							if (invThing == null || invThing.Destroyed) continue;
							int remaining = invThing.stackCount;
							var currentPriority = StoragePriority.Unstored;
							foreach (var loc in PartialReservationSystem.PRSReservationSystem.GetAllValidStorageLocations(invThing, actor, map, currentPriority, fac))
							{
								if (remaining <= 0) break;
								var cap = PartialReservationSystem.PRSReservationSystem.GetAvailableCapacity(loc, invThing, map);
								if (cap <= 0) continue;
								var reserveCount = Math.Min(remaining, cap);
								if (reserveCount > 0 && PartialReservationSystem.PRSReservationSystem.TryReservePartialStorage(actor, invThing, reserveCount, loc, unloadJob, map))
								{
									remaining -= reserveCount;
									Log.Message($"Pre-reserved unload: {reserveCount}x{invThing.def.defName} at {(loc.IsContainer ? loc.Container.ToString() : loc.Cell.ToString())} remaining={remaining}", actor);
									PickUpAndHaul.Log.Message($"PreReserveUnload: thing={invThing} reserve={reserveCount} at {(loc.IsContainer ? loc.Container.ToString() : loc.Cell.ToString())} rem={remaining}", actor);
								}
							}
						}
					}
				}
				catch (System.Exception ex)
				{
					Log.Warning($"Pre-reserve for unload failed: {ex.Message}", actor);
				}
				// If there is nothing flagged as hauled-to-inventory, do not enqueue an unload job
				var carriedSetNow = actor.TryGetComp<CompHauledToInventory>()?.GetHashSet();
				if (carriedSetNow == null || carriedSetNow.Count == 0)
				{
					Log.Message($"No hauled inventory to unload; finishing job without creating unload.", actor);
					EndJobWith(JobCondition.Succeeded);
					return;
				}

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
			return toil;
		}

		toil.initAction = () =>
		{
			var actor = toil.actor;
			var curJob = actor.jobs.curJob;
			var nextThing = curJob.targetA.Thing;

			var ceOverweight = CompatHelper.CeOverweight(pawn);

			if (!(MassUtility.EncumbrancePercent(actor) <= 0.9f && !ceOverweight))
			{
				var haul = HaulAIUtility.HaulToStorageJob(actor, nextThing, job.playerForced);
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