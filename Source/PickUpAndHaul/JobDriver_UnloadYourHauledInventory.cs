using System.Linq;

namespace PickUpAndHaul;

public class JobDriver_UnloadYourHauledInventory : JobDriver
{
	private int _countToDrop = -1;
	private int _unloadDuration = 3;

	public override void ExposeData()
	{
		PerformanceProfiler.StartTimer("ExposeData");
		// Don't save any data for this job driver to prevent save corruption
		// when the mod is removed
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			Log.Message("[PickUpAndHaul] Skipping save data for UnloadYourHauledInventory job driver");
			PerformanceProfiler.EndTimer("ExposeData");
			return;
		}
		
		// Only load data if we're in loading mode and the mod is active
		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			Log.Message("[PickUpAndHaul] Skipping load data for UnloadYourHauledInventory job driver");
			PerformanceProfiler.EndTimer("ExposeData");
			return;
		}
		
		// Only expose data if we're in a different mode (like copying)
		base.ExposeData();
		Scribe_Values.Look<int>(ref _countToDrop, "countToDrop", -1);
		PerformanceProfiler.EndTimer("ExposeData");
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed) 
	{
		PerformanceProfiler.StartTimer("TryMakePreToilReservations");
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"[PickUpAndHaul] Skipping UnloadYourHauledInventory job reservations during save operation for {pawn}");
			PerformanceProfiler.EndTimer("TryMakePreToilReservations");
			return false;
		}
		PerformanceProfiler.EndTimer("TryMakePreToilReservations");
		return true;
	}

	/// <summary>
	/// Find spot, reserve spot, pull thing out of inventory, go to spot, drop stuff, repeat.
	/// </summary>
	/// <returns></returns>
	public override IEnumerable<Toil> MakeNewToils()
	{
		PerformanceProfiler.StartTimer("MakeNewToils");

		/* ───────────────────────── early-out on save ───────────────────────── */
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"[PickUpAndHaul] Ending HaulToInventory during save for {pawn}");
			EndJobWith(JobCondition.InterruptForced);
			PerformanceProfiler.EndTimer("MakeNewToils");
			yield break;
		}

		var carriedComp   = pawn.TryGetComp<CompHauledInventory>();
		var waitShort     = Toils_General.Wait(2);

		/* pull first thing/count from the queued lists */
		var nextTarget    = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A);
		yield return nextTarget;

		/* CE overweight check (unchanged) */
		yield return CheckForOverencumberedForCombatExtended();

		/* ───────────────────────── go to the thing ─────────────────────────── */
		var gotoThing = new Toil
		{
			initAction = () =>
			{
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

		/* ─────────────────── take (split & add to inventory) ────────────────── */
		var takeThing = new Toil
		{
			initAction = () =>
			{
				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					EndJobWith(JobCondition.InterruptForced);
					return;
				}

				var actor = pawn;
				var thing = actor.CurJob.GetTarget(TargetIndex.A).Thing;

				/* NEW: guarantee a positive count for ErrorCheckForCarry */
				if (actor.CurJob.count <= 0 || actor.CurJob.count > thing.stackCount)
					actor.CurJob.count = Math.Min(thing.stackCount, 1);

				Toils_Haul.ErrorCheckForCarry(actor, thing);

				/* work out how many we can really pick up */
				int pickUp = Mathf.Min(
					actor.CurJob.count,
					MassUtility.CountToPickUpUntilOverEncumbered(actor, thing));

				if (ModCompatibilityCheck.CombatExtendedIsActive)
					pickUp = CompatHelper.CanFitInInventory(actor, thing);

				if (pickUp > 0)
				{
					Thing split = thing.SplitOff(pickUp);
					bool merge  = carriedComp.GetHashSet().Any(x => x.def == thing.def);

					actor.inventory.GetDirectlyHeldThings().TryAdd(split, merge);
					carriedComp.RegisterHauledItem(split);

					if (ModCompatibilityCheck.CombatExtendedIsActive)
						CompatHelper.UpdateInventory(actor);
				}

				/* any remainder on the ground? queue a haul job for it */
				if (thing.Spawned)
				{
					Job haul = HaulAIUtility.HaulToStorageJob(actor, thing, false);
					if (haul?.TryMakePreToilReservations(actor, false) ?? false)
						actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);

					actor.jobs.curDriver.JumpToToil(waitShort);
				}
			}
		};
		yield return takeThing;

		/* if A-queue still has entries, loop */
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());

		/* ─────────── rest of original method (return, unload, etc.) ─────────── */
		yield return TargetB.HasThing
			? Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
			: Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.ClosestTouch);

		yield return new Toil
		{
			initAction = () =>
			{
				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					EndJobWith(JobCondition.InterruptForced);
					return;
				}

				var storeCell = pawn.jobs.curJob.targetB;
				var unloadJob = JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, storeCell);

				if (unloadJob.TryMakePreToilReservations(pawn, false))
				{
					pawn.jobs.jobQueue.EnqueueFirst(unloadJob, JobTag.Misc);
					EndJobWith(JobCondition.Succeeded);
				}
			}
		};

		yield return waitShort;
		PerformanceProfiler.EndTimer("MakeNewToils");
	}


	private bool TargetIsCell() => !TargetB.HasThing;

	private Toil ReleaseReservation()
	{
		return new()
		{
			initAction = () =>
			{
				// Check for save operation before releasing reservation
				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					return;
				}

				if (pawn.Map.reservationManager.ReservedBy(job.targetB, pawn, pawn.CurJob)
				    && !ModCompatibilityCheck.HCSKIsActive)
				{
					pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);
				}
			}
		};
	}

	private Toil PullItemFromInventory(HashSet<Thing> carriedThings, Toil wait)
	{
		return new()
		{
			initAction = () =>
			{
				PerformanceProfiler.StartTimer("PullItemFromInventory");

				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					EndJobWith(JobCondition.InterruptForced);
					PerformanceProfiler.EndTimer("PullItemFromInventory");
					return;
				}

				var thing = job.GetTarget(TargetIndex.A).Thing;
				if (thing == null || !pawn.inventory.innerContainer.Contains(thing))
				{
					carriedThings.Remove(thing);
					pawn.jobs.curDriver.JumpToToil(wait);
					PerformanceProfiler.EndTimer("PullItemFromInventory");
					return;
				}

				/* ---------- clamp _countToDrop ---------- */
				if (_countToDrop <= 0 || _countToDrop > thing.stackCount)
					_countToDrop = thing.stackCount;

				var destCell = TargetB.HasThing ? job.targetB.Thing.Position : job.targetB.Cell;
				if (destCell.IsValid &&
					HoldMultipleThings_Support.CapacityAt(thing, destCell, pawn.Map, out var cap))
				{
					if (cap <= 0)
					{
						pawn.inventory.innerContainer.TryDrop(
							thing, ThingPlaceMode.Near, _countToDrop, out var dropped);
						dropped?.SetForbidden(false, false);

						if (pawn.Map.reservationManager.ReservedBy(pawn, job.targetB))
							pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);

						EndJobWith(JobCondition.Succeeded);
						/* stack fully dropped → remove from tracking */
						carriedThings.Remove(thing);
						PerformanceProfiler.EndTimer("PullItemFromInventory");
						return;
					}

					_countToDrop = Math.Min(_countToDrop, cap);
				}

				/* ---------- incapable pawn path ---------- */
				if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) ||
					!thing.def.EverStorable(false))
				{
					pawn.inventory.innerContainer.TryDrop(
						thing, ThingPlaceMode.Near, _countToDrop, out var dropped);
					dropped?.SetForbidden(false, false);

					if (pawn.Map.reservationManager.ReservedBy(pawn, job.targetB))
						pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);

					EndJobWith(JobCondition.Succeeded);
					carriedThings.Remove(thing);
					PerformanceProfiler.EndTimer("PullItemFromInventory");
					return;
				}

				/* ---------- normal transfer ---------- */
				pawn.inventory.innerContainer.TryTransferToContainer(
					thing, pawn.carryTracker.innerContainer, _countToDrop, out var carried);

				if (carried == null)   // transfer failed → drop & release
				{
					pawn.inventory.innerContainer.TryDrop(
						thing, ThingPlaceMode.Near, _countToDrop, out carried);
					carried?.SetForbidden(false, false);

					if (pawn.Map.reservationManager.ReservedBy(pawn, job.targetB))
						pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);

					EndJobWith(JobCondition.Succeeded);
					/* stack may be partial – only remove if empty */
					if (thing.DestroyedOrNull() || thing.stackCount == 0)
						carriedThings.Remove(thing);

					PerformanceProfiler.EndTimer("PullItemFromInventory");
					return;
				}

				/* success – keep tracking if stack still exists */
				if (thing.DestroyedOrNull() || thing.stackCount == 0)
					carriedThings.Remove(thing);

				job.count = _countToDrop;
				job.SetTarget(TargetIndex.A, carried);
				carried.SetForbidden(false, false);

				if (ModCompatibilityCheck.CombatExtendedIsActive)
					CompatHelper.UpdateInventory(pawn);

				PerformanceProfiler.EndTimer("PullItemFromInventory");
			}
		};
	}



	private Toil FindTargetOrDrop(HashSet<Thing> carriedThings)
	{
		return new()
		{
			initAction = () =>
			{
				PerformanceProfiler.StartTimer("FindTargetOrDrop");

				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					EndJobWith(JobCondition.InterruptForced);
					PerformanceProfiler.EndTimer("FindTargetOrDrop");
					return;
				}

				var unloadable = FirstUnloadableThing(pawn, carriedThings);
				if (unloadable.Count == 0)
				{
					if (carriedThings.Count == 0)
						EndJobWith(JobCondition.Succeeded);
					PerformanceProfiler.EndTimer("FindTargetOrDrop");
					return;
				}

				if (!StoreUtility.TryFindBestBetterStorageFor(
						unloadable.Thing, pawn, pawn.Map, StoragePriority.Unstored, pawn.Faction,
						out var cell, out var dest))
				{
					/* no storage at all → drop now */
					pawn.inventory.innerContainer.TryDrop(
						unloadable.Thing, ThingPlaceMode.Near,
						unloadable.Thing.stackCount, out var dropped);
					dropped?.SetForbidden(false, false);
					carriedThings.Remove(unloadable.Thing);
					EndJobWith(JobCondition.Succeeded);
					PerformanceProfiler.EndTimer("FindTargetOrDrop");
					return;
				}

				/* choose targetB */
				var targetB = cell == IntVec3.Invalid ? (LocalTargetInfo)dest : cell;
				job.SetTarget(TargetIndex.A, unloadable.Thing);
				job.SetTarget(TargetIndex.B, targetB);

				/* capacity probe */
				var probeCell = cell == IntVec3.Invalid
									? (dest as Thing)?.Position ?? IntVec3.Invalid
									: cell;

				int capacity = unloadable.Thing.stackCount;
				bool skipReservation = false;

				if (probeCell.IsValid &&
					HoldMultipleThings_Support.CapacityAt(
						unloadable.Thing, probeCell, pawn.Map, out var cap))
				{
					capacity = cap;
					skipReservation = true;            // crate manages its own reservations
				}

				/* -------- handle full / zero-capacity destinations up-front -------- */
				if (capacity <= 0)
				{
					pawn.inventory.innerContainer.TryDrop(
						unloadable.Thing, ThingPlaceMode.Near,
						unloadable.Thing.stackCount, out var dropped);
					dropped?.SetForbidden(false, false);
					carriedThings.Remove(unloadable.Thing);
					EndJobWith(JobCondition.Succeeded);
					PerformanceProfiler.EndTimer("FindTargetOrDrop");
					return;
				}

				/* reserve if needed */
				if (!skipReservation &&
					!pawn.Map.reservationManager.Reserve(pawn, job, targetB))
				{
					pawn.inventory.innerContainer.TryDrop(
						unloadable.Thing, ThingPlaceMode.Near,
						unloadable.Thing.stackCount, out var dropped);
					dropped?.SetForbidden(false, false);
					carriedThings.Remove(unloadable.Thing);
					EndJobWith(JobCondition.Incompletable);
					PerformanceProfiler.EndTimer("FindTargetOrDrop");
					return;
				}

				_countToDrop = Math.Min(unloadable.Thing.stackCount, capacity);
				if (_countToDrop <= 0) _countToDrop = 1;

				PerformanceProfiler.EndTimer("FindTargetOrDrop");
			}
		};
	}




        private static ThingCount FirstUnloadableThing(Pawn pawn, HashSet<Thing> carriedThings)
        {
			PerformanceProfiler.StartTimer("FirstUnloadableThing");
                var innerPawnContainer = pawn.inventory.innerContainer;
                Thing best = null;

                foreach (var thing in carriedThings)
                {
                        // Handle stacks that changed IDs after being picked up
                        if (!innerPawnContainer.Contains(thing))
                        {
                                var stragglerDef = thing.def;
                                carriedThings.Remove(thing);

                                for (var i = 0; i < innerPawnContainer.Count; i++)
                                {
                                        var dirtyStraggler = innerPawnContainer[i];
                                        if (dirtyStraggler.def == stragglerDef)
                                        {
                                                PerformanceProfiler.EndTimer("FirstUnloadableThing");
                                                return new ThingCount(dirtyStraggler, dirtyStraggler.stackCount);
                                        }
                                }
                                continue;
                        }

                        if (best == null || CompareInventoryOrder(best, thing) > 0)
                        {
                                best = thing;
                        }
                }

                PerformanceProfiler.EndTimer("FirstUnloadableThing");
                return best != null ? new ThingCount(best, best.stackCount) : default;

                static int CompareInventoryOrder(Thing a, Thing b)
                {
                        var catA = a.def.FirstThingCategory?.index ?? int.MaxValue;
                        var catB = b.def.FirstThingCategory?.index ?? int.MaxValue;
                        var compare = catA.CompareTo(catB);
                        return compare != 0 ? compare : string.CompareOrdinal(a.def.defName, b.def.defName);
                }
        }
}
