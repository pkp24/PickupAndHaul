using Verse;
using System;

namespace PickUpAndHaul
{
    public class PickupAndHaulSaveLoadLogger : GameComponent
    {
        public PickupAndHaulSaveLoadLogger() : base() { }
        public PickupAndHaulSaveLoadLogger(Game game) : base() { }

        public override void ExposeData()
        {
            base.ExposeData();
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                Log.Message("[PickUpAndHaul] GameComponent: On Save (ExposeData, Saving)");
                LongEventHandler.ExecuteWhenFinished(() => Log.Message("[PickUpAndHaul] GameComponent: After Save (ExecuteWhenFinished, Saving)"));
            }
            else if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                Log.Message("[PickUpAndHaul] GameComponent: On Load (ExposeData, LoadingVars)");
                LongEventHandler.ExecuteWhenFinished(() => Log.Message("[PickUpAndHaul] GameComponent: After Load (ExecuteWhenFinished, LoadingVars)"));
            }
        }

        public override void FinalizeInit()
        {
            base.FinalizeInit();
            Log.Message("[PickUpAndHaul] GameComponent: On Load (FinalizeInit)");
        }
    }
} 