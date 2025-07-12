using System.Linq;
using UnityEngine;

namespace PickUpAndHaul;

public class JobDriver_UnloadYourHauledInventory : JobDriver
{
	private int _countToDrop = -1;

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

		/* ----- Hard exit if a save is happening -------------------------------- */
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"[PickUpAndHaul] Ending HaulToInventory job during save operation for {pawn}");
			EndJobWith(JobCondition.InterruptForced);
			PerformanceProfiler.EndTimer("MakeNewToils");
			yield break;
		}

		var takenToInventory = pawn.TryGetComp<CompHauledToInventory>();          // existing helper
		var wait             = Toils_General.Wait(2);

		/* ----------------------------------------------------------------------- */
		/* 1. Pull next queue entry – this also sets job.count from countQueue     */
		/* ----------------------------------------------------------------------- */
		var extractNext = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A);
		yield return extractNext;

		/* Extra CE encumbrance guard (unchanged) */
		yield return CheckForOverencumberedForCombatExtended();

		/* ----------------------------------------------------------------------- */
		/* 2. Go to thing                                                          */
		/* ----------------------------------------------------------------------- */
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

		/* ----------------------------------------------------------------------- */
		/* 3. Pick it up (fixed count handling)                                    */
		/* ----------------------------------------------------------------------- */
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

				/* -------- determine a safe number to take ---------------------- */
				int requested       = actor.jobs.curJob.count <= 0 ? thing.stackCount
																: actor.jobs.curJob.count;
				int countToPickUp   = Mathf.Min(requested,
												MassUtility.CountToPickUpUntilOverEncumbered(actor, thing));

				if (ModCompatibilityCheck.CombatExtendedIsActive)
					countToPickUp = Mathf.Min(countToPickUp, CompatHelper.CanFitInInventory(pawn, thing));

				/* nothing at all fits – bail out gracefully */
				if (countToPickUp <= 0)
				{
					EndJobWith(JobCondition.Incompletable);
					return;
				}

				/* set official job count BEFORE Verse’s safety check */
				actor.jobs.curJob.count = countToPickUp;
				Toils_Haul.ErrorCheckForCarry(actor, thing);   // no more “count = 0” warnings

				Log.Message($"{actor} is hauling to inventory {thing}:{countToPickUp}");

				/* do the actual split & inventory merge */
				var splitThing  = thing.SplitOff(countToPickUp);
				bool shouldMerge = takenToInventory.GetHashSet().Any(x => x.def == thing.def);

				actor.inventory.GetDirectlyHeldThings().TryAdd(splitThing, shouldMerge);
				takenToInventory.RegisterHauledItem(splitThing);

				if (ModCompatibilityCheck.CombatExtendedIsActive)
					CompatHelper.UpdateInventory(pawn);

				/* if part of the stack remains on the ground, queue a normal haul */
				if (thing.Spawned)
				{
					var haul = HaulAIUtility.HaulToStorageJob(actor, thing, false);
					if (haul?.TryMakePreToilReservations(actor, false) ?? false)
						actor.jobs.jobQueue.EnqueueFirst(haul, JobTag.Misc);

					actor.jobs.curDriver.JumpToToil(wait);   // continue our own job smoothly
				}
			}
		};
		yield return takeThing;

		/* Loop back for more queued targets */
		yield return Toils_Jump.JumpIf(extractNext, () => !job.targetQueueA.NullOrEmpty());

		/* ----------------------------------------------------------------------- */
		/* 4. Opportunistic extra hauling (unchanged)                              */
		/* ----------------------------------------------------------------------- */
		yield return new Toil
		{
			initAction = () =>
			{
				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					EndJobWith(JobCondition.InterruptForced);
					return;
				}

				var haulables = TempListForThings;
				haulables.Clear();
				haulables.AddRange(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());

				var haulMoreWork = DefDatabase<WorkGiverDef>.AllDefsListForReading
								.First(wg => wg.Worker is WorkGiver_HaulToInventory)
								.Worker as WorkGiver_HaulToInventory;

				Job  haulMoreJob  = null;
				var  haulMoreThing = WorkGiver_HaulToInventory.GetClosestAndRemove(
										pawn.Position, pawn.Map, haulables,
										PathEndMode.ClosestTouch, TraverseParms.For(pawn), 12,
										t => (haulMoreJob = haulMoreWork.JobOnThing(pawn, t)) != null);

				if (haulMoreThing != null && haulMoreJob.TryMakePreToilReservations(pawn, false))
				{
					pawn.jobs.jobQueue.EnqueueFirst(haulMoreJob, JobTag.Misc);
					EndJobWith(JobCondition.Succeeded);
				}
			}
		};

		/* ----------------------------------------------------------------------- */
		/* 5. Walk to destination and enqueue unload-inventory job                 */
		/* ----------------------------------------------------------------------- */
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

				var unloadJob = JobMaker.MakeJob(
									PickUpAndHaulJobDefOf.UnloadYourHauledInventory,
									job.targetB);

				if (unloadJob.TryMakePreToilReservations(pawn, false))
				{
					pawn.jobs.jobQueue.EnqueueFirst(unloadJob, JobTag.Misc);
					EndJobWith(JobCondition.Succeeded);
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

        /// <summary>
		/// After the pawn has picked the item up, decide how many to drop
		/// and deal gracefully with “no capacity” cases.
		/// </summary>
		private Toil AdjustDropCount(Toil jumpTo)
		{
			return new()
			{
				initAction = () =>
				{
					PerformanceProfiler.StartTimer("AdjustDropCount");

					// Nothing carried – just bail out.
					if (pawn.carryTracker.CarriedThing is not Thing carried)
					{
						EndJobWith(JobCondition.Succeeded);
						PerformanceProfiler.EndTimer("AdjustDropCount");
						return;
					}

					/*------------------------------------------------------------
					* Re-calculate how many we can store in the target.  
					* We do it here once and use the result consistently.
					*-----------------------------------------------------------*/
					int capacity;
					if (TargetB.HasThing)                                            // unloading into a container
					{
						var owner = TargetB.Thing.TryGetInnerInteractableThingOwner();
						capacity = owner?.GetCountCanAccept(carried) ?? 0;
					}
					else                                                             // unloading into a cell
					{
						capacity = WorkGiver_HaulToInventory.CapacityAt(
									carried, TargetB.Cell, pawn.Map);
					}

					/*------------------------------------------------------------
					* If no room is left, drop the item and end cleanly – 
					* prevents the carryTracker ↔ inventory mismatch loop.
					*-----------------------------------------------------------*/
					if (capacity <= 0)
					{
						pawn.carryTracker.TryDropCarriedThing(
							pawn.Position,
							ThingPlaceMode.Near,
							out _);

						EndJobWith(JobCondition.Incompletable);
						PerformanceProfiler.EndTimer("AdjustDropCount");
						return;
					}

					/*------------------------------------------------------------
					* Otherwise limit the drop to the space we actually have.
					*-----------------------------------------------------------*/
					_countToDrop = Mathf.Min(carried.stackCount, capacity);
					job.count    = _countToDrop;

					PerformanceProfiler.EndTimer("AdjustDropCount");
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
				// Check for save operation before pulling item
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
				if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !thing.def.EverStorable(false))
				{
					Log.Message($"Pawn {pawn} incapable of hauling, dropping {thing}");
					pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, _countToDrop, out thing);
					EndJobWith(JobCondition.Succeeded);
					carriedThings.Remove(thing);
					PerformanceProfiler.EndTimer("PullItemFromInventory");
				}
				else
				{
					pawn.inventory.innerContainer.TryTransferToContainer(thing, pawn.carryTracker.innerContainer,
						_countToDrop, out thing);
					job.count = _countToDrop;
					job.SetTarget(TargetIndex.A, thing);
					carriedThings.Remove(thing);
					PerformanceProfiler.EndTimer("PullItemFromInventory");
				}

				if (ModCompatibilityCheck.CombatExtendedIsActive)
				{
					CompatHelper.UpdateInventory(pawn);
				}

				thing.SetForbidden(false, false);
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
				// Check for save operation before finding target
				if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
				{
					EndJobWith(JobCondition.InterruptForced);
					PerformanceProfiler.EndTimer("FindTargetOrDrop");
					return;
				}

				var unloadableThing = FirstUnloadableThing(pawn, carriedThings);

				if (unloadableThing.Count == 0)
				{
					if (carriedThings.Count == 0)
					{
						EndJobWith(JobCondition.Succeeded);
					}
					PerformanceProfiler.EndTimer("FindTargetOrDrop");
					return;
				}

				var currentPriority = StoragePriority.Unstored; // Currently in pawns inventory, so it's unstored
                                if (StoreUtility.TryFindBestBetterStorageFor(unloadableThing.Thing, pawn, pawn.Map, currentPriority,
                                            pawn.Faction, out var cell, out var destination))
                                {
                                        job.SetTarget(TargetIndex.A, unloadableThing.Thing);
                                        if (cell == IntVec3.Invalid)
                                        {
                                                job.SetTarget(TargetIndex.B, destination as Thing);
                                        }
                                        else
                                        {
                                                job.SetTarget(TargetIndex.B, cell);
                                        }

                                        Log.Message($"{pawn} found destination {job.targetB} for thing {unloadableThing.Thing}");
                                        var isContainer = cell == IntVec3.Invalid && destination is Thing;
                                        if (!isContainer && !pawn.Map.reservationManager.Reserve(pawn, job, job.targetB))
                                        {
                                                Log.Message(
                                                        $"{pawn} failed reserving destination {job.targetB}, dropping {unloadableThing.Thing}");
                                                pawn.inventory.innerContainer.TryDrop(unloadableThing.Thing, ThingPlaceMode.Near,
                                                        unloadableThing.Thing.stackCount, out _);
                                                EndJobWith(JobCondition.Incompletable);
                                                PerformanceProfiler.EndTimer("FindTargetOrDrop");
                                                return;
                                        }
                                        int capacity;
                                        if (cell == IntVec3.Invalid)
                                        {
                                                var owner = (destination as Thing)?.TryGetInnerInteractableThingOwner();
                                                capacity = owner?.GetCountCanAccept(unloadableThing.Thing) ?? 0;
                                        }
                                        else
                                        {
                                                capacity = WorkGiver_HaulToInventory.CapacityAt(unloadableThing.Thing, cell, pawn.Map);
                                        }

                                        if (capacity <= 0)
                                        {
                                                pawn.inventory.innerContainer.TryDrop(unloadableThing.Thing, ThingPlaceMode.Near,
                                                        unloadableThing.Thing.stackCount, out _);
                                                EndJobWith(JobCondition.Incompletable);
                                                PerformanceProfiler.EndTimer("FindTargetOrDrop");
                                                return;
                                        }

                                        _countToDrop = Mathf.Min(unloadableThing.Thing.stackCount, capacity);
                                        PerformanceProfiler.EndTimer("FindTargetOrDrop");
                                }
				else
				{
					Log.Message(
						$"Pawn {pawn} unable to find hauling destination, dropping {unloadableThing.Thing}");
					pawn.inventory.innerContainer.TryDrop(unloadableThing.Thing, ThingPlaceMode.Near,
						unloadableThing.Thing.stackCount, out _);
					EndJobWith(JobCondition.Succeeded);
					PerformanceProfiler.EndTimer("FindTargetOrDrop");
				}
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
