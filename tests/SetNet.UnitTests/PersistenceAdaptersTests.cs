using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SetNet.Persistence;
using SetNet.Persistence.Dapper;
using SetNet.Persistence.EfCore;
using Xunit;

namespace SetNet.UnitTests
{
    /// <summary>
    /// Real round-trip tests for the Dapper and EF Core persistence adapters against in-process SQLite (no server). Proves
    /// the key point: a POCO with arbitrary <b>custom fields</b> (here <c>VipUntil</c> + an <c>Extra</c> bag) survives a
    /// store/load with no schema change. (Postgres/MongoDB use the identical serialize-whole-object pattern.)
    /// </summary>
    public class PersistenceAdaptersTests
    {
        // A character document with custom fields the app invents freely.
        public sealed class CharacterData
        {
            public string Name { get; set; } = "";
            public int Level { get; set; }
            public DateTime? VipUntil { get; set; }                       // ← custom per-character field
            public Dictionary<string, string> Extra { get; set; } = new();
        }

        private static string TempDbPath() =>
            Path.Combine(Path.GetTempPath(), "setnet_test_" + Guid.NewGuid().ToString("N") + ".db");

        private static CharacterData Sample()
        {
            var c = new CharacterData
            {
                Name = "Archer",
                Level = 80,
                VipUntil = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            };
            c.Extra["clan"] = "Redemption";
            c.Extra["title"] = "Hero";
            return c;
        }

        private static void AssertMatches(CharacterData loaded)
        {
            Assert.Equal("Archer", loaded.Name);
            Assert.Equal(80, loaded.Level);
            Assert.Equal(new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc), loaded.VipUntil);
            Assert.Equal("Redemption", loaded.Extra["clan"]);
            Assert.Equal("Hero", loaded.Extra["title"]);
        }

        [Fact]
        public async Task Dapper_sqlite_roundtrips_including_custom_fields()
        {
            var file = TempDbPath();
            try
            {
                IDocumentStore<CharacterData> store =
                    new DapperDocumentStore<CharacterData>(() => new SqliteConnection($"Data Source={file}"));

                await store.SetAsync("char:1", Sample());

                var loaded = await store.GetAsync("char:1");
                Assert.NotNull(loaded);
                AssertMatches(loaded!);

                Assert.True(await store.ExistsAsync("char:1"));
                Assert.Contains("char:1", await store.KeysAsync());
                Assert.Single(await store.AllAsync());

                // update (upsert) then delete
                var lvl90 = Sample(); lvl90.Level = 90;
                await store.SetAsync("char:1", lvl90);
                Assert.Equal(90, (await store.GetAsync("char:1"))!.Level);

                Assert.True(await store.RemoveAsync("char:1"));
                Assert.False(await store.ExistsAsync("char:1"));
                Assert.Null(await store.GetAsync("char:1"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(file)) File.Delete(file);
            }
        }

        [Fact]
        public async Task EfCore_sqlite_roundtrips_documents_and_snapshots()
        {
            var file = TempDbPath();
            try
            {
                var options = new DbContextOptionsBuilder<SetNetPersistenceContext>()
                    .UseSqlite($"Data Source={file}").Options;

                IDocumentStore<CharacterData> store = new EfCoreDocumentStore<CharacterData>(options);
                await store.SetAsync("char:7", Sample());
                var loaded = await store.GetAsync("char:7");
                Assert.NotNull(loaded);
                AssertMatches(loaded!);
                Assert.Single(await store.AllAsync());
                Assert.True(await store.RemoveAsync("char:7"));
                Assert.False(await store.ExistsAsync("char:7"));

                ISnapshotStore snaps = new EfCoreSnapshotStore(options);
                await snaps.SaveAsync("world", new byte[] { 1, 2, 3 });
                Assert.Equal(new byte[] { 1, 2, 3 }, await snaps.LoadAsync("world"));
                Assert.True(await snaps.DeleteAsync("world"));
                Assert.Null(await snaps.LoadAsync("world"));
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(file)) File.Delete(file);
            }
        }
    }
}
