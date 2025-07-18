namespace PickUpAndHaul.Structs;

/// <summary>
/// Represents a storage location (either a cell or a container thing)
/// </summary>
public readonly struct StorageLocation : IEquatable<StorageLocation>
{
	public IntVec3 Cell { get; }
	public Thing Container { get; }

	public StorageLocation(IntVec3 cell)
	{
		Cell = cell;
		Container = null;
	}

	public StorageLocation(Thing container)
	{
		Cell = IntVec3.Invalid;
		Container = container;
	}

	public bool Equals(StorageLocation other) => Container != null ? Container == other.Container : Cell == other.Cell;

	public override bool Equals(object obj) => obj is StorageLocation other && Equals(other);

	public override int GetHashCode() => Container?.GetHashCode() ?? Cell.GetHashCode();

	public override string ToString() => Container?.ToString() ?? Cell.ToString();
	public static bool operator ==(StorageLocation left, StorageLocation right) => left.Equals(right);

	public static bool operator !=(StorageLocation left, StorageLocation right) => !(left == right);
}
