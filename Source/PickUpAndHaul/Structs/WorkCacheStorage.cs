namespace PickUpAndHaul.Structs;
public struct WorkCacheStorage(StoragePriority priority, Thing thing) : IEquatable<WorkCacheStorage>
{
	public StoragePriority Priority { get; private set; } = priority;
	public Thing Thing { get; private set; } = thing;

	public override bool Equals(object obj) => obj is WorkCacheStorage storage && Equals(storage);
	public bool Equals(WorkCacheStorage other) => Priority == other.Priority && EqualityComparer<Thing>.Default.Equals(Thing, other.Thing);
	public override int GetHashCode() => HashCode.Combine(Priority, Thing);

	public static bool operator ==(WorkCacheStorage left, WorkCacheStorage right) => left.Equals(right);
	public static bool operator !=(WorkCacheStorage left, WorkCacheStorage right) => !(left == right);
}
