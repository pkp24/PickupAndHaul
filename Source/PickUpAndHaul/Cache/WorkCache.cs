using PickUpAndHaul.Structs;

namespace PickUpAndHaul.Cache;

public class WorkCache : ICache
{
	private const int TICKS_DELAY = 60;
	private int _nextWorkCacheTick;
	private readonly object _lockObject = new();

	static WorkCache() =>
		CacheManager.RegisterCache(Instance);

	public static WorkCache Instance { get; } = new();

	public static List<WorkCacheStorage> Cache { get; private set; } = [];

	public void CalculatePotentialWork(Pawn pawn)
	{
		var currentTick = Find.TickManager.TicksGame;
		if (currentTick < _nextWorkCacheTick)
			return;

		lock (_lockObject)
		{
			// Ensure items are sorted by priority and then by distance from pawn
			Cache = [.. pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling()
					.Select(thing => new WorkCacheStorage(StoreUtility.CurrentStoragePriorityOf(thing), thing))
					.OrderByDescending(x => x.Priority)
					.ThenBy((x) => (x.Thing.Position - pawn.Position).LengthHorizontalSquared)];
			_nextWorkCacheTick = currentTick + Cache.Count + TICKS_DELAY;
		}

		Log.Message($"CalculatePotentialWork at {pawn.Position} found {Cache.Count} items");
	}

	public void ForceCleanup()
	{
		Cache.Clear();
		_nextWorkCacheTick = 0;
	}

	public string GetDebugInfo() => $"Number of items in cache: {Cache.Count}";
}