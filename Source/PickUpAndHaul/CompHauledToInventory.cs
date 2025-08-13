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
		takenToInventory.RemoveWhere(x => x == null);
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
		// prune expired entries opportunistically
		var expired = recentlyUnloadedUntilTick.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
		for (var i = 0; i < expired.Count; i++) recentlyUnloadedUntilTick.Remove(expired[i]);
		return recentlyUnloadedUntilTick.TryGetValue(thing, out var until) && until > now;
	}

	public override void PostExposeData()
	{
		base.PostExposeData();
		Scribe_Collections.Look(ref takenToInventory, "ThingsHauledToInventory", LookMode.Reference);
		Scribe_Values.Look(ref isUnloading, "PUAH_IsUnloading", false);
	}
}