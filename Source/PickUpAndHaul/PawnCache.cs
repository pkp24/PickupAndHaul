using System.Collections.Concurrent;

namespace PickUpAndHaul;

/// <summary>
/// Generic cache for pawn-based data using weak references to prevent memory leaks
/// </summary>
/// <typeparam name="T">The type of data to cache per pawn</typeparam>
public class PawnCache<T> : ICache
{
	private readonly ConcurrentDictionary<Pawn, T> _pawns = new();

	/// <summary>
	/// Sets a value for a pawn in the cache
	/// </summary>
	public void Set(Pawn pawn, T value)
	{
		if (pawn == null)
			return;

		if (_pawns.TryGetValue(pawn, out var oldValue))
			_pawns.TryUpdate(pawn, value, oldValue);

		_pawns.TryAdd(pawn, value);
	}

	/// <summary>
	/// Tries to get a value for a pawn from the cache
	/// </summary>
	public bool TryGet(Pawn pawn, out T value)
		=> _pawns.TryGetValue(pawn, out value);

	/// <summary>
	/// Forces a cleanup of dead references (for testing or manual cleanup)
	/// </summary>
	public void ForceCleanup() => _pawns.Clear();
}