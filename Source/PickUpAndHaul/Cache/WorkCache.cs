namespace PickUpAndHaul.Cache;

public class WorkCache : ICache
{
	private const int TICKS_DELAY = 60;

	static WorkCache() =>
		CacheManager.RegisterCache(Instance);

	public static WorkCache Instance { get; } = new();

	public static ConcurrentQueue<Thing> Cache { get; private set; } = [];

	public static int NextWorkCacheTick { get; private set; }

	public static ConcurrentQueue<Thing> CalculatePotentialWork(Pawn pawn)
	{
		var currentTick = Find.TickManager.TicksGame;

		if (currentTick < NextWorkCacheTick)
		{
			return Cache;
		}

		var allHaulables = pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling();

		// Ensure things are sorted by distance from pawn - using exact same logic as original
		Cache = new ConcurrentQueue<Thing>([.. allHaulables
			.Where(x => x.Map != null && HaulAIUtility.PawnCanAutomaticallyHaul(pawn, x, false))
			.OrderBy((x) => (x.Position - pawn.Position).LengthHorizontalSquared)]);

		NextWorkCacheTick = currentTick + Cache.Count + TICKS_DELAY;

		Log.Message($"Recalculated cache with {Cache.Count} items for {pawn.Name?.ToStringShort ?? "Unknown"}");
		return Cache;
	}

	public void ForceCleanup()
	{
		Cache.Clear();
		NextWorkCacheTick = 0;
	}

	public string GetDebugInfo() => $"Number of items in cache: {Cache.Count}";
}