using System.IO;
using SetNet.GameData;
using Xunit;

namespace SetNet.UnitTests
{
    /// <summary>Covers SetNet.GameData: load id-keyed tables from JSON (custom columns), look up, and hot-reload a file.</summary>
    public class GameDataTests
    {
        // A data row with arbitrary custom columns.
        private sealed class ItemDef
        {
            public int Id { get; set; }
            public string Name { get; set; } = "";
            public int Grade { get; set; }
            public bool Tradable { get; set; }
        }

        [Fact]
        public void LoadJson_lookups_work()
        {
            var reg = new GameDataRegistry();
            var items = reg.LoadJson<int, ItemDef>("items",
                "[{\"Id\":1,\"Name\":\"Sword\",\"Grade\":3,\"Tradable\":true}," +
                " {\"Id\":2,\"Name\":\"Shield\",\"Grade\":2,\"Tradable\":false}]",
                r => r.Id);

            Assert.Equal(2, items.Count);
            Assert.Equal("Sword", items.Get(1)!.Name);
            Assert.Equal(3, items.Get(1)!.Grade);
            Assert.False(items.Get(2)!.Tradable);
            Assert.True(items.Contains(1));
            Assert.Null(items.Get(99));

            // retrievable from the registry by name
            Assert.True(reg.Has("items"));
            Assert.Equal("Shield", reg.Get<int, ItemDef>("items").Get(2)!.Name);
        }

        [Fact]
        public void LoadFrom_any_source_reloads_by_requerying()
        {
            // Stands in for a DB table (or IDocumentStore.AllAsync()): Reload() re-runs the source.
            var db = new List<ItemDef> { new ItemDef { Id = 1, Name = "Sword", Grade = 1 } };

            var reg = new GameDataRegistry();
            var t = reg.LoadFrom<int, ItemDef>("items", () => db, r => r.Id);
            Assert.Equal(1, t.Get(1)!.Grade);

            // "someone edited the DB", then reload → re-queries the source
            db[0] = new ItemDef { Id = 1, Name = "Sword", Grade = 9 };
            db.Add(new ItemDef { Id = 2, Name = "Bow", Grade = 4 });
            reg.Reload();

            var fresh = reg.Get<int, ItemDef>("items");
            Assert.Equal(9, fresh.Get(1)!.Grade);
            Assert.Equal("Bow", fresh.Get(2)!.Name);
        }

        [Fact]
        public void Load_from_a_plain_enumerable()
        {
            var reg = new GameDataRegistry();
            var rows = new[] { new ItemDef { Id = 5, Name = "Potion", Grade = 0 } };
            var t = reg.Load<int, ItemDef>("items", rows, r => r.Id);   // e.g. rows from a DB query
            Assert.Equal("Potion", t.Get(5)!.Name);
        }

        [Fact]
        public void LoadFile_hot_reloads()
        {
            var path = Path.Combine(Path.GetTempPath(), "setnet_items_" + System.Guid.NewGuid().ToString("N") + ".json");
            try
            {
                File.WriteAllText(path, "[{\"Id\":1,\"Name\":\"Sword\",\"Grade\":1}]");
                var reg = new GameDataRegistry();
                var t = reg.LoadFile<int, ItemDef>("items", path, r => r.Id);
                Assert.Equal(1, t.Get(1)!.Grade);

                // edit the file on disk, then reload
                File.WriteAllText(path, "[{\"Id\":1,\"Name\":\"Sword\",\"Grade\":5},{\"Id\":2,\"Name\":\"Bow\",\"Grade\":4}]");
                var reloaded = false;
                reg.Reloaded += () => reloaded = true;
                reg.Reload();

                var fresh = reg.Get<int, ItemDef>("items");
                Assert.True(reloaded);
                Assert.Equal(5, fresh.Get(1)!.Grade);        // updated
                Assert.Equal("Bow", fresh.Get(2)!.Name);     // added
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
