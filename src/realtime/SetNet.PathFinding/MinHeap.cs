using System;
using System.Collections.Generic;

namespace SetNet.PathFinding
{
    /// <summary>A tiny binary min-heap keyed by float priority (netstandard2.1 has no <c>PriorityQueue</c>). Used by the A* open set.</summary>
    internal sealed class MinHeap
    {
        private readonly List<(int item, float priority)> _h = new List<(int, float)>();

        public int Count => _h.Count;

        /// <summary>Empties the heap but keeps its backing capacity (so a pooled/reused heap doesn't re-allocate).</summary>
        public void Clear() => _h.Clear();

        public void Push(int item, float priority)
        {
            _h.Add((item, priority));
            var i = _h.Count - 1;
            while (i > 0)
            {
                var p = (i - 1) / 2;
                if (_h[p].priority <= _h[i].priority) break;
                (_h[p], _h[i]) = (_h[i], _h[p]);
                i = p;
            }
        }

        public int Pop()
        {
            var root = _h[0].item;
            var last = _h.Count - 1;
            _h[0] = _h[last];
            _h.RemoveAt(last);
            var i = 0; var n = _h.Count;
            while (true)
            {
                int l = 2 * i + 1, r = 2 * i + 2, smallest = i;
                if (l < n && _h[l].priority < _h[smallest].priority) smallest = l;
                if (r < n && _h[r].priority < _h[smallest].priority) smallest = r;
                if (smallest == i) break;
                (_h[smallest], _h[i]) = (_h[i], _h[smallest]);
                i = smallest;
            }
            return root;
        }
    }
}
