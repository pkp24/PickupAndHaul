using System.Collections.Generic;

namespace PickUpAndHaul;

internal static class ListPool<T>
{
    private static readonly Stack<List<T>> _pool = new();

    public static List<T> Get()
    {
        lock (_pool)
        {
            return _pool.Count > 0 ? _pool.Pop() : new List<T>();
        }
    }

    public static void Return(List<T> list)
    {
        list.Clear();
        lock (_pool)
        {
            _pool.Push(list);
        }
    }
}
