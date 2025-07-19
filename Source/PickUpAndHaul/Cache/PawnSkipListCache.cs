namespace PickUpAndHaul.Cache;
/// <summary>
/// Cache wrapper for pawn skip lists to integrate with CacheManager
/// </summary>
internal class PawnSkipListCache : ICache
{
	static PawnSkipListCache() =>
		CacheManager.RegisterCache(Instance);

	public static ConcurrentDictionary<Pawn, HashSet<IntVec3>> PawnSkipCells { get; } = [];
	public static ConcurrentDictionary<Pawn, HashSet<Thing>> PawnSkipThings { get; } = [];

	public static PawnSkipListCache Instance { get; } = new();

	public void ForceCleanup() => CleanupPawnSkipLists();

	public string GetDebugInfo() => $"Pawn skip lists: {PawnSkipCells.Count} pawns tracked";

	/// <summary>
	/// Cleans up pawn-specific skip lists for dead or invalid pawns
	/// </summary>
	private static void CleanupPawnSkipLists()
	{
		foreach (var pawn in PawnSkipCells.Keys)
		{
			if (pawn == null || pawn.Dead || pawn.Destroyed)
			{
				PawnSkipCells.Remove(pawn, out var _);
				PawnSkipThings.Remove(pawn, out var _);
				if (Settings.EnableDebugLogging)
					Log.Message($"Cleaned up skip lists for {pawn} pawn");
			}
		}
	}
}
