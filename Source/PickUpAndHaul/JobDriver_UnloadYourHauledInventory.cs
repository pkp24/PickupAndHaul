﻿using System;
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

					// Calculate how much can actually be stored at the destination
					var availableSpace = GetAvailableStorageSpace(destination, cell, unloadableThing.Thing);
					_countToDrop = Math.Min(unloadableThing.Thing.stackCount, availableSpace);
					
					// If no space is available, drop the item instead of trying to transfer 0 items
					if (_countToDrop <= 0)
					{
						Log.Message($"No space available at destination {job.targetB}, dropping {unloadableThing.Thing}");
						pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);
						pawn.inventory.innerContainer.TryDrop(unloadableThing.Thing, ThingPlaceMode.Near,
							unloadableThing.Thing.stackCount, out _);
						EndJobWith(JobCondition.Succeeded);
						PerformanceProfiler.EndTimer("FindTargetOrDrop");
						return;
					}
					
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

	private static int GetAvailableStorageSpace(Thing destination, IntVec3 cell, Thing thingToStore)
	{
		// If destination is a container (like a shelf or stockpile zone building)
		if (destination != null && destination.TryGetComp<CompDeepStorage>() != null)
		{
			// For deep storage, calculate available space
			var deepStorageComp = destination.TryGetComp<CompDeepStorage>();
			if (deepStorageComp != null)
			{
				var maxStacks = deepStorageComp.maxNumberStacks;
				var currentStacks = destination.GetInnerContainer().Count;
				if (currentStacks >= maxStacks)
				{
					// Check if we can add to existing stacks
					var existingStack = destination.GetInnerContainer().FirstOrDefault(t => t.def == thingToStore.def && t.stackCount < t.def.stackLimit);
					if (existingStack != null)
					{
						return existingStack.def.stackLimit - existingStack.stackCount;
					}
					return 0;
				}
				return thingToStore.def.stackLimit; // Can create new stack
			}
		}
		
		// For regular storage (shelves, containers, etc.)
		if (destination != null && destination.TryGetInnerContainer() != null)
		{
			var container = destination.TryGetInnerContainer();
			var existingStack = container.FirstOrDefault(t => t.def == thingToStore.def && t.stackCount < t.def.stackLimit);
			if (existingStack != null)
			{
				return existingStack.def.stackLimit - existingStack.stackCount;
			}
			
			// Check if container has space for new stack
			if (container.Count < container.maxCount)
			{
				return thingToStore.def.stackLimit;
			}
			return 0;
		}
		
		// For ground storage (stockpile zones)
		if (cell != IntVec3.Invalid)
		{
			var existingThings = cell.GetThingList(thingToStore.Map);
			var existingStack = existingThings.FirstOrDefault(t => t.def == thingToStore.def && t.stackCount < t.def.stackLimit);
			if (existingStack != null)
			{
				return existingStack.def.stackLimit - existingStack.stackCount;
			}
			
			// Check if cell can accept new stack
			if (existingThings.Count(t => t.def.category == ThingCategory.Item) < cell.GetMaxItemsAllowedInCell(thingToStore.Map))
			{
				return thingToStore.def.stackLimit;
			}
			return 0;
		}
		
		// Default case - assume full stack can be stored
		return thingToStore.def.stackLimit;
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
