using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using PickUpAndHaul; // for debug logging
using RimWorld;
using Verse;
using Verse.AI;

namespace PartialReservationSystem
{
	/// <summary>
	/// Optional wrapper for HoldMultipleThings integration.
	/// Uses reflection so the mod can compile and run without the dependency.
	/// </summary>
	internal static class HoldMultipleThingsSupportWrapper
	{
		private static bool _initialized;
		private static MethodInfo _capacityAt;

		private static void EnsureInit()
		{
			if (_initialized) return;
			_initialized = true;

			try
			{
				// Try to find a type that exposes capacity checks. Common patterns:
				// - HoldMultipleThings.Support
				// - IHoldMultipleThings.HoldMultipleThings_Support
				// - PickUpAndHaul.HoldMultipleThings_Support
				var type =
					AccessTools.TypeByName("HoldMultipleThings.Support") ??
					AccessTools.TypeByName("IHoldMultipleThings.HoldMultipleThings_Support") ??
					AccessTools.TypeByName("PickUpAndHaul.HoldMultipleThings_Support") ??
					AccessTools.TypeByName("HoldMultipleThings_Support");

				if (type != null)
				{
					// Expected signature: static bool CapacityAt(Thing probe, IntVec3 cell, Map map, out int capacity)
					_capacityAt = AccessTools.Method(type, "CapacityAt", new[] {
						typeof(Thing), typeof(IntVec3), typeof(Map), typeof(int).MakeByRefType()
					});
				}
			}
			catch
			{
				_capacityAt = null;
			}
		}

		public static bool CapacityAt(Thing probe, IntVec3 cell, Map map, out int capacity)
		{
			EnsureInit();
			capacity = 0;

			if (_capacityAt == null || probe == null || map == null)
				return false;

			try
			{
				var args = new object[] { probe, cell, map, 0 };
				var ok = (bool)(_capacityAt.Invoke(null, args) ?? false);
				if (ok) capacity = (int)args[3];
				return ok;
			}
			catch
			{
				capacity = 0;
				return false;
			}
		}
	}

	/// <summary>
	/// Custom reservation system that allows partial reservations of storage locations
	/// </summary>
	public static class PRSReservationSystem
	{
		// Storage reservations by map
		private static readonly Dictionary<int, Dictionary<StorageLocation, StorageReservation>> _storageReservations = new();
		
		// Lock object for thread safety
		private static readonly object _reservationLock = new();

		/// <summary>
		/// Represents a storage location (either a cell or a container)
		/// </summary>
		public struct StorageLocation : IEquatable<StorageLocation>
		{
			public IntVec3 Cell { get; }
			public Thing Container { get; }
			public bool IsContainer => Container != null;

			// Stable identities
			private readonly int _mapId;
			private readonly int _thingId; // valid only when IsContainer

			public StorageLocation(IntVec3 cell)
			{
				Cell = cell;
				Container = null;
				_mapId = 0;
				_thingId = 0;
			}

			public StorageLocation(IntVec3 cell, Map map)
			{
				Cell = cell;
				Container = null;
				_mapId = map?.uniqueID ?? 0;
				_thingId = 0;
			}

			public StorageLocation(Thing container)
			{
				Cell = IntVec3.Invalid;
				Container = container;
				_mapId = container?.Map?.uniqueID ?? 0;
				// Use ThingID to get a stable unique identifier for the Thing
				_thingId = container != null ? container.thingIDNumber : 0;
			}

			public bool Equals(StorageLocation other)
			{
				if (IsContainer || other.IsContainer)
				{
					return IsContainer && other.IsContainer
					   && _mapId == other._mapId
					   && _thingId == other._thingId;
				}
				return !IsContainer && !other.IsContainer
				   && _mapId == other._mapId
				   && Cell == other.Cell;
			}

			public override bool Equals(object obj)
			{
				return obj is StorageLocation sl && Equals(sl);
			}

			public override int GetHashCode()
			{
				unchecked
				{
					if (IsContainer)
					{
						int hash = 17;
						hash = hash * 31 + _mapId;
						hash = hash * 31 + _thingId;
						return hash;
					}
					else
					{
						int hash = 17;
						hash = hash * 31 + _mapId;
						hash = hash * 31 + Cell.GetHashCode();
						return hash;
					}
				}
			}

			public override string ToString()
			{
				string mapStr = $"mapId={_mapId}";
				return IsContainer
					? $"Container:{Container} ({mapStr} thingId={_thingId})"
					: $"Cell:{Cell} ({mapStr})";
			}
		}

		/// <summary>
		/// Tracks reservations for a specific storage location
		/// </summary>
		public class StorageReservation
		{
			private readonly Dictionary<Pawn, List<ReservationEntry>> _reservations = new();
			private int _totalReservedCount = 0;
			
			public int TotalReservedCount => _totalReservedCount;
			
			/// <summary>
			/// Individual reservation entry
			/// </summary>
			public class ReservationEntry
			{
				public Thing Thing { get; }
				public int Count { get; }
				public Job Job { get; }
				public int TickCreated { get; }
				
				public ReservationEntry(Thing thing, int count, Job job)
				{
					Thing = thing;
					Count = count;
					Job = job;
					TickCreated = Find.TickManager.TicksGame;
				}
			}
			
			/// <summary>
			/// Try to add a reservation
			/// </summary>
			public bool TryAddReservation(Pawn pawn, Thing thing, int count, Job job)
			{
				if (!_reservations.TryGetValue(pawn, out var entries))
				{
					entries = new List<ReservationEntry>();
					_reservations[pawn] = entries;
				}
				
				entries.Add(new ReservationEntry(thing, count, job));
				_totalReservedCount += count;
				return true;
			}
			
			/// <summary>
			/// Remove all reservations for a pawn
			/// </summary>
			public void RemoveReservationsForPawn(Pawn pawn)
			{
				if (_reservations.TryGetValue(pawn, out var entries))
				{
					foreach (var entry in entries)
					{
						_totalReservedCount -= entry.Count;
					}
					_reservations.Remove(pawn);
				}
			}
			
			/// <summary>
			/// Remove reservations for a specific job
			/// </summary>
			public void RemoveReservationsForJob(Pawn pawn, Job job)
			{
				if (_reservations.TryGetValue(pawn, out var entries))
				{
					var toRemove = entries.Where(e => e.Job == job).ToList();
					foreach (var entry in toRemove)
					{
						_totalReservedCount -= entry.Count;
						entries.Remove(entry);
					}
					
					if (entries.Count == 0)
					{
						_reservations.Remove(pawn);
					}
				}
			}
			
			/// <summary>
			/// Get reserved count for a specific thing type
			/// </summary>
			public int GetReservedCountForThingDef(ThingDef def)
			{
				var count = 0;
				foreach (var entries in _reservations.Values)
				{
					foreach (var entry in entries)
					{
						if (entry.Thing.def == def)
						{
							count += entry.Count;
						}
					}
				}
				return count;
			}

			/// <summary>
			/// Get how many of the specified thing a particular pawn has reserved
			/// </summary>
			public int GetReservedCountForPawn(Pawn pawn, ThingDef def)
			{
				if (pawn == null || def == null)
					return 0;

				if (!_reservations.TryGetValue(pawn, out var entries))
					return 0;

				int count = 0;
				foreach (var entry in entries)
				{
					if (entry.Thing.def == def)
					{
						count += entry.Count;
					}
				}
				return count;
			}
			
			/// <summary>
			/// Check if this storage has any reservations
			/// </summary>
			public bool HasAnyReservations => _reservations.Count > 0;
			
			/// <summary>
			/// Clean up expired reservations
			/// </summary>
			public void CleanupExpiredReservations()
			{
				var currentTick = Find.TickManager.TicksGame;
				var pawnsToRemove = new List<Pawn>();
				
				foreach (var kvp in _reservations)
				{
					var pawn = kvp.Key;
					var entries = kvp.Value;
					var stale = new List<ReservationEntry>();
					
					foreach (var entry in entries)
					{
						var job = pawn?.CurJob;

						var currentJob = pawn?.CurJob;
						bool isJobEnqueued = false;
						try
						{
							var queue = pawn?.jobs?.jobQueue;
							if (queue != null)
							{
								foreach (var q in queue)
								{
									if (q.job == entry.Job) { isJobEnqueued = true; break; }
								}
							}
						}
						catch { }

						bool stillValid =
							pawn != null &&
							pawn.Spawned &&
							!pawn.Dead &&
							!pawn.Destroyed &&
							(currentJob == entry.Job || isJobEnqueued);    // same job or queued for this pawn

						// If pawn has the same job, keep reservation regardless of carrying status
						// This protects pawns who are on their way to pick up items
						if (pawn == null || pawn.Dead || pawn.Destroyed || !stillValid)
							stale.Add(entry);
					}
						
					foreach (var entry in stale)
					{
						_totalReservedCount -= entry.Count;
						entries.Remove(entry);
						Log.Warning($"[PRS DEBUG] CLEANUP removed {entry.Count}x{entry.Thing?.def?.defName} "
							+ $"dest={kvp.Key} byPawn={pawn?.LabelShort ?? "null"} "
							+ $"reason: pawnAlive={(pawn != null && !pawn.Dead)} "
							+ $"jobMatch={(entry.Job == pawn?.CurJob)} "
							+ $"thingSpawned={(entry.Thing?.Spawned ?? false)}");
					}
					
					if (entries.Count == 0)
					{
						pawnsToRemove.Add(pawn);
					}
				}
				
				foreach (var pawn in pawnsToRemove)
				{
					_reservations.Remove(pawn);
				}
			}

			/// <summary>
			/// Check if a specific pawn already has any reservation on this storage location
			/// </summary>
			public bool HasReservationByPawn(Pawn pawn)
				=> pawn != null && _reservations.ContainsKey(pawn);
			
			/// <summary>
			/// Whether this reservation entry has any reservations regardless of pawn
			/// </summary>
			public bool Any => _reservations.Count > 0;
		}

		/// <summary>
		/// Get or create storage reservations for a map
		/// </summary>
		private static Dictionary<StorageLocation, StorageReservation> GetMapReservations(Map map)
		{
			lock (_reservationLock)
			{
				if (!_storageReservations.TryGetValue(map.uniqueID, out var reservations))
				{
					reservations = new Dictionary<StorageLocation, StorageReservation>();
					_storageReservations[map.uniqueID] = reservations;
				}
				return reservations;
			}
		}

		/// <summary>
		/// Retrieve the reservation bucket for a specific storage location on a map
		/// </summary>
		public static StorageReservation GetReservationForLocation(StorageLocation location, Map map)
		{
			if (map == null)
				return null;

			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				reservations.TryGetValue(location, out var reservation);
				return reservation;
			}
		}

		/// <summary>
		/// Get or create the reservation bucket for a specific storage location on a map
		/// </summary>
		internal static StorageReservation GetReservationForLocation(
			StorageLocation loc, Map map, bool createIfMissing)
		{
			var dict = GetMapReservations(map);          // existing private helper
			if (dict.TryGetValue(loc, out var existing))
				return (StorageReservation)existing;

			if (!createIfMissing)               // caller only wanted to probe
				return null;

			var bucket = new StorageReservation();
			dict[loc] = bucket;
			return bucket;
		}

		/// <summary>
		/// Try to reserve storage space for a thing
		/// </summary>
		public static bool TryReserveStorage(Pawn pawn, Thing thing, StorageLocation location, Job job, Map map)
		{
			if (pawn == null || thing == null || job == null || map == null)
				return false;

			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				
				// Get or create reservation for this location
				if (!reservations.TryGetValue(location, out var reservation))
				{
					reservation = new StorageReservation();
					reservations[location] = reservation;
				}
				
				// Clean up expired reservations first
				reservation.CleanupExpiredReservations();
				
				// Check if we have capacity - allow partial reservations
				var capacity = GetAvailableCapacity(location, thing, map, reservation);
				if (capacity <= 0)
				{
					return false;
				}
				
				// Calculate how much the pawn will actually haul
				int haulAmount = CalculateHaulAmount(pawn, thing);
				
				// Reserve only what will be hauled, limited by available capacity
				var countToReserve = Math.Min(capacity, haulAmount);
				
				// Add the reservation
				return reservation.TryAddReservation(pawn, thing, countToReserve, job);
			}
		}

		/// <summary>
		/// Try to reserve partial storage space
		/// </summary>
		public static bool TryReservePartialStorage(Pawn pawn, Thing thing, int count, StorageLocation location, Job job, Map map)
		{
			if (pawn == null || thing == null || job == null || map == null || count <= 0)
				return false;

			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				
				// Get or create reservation for this location
				if (!reservations.TryGetValue(location, out var reservation))
				{
					reservation = new StorageReservation();
					reservations[location] = reservation;
				}
				
				// Clean up expired reservations first
				reservation.CleanupExpiredReservations();
				
				// Check if we have capacity - use the reservation object we already have to avoid redundant cleanup
				var capacity = GetAvailableCapacity(location, thing, map, reservation);
				var pending = PendingHaulTracker.GetPendingAmount(location, thing.def, map);
				var alreadyReservedByPawn = reservation.GetReservedCountForPawn(pawn, thing.def);
				var netAvailable = Math.Max(0, capacity - pending);
				
				// Calculate the actual haul amount and limit by requested count and net available
				int haulAmount = CalculateHaulAmount(pawn, thing);
				int actualCount = Math.Min(count, Math.Min(haulAmount, netAvailable));
				
				if (actualCount <= 0)
				{
					return false;
				}
				
				// Add the reservation
				return reservation.TryAddReservation(pawn, thing, actualCount, job);
			}
		}

		/// <summary>
		/// Release all reservations for a pawn's job
		/// </summary>
		public static void ReleaseAllReservationsForJob(Pawn pawn, Job job, Map map)
		{
			if (pawn == null || job == null || map == null)
				return;

			Log.Warning($"[PRS DEBUG] ReleaseAllReservationsForJob called by {pawn?.LabelShort} job={job?.def?.defName} reason={new System.Diagnostics.StackTrace(1,true)}");

			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				var locationsToRemove = new List<StorageLocation>();
				
				foreach (var kvp in reservations)
				{
					kvp.Value.RemoveReservationsForJob(pawn, job);
					
					// Remove empty reservations
					if (!kvp.Value.HasAnyReservations)
					{
						locationsToRemove.Add(kvp.Key);
					}
				}
				
				foreach (var location in locationsToRemove)
				{
					reservations.Remove(location);
				}
			}
		}

		/// <summary>
		/// Release all reservations for a pawn
		/// </summary>
		public static void ReleaseAllReservationsForPawn(Pawn pawn, Map map)
		{
			if (pawn == null || map == null)
				return;

			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				var locationsToRemove = new List<StorageLocation>();
				
				foreach (var kvp in reservations)
				{
					kvp.Value.RemoveReservationsForPawn(pawn);
					
					// Remove empty reservations
					if (!kvp.Value.HasAnyReservations)
					{
						locationsToRemove.Add(kvp.Key);
					}
				}
				
				foreach (var location in locationsToRemove)
				{
					reservations.Remove(location);
				}
			}
		}

		/// <summary>
		/// Get available capacity at a storage location
		/// </summary>
		public static int GetAvailableCapacity(StorageLocation location, Thing thing, Map map, StorageReservation reservation = null)
		{
			if (map == null)
				return 0;

			// Establish a probe ThingDef and context for capacity. If 'thing' is null, attempt to infer.
			Thing probe = thing;
			ThingDef probeDef = thing?.def;

			if (probe == null)
			{
				if (location.IsContainer && location.Container != null && !location.Container.Destroyed)
				{
					var owner = location.Container.TryGetInnerInteractableThingOwner();
					if (owner != null && owner.Count > 0)
					{
						probe = owner[0];
						probeDef = probe.def;
					}
				}
				else if (!location.IsContainer && location.Cell.InBounds(map))
				{
					var things = map.thingGrid.ThingsListAtFast(location.Cell);
					if (things != null && things.Count > 0)
					{
						probe = things[0];
						probeDef = probe.def;
					}
				}

				if (probe == null && probeDef == null)
				{
					try
					{
						foreach (var p in map.mapPawns.AllPawnsSpawned)
						{
							var cur = p.CurJob;
							if (cur == null) continue;
							var carried = p.carryTracker?.CarriedThing ?? cur.targetA.Thing;
							if (carried == null) continue;

							bool matches = false;
							if (cur.targetB.IsValid)
							{
								if (!location.IsContainer && cur.targetB.Cell.IsValid && cur.targetB.Cell == location.Cell) matches = true;
								if (location.IsContainer && cur.targetB.HasThing && cur.targetB.Thing == location.Container) matches = true;
							}
							else if (cur.targetQueueB != null)
							{
								foreach (var lt in cur.targetQueueB)
								{
									if (!lt.IsValid) continue;
									if (!location.IsContainer && lt.Cell.IsValid && lt.Cell == location.Cell) { matches = true; break; }
									if (location.IsContainer && lt.HasThing && lt.Thing == location.Container) { matches = true; break; }
								}
							}

							if (matches)
							{
								probe = carried;
								probeDef = carried.def;
								break;
							}
						}
					}
					catch { /* ignore */ }

					if (probe == null && probeDef == null)
					{
						Log.Message($"No probe and no def for {location}. Returning 0.");
						return 0;
					}
				}
			}

			if (probeDef == null && probe != null) probeDef = probe.def;

			int totalCapacity = 0;
			int currentCount = 0;
			bool slotAllows = true;
			string locStr = location.IsContainer ? (location.Container?.ToString() ?? "null") : location.Cell.ToString();

			if (location.IsContainer)
			{
				var container = location.Container;
				if (container == null || container.Destroyed)
					return 0;

				try
				{
					if (container is IHaulDestination haulDest)
					{
						var settings = haulDest.GetStoreSettings();
						if (settings != null && probeDef != null)
						{
							slotAllows = settings.filter.Allows(probeDef);
						}
					}
				}
				catch { /* be resilient */ }

				if (!slotAllows)
				{
					Log.Message($"[{locStr}] def={probeDef?.defName ?? "null"} slotAllows=false -> 0");
					return 0;
				}

				var thingOwner = container.TryGetInnerInteractableThingOwner();
				if (thingOwner == null)
					return 0;

				if (probe == null && probeDef != null)
				{
					totalCapacity = probeDef.stackLimit;
				}
				else
				{
					totalCapacity = thingOwner.GetCountCanAccept(probe);
					currentCount = 0;
				}
			}
			else
			{
				if (!location.Cell.InBounds(map))
					return 0;

				var slotGroup = location.Cell.GetSlotGroup(map);
				if (slotGroup != null && slotGroup.Settings != null && probeDef != null)
				{
					slotAllows = slotGroup.Settings.filter.Allows(probeDef);
					if (!slotAllows)
					{
						Log.Message($"[{locStr}] def={probeDef?.defName ?? "null"} slotAllows=false -> 0");
						return 0;
					}
				}

				var existingThing = (probeDef != null) ? map.thingGrid.ThingAt(location.Cell, probeDef) : null;

				if (HoldMultipleThingsSupportWrapper.CapacityAt(probe ?? existingThing, location.Cell, map, out var modCapacity))
				{
					totalCapacity = modCapacity;
					currentCount = 0;
				}
				else
				{
					var baseStackLimit = probeDef?.stackLimit ?? (probe?.def.stackLimit ?? 0);

					if (existingThing != null)
					{
						currentCount = existingThing.stackCount;
						totalCapacity = baseStackLimit;
					}
					else
					{
						currentCount = 0;
						totalCapacity = baseStackLimit;
					}
				}
			}

			// Fetch reservedCount from the SAME bucket if provided; otherwise use map dictionary
			var reservedCount = 0;
			bool usedParamBucket = false;
			if (reservation != null)
			{
				if (probeDef != null)
				{
					reservedCount = reservation.GetReservedCountForThingDef(probeDef);
					usedParamBucket = true;
				}
			}
			else
			{
				lock (_reservationLock)
				{
					var reservations = GetMapReservations(map);
					if (reservations.TryGetValue(location, out var res2))
					{
						res2.CleanupExpiredReservations();
						if (probeDef != null)
							reservedCount = res2.GetReservedCountForThingDef(probeDef);
					}
				}
			}

			// Get pending hauls for this location and def
			int pendingCount = 0;
			if (probeDef != null)
			{
				pendingCount = PendingHaulTracker.GetPendingAmount(location, probeDef, map);
			}
		 
			var available = Math.Max(0, totalCapacity - currentCount - reservedCount - pendingCount);
		 
			int perPickupLimit = probeDef?.stackLimit ?? probe?.def?.stackLimit ?? 0;
			if (perPickupLimit > 0 && available > perPickupLimit)
			{
				available = perPickupLimit;
			}
		 
            Log.Message($"loc={locStr} def={probeDef?.defName ?? probe?.def?.defName ?? "null"} totalCap={totalCapacity} current={currentCount} reserved={reservedCount} pending={pendingCount} available={available}{(usedParamBucket ? " [bucket=param]" : " [bucket=global]")}");
            // Also write to PUAH debug log file
            PickUpAndHaul.Log.Message($"PRS.GetAvailableCapacity loc={locStr} def={probeDef?.defName ?? probe?.def?.defName ?? "null"} totalCap={totalCapacity} current={currentCount} reserved={reservedCount} pending={pendingCount} => available={available}{(usedParamBucket ? " [bucket=param]" : " [bucket=global]")}");
			return available;
		}

		/// <summary>
		/// Find best storage location with partial reservation support
		/// </summary>
		public static bool TryFindBestStorageWithReservation(Thing thing, Pawn pawn, Map map, 
			StoragePriority currentPriority, Faction faction, out StorageLocation location, 
			out int availableCapacity)
		{
			location = default;
			availableCapacity = 0;
			
			if (thing == null || pawn == null || map == null)
				return false;

			var bestPriority = currentPriority;
			var bestLocation = default(StorageLocation);
			var bestCapacity = 0;
			var closestDistance = float.MaxValue;

			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				
				// Check storage cells
				var haulDestinations = map.haulDestinationManager.AllGroupsListInPriorityOrder;
				foreach (var slotGroup in haulDestinations)
				{
					if (slotGroup.Settings.Priority <= currentPriority || !slotGroup.parent.Accepts(thing))
						continue;
						
					foreach (var cell in slotGroup.CellsList)
					{
						if (!StoreUtility.IsGoodStoreCell(cell, map, thing, pawn, faction))
							continue;
							
						var storageLocation = new StorageLocation(cell, map);
						var capacity = GetAvailableCapacity(storageLocation, thing, map);
						
						if (capacity > 0)
						{
							var distance = (cell - pawn.Position).LengthHorizontalSquared;
							
							// Prefer higher priority or closer distance
							if (slotGroup.Settings.Priority > bestPriority || 
								(slotGroup.Settings.Priority == bestPriority && distance < closestDistance))
							{
								bestLocation = storageLocation;
								bestCapacity = capacity;
								bestPriority = slotGroup.Settings.Priority;
								closestDistance = distance;
							}
						}
					}
				}
				
				// Check storage containers
				var allHaulDestinations = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
				foreach (var haulDest in allHaulDestinations)
				{
					if (haulDest is not Thing container || haulDest is ISlotGroupParent)
						continue;
						
					var settings = haulDest.GetStoreSettings();
					if (settings.Priority <= currentPriority || !haulDest.Accepts(thing))
						continue;
						
					if (!pawn.CanReserveNew(container) || container.IsForbidden(pawn))
						continue;
						
					var storageLocation = new StorageLocation(container);
					var capacity = GetAvailableCapacity(storageLocation, thing, map);
					
					if (capacity > 0)
					{
						var distance = (container.Position - pawn.Position).LengthHorizontalSquared;
						
						if (settings.Priority > bestPriority || 
							(settings.Priority == bestPriority && distance < closestDistance))
						{
							bestLocation = storageLocation;
							bestCapacity = capacity;
							bestPriority = settings.Priority;
							closestDistance = distance;
						}
					}
				}
			}
			
			if (bestCapacity > 0)
			{
				location = bestLocation;
				availableCapacity = bestCapacity;
				
				// Add detailed logging for reservation debugging
				var locationStr = location.IsContainer ? $"Container:{location.Container}" : $"Cell:{location.Cell}";
				Log.Message($"PRSReservationSystem found storage for {thing} at {locationStr} with capacity {bestCapacity} (NO RESERVATION MADE - will be reserved by JobDriver)");
				
				return true;
			}
			
			Log.Message($"PRSReservationSystem found NO storage for {thing}");
			return false;
		}

		/// <summary>
		/// Get all valid storage locations for a thing, sorted by priority and distance
		/// </summary>
		public static IEnumerable<StorageLocation> GetAllValidStorageLocations(Thing thing, Pawn pawn, Map map, 
			StoragePriority currentPriority, Faction faction)
		{
			if (thing == null || pawn == null || map == null)
				yield break;

			var validLocations = new List<(StorageLocation location, StoragePriority priority, float distance, int capacity)>();

			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				
				// Check storage cells
				var haulDestinations = map.haulDestinationManager.AllGroupsListInPriorityOrder;
				foreach (var slotGroup in haulDestinations)
				{
					if (slotGroup.Settings.Priority < currentPriority || !slotGroup.parent.Accepts(thing))
						continue;
						
					foreach (var cell in slotGroup.CellsList)
					{
						if (!StoreUtility.IsGoodStoreCell(cell, map, thing, pawn, faction))
							continue;
							
						var storageLocation = new StorageLocation(cell, map);
						var capacity = GetAvailableCapacity(storageLocation, thing, map);
						
						if (capacity > 0)
						{
							var distance = (cell - pawn.Position).LengthHorizontalSquared;
							validLocations.Add((storageLocation, slotGroup.Settings.Priority, distance, capacity));
						}
					}
				}
				
				// Check storage containers
				var allHaulDestinations = map.haulDestinationManager.AllHaulDestinationsListInPriorityOrder;
				foreach (var haulDest in allHaulDestinations)
				{
					if (haulDest is not Thing container || haulDest is ISlotGroupParent)
						continue;
						
					var settings = haulDest.GetStoreSettings();
					if (settings.Priority < currentPriority || !haulDest.Accepts(thing))
						continue;
						
					if (!pawn.CanReserveNew(container) || container.IsForbidden(pawn))
						continue;
						
					var storageLocation = new StorageLocation(container);
					var capacity = GetAvailableCapacity(storageLocation, thing, map);
					
					if (capacity > 0)
					{
						var distance = (container.Position - pawn.Position).LengthHorizontalSquared;
						validLocations.Add((storageLocation, settings.Priority, distance, capacity));
					}
				}
			}
			
			// Sort by priority (highest first), then by distance (closest first)
			var sortedLocations = validLocations
				.OrderByDescending(x => x.priority)
				.ThenBy(x => x.distance)
				.Select(x => x.location);
				
			foreach (var location in sortedLocations)
			{
				yield return location;
			}
		}

		/// <summary>
		/// Clean up all expired reservations for a map
		/// </summary>
		public static void CleanupExpiredReservations(Map map)
		{
			if (map == null)
				return;

			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				var locationsToRemove = new List<StorageLocation>();
				
				foreach (var kvp in reservations)
				{
					kvp.Value.CleanupExpiredReservations();
					
					if (!kvp.Value.HasAnyReservations)
					{
						locationsToRemove.Add(kvp.Key);
					}
				}
				
				foreach (var location in locationsToRemove)
				{
					reservations.Remove(location);
				}
			}
		}

		/// <summary>
		/// Clear all reservations for a map
		/// </summary>
		public static void ClearAllReservations(Map map)
		{
			if (map == null)
				return;

			lock (_reservationLock)
			{
				_storageReservations.Remove(map.uniqueID);
			}
		}

		/// <summary>
		/// Get the actual reserved count for a specific thing type at a location
		/// </summary>
		public static int GetReservedCount(StorageLocation location, ThingDef thingDef, Map map)
		{
			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				if (reservations.TryGetValue(location, out var reservation))
				{
					reservation.CleanupExpiredReservations();
					return reservation.GetReservedCountForThingDef(thingDef);
				}
				return 0;
			}
		}

		/// <summary>
		/// Compute how much of 'thing' can be accepted at 'location' and what remainder would be left.
		/// This does not mutate reservations; caller can then TryReservePartialStorage for the accepted portion.
		/// </summary>
		public static void ComputeAcceptAndRemainder(StorageLocation location, Thing thing, Map map, out int canAccept, out int remainder)
		{
			canAccept = 0;
			remainder = 0;
			if (thing == null || map == null) return;

			var cap = GetAvailableCapacity(location, thing, map);
			canAccept = System.Math.Min(cap, thing.stackCount);
			remainder = System.Math.Max(0, thing.stackCount - canAccept);
		}

		/// <summary>
		/// Try to find a nearby cell to drop a remainder. Uses vanilla cell validators similar to StoreUtility, but relaxed.
		/// </summary>
		public static bool TryFindNearbyDropCell(IntVec3 origin, Map map, out IntVec3 dropCell, int maxRadius = 6)
		{
			dropCell = IntVec3.Invalid;
			if (map == null || !origin.InBounds(map)) return false;

			for (int r = 0; r <= maxRadius; r++)
			{
				foreach (var cell in GenRadial.RadialCellsAround(origin, r, true))
				{
					if (!cell.InBounds(map) || cell.Filled(map)) continue;
					if (cell.Standable(map) || cell.GetFirstItem(map) == null)
					{
						dropCell = cell;
						return true;
					}
				}
			}
			return false;
		}

		/// <summary>
		/// Debug: Get reservation info for a location
		/// </summary>
		public static string GetReservationDebugInfo(StorageLocation location, Map map)
		{
			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				if (reservations.TryGetValue(location, out var reservation))
				{
					return $"Total reserved: {reservation.TotalReservedCount}";
				}
				return "No reservations";
			}
		}

					/// <summary>
		/// Calculate how much a pawn will actually haul based on RimWorld's hauling logic.
		/// This mirrors vanilla StartCarryThing behavior:
		/// - If already carrying same def, fill to that stack's limit (respecting thing.def.stackLimit and thing.stackCount).
		/// - Otherwise start a new stack up to the item's stackLimit.
		/// Mods like PickUpAndHaul/HoldMultipleThings may later add more items via subsequent pickups; this function
		/// returns the immediate pickup amount for the next carry operation.
		/// </summary>
		public static int CalculateHaulAmount(Pawn pawn, Thing thing)
		{
			if (pawn == null || thing == null) return 0;

			// Respect forbidden/inaccessible edge cases defensively
			if (thing.DestroyedOrNull()) return 0;

			var def = thing.def;
			int stackLimit = def?.stackLimit ?? 0;
			if (stackLimit <= 0) return 0;

			var carryTracker = pawn.carryTracker;
			var carried = carryTracker?.CarriedThing;

			// If already carrying the same def, top off the carried stack
			if (carried != null && carried.def == def)
			{
				int remainingInCarriedStack = Math.Max(0, stackLimit - carried.stackCount);
				if (remainingInCarriedStack <= 0) return 0;
				return Math.Min(thing.stackCount, remainingInCarriedStack);
			}

			// Not carrying same def: start a new stack limited by stackLimit and available source stack
			return Math.Min(thing.stackCount, stackLimit);
		}

		/// <summary>
		/// Get the reserved count for a specific pawn/job/location/def combination
		/// </summary>
		public static int GetReservedCountForPawnJob(Pawn pawn, Job job, StorageLocation location, ThingDef thingDef, Map map)
		{
			if (pawn == null || job == null || thingDef == null || map == null)
				return 0;

			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				if (!reservations.TryGetValue(location, out var reservation))
					return 0;

				// Access the private _reservations field
				var entriesField = AccessTools.Field(typeof(StorageReservation), "_reservations");
				var dict = (System.Collections.IDictionary)entriesField.GetValue(reservation);
				if (dict == null || !dict.Contains(pawn))
					return 0;

				var list = (System.Collections.IEnumerable)dict[pawn];
				int totalCount = 0;
				foreach (var entry in list)
				{
					var entryThing = (Thing)AccessTools.Property(typeof(StorageReservation.ReservationEntry), "Thing").GetValue(entry);
					var entryJob = (Job)AccessTools.Property(typeof(StorageReservation.ReservationEntry), "Job").GetValue(entry);
					if (entryJob == job && entryThing?.def == thingDef)
					{
						var entryCount = (int)AccessTools.Property(typeof(StorageReservation.ReservationEntry), "Count").GetValue(entry);
						totalCount += entryCount;
					}
				}
				return totalCount;
			}
		}

		/// <summary>
		/// Check if the specified pawn already has a reservation at the given location
		/// </summary>
		public static bool PawnHasReservation(Pawn pawn, StorageLocation location, Map map)
		{
			if (pawn == null || map == null)
				return false;
			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				return reservations.TryGetValue(location, out var res) && res.HasReservationByPawn(pawn);
			}
		}

		/// <summary>
		/// Whether any pawn has a reservation at the given location
		/// </summary>
		public static bool HasAnyReservation(StorageLocation location, Map map)
		{
			if (map == null)
				return false;
			lock (_reservationLock)
			{
				var reservations = GetMapReservations(map);
				return reservations.TryGetValue(location, out var res) && res.Any;
			}
		}
	}
}


