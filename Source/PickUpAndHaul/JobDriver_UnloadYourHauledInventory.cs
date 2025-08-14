using System.Linq;

namespace PickUpAndHaul;

public class JobDriver_UnloadYourHauledInventory : JobDriver
{
	private int _countToDrop = -1;
	private int _unloadDuration = 3;

	public override void ExposeData()
	{
		base.ExposeData();
		Scribe_Values.Look(ref _countToDrop, "countToDrop", -1);
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

	/// <summary>
	/// Find spot, reserve spot, pull thing out of inventory, go to spot, drop stuff, repeat.
	/// </summary>
	/// <returns></returns>
	public override IEnumerable<Toil> MakeNewToils()
	{
		// Mark pawn as unloading for the duration of this job
		pawn.TryGetComp<CompHauledToInventory>()?.SetUnloading(true);
		Log.Message($"Begin UnloadYourHauledInventory for {pawn}, items={pawn.TryGetComp<CompHauledToInventory>()?.GetHashSet()?.Count ?? 0}", pawn);

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
		yield return Toils_Haul.JumpToCarryToNextContainerIfPossible(carryToContainer, TargetIndex.B);
		// Equivalent to jumping out of the else block
		yield return Toils_Jump.Jump(releaseReservation);

		// Equivalent to else
		yield return carryToCell;
		yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true);

		//If the original cell is full, PlaceHauledThingInCell will set a different TargetIndex resulting in errors on yield return Toils_Reserve.Release.
		//We still gotta release though, mostly because of Extended Storage.
		yield return releaseReservation;
		yield return Toils_Jump.Jump(begin);
	}

	private void EndUnloadJob(JobCondition condition)
	{
		pawn.TryGetComp<CompHauledToInventory>()?.SetUnloading(false);
		EndJobWith(condition);
	}

	private bool TargetIsCell() => !TargetB.HasThing;

	private Toil ReleaseReservation() => new()
	{
		initAction = () =>
		{
			if (pawn.Map.reservationManager.ReservedBy(job.targetB, pawn, pawn.CurJob))
			{
				pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);
			}
		}
	};

	private Toil PullItemFromInventory(HashSet<Thing> carriedThings, Toil wait) => new()
	{
		initAction = () =>
		{
			var thing = job.GetTarget(TargetIndex.A).Thing;
			if (thing == null || !pawn.inventory.innerContainer.Contains(thing))
			{
				Log.Message($"Skip pull: not in inventory or null for A={job.GetTarget(TargetIndex.A)}", pawn);
				carriedThings.Remove(thing);
				pawn.jobs.curDriver.JumpToToil(wait);
				return;
			}
			if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !thing.def.EverStorable(false))
			{
				Log.Message($"Pawn {pawn} incapable of hauling, dropping {thing}", pawn, job, thing);
				pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, _countToDrop, out thing);
				EndUnloadJob(JobCondition.Succeeded);
				carriedThings.Remove(thing);
			}
			else
			{
				pawn.inventory.innerContainer.TryTransferToContainer(thing, pawn.carryTracker.innerContainer,
					_countToDrop, out thing);
				job.count = _countToDrop;

				// no more job was 0 errors
				if (job.count <= 0)
				{
					Log.Warning($"Unload job count was {job.count}, setting to 1 for thing {thing?.def?.defName ?? "null"}", pawn, job, thing);
					job.count = 1;
					_countToDrop = 1; // Fix Bug 1: Update _countToDrop to match job.count
				}

				job.SetTarget(TargetIndex.A, thing);
				// mark as recently unloaded so the WorkGiver won't immediately re-target this stack
				pawn.TryGetComp<CompHauledToInventory>()?.MarkRecentlyUnloaded(thing, 300);
				carriedThings.Remove(thing);
				Log.Message($"Pulled from inventory to carryTracker: {thing} count={job.count}", pawn, job, thing);
			}

			if (ModCompatibilityCheck.CombatExtendedIsActive)
			{
				CompatHelper.UpdateInventory(pawn);
			}

			thing.SetForbidden(false, false);
		}
	};

	private Toil FindTargetOrDrop(HashSet<Thing> carriedThings) => new()
	{
		initAction = () =>
		{
			var unloadableThing = FirstUnloadableThing(pawn, carriedThings);

			if (unloadableThing.Count == 0)
			{
				if (carriedThings.Count == 0)
				{
					EndJobWith(JobCondition.Succeeded);
				}
				return;
			}
			Log.Message($"FindTargetOrDrop: next unload={unloadableThing.Thing} count={unloadableThing.Count}", pawn, job, unloadableThing.Thing);

			// Currently in pawn's inventory, so it's unstored
			var currentPriority = StoragePriority.Unstored;
			job.SetTarget(TargetIndex.A, unloadableThing.Thing);
			var map = pawn.Map;
			var fac = pawn.Faction;
			var thing = unloadableThing.Thing;

			// Prefer a destination where this job already has a PRS reservation
			PartialReservationSystem.PRSReservationSystem.StorageLocation chosenLoc = default;
			int available = 0;
			int reservedByThisJob = 0;
			foreach (var loc in PartialReservationSystem.PRSReservationSystem.GetAllValidStorageLocations(thing, pawn, map, currentPriority, fac))
			{
				reservedByThisJob = PartialReservationSystem.PRSReservationSystem.GetReservedCountForPawnJob(pawn, job, loc, thing.def, map);
				if (reservedByThisJob > 0)
				{
					chosenLoc = loc;
					break;
				}
			}

			// If no reserved location, pick a best one by capacity/priority
			if (reservedByThisJob <= 0)
			{
				if (!PartialReservationSystem.PRSReservationSystem.TryFindBestStorageWithReservation(thing, pawn, map, currentPriority, fac, out chosenLoc, out available) || available <= 0)
				{
					foreach (var loc in PartialReservationSystem.PRSReservationSystem.GetAllValidStorageLocations(thing, pawn, map, currentPriority, fac))
					{
						var cap = PartialReservationSystem.PRSReservationSystem.GetAvailableCapacity(loc, thing, map);
						if (cap > 0) { chosenLoc = loc; available = cap; break; }
					}
				}
			}

			if (available > 0)
			{
				// Try reserving the destination; if not reservable, try alternates
				bool reserved = false;
				LocalTargetInfo destTarget;
				if (chosenLoc.IsContainer)
				{
					destTarget = chosenLoc.Container;
					reserved = map.reservationManager.Reserve(pawn, job, destTarget);
				}
				else
				{
					destTarget = chosenLoc.Cell;
					reserved = map.reservationManager.Reserve(pawn, job, destTarget);
				}
				Log.Message($"Try reserve unload dest {destTarget}: reserved={reserved}, available={available}, reservedByThisJob={reservedByThisJob}", pawn, job, thing);
				PickUpAndHaul.Log.Message($"Unload pick dest={destTarget} available={available} reservedByThisJob={reservedByThisJob}", pawn, job, thing);

				if (!reserved)
				{
					foreach (var loc in PartialReservationSystem.PRSReservationSystem.GetAllValidStorageLocations(thing, pawn, map, currentPriority, fac))
					{
						var cap = PartialReservationSystem.PRSReservationSystem.GetAvailableCapacity(loc, thing, map);
						if (cap <= 0) continue;
						var altTarget = loc.IsContainer ? (LocalTargetInfo)loc.Container : (LocalTargetInfo)loc.Cell;
						if (map.reservationManager.Reserve(pawn, job, altTarget))
						{
							chosenLoc = loc;
							available = cap;
							destTarget = altTarget;
							reserved = true;
							break;
						}
					}
				}

				if (reserved)
				{
					// If we already have a PRS reservation for this job at this location, use it; otherwise reserve now.
					var effectiveCapacity = reservedByThisJob > 0 ? reservedByThisJob : available;
					var countToUnload = Math.Min(thing.stackCount, effectiveCapacity);
					if (reservedByThisJob <= 0 && countToUnload > 0)
					{
						PartialReservationSystem.PRSReservationSystem.TryReservePartialStorage(pawn, thing, countToUnload, chosenLoc, job, map);
					}
					_countToDrop = countToUnload;
					job.SetTarget(TargetIndex.B, destTarget);
					Log.Message($"{pawn} found destination {job.targetB} for thing {thing} (count={_countToDrop})", pawn, job, thing);
					PickUpAndHaul.Log.Message($"Unload will place {countToUnload} at {destTarget}; thing={thing} remainingAfter={(thing.stackCount - countToUnload)}", pawn, job, thing);
				}
				else
				{
					Log.Message($"{pawn} failed reserving any destination for {thing}, dropping near", pawn, job, thing);
					pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, thing.stackCount, out _);
					EndUnloadJob(JobCondition.Incompletable);
					return;
				}
			}
			else
			{
				Log.Message($"Pawn {pawn} unable to find hauling destination, dropping {thing}", pawn, job, thing);
				pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, thing.stackCount, out _);
				EndUnloadJob(JobCondition.Succeeded);
			}
		}
	};

	private static ThingCount FirstUnloadableThing(Pawn pawn, HashSet<Thing> carriedThings)
	{
		var innerPawnContainer = pawn.inventory.innerContainer;

		foreach (var thing in carriedThings.OrderBy(t => t.def.FirstThingCategory?.index).ThenBy(x => x.def.defName))
		{
			//find the overlap.
			if (!innerPawnContainer.Contains(thing))
			{
				//merged partially picked up stacks get a different thingID in inventory
				var stragglerDef = thing.def;
				carriedThings.Remove(thing);

				//we have no method of grabbing the newly generated thingID. This is the solution to that.
				for (var i = 0; i < innerPawnContainer.Count; i++)
				{
					var dirtyStraggler = innerPawnContainer[i];
					if (dirtyStraggler.def == stragglerDef)
					{
						return new ThingCount(dirtyStraggler, dirtyStraggler.stackCount);
					}
				}
			}
			return new ThingCount(thing, thing.stackCount);
		}
		return default;
	}
}