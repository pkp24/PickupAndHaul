using System.Collections.Generic;
using Verse;
using Verse.AI;

namespace PickUpAndHaul;

/// <summary>
/// Custom job class that encapsulates all hauling data to prevent desync issues
/// </summary>
public class HaulToInventoryJob : Job
{
	private List<HaulItem> _haulItems;
	private List<LocalTargetInfo> _storageTargets;
	private Dictionary<StorageAllocationTracker.StorageLocation, int> _storageReservations;

	public HaulToInventoryJob() : base()
	{
		_haulItems = new List<HaulItem>();
		_storageTargets = new List<LocalTargetInfo>();
		_storageReservations = new Dictionary<StorageAllocationTracker.StorageLocation, int>();
	}

	public HaulToInventoryJob(JobDef jobDef) : base(jobDef)
	{
		_haulItems = new List<HaulItem>();
		_storageTargets = new List<LocalTargetInfo>();
		_storageReservations = new Dictionary<StorageAllocationTracker.StorageLocation, int>();
	}

	/// <summary>
	/// Gets the haul items in a thread-safe manner
	/// </summary>
	public List<HaulItem> HaulItems
	{
		get
		{
			lock (this)
			{
				return new List<HaulItem>(_haulItems);
			}
		}
	}

	/// <summary>
	/// Gets the storage targets in a thread-safe manner
	/// </summary>
	public List<LocalTargetInfo> StorageTargets
	{
		get
		{
			lock (this)
			{
				return new List<LocalTargetInfo>(_storageTargets);
			}
		}
	}

	/// <summary>
	/// Adds a haul item atomically
	/// </summary>
	public void AddHaulItem(Thing thing, int count, StorageAllocationTracker.StorageLocation storageLocation)
	{
		lock (this)
		{
			var haulItem = new HaulItem
			{
				Thing = thing,
				Count = count,
				StorageLocation = storageLocation
			};

			_haulItems.Add(haulItem);

			// Add to storage targets if not already present
			var target = storageLocation.Container != null
				? new LocalTargetInfo(storageLocation.Container)
				: new LocalTargetInfo(storageLocation.Cell);

			if (!_storageTargets.Contains(target))
			{
				_storageTargets.Add(target);
			}

			// Track storage reservation
			if (!_storageReservations.ContainsKey(storageLocation))
			{
				_storageReservations[storageLocation] = 0;
			}
			_storageReservations[storageLocation] += count;
		}
	}

	/// <summary>
	/// Removes a haul item atomically
	/// </summary>
	public bool RemoveHaulItem(Thing thing)
	{
		lock (this)
		{
			for (int i = 0; i < _haulItems.Count; i++)
			{
				if (_haulItems[i].Thing == thing)
				{
					var item = _haulItems[i];
					_haulItems.RemoveAt(i);

					// Update storage reservation
					if (_storageReservations.ContainsKey(item.StorageLocation))
					{
						_storageReservations[item.StorageLocation] -= item.Count;
						if (_storageReservations[item.StorageLocation] <= 0)
						{
							_storageReservations.Remove(item.StorageLocation);
						}
					}

					return true;
				}
			}
			return false;
		}
	}

	/// <summary>
	/// Gets the next haul item and removes it from the queue
	/// </summary>
	public HaulItem GetNextHaulItem()
	{
		lock (this)
		{
			if (_haulItems.Count > 0)
			{
				var item = _haulItems[0];
				_haulItems.RemoveAt(0);

				// Update storage reservation
				if (_storageReservations.ContainsKey(item.StorageLocation))
				{
					_storageReservations[item.StorageLocation] -= item.Count;
					if (_storageReservations[item.StorageLocation] <= 0)
					{
						_storageReservations.Remove(item.StorageLocation);
					}
				}

				return item;
			}
			return null;
		}
	}

	/// <summary>
	/// Validates that all data is consistent
	/// </summary>
	public bool IsValid()
	{
		lock (this)
		{
			// Check that we have items
			if (_haulItems.Count == 0)
			{
				return false;
			}

			// Check that all items have valid things
			foreach (var item in _haulItems)
			{
				if (item.Thing == null || item.Thing.Destroyed || !item.Thing.Spawned)
				{
					return false;
				}

				if (item.Count <= 0)
				{
					return false;
				}
			}

			// Check that storage reservations match items
			var expectedReservations = new Dictionary<StorageAllocationTracker.StorageLocation, int>();
			foreach (var item in _haulItems)
			{
				if (!expectedReservations.ContainsKey(item.StorageLocation))
				{
					expectedReservations[item.StorageLocation] = 0;
				}
				expectedReservations[item.StorageLocation] += item.Count;
			}

			foreach (var kvp in expectedReservations)
			{
				if (!_storageReservations.ContainsKey(kvp.Key) || _storageReservations[kvp.Key] != kvp.Value)
				{
					return false;
				}
			}

			return true;
		}
	}

	/// <summary>
	/// Releases all storage reservations
	/// </summary>
	public void ReleaseAllReservations(Pawn pawn)
	{
		lock (this)
		{
			foreach (var kvp in _storageReservations)
			{
				StorageAllocationTracker.Instance.ReleaseCapacity(kvp.Key, null, kvp.Value, pawn);
			}
			_storageReservations.Clear();
		}
	}

	/// <summary>
	/// Gets debug information about the job
	/// </summary>
	public string GetDebugInfo()
	{
		lock (this)
		{
			var info = new System.Text.StringBuilder();
			info.AppendLine($"HaulToInventoryJob: {_haulItems.Count} items, {_storageTargets.Count} targets");

			for (int i = 0; i < _haulItems.Count; i++)
			{
				var item = _haulItems[i];
				info.AppendLine($"  Item {i}: {item.Thing} x{item.Count} -> {item.StorageLocation}");
			}

			info.AppendLine("Storage reservations:");
			foreach (var kvp in _storageReservations)
			{
				info.AppendLine($"  {kvp.Key}: {kvp.Value}");
			}

			return info.ToString();
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
			_haulItems = new List<HaulItem>();
			_storageTargets = new List<LocalTargetInfo>();
			_storageReservations = new Dictionary<StorageAllocationTracker.StorageLocation, int>();
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