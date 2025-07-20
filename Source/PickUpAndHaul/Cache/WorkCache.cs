namespace PickUpAndHaul.Cache;

public class WorkCache : ICache
{
	private const int TICKS_DELAY = 60;
	private int _nextWorkCacheTick;
	private readonly object _lockObject = new();

	static WorkCache() =>
		CacheManager.RegisterCache(Instance);

	public static WorkCache Instance { get; } = new();

	public static List<Thing> Cache { get; private set; } = [];

	public void CalculatePotentialWork(Pawn pawn)
	{
		var currentTick = Find.TickManager.TicksGame;
		if (currentTick < _nextWorkCacheTick)
			return;

		var list = new List<Thing>(pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling());
		// Ensure items are sorted by distance from pawn to prioritize closest items
		Comparer.RootCell = pawn.Position;
		list.Sort(Comparer);
		_nextWorkCacheTick = currentTick + list.Count + TICKS_DELAY;
		lock (_lockObject)
			Cache = list;

		if (Settings.EnableDebugLogging)
			Log.Message($"CalculatePotentialWork at {pawn.Position} found {list.Count} items, first item: {list.FirstOrDefault()?.Position}");
	}

	public void ForceCleanup()
	{
		Cache.Clear();
		_nextWorkCacheTick = 0;
	}

	public string GetDebugInfo() => $"Number of items in cache: {Cache.Count}";

	private static ThingPositionComparer Comparer { get; } = new();

	private class ThingPositionComparer : IComparer<Thing>
	{
		public IntVec3 RootCell { get; set; }

		public int Compare(Thing x, Thing y) => (x.Position - RootCell).LengthHorizontalSquared.CompareTo((y.Position - RootCell).LengthHorizontalSquared);
	}
}