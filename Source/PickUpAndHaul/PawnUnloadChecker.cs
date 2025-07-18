﻿namespace PickUpAndHaul;
public static class PawnUnloadChecker
{
	public static void CheckIfPawnShouldUnloadInventory(Pawn pawn, bool forced = false)
	{
		// Check if save operation is in progress
		if (PickupAndHaulSaveLoadLogger.IsSaveInProgress())
		{
			return; // Skip unload checking during save operations
		}

		// Ignore pawns that are currently in a mental state
		if (pawn == null || pawn.InMentalState)
		{
			return;
		}

		var job = JobMaker.MakeJob(PickUpAndHaulJobDefOf.UnloadYourHauledInventory, pawn);
		var itemsTakenToInventory = pawn.GetComp<CompHauledToInventory>();

		if (itemsTakenToInventory == null)
		{
			return;
		}

		// Clean up nulls at a safe point before accessing the collection
		itemsTakenToInventory.CleanupNulls();

		var carriedThing = itemsTakenToInventory.HashSet;

		if (pawn.Faction != Faction.OfPlayerSilentFail || !Settings.IsAllowedRace(pawn.RaceProps)
			|| carriedThing == null || carriedThing.Count == 0
			|| pawn.inventory.innerContainer is not { } inventoryContainer || inventoryContainer.Count == 0)
		{
			return;
		}

		if ((forced && job.TryMakePreToilReservations(pawn, false))
			|| ((MassUtility.EncumbrancePercent(pawn) >= 0.90f || carriedThing.Count >= 1)
			&& job.TryMakePreToilReservations(pawn, false)))
		{
			pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
			return;
		}

		if (inventoryContainer.Count >= 1)
		{
			for (var i = 0; i < inventoryContainer.Count; i++)
			{
				var compRottable = inventoryContainer[i].TryGetComp<CompRottable>();

				if (compRottable?.TicksUntilRotAtCurrentTemp < 30000)
				{
					pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
					return;
				}
			}
		}

		// Stagger inventory sync checks to prevent all pawns checking at once
		var staggeredCheck = (Find.TickManager.TicksGame + (pawn.thingIDNumber % 50)) % 50 == 0;
		if (staggeredCheck && inventoryContainer.Count < carriedThing.Count)
		{
			Log.Warning("[PickUpAndHaul] " + pawn + " inventory was found out of sync with haul index. Pawn will drop their inventory.");
			carriedThing.Clear();
			pawn.inventory.UnloadEverything = true;
		}
	}
}

[DefOf]
public static class PickUpAndHaulJobDefOf
{
	public static JobDef UnloadYourHauledInventory;
	public static JobDef HaulToInventory;
}