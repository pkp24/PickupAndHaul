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
		try
		{
			var currentTick = Find.TickManager.TicksGame;

			Log.Message($"CalculatePotentialWork called for pawn {pawn?.Name.ToStringShort} at tick {currentTick}");

			if (NextWorkCacheTick.TryGetValue(pawn.Map, out var tick) && currentTick < tick)
			{
				var existingCacheCount = Cache.TryGetValue(pawn.Map, out var existingCache) ? existingCache.Count : 0;
				var existingUrgentCount = UrgentCache.TryGetValue(pawn.Map, out var existingUrgentCache) ? existingUrgentCache.Count : 0;

				Log.Message($"Using cached work for pawn {pawn.Name.ToStringShort} - Cache: {existingCacheCount}, Urgent: {existingUrgentCount}");
				return UrgentCache[pawn.Map].IsEmpty ? Cache[pawn.Map] : UrgentCache[pawn.Map];
			}

			Log.Message($"Recalculating work cache for pawn {pawn.Name.ToStringShort}");

			// Ensure things are sorted by distance from center
			var haulables = pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling();
			Log.Message($"Found {haulables.Count} haulable items for pawn {pawn.Name.ToStringShort}");

			var validHaulables = haulables.Where(x => !x.IsCorrupted(pawn) && !x.IsUrgent(pawn.Map)).ToList();
			var urgentHaulables = haulables.Where(x => !x.IsCorrupted(pawn) && x.IsUrgent(pawn.Map)).ToList();

			Log.Message($"Valid haulables: {validHaulables.Count}, Urgent haulables: {urgentHaulables.Count} for pawn {pawn.Name.ToStringShort}");

			var newCache = new ConcurrentQueue<Thing>([.. validHaulables
					.OrderBy((x) => (x.Position - pawn.Map.Center).LengthHorizontalSquared)]);
			Cache.AddOrUpdate(pawn.Map, newCache, (key, oldValue) => newCache);

			var newUrgentCache = new ConcurrentQueue<Thing>([.. urgentHaulables
					.OrderBy((x) => (x.Position - pawn.Map.Center).LengthHorizontalSquared)]);
			UrgentCache.AddOrUpdate(pawn.Map, newUrgentCache, (key, oldValue) => newUrgentCache);

			var nextTick = UrgentCache[pawn.Map].IsEmpty ? currentTick + Math.Max(newCache.Count, TICKS_DELAY) : currentTick + TICKS_DELAY;
			NextWorkCacheTick.AddOrUpdate(pawn.Map, nextTick, (key, oldValue) => nextTick);

			Log.Message($"Work cache updated for pawn {pawn.Name.ToStringShort} - Cache: {newCache.Count}, Urgent: {newUrgentCache.Count}, Next update: {nextTick}");

			return UrgentCache[pawn.Map].IsEmpty ? Cache[pawn.Map] : UrgentCache[pawn.Map];
		}
		catch (Exception ex)
		{
			Log.Error(ex, $"Error in CalculatePotentialWork for pawn {pawn?.Name.ToStringShort}");
			return new ConcurrentQueue<Thing>();
		}
	}

	public void ForceCleanup()
	{
		try
		{
			Log.Message("ForceCleanup called for WorkCache");

			var cacheCount = Cache.Count;
			var urgentCount = UrgentCache.Count;
			var tickCount = NextWorkCacheTick.Count;

			Cache.Clear();
			UrgentCache.Clear();
			NextWorkCacheTick.Clear();

			Log.Message($"WorkCache cleanup completed - Cleared {cacheCount} caches, {urgentCount} urgent caches, {tickCount} tick entries");
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error in WorkCache ForceCleanup");
		}
	}

	public string GetDebugInfo()
	{
		try
		{
			var totalCacheItems = Cache.Values.Sum(q => q.Count);
			var totalUrgentItems = UrgentCache.Values.Sum(q => q.Count);
			return $"Number of items in cache: {totalCacheItems} and urgent cache {totalUrgentItems}";
		}
		catch (Exception ex)
		{
			Log.Error(ex, "Error in GetDebugInfo");
			return "Error getting debug info";
		}
	}
}