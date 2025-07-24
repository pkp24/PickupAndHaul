namespace PickUpAndHaul;
public class JobDriver_HaulToInventory : JobDriver
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
		var targetQueue = job.GetTargetQueue(TargetIndex.A);

		// Add null check for targetQueue
		if (targetQueue != null && targetQueue.Count > 0)
		{
			pawn.ReserveAsManyAsPossible(targetQueue, job);
		}
		else
		{
			Log.Warning($"Target queue is null or empty for pawn {pawn.Name?.ToStringShort ?? "Unknown"}");
		}

		return true;
	}

	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A);
		yield return nextTarget;
		yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.A);
		yield return Toils_General.Do(() =>
		{
			// Add null checks to prevent NullReferenceException
			if (TargetThingA == null)
			{
				Log.Warning($"TargetThingA is null for pawn {pawn.Name?.ToStringShort ?? "Unknown"}");
				return;
			}

			var countToPickUp = Math.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(pawn, TargetThingA));
			if (countToPickUp > 0)
			{
				var splitOff = TargetThingA.SplitOff(countToPickUp);
				if (splitOff != null)
				{
					var added = pawn.inventory.innerContainer.TryAdd(splitOff);
				}
				else
				{
					Log.Warning($"SplitOff returned null for {TargetThingA?.LabelShort ?? "null"}");
				}
			}
		});
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());
	}
}