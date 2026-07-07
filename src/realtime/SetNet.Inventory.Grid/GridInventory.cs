using System;
using System.Collections.Generic;

namespace SetNet.Inventory.Grid
{
    /// <summary>A cell coordinate in the grid (top-left origin).</summary>
    public readonly struct GridPos : IEquatable<GridPos>
    {
        /// <summary>Column.</summary>
        public int X { get; }
        /// <summary>Row.</summary>
        public int Y { get; }
        /// <summary>Creates a position.</summary>
        public GridPos(int x, int y) { X = x; Y = y; }
        /// <inheritdoc/>
        public bool Equals(GridPos o) => X == o.X && Y == o.Y;
        /// <inheritdoc/>
        public override bool Equals(object? o) => o is GridPos g && Equals(g);
        /// <inheritdoc/>
        public override int GetHashCode() => (X << 16) ^ Y;
        /// <inheritdoc/>
        public override string ToString() => $"({X},{Y})";
    }

    /// <summary>Item orientation on the grid.</summary>
    public enum GridRotation
    {
        /// <summary>Footprint as authored (W×H).</summary>
        None = 0,
        /// <summary>Rotated 90° (H×W).</summary>
        Quarter = 1
    }

    /// <summary>An item that can live in a grid inventory: a footprint, a stack count, and your payload.</summary>
    public sealed class GridItem
    {
        /// <summary>Unique instance id (assigned on add if empty).</summary>
        public string Id { get; set; } = "";
        /// <summary>Catalog/type id (used for stacking).</summary>
        public string Type { get; set; } = "";
        /// <summary>Footprint width (cells).</summary>
        public int Width { get; set; } = 1;
        /// <summary>Footprint height (cells).</summary>
        public int Height { get; set; } = 1;
        /// <summary>Stack count.</summary>
        public int Count { get; set; } = 1;
        /// <summary>Max stack size (1 = not stackable).</summary>
        public int MaxStack { get; set; } = 1;
        /// <summary>Your payload (durability, mods, custom fields…).</summary>
        public object? Tag { get; set; }
    }

    /// <summary>An item placed on the grid at a position and rotation.</summary>
    public sealed class PlacedItem
    {
        /// <summary>The item.</summary>
        public GridItem Item { get; }
        /// <summary>Its top-left cell.</summary>
        public GridPos Position { get; internal set; }
        /// <summary>Its orientation.</summary>
        public GridRotation Rotation { get; internal set; }

        internal PlacedItem(GridItem item, GridPos pos, GridRotation rot) { Item = item; Position = pos; Rotation = rot; }

        /// <summary>Effective width given the rotation.</summary>
        public int Width => Rotation == GridRotation.Quarter ? Item.Height : Item.Width;
        /// <summary>Effective height given the rotation.</summary>
        public int Height => Rotation == GridRotation.Quarter ? Item.Width : Item.Height;
    }

    /// <summary>
    /// A spatial grid ("tetris") inventory: items occupy a width×height footprint, can be rotated 90°, and are placed with
    /// server-authoritative occupancy checks. Supports auto-fit add, place/move at a cell, rotate, remove, cell queries and
    /// optional stacking. One instance per container (backpack, stash…); pure data — persist/replicate it however you like.
    /// </summary>
    public sealed class GridInventory
    {
        private readonly string?[,] _cells;                                   // itemId per cell, null = free
        private readonly Dictionary<string, PlacedItem> _items = new Dictionary<string, PlacedItem>();
        private long _nextId;

        /// <summary>Grid width in cells.</summary>
        public int Width { get; }
        /// <summary>Grid height in cells.</summary>
        public int Height { get; }
        /// <summary>Raised after any change (add/move/remove/rotate/stack).</summary>
        public event Action? Changed;

        /// <summary>Creates an empty grid.</summary>
        public GridInventory(int width, int height)
        {
            if (width <= 0 || height <= 0) throw new ArgumentException("grid must be at least 1x1");
            Width = width; Height = height;
            _cells = new string?[width, height];
        }

        /// <summary>All placed items.</summary>
        public IReadOnlyCollection<PlacedItem> Items => _items.Values;
        /// <summary>Number of free cells.</summary>
        public int FreeCells
        {
            get { var used = 0; foreach (var p in _items.Values) used += p.Width * p.Height; return Width * Height - used; }
        }

        private static (int w, int h) Footprint(GridItem item, GridRotation rot)
            => rot == GridRotation.Quarter ? (item.Height, item.Width) : (item.Width, item.Height);

        /// <summary>Whether a footprint of <paramref name="w"/>×<paramref name="h"/> fits at <paramref name="pos"/> (cells free, or owned by <paramref name="ignoreId"/>).</summary>
        public bool CanPlace(int w, int h, GridPos pos, string? ignoreId = null)
        {
            if (pos.X < 0 || pos.Y < 0 || pos.X + w > Width || pos.Y + h > Height) return false;
            for (var y = pos.Y; y < pos.Y + h; y++)
                for (var x = pos.X; x < pos.X + w; x++)
                {
                    var occ = _cells[x, y];
                    if (occ != null && occ != ignoreId) return false;
                }
            return true;
        }

        private void Fill(GridPos pos, int w, int h, string? id)
        {
            for (var y = pos.Y; y < pos.Y + h; y++)
                for (var x = pos.X; x < pos.X + w; x++)
                    _cells[x, y] = id;
        }

        /// <summary>Places an item at an exact cell + rotation. Returns false if it doesn't fit. Assigns an id if empty.</summary>
        public bool TryPlaceAt(GridItem item, GridPos pos, GridRotation rotation = GridRotation.None)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            var (w, h) = Footprint(item, rotation);
            if (!CanPlace(w, h, pos)) return false;
            if (string.IsNullOrEmpty(item.Id)) item.Id = "i" + System.Threading.Interlocked.Increment(ref _nextId);
            if (_items.ContainsKey(item.Id)) return false;

            var placed = new PlacedItem(item, pos, rotation);
            _items[item.Id] = placed;
            Fill(pos, w, h, item.Id);
            Changed?.Invoke();
            return true;
        }

        /// <summary>
        /// Adds an item: first tries to merge into existing stacks of the same <see cref="GridItem.Type"/> (if stackable),
        /// then places any remainder at the first free spot (trying both rotations). Returns false only if the whole item
        /// couldn't be accommodated (no partial state is left behind).
        /// </summary>
        public bool TryAdd(GridItem item)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));

            // 1) merge into existing stacks (recorded so we can roll back if the remainder doesn't fit)
            var merges = new List<(PlacedItem target, int amount)>();
            if (item.MaxStack > 1 && item.Count > 0)
            {
                foreach (var p in _items.Values)
                {
                    if (item.Count == 0) break;
                    if (p.Item.Type != item.Type || p.Item.Count >= p.Item.MaxStack) continue;
                    var room = p.Item.MaxStack - p.Item.Count;
                    var move = Math.Min(room, item.Count);
                    p.Item.Count += move; item.Count -= move;
                    merges.Add((p, move));
                }
                if (item.Count == 0) { Changed?.Invoke(); return true; }
            }

            // 2) place the remainder at the first free position
            if (string.IsNullOrEmpty(item.Id)) item.Id = "i" + System.Threading.Interlocked.Increment(ref _nextId);
            foreach (var rot in new[] { GridRotation.None, GridRotation.Quarter })
            {
                var (w, h) = Footprint(item, rot);
                for (var y = 0; y + h <= Height; y++)
                    for (var x = 0; x + w <= Width; x++)
                        if (CanPlace(w, h, new GridPos(x, y)))
                        {
                            var placed = new PlacedItem(item, new GridPos(x, y), rot);
                            _items[item.Id] = placed;
                            Fill(new GridPos(x, y), w, h, item.Id);
                            Changed?.Invoke();
                            return true;
                        }
            }

            // 3) no room → roll back the merges
            foreach (var (target, amount) in merges) { target.Item.Count -= amount; item.Count += amount; }
            return false;
        }

        /// <summary>Moves/rotates an already-placed item to a new cell. Returns false if it doesn't fit there.</summary>
        public bool TryMove(string itemId, GridPos newPos, GridRotation newRotation)
        {
            if (!_items.TryGetValue(itemId, out var placed)) return false;
            var (w, h) = Footprint(placed.Item, newRotation);
            if (!CanPlace(w, h, newPos, ignoreId: itemId)) return false;

            Fill(placed.Position, placed.Width, placed.Height, null);         // clear old cells
            placed.Position = newPos; placed.Rotation = newRotation;
            Fill(newPos, w, h, itemId);                                       // fill new cells
            Changed?.Invoke();
            return true;
        }

        /// <summary>Rotates a placed item in place (if it fits rotated). Returns false otherwise.</summary>
        public bool TryRotate(string itemId)
        {
            if (!_items.TryGetValue(itemId, out var placed)) return false;
            var rot = placed.Rotation == GridRotation.None ? GridRotation.Quarter : GridRotation.None;
            return TryMove(itemId, placed.Position, rot);
        }

        /// <summary>Removes an item. Returns false if there's no such item.</summary>
        public bool Remove(string itemId)
        {
            if (!_items.TryGetValue(itemId, out var placed)) return false;
            Fill(placed.Position, placed.Width, placed.Height, null);
            _items.Remove(itemId);
            Changed?.Invoke();
            return true;
        }

        /// <summary>The placed item with this id, or null.</summary>
        public PlacedItem? Get(string itemId) => _items.TryGetValue(itemId, out var p) ? p : null;

        /// <summary>The item occupying a cell, or null if the cell is free / out of bounds.</summary>
        public PlacedItem? At(GridPos pos)
        {
            if (pos.X < 0 || pos.Y < 0 || pos.X >= Width || pos.Y >= Height) return null;
            var id = _cells[pos.X, pos.Y];
            return id != null && _items.TryGetValue(id, out var p) ? p : null;
        }

        /// <summary>True if a cell is free (in bounds and unoccupied).</summary>
        public bool IsFree(GridPos pos)
            => pos.X >= 0 && pos.Y >= 0 && pos.X < Width && pos.Y < Height && _cells[pos.X, pos.Y] == null;
    }
}
