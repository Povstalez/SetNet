using SetNet.Inventory.Grid;
using Xunit;

namespace SetNet.UnitTests
{
    /// <summary>Covers SetNet.Inventory.Grid: placement, occupancy, rotation, move, remove, auto-fit add, and stacking with rollback.</summary>
    public class GridInventoryTests
    {
        private static GridItem Item(string type, int w, int h, int count = 1, int maxStack = 1) =>
            new GridItem { Type = type, Width = w, Height = h, Count = count, MaxStack = maxStack };

        [Fact]
        public void Place_occupies_cells_and_At_finds_it()
        {
            var inv = new GridInventory(4, 4);
            var rifle = Item("rifle", 3, 1);

            Assert.True(inv.TryPlaceAt(rifle, new GridPos(0, 0)));
            Assert.NotEqual("", rifle.Id);
            Assert.False(inv.IsFree(new GridPos(0, 0)));
            Assert.False(inv.IsFree(new GridPos(2, 0)));
            Assert.True(inv.IsFree(new GridPos(3, 0)));
            Assert.Same(rifle, inv.At(new GridPos(1, 0))!.Item);

            // overlapping placement is rejected
            Assert.False(inv.TryPlaceAt(Item("pistol", 2, 1), new GridPos(2, 0)));
        }

        [Fact]
        public void Out_of_bounds_is_rejected()
        {
            var inv = new GridInventory(3, 3);
            Assert.False(inv.TryPlaceAt(Item("box", 2, 2), new GridPos(2, 2)));  // would spill off the grid
            Assert.False(inv.CanPlace(1, 1, new GridPos(3, 0)));
        }

        [Fact]
        public void Rotation_swaps_the_footprint()
        {
            var inv = new GridInventory(2, 3);
            // a 1x3 item doesn't fit lying down in a 2-wide grid as 3-wide, but fits rotated (3 tall)... actually it's 1 wide 3 tall:
            var spear = Item("spear", 1, 3);
            Assert.True(inv.TryPlaceAt(spear, new GridPos(0, 0)));           // 1x3 fits
            Assert.False(inv.TryRotate(spear.Id));                          // rotated = 3x1, too wide for a 2-wide grid

            var inv2 = new GridInventory(3, 2);
            var s2 = Item("spear", 1, 3);
            Assert.False(inv2.TryPlaceAt(s2, new GridPos(0, 0)));           // 1x3 too tall for a 2-tall grid
            Assert.True(inv2.TryPlaceAt(s2, new GridPos(0, 0), GridRotation.Quarter));  // rotated 3x1 fits
        }

        [Fact]
        public void Move_and_remove_free_the_cells()
        {
            var inv = new GridInventory(4, 4);
            var it = Item("box", 2, 2);
            inv.TryPlaceAt(it, new GridPos(0, 0));

            Assert.True(inv.TryMove(it.Id, new GridPos(2, 2), GridRotation.None));
            Assert.True(inv.IsFree(new GridPos(0, 0)));                     // old cells freed
            Assert.False(inv.IsFree(new GridPos(3, 3)));                    // new cells filled

            Assert.True(inv.Remove(it.Id));
            Assert.True(inv.IsFree(new GridPos(2, 2)));
            Assert.Equal(16, inv.FreeCells);
        }

        [Fact]
        public void Add_auto_fits_and_fails_when_full()
        {
            var inv = new GridInventory(2, 2);
            Assert.True(inv.TryAdd(Item("a", 2, 1)));    // fills row 0
            Assert.True(inv.TryAdd(Item("b", 2, 1)));    // fills row 1
            Assert.Equal(0, inv.FreeCells);
            Assert.False(inv.TryAdd(Item("c", 1, 1)));   // no room
        }

        [Fact]
        public void Add_finds_a_spot_by_rotating_when_needed()
        {
            var inv = new GridInventory(3, 1);           // a 1-tall, 3-wide strip
            var spear = Item("spear", 1, 3);             // 1x3 — only fits rotated to 3x1
            Assert.True(inv.TryAdd(spear));
            Assert.Equal(GridRotation.Quarter, inv.Get(spear.Id)!.Rotation);
        }

        [Fact]
        public void Stacking_merges_and_rolls_back_when_the_remainder_wont_fit()
        {
            var inv = new GridInventory(2, 1);   // only 2 cells
            inv.TryPlaceAt(Item("ammo", 1, 1, count: 28, maxStack: 30), new GridPos(0, 0));   // room for 2 more
            inv.TryPlaceAt(Item("ammo", 1, 1, count: 28, maxStack: 30), new GridPos(1, 0));   // room for 2 more; grid full

            // add 10 ammo: only 4 can top up the two stacks (→30,30); 6 remain and there's no free cell → whole add fails, rolled back
            var more = Item("ammo", 1, 1, count: 10, maxStack: 30);
            Assert.False(inv.TryAdd(more));
            Assert.Equal(10, more.Count);                                   // untouched (rolled back)
            foreach (var p in inv.Items) Assert.Equal(28, p.Item.Count);    // existing stacks unchanged

            // now add 4 → tops both stacks to 30/30 exactly, fully merged
            var fits = Item("ammo", 1, 1, count: 4, maxStack: 30);
            Assert.True(inv.TryAdd(fits));
            Assert.Equal(0, fits.Count);
        }
    }
}
