using Verse;

namespace PartialReservationSystem
{
    /// <summary>
    /// Runs once every 250 ticks (~4 sec real-time on normal speed) and purges
    /// stale PRS reservations and pending hauls.
    /// </summary>
    public class PRS_MapComponent : MapComponent
    {
        private const int CleanupInterval = 250;

        public PRS_MapComponent(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            if (Find.TickManager.TicksGame % CleanupInterval == 0)
            {
                try
                {
                    PRSReservationSystem.CleanupExpiredReservations(map);
                    PendingHaulTracker.CleanupExpiredPending(map);
                }
                catch (System.Exception ex)
                {
                    PartialReservationSystem.Log.Error($"PRS cleanup tick ex: {ex}");
                }
            }
        }
    }
}


