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

	public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

	//get next, goto, take, check for more. Branches off to "all over the place"
	public override IEnumerable<Toil> MakeNewToils()
	{
		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A);
		yield return nextTarget;
		yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.ClosestTouch);
		yield return Toils_General.Do(() =>
		{
			var countToPickUp = Mathf.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(pawn, TargetThingA));
			if (countToPickUp > 0)
				pawn.inventory.innerContainer.TryAdd(TargetThingA.SplitOff(countToPickUp));
		});
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty());
	}
}