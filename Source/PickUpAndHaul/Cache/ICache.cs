namespace PickUpAndHaul.Cache;

/// <summary>
/// Interface for caches that can be managed by CacheManager
/// </summary>
public interface ICache
{
	/// <summary>
	/// Forces a cleanup of the cache
	/// </summary>
	void ForceCleanup();

	/// <summary>
	/// Gets debug information about the cache
	/// </summary>
	string GetDebugInfo();
}