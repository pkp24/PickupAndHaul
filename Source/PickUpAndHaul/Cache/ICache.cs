namespace PickUpAndHaul.Cache;

public interface ICache
{
	void ForceCleanup();
	string GetDebugInfo();
}