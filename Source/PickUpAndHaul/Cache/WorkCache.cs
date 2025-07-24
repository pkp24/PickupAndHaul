namespace PickUpAndHaul.Cache;

public class WorkCache : ICache
{
	private const int TICKS_DELAY = 360;

	static WorkCache() =>
		CacheManager.RegisterCache(Instance);

	public static WorkCache Instance { get; } = new();

	public static ConcurrentQueue<Thing> Cache { get; private set; } = [];
	public static ConcurrentQueue<Thing> UrgentCache { get; private set; } = [];

	public static int NextWorkCacheTick { get; private set; }

	public static ConcurrentQueue<Thing> CalculatePotentialWork(Pawn pawn)
	{
		var currentTick = Find.TickManager.TicksGame;
		if (currentTick < NextWorkCacheTick)
			return Cache;

		// Ensure things are sorted by distance from center
		Cache = new ConcurrentQueue<Thing>([.. pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()
					.Where(x => !x.IsCorrupted(pawn) && !x.IsUrgent(pawn.Map))
					.OrderBy((x) => (x.Position - pawn.Map.Center).LengthHorizontalSquared)]);
		UrgentCache = new ConcurrentQueue<Thing>([.. pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()
					.Where(x => !x.IsCorrupted(pawn) && x.IsUrgent(pawn.Map))
					.OrderBy((x) => (x.Position - pawn.Map.Center).LengthHorizontalSquared)]);
		NextWorkCacheTick = currentTick + Math.Max(Cache.Count, TICKS_DELAY);
		return Cache;
	}

	public void ForceCleanup()
	{
		Cache.Clear();
		UrgentCache.Clear();
		NextWorkCacheTick = 0;
	}

	public string GetDebugInfo() => $"Number of items in cache: {Cache.Count} and urgent cache {Cache.Count}";
}