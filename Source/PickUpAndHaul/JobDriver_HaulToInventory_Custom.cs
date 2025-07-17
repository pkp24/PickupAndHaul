using System.Collections.Concurrent;
using System.Linq;
using System.Text;

namespace PickUpAndHaul;

/// <summary>
/// Custom job class that encapsulates all hauling data to prevent desync issues
/// </summary>
public class HaulToInventoryJob : Job
{
	public HaulToInventoryJob() : base()
	{
		HaulItems = [];
		StorageTargets = [];
		StorageReservations = new ConcurrentDictionary<StorageAllocationTracker.StorageLocation, int>();
	}

	public HaulToInventoryJob(JobDef jobDef) : base(jobDef)
	{
		HaulItems = [];
		StorageTargets = [];
		StorageReservations = new ConcurrentDictionary<StorageAllocationTracker.StorageLocation, int>();
	}

	/// <summary>
	/// Gets the haul items in a thread-safe manner
	/// </summary>
	public ConcurrentBag<HaulItem> HaulItems;

	/// <summary>
	/// Gets the storage targets in a thread-safe manner
	/// </summary>
	public ConcurrentBag<LocalTargetInfo> StorageTargets;

	public ConcurrentDictionary<StorageAllocationTracker.StorageLocation, int> StorageReservations;

	/// <summary>
	/// Adds a haul item atomically
	/// </summary>
	public void AddHaulItem(Thing thing, int count, StorageAllocationTracker.StorageLocation storageLocation)
	{
		var haulItem = new HaulItem
		{
			Thing = thing,
			Count = count,
			StorageLocation = storageLocation
		};

		HaulItems.Add(haulItem);

		// Add to storage targets if not already present
		var target = storageLocation.Container != null
			? new LocalTargetInfo(storageLocation.Container)
			: new LocalTargetInfo(storageLocation.Cell);

		if (!StorageTargets.Contains(target))
			StorageTargets.Add(target);

		// Track storage reservation
		if (StorageReservations.TryGetValue(storageLocation, out var resultStorageReservations))
		{
			var oldStorageReservations = resultStorageReservations;
			resultStorageReservations += count;
			StorageReservations.TryUpdate(storageLocation, resultStorageReservations, oldStorageReservations);
		}
		GetDebugInfo();
	}

	/// <summary>
	/// Removes a haul item atomically
	/// </summary>
	public bool RemoveHaulItem(Thing thing)
	{
		for (var i = 0; i < HaulItems.Count; i++)
		{
			HaulItems.TryPeek(out var result);
			if (result.Thing == thing)
			{
				HaulItems.TryTake(out var item);

				if (item == null)
					return false;

				if (StorageReservations.TryGetValue(item.StorageLocation, out var storageLocation))
				{
					var oldStorageLocation = storageLocation;
					storageLocation -= item.Count;
					if (storageLocation <= 0)
						StorageReservations.TryRemove(item.StorageLocation, out _);
					else
						StorageReservations.TryUpdate(item.StorageLocation, storageLocation, oldStorageLocation);
				}

				return true;
			}
		}
		GetDebugInfo();
		return false;
	}

	/// <summary>
	/// Gets the next haul item and removes it from the queue
	/// </summary>
	public HaulItem GetNextHaulItem()
	{
		if (!HaulItems.IsEmpty)
		{
			HaulItems.TryPeek(out var item);

			// Update storage reservation
			if (StorageReservations.TryGetValue(item.StorageLocation, out var storageLocation))
			{
				var oldStorageLocation = storageLocation;
				storageLocation -= item.Count;
				if (storageLocation <= 0)
					StorageReservations.TryRemove(item.StorageLocation, out _);
				else
					StorageReservations.TryUpdate(item.StorageLocation, storageLocation, oldStorageLocation);
			}

			return item;
		}
		return null;
	}

	/// <summary>
	/// Validates that all data is consistent
	/// </summary>
	public bool IsValid()
	{
		// Check that we have items
		if (HaulItems.IsEmpty)
			return false;

		// Check that all items have valid things
		foreach (var item in HaulItems)
		{
			if (item.Thing == null || item.Thing.Destroyed || !item.Thing.Spawned)
				return false;

			if (item.Count <= 0)
				return false;
		}

		// Check that storage reservations match items
		var expectedReservations = new Dictionary<StorageAllocationTracker.StorageLocation, int>();
		foreach (var item in HaulItems)
		{
			if (!expectedReservations.ContainsKey(item.StorageLocation))
			{
				expectedReservations[item.StorageLocation] = 0;
			}
			expectedReservations[item.StorageLocation] += item.Count;
		}

		foreach (var kvp in expectedReservations)
		{
			if (!StorageReservations.TryGetValue(kvp.Key, out var value) || value != kvp.Value)
			{
				return false;
			}
		}

		return true;
	}

	/// <summary>
	/// Releases all storage reservations
	/// </summary>
	public void ReleaseAllReservations(Pawn pawn)
	{
		foreach (var kvp in StorageReservations)
			StorageAllocationTracker.Instance.ReleaseCapacity(kvp.Key, null, kvp.Value, pawn);
		StorageReservations.Clear();
	}

	/// <summary>
	/// Gets debug information about the job
	/// </summary>
	public void GetDebugInfo()
	{
		if (Settings.EnableDebugLogging)
		{
			var info = new StringBuilder();
			info.AppendLine($"HaulToInventoryJob: {HaulItems.Count} items, {StorageTargets.Count} targets");

			foreach (var item in HaulItems)
				info.AppendLine($"  Item: {item.Thing} x{item.Count} -> {item.StorageLocation}");

			info.AppendLine("Storage reservations:");
			foreach (var kvp in StorageReservations)
				info.AppendLine($"  {kvp.Key}: {kvp.Value}");

			Log.Message(info.ToString());
		}
	}

	public new void ExposeData()
	{
		// Don't save custom job data to prevent save corruption
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			Log.Message("[PickUpAndHaul] Skipping save data for HaulToInventoryJob");
			return;
		}

		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			Log.Message("[PickUpAndHaul] Skipping load data for HaulToInventoryJob");
			HaulItems = [];
			StorageTargets = [];
			StorageReservations = new ConcurrentDictionary<StorageAllocationTracker.StorageLocation, int>();
			return;
		}

		base.ExposeData();
	}
}

/// <summary>
/// Represents a single item to be hauled
/// </summary>
public class HaulItem
{
	public Thing Thing { get; set; }
	public int Count { get; set; }
	public StorageAllocationTracker.StorageLocation StorageLocation { get; set; }
}