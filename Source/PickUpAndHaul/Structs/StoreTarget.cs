namespace PickUpAndHaul.Structs;
public struct StoreTarget : IEquatable<StoreTarget>
{
	public IntVec3 Cell { get; set; }
	public Thing Container { get; set; }

	public readonly IntVec3 Position => Container?.Position ?? Cell;

	public StoreTarget(IntVec3 cell)
	{
		Cell = cell;
		Container = null;
	}
	public StoreTarget(Thing container)
	{
		Cell = default;
		Container = container;
	}

	public readonly bool Equals(StoreTarget other) => Container is null ? other.Container is null && Cell == other.Cell : Container == other.Container;
	public override int GetHashCode() => Container?.GetHashCode() ?? Cell.GetHashCode();
	public override string ToString() => Container?.ToString() ?? Cell.ToString();
	public override readonly bool Equals(object obj) => obj is StoreTarget target ? Equals(target) : obj is Thing thing ? Container == thing : obj is IntVec3 intVec && Cell == intVec;
	public static bool operator ==(StoreTarget left, StoreTarget right) => left.Equals(right);
	public static bool operator !=(StoreTarget left, StoreTarget right) => !left.Equals(right);
	public static implicit operator LocalTargetInfo(StoreTarget target) => target.Container != null ? target.Container : target.Cell;
}
