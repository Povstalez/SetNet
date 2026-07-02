using System;
using System.Collections.Concurrent;

namespace SetNet.PathFinding
{
    /// <summary>
    /// Reusable working memory for one A* search over an integer node graph. The whole point is to make a path query
    /// allocation-free after warm-up: instead of clearing per-node arrays every call (O(nodes)), we stamp each node
    /// with a monotonically increasing <see cref="_gen"/> and treat a node as "unvisited this search" whenever its
    /// stamp is stale. Cost per query is therefore proportional to the nodes actually *expanded*, not to the size of
    /// the map — critical when thousands of MMO agents path every second over a large grid.
    /// </summary>
    /// <remarks>Not thread-safe on its own; rent one per concurrent search from a <see cref="SearchStatePool"/>.</remarks>
    internal sealed class SearchState
    {
        private float[] _g = Array.Empty<float>();
        private int[] _came = Array.Empty<int>();
        private int[] _seenGen = Array.Empty<int>();     // g/came for node i are valid iff _seenGen[i] == _gen
        private int[] _closedGen = Array.Empty<int>();   // node i is closed iff _closedGen[i] == _gen
        private int _gen;

        /// <summary>The reusable open set (binary min-heap keyed by f-score).</summary>
        public readonly MinHeap Open = new MinHeap();

        /// <summary>Prepares the state for a fresh search over <paramref name="nodeCount"/> nodes (grows arrays if needed, bumps the generation).</summary>
        public void Begin(int nodeCount)
        {
            if (_g.Length < nodeCount)
            {
                _g = new float[nodeCount];
                _came = new int[nodeCount];
                _seenGen = new int[nodeCount];     // fresh arrays are all-zero
                _closedGen = new int[nodeCount];
                _gen = 0;                          // so the ++ below lands on 1, distinct from the zero-filled stamps
            }
            Open.Clear();
            if (++_gen == int.MaxValue)            // stamp space exhausted (astronomically rare) → hard reset
            {
                Array.Clear(_seenGen, 0, _seenGen.Length);
                Array.Clear(_closedGen, 0, _closedGen.Length);
                _gen = 1;
            }
        }

        /// <summary>True if node <paramref name="i"/> has been popped and finalized in this search.</summary>
        public bool IsClosed(int i) => _closedGen[i] == _gen;

        /// <summary>Marks node <paramref name="i"/> closed for this search.</summary>
        public void Close(int i) => _closedGen[i] = _gen;

        /// <summary>The best known cost-so-far for node <paramref name="i"/> in this search (+∞ if never reached).</summary>
        public float GScore(int i) => _seenGen[i] == _gen ? _g[i] : float.PositiveInfinity;

        /// <summary>Records a better path to node <paramref name="i"/> (cost <paramref name="g"/>, arrived from <paramref name="cameFrom"/>).</summary>
        public void Relax(int i, float g, int cameFrom)
        {
            _g[i] = g;
            _came[i] = cameFrom;
            _seenGen[i] = _gen;
        }

        /// <summary>The predecessor node recorded for <paramref name="i"/> (valid only while walking back from a reached goal).</summary>
        public int CameFrom(int i) => _came[i];
    }

    /// <summary>
    /// A thread-safe pool of <see cref="SearchState"/> instances so a single pathfinder (reused for many agents,
    /// possibly across threads) reuses working memory instead of allocating per query.
    /// </summary>
    internal sealed class SearchStatePool
    {
        private readonly ConcurrentQueue<SearchState> _pool = new ConcurrentQueue<SearchState>();

        /// <summary>Borrows a state (a fresh one if the pool is empty).</summary>
        public SearchState Rent() => _pool.TryDequeue(out var s) ? s : new SearchState();

        /// <summary>Returns a state for reuse.</summary>
        public void Return(SearchState s) => _pool.Enqueue(s);
    }
}
