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
		yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.ClosestTouch);
		yield return Toils_Haul.StartCarryThing(TargetIndex.A, false, true, false, true, true);
		yield return Toils_General.Do(() =>
		{
			if (pawn.carryTracker.CarriedThing == null)
				return;

			var countToPickUp = Math.Min(job.count, MassUtility.CountToPickUpUntilOverEncumbered(pawn, TargetThingA));
			if (countToPickUp == job.count)
				pawn.carryTracker.innerContainer.TryTransferToContainer(pawn.carryTracker.CarriedThing, pawn.inventory.innerContainer);
			else if (countToPickUp > 0)
			{
				pawn.carryTracker.innerContainer.TryTransferToContainer(pawn.carryTracker.CarriedThing.SplitOff(countToPickUp), pawn.inventory.innerContainer);
				pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out var dropped);
				dropped.SetForbidden(false, false);
			}
		});
		yield return Toils_Jump.JumpIfHaveTargetInQueue(TargetIndex.A, nextTarget);
	}
}