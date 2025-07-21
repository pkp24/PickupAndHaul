namespace PickUpAndHaul;

public class JobDriver_UnloadYourHauledInventory : JobDriver
{
	public override void ExposeData()
	{
		if (Scribe.mode is LoadSaveMode.Saving or LoadSaveMode.LoadingVars)
		{
			Log.Message($"Skipping {Enum.GetName(typeof(LoadSaveMode), Scribe.mode)} for job driver");
			return;
		}
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed)
	{
		job.targetQueueA ??= [];
		job.targetQueueB ??= [];
		job.countQueue ??= [];
		pawn.inventory.GetDirectlyHeldThings().RemoveWhere(x => x == null);
		foreach (var thing in pawn.inventory.GetDirectlyHeldThings())
		{
			var currentPriority = StoreUtility.CurrentStoragePriorityOf(thing);
			if (StoreUtility.TryFindBestBetterStorageFor(thing, pawn, pawn.Map, currentPriority, pawn.Faction, out var cell, out var dest))
			{
				var storeTarget = new LocalTargetInfo(cell);
				if (dest is Thing destinationAsThing)
					storeTarget = new(destinationAsThing);
				job.targetQueueA.Add(new LocalTargetInfo(thing));
				job.targetQueueB.Add(storeTarget);
				job.countQueue.Add(thing.stackCount);
			}
		}
		if (job.targetQueueB.Count > 0)
			pawn.ReserveAsManyAsPossible(job.targetQueueB, job);
		else
			Log.Warning($"targetQueueB is empty for {pawn}, couldn't find storage for {pawn.inventory.GetDirectlyHeldThings().Count} items");
		return true;
	}

	/// <summary>
	/// Find spot, reserve spot, pull thing out of inventory, go to spot, drop stuff, repeat.
	/// </summary>
	/// <returns></returns>
	public override IEnumerable<Toil> MakeNewToils()
	{
		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A);
		yield return Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.B);
		yield return nextTarget;
		yield return PullItemFromInventory(nextTarget);
		var carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.B);
		yield return Toils_Jump.JumpIf(carryToCell, TargetIsCell);
		yield return Toils_Haul.CarryHauledThingToContainer();
		yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.None);
		yield return carryToCell;
		yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true);
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());
	}

	private bool TargetIsCell() => !TargetB.HasThing;

	private Toil PullItemFromInventory(Toil nextTarget) => new()
	{
		initAction = () =>
		{
			if (TargetThingA == null)
			{
				pawn.jobs.curDriver.JumpToToil(nextTarget);
				Log.Error("TargetThingA is null");
				return;
			}

			if (!pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !TargetThingA.def.EverStorable(false))
			{
				var isDropped = pawn.inventory.innerContainer.TryDrop(TargetThingA, ThingPlaceMode.Near, TargetThingB.stackCount, out var dropped);
				if (isDropped)
				{
					dropped.SetForbidden(false, false);
					pawn.inventory.GetDirectlyHeldThings().Remove(TargetThingA);
				}

				if (pawn.Map.reservationManager.ReservedBy(job.targetB, pawn, pawn.CurJob))
					pawn.Map.reservationManager.Release(job.targetB, pawn, pawn.CurJob);

				return;
			}

			pawn.inventory.innerContainer.TryTransferToContainer(TargetThingA, pawn.carryTracker.innerContainer, TargetThingA.stackCount, out var carried);
			// If transfer failed, fall back to dropping near the pawn
			if (carried == null)
			{
				var isDropped = pawn.inventory.innerContainer.TryDrop(TargetThingA, ThingPlaceMode.Near, TargetThingA.stackCount, out carried);
				if (isDropped)
				{
					carried.SetForbidden(false, false);
					pawn.inventory.GetDirectlyHeldThings().Remove(TargetThingA);
				}
				return;
			}

			job.count = TargetThingA.stackCount;
			job.SetTarget(TargetIndex.A, carried);
			carried.SetForbidden(false, false);
			if (carried.stackCount == TargetThingA.stackCount)
				pawn.inventory.GetDirectlyHeldThings().Remove(TargetThingA);
			else
			{
				pawn.inventory.GetDirectlyHeldThings().Remove(TargetThingA);
				var thing = TargetThingA;
				thing.stackCount -= carried.stackCount;
				pawn.inventory.GetDirectlyHeldThings().TryAdd(thing);
			}
		}
	};
}