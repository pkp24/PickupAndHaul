using System.Linq;

namespace PickUpAndHaul;

public class JobDriver_UnloadYourHauledInventory : JobDriver
{
        private int _countToDrop = -1;
        private int _unloadDuration = 3;
        private IntVec3 _lastDropCell = IntVec3.Invalid;

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

                _lastDropCell = IntVec3.Invalid;

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
                yield return carryToContainer;
                yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.None);
                yield return RememberDropCell();
                yield return Toils_Haul.JumpToCarryToNextContainerIfPossible(carryToContainer, TargetIndex.B);
                // Equivalent to jumping out of the else block
                yield return Toils_Jump.Jump(releaseReservation);

		// Equivalent to else
                yield return carryToCell;
                yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true);
                yield return RememberDropCell();

		//If the original cell is full, PlaceHauledThingInCell will set a different TargetIndex resulting in errors on yield return Toils_Reserve.Release.
		//We still gotta release though, mostly because of Extended Storage.
                yield return releaseReservation;
                yield return Toils_Jump.Jump(begin);
                PerformanceProfiler.EndTimer("MakeNewToils");
        }

        private Toil RememberDropCell()
        {
                return new()
                {
                        initAction = () =>
                        {
                                _lastDropCell = job.targetB.HasThing ? job.targetB.Thing.Position : job.targetB.Cell;
                        }
                };
        }

       private bool TargetIsCell() => !TargetB.HasThing;

       private const int MaxNearbySearchRadius = 10;

       private static bool TryFindNearbyBetterStoreCellFor(Thing thing, Pawn pawn, Map map,
                       StoragePriority currentPriority, Faction faction, IntVec3 near, out IntVec3 foundCell)
       {
               for (var radius = 1; radius <= MaxNearbySearchRadius; radius++)
               {
                       var start = GenRadial.NumCellsInRadius(radius - 1);
                       var end = GenRadial.NumCellsInRadius(radius);

                       for (var i = start; i < end; i++)
                       {
                               var cell = near + GenRadial.RadialPattern[i];
                               if (!cell.InBounds(map))
                               {
                                       continue;
                               }

                               var slotGroup = map.haulDestinationManager.SlotGroupAt(cell);
                               if (slotGroup == null || slotGroup.Settings.Priority <= currentPriority ||
                                       !slotGroup.parent.Accepts(thing))
                               {
                                       continue;
                               }

                               if (StoreUtility.IsGoodStoreCell(cell, map, thing, pawn, faction))
                               {
                                       foundCell = cell;
                                       return true;
                               }
                       }
               }

               foundCell = IntVec3.Invalid;
               return false;
       }

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
                                IntVec3 cell = IntVec3.Invalid;
                                IHaulDestination destination;

                                var foundCell = false;
                                if (_lastDropCell.IsValid)
                                {
                                        foundCell = TryFindNearbyBetterStoreCellFor(unloadableThing.Thing, pawn, pawn.Map,
                                                currentPriority, pawn.Faction, _lastDropCell, out cell);
                                }
                                if (!foundCell)
                                {
                                        foundCell = TryFindNearbyBetterStoreCellFor(unloadableThing.Thing, pawn, pawn.Map,
                                                currentPriority, pawn.Faction, pawn.Position, out cell);
                                }
                                if (foundCell)
                                {
                                        destination = null;
                                        job.SetTarget(TargetIndex.A, unloadableThing.Thing);
                                        job.SetTarget(TargetIndex.B, cell);

                                        Log.Message($"{pawn} found destination {job.targetB} for thing {unloadableThing.Thing}");
                                        if (!pawn.Map.reservationManager.Reserve(pawn, job, job.targetB))
                                        {
                                                Log.Message(
                                                        $"{pawn} failed reserving destination {job.targetB}, dropping {unloadableThing.Thing}");
                                                pawn.inventory.innerContainer.TryDrop(unloadableThing.Thing, ThingPlaceMode.Near,
                                                        unloadableThing.Thing.stackCount, out _);
                                                EndJobWith(JobCondition.Incompletable);
                                                PerformanceProfiler.EndTimer("FindTargetOrDrop");
                                                return;
                                        }
                                        _countToDrop = unloadableThing.Thing.stackCount;
                                        PerformanceProfiler.EndTimer("FindTargetOrDrop");
                                }
                                else if (StoreUtility.TryFindBestBetterStorageFor(unloadableThing.Thing, pawn, pawn.Map, currentPriority,
                                            pawn.Faction, out cell, out destination))
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
					if (!pawn.Map.reservationManager.Reserve(pawn, job, job.targetB))
					{
						Log.Message(
							$"{pawn} failed reserving destination {job.targetB}, dropping {unloadableThing.Thing}");
						pawn.inventory.innerContainer.TryDrop(unloadableThing.Thing, ThingPlaceMode.Near,
							unloadableThing.Thing.stackCount, out _);
						EndJobWith(JobCondition.Incompletable);
						PerformanceProfiler.EndTimer("FindTargetOrDrop");
						return;
					}
					_countToDrop = unloadableThing.Thing.stackCount;
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

                // Iterate over a copy since the set may be modified as we remove items
                foreach (var thing in carriedThings.ToList())
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
