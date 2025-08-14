using System.Collections.Generic;
using System.Linq;

namespace PickUpAndHaul;

public class CompHauledToInventory : ThingComp
{
	private HashSet<Thing> takenToInventory = [];
	private bool isUnloading;
	private Dictionary<Thing, int> recentlyUnloadedUntilTick = new();

	public HashSet<Thing> GetHashSet()
	{
		takenToInventory.RemoveWhere(x => x == null || x.Destroyed);
		return takenToInventory;
	}

	public void RegisterHauledItem(Thing thing) => takenToInventory.Add(thing);

	public bool IsUnloading() => isUnloading;
	public void SetUnloading(bool unloading) => isUnloading = unloading;

	public void MarkRecentlyUnloaded(Thing thing, int durationTicks)
	{
		if (thing == null) return;
		recentlyUnloadedUntilTick[thing] = Find.TickManager.TicksGame + durationTicks;
	}

	public bool IsRecentlyUnloaded(Thing thing)
	{
		if (thing == null) return false;
		var now = Find.TickManager.TicksGame;
		PruneExpiredAndDestroyed(now);
		return recentlyUnloadedUntilTick.TryGetValue(thing, out var until) && until > now;
	}

	private void PruneExpiredAndDestroyed(int? nowOpt = null)
	{
		if (recentlyUnloadedUntilTick == null || recentlyUnloadedUntilTick.Count == 0) return;
		var now = nowOpt ?? Find.TickManager.TicksGame;
		var toRemove = recentlyUnloadedUntilTick
			.Where(kv => kv.Key == null || kv.Key.Destroyed || kv.Value <= now)
			.Select(kv => kv.Key)
			.ToList();
		for (var i = 0; i < toRemove.Count; i++) recentlyUnloadedUntilTick.Remove(toRemove[i]);
	}

	public override void PostExposeData()
	{
		base.PostExposeData();
		Scribe_Collections.Look(ref takenToInventory, "ThingsHauledToInventory", LookMode.Reference);
		Scribe_Values.Look(ref isUnloading, "PUAH_IsUnloading", false);
		List<Thing> recentlyUnloadedKeys = Scribe.mode == LoadSaveMode.LoadingVars ? new List<Thing>() : null;
		List<int> recentlyUnloadedValues = Scribe.mode == LoadSaveMode.LoadingVars ? new List<int>() : null;
		Scribe_Collections.Look(ref recentlyUnloadedUntilTick, "PUAH_RecentlyUnloadedUntilTick", LookMode.Reference, LookMode.Value, ref recentlyUnloadedKeys, ref recentlyUnloadedValues);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			recentlyUnloadedUntilTick ??= new Dictionary<Thing, int>();
			PruneExpiredAndDestroyed();
		}
	}

	public override void CompTickRare()
	{
		base.CompTickRare();
		PruneExpiredAndDestroyed();
	}
}