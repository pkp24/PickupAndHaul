using RimWorld;
using Verse;

namespace PickUpAndHaul.Performance
{
    /// <summary>
    /// Handles periodic performance tracking by being called every tick
    /// </summary>
    public class PeriodicPerformanceTracker : GameComponent
    {
        public PeriodicPerformanceTracker(Game game) : base()
        {
            // Constructor - called when game starts
        }

        public override void GameComponentTick()
        {
            base.GameComponentTick();
            
            // Update periodic performance logging every tick
            PerformanceMonitor.UpdatePeriodicLogging();
        }

        public override void GameComponentOnGUI()
        {
            base.GameComponentOnGUI();
            
            // This could be used for UI-related performance tracking if needed
        }
    }
} 