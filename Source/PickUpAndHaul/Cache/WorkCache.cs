namespace PickUpAndHaul.Cache;

public class WorkCache : ICache
{
	private const int TICKS_DELAY = 360;

	static WorkCache() =>
		CacheManager.RegisterCache(Instance);

	public static WorkCache Instance { get; } = new();

	public static ConcurrentDictionary<Map, ConcurrentQueue<Thing>> Cache { get; private set; } = [];
	public static ConcurrentDictionary<Map, ConcurrentQueue<Thing>> UrgentCache { get; private set; } = [];

	public static ConcurrentDictionary<Map, int> NextWorkCacheTick { get; private set; } = [];

	public static ConcurrentQueue<Thing> CalculatePotentialWork(Pawn pawn)
	{
		var currentTick = Find.TickManager.TicksGame;
		if (NextWorkCacheTick.TryGetValue(pawn.Map, out var tick) && currentTick < tick)
			return UrgentCache[pawn.Map].IsEmpty ? Cache[pawn.Map] : UrgentCache[pawn.Map];

		// Ensure things are sorted by distance from center
		var cache = new ConcurrentQueue<Thing>([.. pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()
					.Where(x => !x.IsCorrupted(pawn) && !x.IsUrgent(pawn.Map))
					.OrderBy((x) => (x.Position - pawn.Map.Center).LengthHorizontalSquared)]);
		Cache.AddOrUpdate(pawn.Map, cache, (key, oldValue) => cache);

		var urgentCache = new ConcurrentQueue<Thing>([.. pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()
					.Where(x => !x.IsCorrupted(pawn) && x.IsUrgent(pawn.Map))
					.OrderBy((x) => (x.Position - pawn.Map.Center).LengthHorizontalSquared)]);
		UrgentCache.AddOrUpdate(pawn.Map, urgentCache, (key, oldValue) => urgentCache);

		var nextTick = UrgentCache[pawn.Map].IsEmpty ? currentTick + Math.Max(Cache.Count, TICKS_DELAY) : currentTick + TICKS_DELAY;
		NextWorkCacheTick.AddOrUpdate(pawn.Map, nextTick, (key, oldValue) => nextTick);

		return UrgentCache[pawn.Map].IsEmpty ? Cache[pawn.Map] : UrgentCache[pawn.Map];
	}

	public void ForceCleanup()
	{
		Cache.Clear();
		UrgentCache.Clear();
		NextWorkCacheTick.Clear();
	}

	public string GetDebugInfo() => $"Number of items in cache: {Cache.Count} and urgent cache {Cache.Count}";
}