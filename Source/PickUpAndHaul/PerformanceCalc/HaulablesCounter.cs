
// HaulablesCounter.cs  -- O(1) per‑map haulable tally
using System.Collections.Generic;
using Verse;

namespace PickUpAndHaul.Performance
{
    public static class HaulablesCounter
    {
        private static readonly Dictionary<int, int> _perMap = new();

        		public static void NotifyAdd(Map map)
		{
			if (map == null) return;
			if (_perMap.TryGetValue(map.uniqueID, out int n))
				_perMap[map.uniqueID] = n + 1;
			else
				_perMap[map.uniqueID] = 1;
		}

		public static void NotifyRemove(Map map)
		{
			if (map == null) return;
			if (_perMap.TryGetValue(map.uniqueID, out int n) && n > 0)
			{
				_perMap[map.uniqueID] = n - 1;
			}
		}

        public static int Get(Map map) =>
            (map != null && _perMap.TryGetValue(map.uniqueID, out int n)) ? n : 0;
    }
}
