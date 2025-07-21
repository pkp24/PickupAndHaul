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
		if (job.targetQueueA.Count > 0)
			pawn.ReserveAsManyAsPossible(job.targetQueueA, job);
		else
		{
			Log.Warning($"targetQueueA is null or empty for {pawn}");
			return false;
		}

		return true;
	}

	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A);
		yield return nextTarget;
		var gotoThing = Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.ClosestTouch);
		gotoThing.FailOnDespawnedNullOrForbidden(TargetIndex.A);
		yield return gotoThing;
		yield return LoadInventory(pawn);
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());
	}

	private Toil LoadInventory(Pawn pawn) => new()
	{
		initAction = () =>
		{
			Toils_Haul.ErrorCheckForCarry(pawn, TargetThingA);

			//get max we can pick up
			var countToPickUp = Mathf.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(pawn, TargetThingA));

			if (countToPickUp > 0)
				pawn.inventory.GetDirectlyHeldThings().TryAdd(TargetThingA.SplitOff(countToPickUp));
			else
				pawn.UnloadInventory();
		}
	};
}