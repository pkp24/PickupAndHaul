namespace PickUpAndHaul;

public class JobDriver_UnloadYourHauledInventory : JobDriver
{
	public override void ExposeData()
	{
		if (Scribe.mode is LoadSaveMode.Saving or LoadSaveMode.LoadingVars)
			return;
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed)
	{
		try
		{
			Log.Message($"TryMakePreToilReservations called for unload job - pawn {pawn?.Name.ToStringShort}");
			pawn.ReserveAsManyAsPossible(job.GetTargetQueue(TargetIndex.B), job);
			return true;
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in TryMakePreToilReservations for unload job - pawn {pawn?.Name.ToStringShort}");
			return false;
		}
	}

	public override IEnumerable<Toil> MakeNewToils()
	{
		Log.Message($"MakeNewToils called for unload job - pawn {pawn?.Name.ToStringShort}");

		var nextTarget = Toils_General.Do(() =>
		{
			try
			{
				Log.Message($"Setting up next unload target for pawn {pawn.Name.ToStringShort}");

				job.SetTarget(TargetIndex.A, job.targetQueueA[0]);
				job.targetQueueA.RemoveAt(0);
				job.SetTarget(TargetIndex.B, job.targetQueueB[0]);
				job.targetQueueB.RemoveAt(0);
				job.count = job.countQueue[0];
				job.countQueue.RemoveAt(0);

				Log.Message($"Unload target set - Thing: {TargetThingA?.def.defName} (Stack: {TargetThingA?.stackCount}), Count: {job.count}, Storage: {TargetB}");

				if (pawn.carryTracker.CarriedThing != null)
				{
					Log.Message($"Dropping carried thing {pawn.carryTracker.CarriedThing.def.defName} for pawn {pawn.Name.ToStringShort}");
					pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out var dropped);
					dropped.SetForbidden(false, false);
				}

				pawn.inventory.innerContainer.RemoveWhere(x => x == null);

				// Log inventory state before unload
				var inventoryCount = pawn.inventory?.innerContainer?.Count ?? 0;
				var inventoryMass = MassUtility.GearAndInventoryMass(pawn);
				Log.Message($"Pawn {pawn.Name.ToStringShort} inventory before unload - Items: {inventoryCount}, Mass: {inventoryMass:F2}");
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"Error in nextTarget setup for pawn {pawn?.Name.ToStringShort}");
			}
		});

		yield return nextTarget;

		yield return Toils_General.Do(() =>
		{
			try
			{
				Log.Message($"Transferring {TargetThingA?.def.defName} from inventory to carry tracker for pawn {pawn.Name.ToStringShort}");
				var transferredCount = pawn.inventory.innerContainer.TryTransferToContainer(TargetThingA, pawn.carryTracker.innerContainer, job.count);

				if (transferredCount > 0)
				{
					Log.Message($"Successfully transferred {transferredCount} items of {TargetThingA?.def.defName} to carry tracker for pawn {pawn.Name.ToStringShort}");

					// Log inventory state after transfer
					var newInventoryCount = pawn.inventory?.innerContainer?.Count ?? 0;
					var newInventoryMass = MassUtility.GearAndInventoryMass(pawn);
					Log.Message($"Pawn {pawn.Name.ToStringShort} inventory after transfer - Items: {newInventoryCount}, Mass: {newInventoryMass:F2}");
				}
				else
				{
					Log.Warning($"Failed to transfer {TargetThingA?.def.defName} to carry tracker for pawn {pawn.Name.ToStringShort}");
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"Error in inventory transfer for pawn {pawn?.Name.ToStringShort}");
			}
		});

		var carryToCell = Toils_Goto.Goto(TargetIndex.B, PathEndMode.ClosestTouch);
		yield return Toils_Jump.JumpIf(carryToCell, () => !TargetB.HasThing);

		yield return Toils_Goto.Goto(TargetIndex.B, PathEndMode.ClosestTouch);

		yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.None);

		yield return Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.A, nextTarget);

		yield return carryToCell;

		yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true, true);

		yield return Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.A, nextTarget);
	}
}