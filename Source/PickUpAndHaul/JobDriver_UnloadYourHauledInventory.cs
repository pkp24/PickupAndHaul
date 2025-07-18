namespace PickUpAndHaul;

public class JobDriver_UnloadYourHauledInventory : JobDriver
{
	private int _countToDrop = -1;
	private int _unloadDuration = 3;

	public override void ExposeData()
	{
		// Don't save any data for this job driver to prevent save corruption
		// when the mod is removed
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			Log.Message("Skipping save data for UnloadYourHauledInventory job driver");
			return;
		}

		// Only load data if we're in loading mode and the mod is active
		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			Log.Message("Skipping load data for UnloadYourHauledInventory job driver");
			return;
		}

		// Only expose data if we're in a different mode (like copying)
		base.ExposeData();
		Scribe_Values.Look(ref _countToDrop, "countToDrop", -1);
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed)
	{
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"Skipping UnloadYourHauledInventory job reservations during save operation for {pawn}");
			return false;
		}
		return true;
	}

	/// <summary>
	/// Find spot, reserve spot, pull thing out of inventory, go to spot, drop stuff, repeat.
	/// </summary>
	/// <returns></returns>
	public override IEnumerable<Toil> MakeNewToils()
	{
		// Check if save operation is in progress at the start
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			Log.Message($"Ending UnloadYourHauledInventory job during save operation for {pawn}");
			EndJobWith(JobCondition.InterruptForced);
			yield break;
		}

		// Clean up nulls at a safe point before we start iterating
		var comp = pawn.TryGetComp<CompHauledToInventory>();
		comp?.CleanupNulls();

		if (ModCompatibilityCheck.ExtendedStorageIsActive)
		{
			_unloadDuration = 20;
		}

		var begin = Toils_General.Wait(_unloadDuration);
		yield return begin;

		var carriedThings = comp?.HashSet ?? [];
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

	private Toil PullItemFromInventory(HashSet<Thing> carriedThings, Toil wait) => new()
	{
		initAction = () =>
		{
			if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
			{
				EndJobWith(JobCondition.InterruptForced);
				return;
			}

			var thing = job.GetTarget(TargetIndex.A).Thing;
			if (thing == null || !pawn.inventory.innerContainer.Contains(thing))
			{
				carriedThings.Remove(thing);
				pawn.jobs.curDriver.JumpToToil(wait);
				return;
			}

			// Clamp to a positive amount
			if (_countToDrop <= 0 || _countToDrop > thing.stackCount)
				_countToDrop = thing.stackCount;

			var destCell = TargetB.HasThing ? job.targetB.Thing.Position : job.targetB.Cell;
			if (destCell.IsValid &&
				HoldMultipleThings_Support.CapacityAt(thing, destCell, pawn.Map, out var cap))
			{
				if (cap <= 0)
				{
					pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near,
						_countToDrop, out var dropped);
					dropped?.SetForbidden(false, false);

					// Release only if we actually reserved
					if (pawn.Map.reservationManager.ReservedBy(job.targetB, pawn, pawn.CurJob))
						pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);

					EndJobWith(JobCondition.Succeeded);
					carriedThings.Remove(thing);
					return;
				}

				_countToDrop = Math.Min(_countToDrop, cap);
			}

			if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) ||
				!thing.def.EverStorable(false))
			{
				pawn.inventory.innerContainer.TryDrop(thing, ThingPlaceMode.Near,
					_countToDrop, out var dropped);
				dropped?.SetForbidden(false, false);

				if (pawn.Map.reservationManager.ReservedBy(job.targetB, pawn, pawn.CurJob))
					pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);

				EndJobWith(JobCondition.Succeeded);
				carriedThings.Remove(thing);
				return;
			}

			pawn.inventory.innerContainer.TryTransferToContainer(
				thing, pawn.carryTracker.innerContainer, _countToDrop, out var carried);

			// If transfer failed, fall back to dropping near the pawn
			if (carried == null)
			{
				pawn.inventory.innerContainer.TryDrop(
					thing, ThingPlaceMode.Near, _countToDrop, out carried);
				carried?.SetForbidden(false, false);
				EndJobWith(JobCondition.Succeeded);
				carriedThings.Remove(thing);
				return;
			}

			job.count = _countToDrop;
			job.SetTarget(TargetIndex.A, carried);
			carried.SetForbidden(false, false);
			carriedThings.Remove(thing);

			if (ModCompatibilityCheck.CombatExtendedIsActive)
				CompatHelper.UpdateInventory(pawn);
		}
	};

	private Toil FindTargetOrDrop(HashSet<Thing> carriedThings) => new()
	{
		initAction = () =>
		{
			if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
			{
				EndJobWith(JobCondition.InterruptForced);
				return;
			}

			var unloadable = FirstUnloadableThing(pawn, carriedThings);
			if (unloadable.Count == 0)
			{
				if (carriedThings.Count == 0)
					EndJobWith(JobCondition.Succeeded);
				return;
			}

			// Locate storage
			if (StoreUtility.TryFindBestBetterStorageFor(
					unloadable.Thing, pawn, pawn.Map, StoragePriority.Unstored,
					pawn.Faction, out var cell, out var dest))
			{
				job.SetTarget(TargetIndex.A, unloadable.Thing);

				var targetB = cell == IntVec3.Invalid && dest is Thing destThing ? (LocalTargetInfo)destThing : cell;
				job.SetTarget(TargetIndex.B, targetB);

				var targetCell = cell == IntVec3.Invalid
									? (dest as Thing)?.Position ?? IntVec3.Invalid
									: cell;

				var capacity = unloadable.Thing.stackCount;
				var skipRes = false;

				if (targetCell.IsValid &&
					HoldMultipleThings_Support.CapacityAt(unloadable.Thing, targetCell,
														pawn.Map, out var cap))
				{
					capacity = cap;
					skipRes = true;              // handled by the crate itself
				}

				// Reserve if necessary
				if (!skipRes &&
					!pawn.Map.reservationManager.Reserve(pawn, job, targetB))
				{
					pawn.inventory.innerContainer.TryDrop(unloadable.Thing,
						ThingPlaceMode.Near, unloadable.Thing.stackCount, out var dropped);
					dropped?.SetForbidden(false, false);
					EndJobWith(JobCondition.Incompletable);
					return;
				}

				_countToDrop = capacity > 0
					? Math.Min(unloadable.Thing.stackCount, capacity)
					: unloadable.Thing.stackCount;

				if (_countToDrop <= 0)
					_countToDrop = 1;
			}
			else
			{
				pawn.inventory.innerContainer.TryDrop(unloadable.Thing,
					ThingPlaceMode.Near, unloadable.Thing.stackCount, out var dropped);
				dropped?.SetForbidden(false, false);
				EndJobWith(JobCondition.Succeeded);
			}
		}
	};

	private static ThingCount FirstUnloadableThing(Pawn pawn, HashSet<Thing> carriedThings)
	{
		var innerPawnContainer = pawn.inventory.innerContainer;
		Thing best = null;

		// Use ToList() to avoid "collection modified during iteration" exception
		// since we may need to remove items from carriedThings
		var carriedThingsList = carriedThings.ToList();

		// Track items to remove after iteration completes
		var itemsToRemove = new List<Thing>();

		foreach (var thing in carriedThingsList)
		{
			// Skip null items without modifying the collection
			if (thing == null)
			{
				itemsToRemove.Add(thing);
				continue;
			}

			// Handle stacks that changed IDs after being picked up
			if (!innerPawnContainer.Contains(thing))
			{
				var stragglerDef = thing.def;
				itemsToRemove.Add(thing);

				for (var i = 0; i < innerPawnContainer.Count; i++)
				{
					var dirtyStraggler = innerPawnContainer[i];
					if (dirtyStraggler.def == stragglerDef)
					{
						// Clean up all invalid items from carriedThings before returning
						CleanupInvalidItems(carriedThings, innerPawnContainer);
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

		// Remove all tracked items after iteration completes
		foreach (var item in itemsToRemove)
		{
			carriedThings.Remove(item);
		}

		return best != null ? new ThingCount(best, best.stackCount) : default;

		static int CompareInventoryOrder(Thing a, Thing b)
		{
			var catA = a.def.FirstThingCategory?.index ?? int.MaxValue;
			var catB = b.def.FirstThingCategory?.index ?? int.MaxValue;
			var compare = catA.CompareTo(catB);
			return compare != 0 ? compare : string.CompareOrdinal(a.def.defName, b.def.defName);
		}

		static void CleanupInvalidItems(HashSet<Thing> carriedThings, ThingOwner innerPawnContainer) =>
			// Remove all null items and items not in the container
			carriedThings.RemoveWhere(thing => thing == null || !innerPawnContainer.Contains(thing));
	}
}