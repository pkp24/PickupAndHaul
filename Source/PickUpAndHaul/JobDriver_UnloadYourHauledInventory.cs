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

	public override IEnumerable<Toil> MakeNewToils()
	{
		var begin = FindBestBetterStorageFor();
		yield return begin; //do
		var carryToCell = Toils_Haul.CarryHauledThingToCell(TargetIndex.B);
		yield return Toils_Jump.JumpIf(carryToCell, () => !TargetB.HasThing); // if

		var carryToContainer = Toils_Haul.CarryHauledThingToContainer();
		yield return carryToContainer;
		yield return Toils_Haul.DepositHauledThingInContainer(TargetIndex.B, TargetIndex.None);
		yield return Toils_Haul.JumpToCarryToNextContainerIfPossible(carryToContainer, TargetIndex.B);
		yield return Toils_Jump.JumpIf(begin, () => pawn.inventory.FirstUnloadableThing != default); // while

		yield return carryToCell; // else
		yield return Toils_Haul.PlaceHauledThingInCell(TargetIndex.B, carryToCell, true);
		yield return Toils_Jump.JumpIf(begin, () => pawn.inventory.FirstUnloadableThing != default); // while
	}
	public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

	private Toil FindBestBetterStorageFor() => new()
	{
		initAction = () =>
		{
			if (pawn.IsCarrying())
				pawn.carryTracker.innerContainer.TryDropAll(pawn.InteractionCell, pawn.Map, ThingPlaceMode.Near);
			pawn.inventory.innerContainer.RemoveWhere(x => x == null);

			var thingCount = pawn.inventory.FirstUnloadableThing;
			if (thingCount == default && pawn.inventory.innerContainer.Count != 0)
			{
				pawn.inventory.innerContainer.TryDropAll(pawn.InteractionCell, pawn.Map, ThingPlaceMode.Near);
				EndJobWith(JobCondition.Succeeded);
				return;
			}
			if (thingCount != default && StoreUtility.TryFindBestBetterStorageFor(thingCount.Thing, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(thingCount.Thing), pawn.Faction, out var cell, out var dest))
			{
				var storeTarget = new LocalTargetInfo(cell);
				if (dest is Thing container)
					storeTarget = new LocalTargetInfo(container);
				pawn.CurJob.SetTarget(TargetIndex.A, thingCount.Thing);
				pawn.CurJob.SetTarget(TargetIndex.B, storeTarget);
				if (pawn.CanReserve(storeTarget))
					pawn.Reserve(storeTarget, pawn.CurJob);
				pawn.jobs.curDriver.SetNextToil(Toils_Misc.TakeItemFromInventoryToCarrier(pawn, TargetIndex.A));
			}
		}
	};
}