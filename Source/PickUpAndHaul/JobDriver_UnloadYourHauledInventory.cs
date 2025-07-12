using System.Linq;
using UnityEngine;

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
		// Check if save operation is in progress at the start
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"[PickUpAndHaul] Ending UnloadYourHauledInventory job during save operation for {pawn}");
			EndJobWith(JobCondition.InterruptForced);
			PerformanceProfiler.EndTimer("MakeNewToils");
			yield break;
		}

		if (ModCompatibilityCheck.ExtendedStorageIsActive)
		{
			_unloadDuration = 20;
		}

                var begin = Toils_General.Wait(_unloadDuration);
                yield return begin;

                var carriedThings = pawn.TryGetComp<CompHauledToInventory>().GetHashSet();
                yield return FindTargetOrDrop(carriedThings);
                yield return PullItemFromInventory(carriedThings, begin);

                var releaseReservation = ReleaseReservation();
                var carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.B);

                // Equivalent to if (TargetB.HasThing)
                yield return Toils_Jump.JumpIf(carryToCell, TargetIsCell);

                var carryToContainer = Toils_Haul.CarryHauledThingToContainer();
                var adjustDropCountContainer = AdjustDropCount(begin);
                yield return carryToContainer;
                yield return adjustDropCountContainer;
                yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.None);
                yield return Toils_Haul.JumpToCarryToNextContainerIfPossible(carryToContainer, TargetIndex.B);
                // Equivalent to jumping out of the else block
                yield return Toils_Jump.Jump(releaseReservation);

                // Equivalent to else
                var adjustDropCountCell = AdjustDropCount(begin);
                yield return carryToCell;
                yield return adjustDropCountCell;
                yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true);

                //If the original cell is full, PlaceHauledThingInCell will set a different TargetIndex resulting in errors on yield return Toils_Reserve.Release.
                //We still gotta release though, mostly because of Extended Storage.
                yield return releaseReservation;
                yield return Toils_Jump.Jump(begin);
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

        /// <summary>
		/// After the pawn has picked the item up, decide how many to drop
		/// and deal gracefully with “no capacity” cases.
		/// </summary>
		private void AdjustDropCount(Toil jumpTo)
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
