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
		pawn.ReserveAsManyAsPossible(job.GetTargetQueue(TargetIndex.A), job);
		return true;
	}

	public override IEnumerable<Toil> MakeNewToils()
	{
		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A);
		yield return nextTarget;
		yield return Toils_Goto.Goto(TargetIndex.A, PathEndMode.ClosestTouch);
		yield return Toils_General.Do(() =>
		{
			pawn.carryTracker.TryStartCarry(TargetThingA, job.count);
			if (!pawn.IsCarrying())
				return;
			pawn.carryTracker.innerContainer.TryTransferToContainer(pawn.carryTracker.CarriedThing, pawn.inventory.innerContainer);
		});
		yield return Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.A, nextTarget);
	}
}