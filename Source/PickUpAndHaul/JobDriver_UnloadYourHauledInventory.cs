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
		pawn.ReserveAsManyAsPossible(job.GetTargetQueue(TargetIndex.A), job);
		return true;
	}

	public override IEnumerable<Toil> MakeNewToils()
	{
		var nextTarget = Toils_JobTransforms.ExtractNextTargetFromQueue(TargetIndex.A);
		yield return nextTarget; //do
		yield return Toils_General.Wait(20); // unload
		yield return Toils_General.Do(() =>
		{
			pawn.inventory.innerContainer.RemoveWhere(x => x == null);
			if (pawn.IsCarrying() || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation) || !TargetThingA.def.EverStorable(false))
			{
				pawn.carryTracker.innerContainer.TryDropAll(pawn.InteractionCell, pawn.Map, ThingPlaceMode.Near);
				EndJobWith(JobCondition.Succeeded);
			}
			pawn.inventory.innerContainer.TryTransferToContainer(TargetThingA, pawn.carryTracker.innerContainer, pawn.CurJob.count);
			TargetThingA.SetForbidden(false, false);
		});
		var carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.B);
		yield return TargetB.HasThing ? Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch) : Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.ClosestTouch);
		yield return Toils_Jump.JumpIf(carryToCell, () => !TargetB.HasThing); // if cell

		var carryToContainer = Toils_Haul.CarryHauledThingToContainer();
		yield return carryToContainer;
		yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.None);
		yield return Toils_Haul.JumpToCarryToNextContainerIfPossible(carryToContainer, TargetIndex.B);
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty()); // while

		yield return carryToCell; // else
		yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true);
		yield return Toils_Jump.JumpIf(nextTarget, () => !job.targetQueueA.NullOrEmpty()); // while
	}
}