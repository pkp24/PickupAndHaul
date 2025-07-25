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

				// Validate job queues before accessing them
				if (job.targetQueueA == null || job.targetQueueA.Count == 0 ||
					job.targetQueueB == null || job.targetQueueB.Count == 0 ||
					job.countQueue == null || job.countQueue.Count == 0)
				{
					Log.Error($"Invalid job queues for pawn {pawn.Name.ToStringShort} - QueueA: {job.targetQueueA?.Count ?? 0}, QueueB: {job.targetQueueB?.Count ?? 0}, CountQueue: {job.countQueue?.Count ?? 0}");
					return;
				}

				job.SetTarget(TargetIndex.A, job.targetQueueA[0]);
				job.targetQueueA.RemoveAt(0);
				job.SetTarget(TargetIndex.B, job.targetQueueB[0]);
				job.targetQueueB.RemoveAt(0);
				job.count = job.countQueue[0];
				job.countQueue.RemoveAt(0);

				Log.Message($"Unload target set - Thing: {TargetThingA?.def.defName} (Stack: {TargetThingA?.stackCount}), Count: {job.count}, Storage: {TargetB}");

				// Validate target thing exists
				if (TargetThingA == null)
				{
					Log.Error($"TargetThingA is null for pawn {pawn.Name.ToStringShort}");
					return;
				}

				if (pawn.carryTracker.CarriedThing != null)
				{
					Log.Message($"Dropping carried thing {pawn.carryTracker.CarriedThing.def.defName} for pawn {pawn.Name.ToStringShort}");
					pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out var dropped);
					dropped?.SetForbidden(false, false);
				}

				// Clean up null items from inventory
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
				// Validate target thing exists before transfer
				if (TargetThingA == null)
				{
					Log.Error($"Cannot transfer null target thing for pawn {pawn.Name.ToStringShort}");
					return;
				}

				// Check if the thing is still in the pawn's inventory
				if (!pawn.inventory.innerContainer.Contains(TargetThingA))
				{
					Log.Warning($"Target thing {TargetThingA.def.defName} not found in pawn {pawn.Name.ToStringShort}'s inventory");
					return;
				}

				Log.Message($"Transferring {TargetThingA.def.defName} from inventory to carry tracker for pawn {pawn.Name.ToStringShort}");

				// Validate carry tracker
				if (pawn.carryTracker?.innerContainer == null)
				{
					Log.Error($"Carry tracker is null for pawn {pawn.Name.ToStringShort}");
					return;
				}

				var transferredCount = pawn.inventory.innerContainer.TryTransferToContainer(TargetThingA, pawn.carryTracker.innerContainer, job.count);

				if (transferredCount > 0)
				{
					Log.Message($"Successfully transferred {transferredCount} items of {TargetThingA.def.defName} to carry tracker for pawn {pawn.Name.ToStringShort}");

					// Log inventory state after transfer
					var newInventoryCount = pawn.inventory?.innerContainer?.Count ?? 0;
					var newInventoryMass = MassUtility.GearAndInventoryMass(pawn);
					Log.Message($"Pawn {pawn.Name.ToStringShort} inventory after transfer - Items: {newInventoryCount}, Mass: {newInventoryMass:F2}");
				}
				else
				{
					Log.Warning($"Failed to transfer {TargetThingA.def.defName} to carry tracker for pawn {pawn.Name.ToStringShort}");
					// If transfer failed, we should end this job and try again
					EndJobWith(JobCondition.Incompletable);
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"Error in inventory transfer for pawn {pawn?.Name.ToStringShort}");
				EndJobWith(JobCondition.Errored);
			}
		});

		var carryToCell = Toils_Goto.Goto(TargetIndex.B, PathEndMode.ClosestTouch);
		yield return Toils_Jump.JumpIf(carryToCell, () => !TargetB.HasThing);

		yield return Toils_Goto.Goto(TargetIndex.B, PathEndMode.ClosestTouch);

		yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.None);

		yield return Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.A, nextTarget);

		yield return carryToCell;

		// Add validation before placing hauled thing
		yield return Toils_General.Do(() =>
		{
			if (pawn.carryTracker.CarriedThing == null)
			{
				Log.Warning($"Pawn {pawn.Name.ToStringShort} has no carried thing to place");
				return;
			}
		});

		// Custom toil to replace PlaceHauledThingInCell with better validation
		yield return Toils_General.Do(() =>
		{
			try
			{
				if (pawn.carryTracker.CarriedThing == null)
				{
					Log.Warning($"Pawn {pawn.Name.ToStringShort} has no carried thing to place");
					return;
				}

				if (TargetB == null || !TargetB.IsValid)
				{
					Log.Warning($"Invalid target cell for pawn {pawn.Name.ToStringShort}");
					return;
				}

				// Use the standard place hauled thing logic but with validation
				pawn.carryTracker.TryDropCarriedThing(TargetB.Cell, ThingPlaceMode.Direct, out var dropped);
				if (dropped != null)
				{
					dropped.SetForbidden(false, false);
					Log.Message($"Successfully placed {dropped.def.defName} at {TargetB.Cell} for pawn {pawn.Name.ToStringShort}");
				}
				else
				{
					Log.Warning($"Failed to place carried thing for pawn {pawn.Name.ToStringShort}");
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"Error placing hauled thing for pawn {pawn.Name.ToStringShort}");
			}
		});

		yield return Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.A, nextTarget);
	}
}