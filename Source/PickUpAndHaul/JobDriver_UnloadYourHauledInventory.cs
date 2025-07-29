using System;
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
				carriedThings.Remove(thing);
				pawn.jobs.curDriver.JumpToToil(wait);
				return;
			}
			if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !thing.def.EverStorable(false))
			{
				Log.Message($"Pawn {pawn} incapable of hauling, dropping {thing}");
				pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near, _countToDrop, out thing);
				EndJobWith(JobCondition.Succeeded);
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
					Log.Warning($"Unload job count was {job.count}, setting to 1 for thing {thing?.def?.defName ?? "null"}");
					job.count = 1;
					_countToDrop = 1; // Fix Bug 1: Update _countToDrop to match job.count
				}

				job.SetTarget(TargetIndex.A, thing);
				carriedThings.Remove(thing);
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

			var currentPriority = StoragePriority.Unstored; // Currently in pawns inventory, so it's unstored
			
			// Use our new reservation system to find storage with available capacity
			PUAHReservationSystem.StorageLocation storageLocation;
			int availableCapacity;
			
			if (PUAHReservationSystem.TryFindBestStorageWithReservation(unloadableThing.Thing, pawn, pawn.Map, 
				currentPriority, pawn.Faction, out storageLocation, out availableCapacity))
			{
				job.SetTarget(TargetIndex.A, unloadableThing.Thing);
				
				// Set target based on storage type
				if (storageLocation.IsContainer)
				{
					job.SetTarget(TargetIndex.B, storageLocation.Container);
				}
				else
				{
					job.SetTarget(TargetIndex.B, storageLocation.Cell);
				}

				Log.Message($"{pawn} found destination {job.targetB} for thing {unloadableThing.Thing} with capacity {availableCapacity}");
				
				// Determine how much we can actually drop
				var countToReserve = Math.Min(unloadableThing.Thing.stackCount, availableCapacity);
				_countToDrop = countToReserve;
				
				// Try to reserve using our system
				bool reserved = PUAHReservationSystem.TryReservePartialStorage(pawn, unloadableThing.Thing, 
					countToReserve, storageLocation, job, pawn.Map);
				
				if (!reserved)
				{
					Log.Message($"{pawn} failed PUAH reservation for {job.targetB}, dropping {unloadableThing.Thing}");
					pawn.inventory.innerContainer.TryDrop(unloadableThing.Thing, ThingPlaceMode.Near,
						unloadableThing.Thing.stackCount, out _);
					EndJobWith(JobCondition.Incompletable);
					return;
				}
				
				// Also make vanilla reservation for compatibility
				if (!pawn.Map.reservationManager.Reserve(pawn, job, job.targetB))
				{
					// Release our reservation if vanilla fails
					PUAHReservationSystem.ReleaseAllReservationsForJob(pawn, job, pawn.Map);
					
					Log.Message($"{pawn} failed vanilla reservation for {job.targetB}, dropping {unloadableThing.Thing}");
					pawn.inventory.innerContainer.TryDrop(unloadableThing.Thing, ThingPlaceMode.Near,
						unloadableThing.Thing.stackCount, out _);
					EndJobWith(JobCondition.Incompletable);
					return;
				}
			}
			else
			{
				Log.Message(
					$"Pawn {pawn} unable to find hauling destination, dropping {unloadableThing.Thing}");
				pawn.inventory.innerContainer.TryDrop(unloadableThing.Thing, ThingPlaceMode.Near,
					unloadableThing.Thing.stackCount, out _);
				EndJobWith(JobCondition.Succeeded);
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