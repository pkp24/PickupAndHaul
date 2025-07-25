namespace PickUpAndHaul;
public class JobDriver_HaulToInventory : JobDriver
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
			Log.Message($"TryMakePreToilReservations called for pawn {pawn?.Name.ToStringShort}");
			pawn.ReserveAsManyAsPossible(job.GetTargetQueue(TargetIndex.A), job);
			return true;
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in TryMakePreToilReservations for pawn {pawn?.Name.ToStringShort}");
			return false;
		}
	}

	public override IEnumerable<Toil> MakeNewToils()
	{
		Log.Message($"MakeNewToils called for pawn {pawn?.Name.ToStringShort}, job targets: {job?.GetTargetQueue(TargetIndex.A)?.Count ?? 0}");

		// Check if we have any targets
		if (job?.GetTargetQueue(TargetIndex.A)?.Count == 0)
		{
			Log.Warning($"Job for pawn {pawn?.Name.ToStringShort} has no targets, ending job");
			yield break;
		}

		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A);
		yield return nextTarget;

		yield return Toils_Goto.Goto(TargetIndex.A, PathEndMode.ClosestTouch);

		yield return Toils_General.Do(() =>
		{
			try
			{
				var targetThing = TargetThingA;
				var count = job.count;

				Log.Message($"Starting haul action for pawn {pawn.Name.ToStringShort} - Target: {targetThing?.def.defName} (Stack: {targetThing?.stackCount}), Count: {count}");

				// Log weight information before pickup
				var carryingCapacity = pawn.GetStatValue(StatDefOf.CarryingCapacity, true);
				var currentMass = MassUtility.GearAndInventoryMass(pawn);
				var inventoryCount = pawn.inventory?.innerContainer?.Count ?? 0;

				Log.Message($"Pawn {pawn.Name.ToStringShort} weight info - Capacity: {carryingCapacity:F2}, Current Mass: {currentMass:F2}, Inventory Items: {inventoryCount}");

				pawn.carryTracker.TryStartCarry(TargetThingA, job.count);

				if (!pawn.IsCarrying())
				{
					Log.Warning($"Pawn {pawn.Name.ToStringShort} failed to start carrying {targetThing?.def.defName}");
					return;
				}

				Log.Message($"Pawn {pawn.Name.ToStringShort} successfully started carrying {targetThing?.def.defName}");

				// Transfer to inventory
				var transferred = pawn.carryTracker.innerContainer.TryTransferToContainer(pawn.carryTracker.CarriedThing, pawn.inventory.innerContainer);

				if (transferred)
				{
					Log.Message($"Successfully transferred {targetThing?.def.defName} to inventory for pawn {pawn.Name.ToStringShort}");

					// Log weight information after pickup
					var newMass = MassUtility.GearAndInventoryMass(pawn);
					var newInventoryCount = pawn.inventory?.innerContainer?.Count ?? 0;

					Log.Message($"Pawn {pawn.Name.ToStringShort} weight after pickup - New Mass: {newMass:F2}, New Inventory Items: {newInventoryCount}");

					if (newMass > carryingCapacity)
					{
						Log.Warning($"Pawn {pawn.Name.ToStringShort} is now over weight limit! Capacity: {carryingCapacity:F2}, Current Mass: {newMass:F2}");
					}
				}
				else
				{
					Log.Warning($"Failed to transfer {targetThing?.def.defName} to inventory for pawn {pawn.Name.ToStringShort}");
				}
			}
			catch (Exception ex)
			{
				Log.Error(ex, $"Error in haul action for pawn {pawn?.Name.ToStringShort}");
			}
		});

		yield return Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.A, nextTarget);
	}
}