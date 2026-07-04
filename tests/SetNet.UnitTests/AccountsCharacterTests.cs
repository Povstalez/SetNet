using System;
using System.Threading.Tasks;
using SetNet.Accounts;
using SetNet.CharacterStore;
using SetNet.Persistence;
using Xunit;

namespace SetNet.UnitTests
{
    /// <summary>Covers SetNet.Accounts and SetNet.CharacterStore, including that custom fields survive a store round-trip.</summary>
    public class AccountsCharacterTests
    {
        // Custom account type with an app-specific field.
        private sealed class MyAccount : AccountBase
        {
            public string? ReferralCode { get; set; }
        }

        // Custom character type with the requested per-character VIP field.
        private sealed class MyCharacter : CharacterBase
        {
            public DateTime? VipUntil { get; set; }
            public int ClassId { get; set; }
        }

        // ---- Accounts ----

        [Fact]
        public async Task Register_authenticate_ban_and_custom_field()
        {
            var accounts = new AccountServer<MyAccount>(
                new MemoryDocumentStore<MyAccount>(), new MemoryDocumentStore<string>());

            var acc = await accounts.RegisterAsync("Alice", "s3cret", a => a.ReferralCode = "FRIEND42");
            Assert.NotEqual("", acc.Id);
            Assert.Equal("FRIEND42", acc.ReferralCode);

            // duplicate username (case-insensitive) rejected
            await Assert.ThrowsAsync<AccountException>(() => accounts.RegisterAsync("alice", "other"));

            // wrong / unknown / correct
            Assert.Equal(AccountAuthStatus.WrongPassword, (await accounts.AuthenticateAsync("alice", "nope")).Status);
            Assert.Equal(AccountAuthStatus.UnknownUser, (await accounts.AuthenticateAsync("bob", "x")).Status);

            var ok = await accounts.AuthenticateAsync("ALICE", "s3cret");
            Assert.Equal(AccountAuthStatus.Ok, ok.Status);
            Assert.Equal("FRIEND42", ok.Account!.ReferralCode);      // custom field round-tripped
            Assert.True(ok.Account.LastLoginUnix > 0);

            // ban blocks login
            Assert.True(await accounts.BanAsync(acc.Id, "cheating"));
            Assert.Equal(AccountAuthStatus.Banned, (await accounts.AuthenticateAsync("alice", "s3cret")).Status);
            Assert.True(await accounts.UnbanAsync(acc.Id));
            Assert.Equal(AccountAuthStatus.Ok, (await accounts.AuthenticateAsync("alice", "s3cret")).Status);

            // change password
            Assert.True(await accounts.SetPasswordAsync(acc.Id, "newpass"));
            Assert.Equal(AccountAuthStatus.WrongPassword, (await accounts.AuthenticateAsync("alice", "s3cret")).Status);
            Assert.Equal(AccountAuthStatus.Ok, (await accounts.AuthenticateAsync("alice", "newpass")).Status);
        }

        // ---- CharacterStore ----

        [Fact]
        public async Task Create_list_slotlimit_uniquename_and_custom_field()
        {
            var chars = new CharacterServer<MyCharacter>(
                new MemoryDocumentStore<MyCharacter>(),
                new CharacterOptions { MaxPerAccount = 2 });

            var vip = DateTime.UtcNow.AddDays(30);
            var c1 = await chars.CreateAsync("acc-1", new MyCharacter { Name = "Archer", Slot = 0, ClassId = 5, VipUntil = vip });
            Assert.NotEqual("", c1.Id);
            Assert.Equal("acc-1", c1.AccountId);

            await chars.CreateAsync("acc-1", new MyCharacter { Name = "Mage", Slot = 1 });

            // slot limit
            await Assert.ThrowsAsync<CharacterException>(() =>
                chars.CreateAsync("acc-1", new MyCharacter { Name = "Third", Slot = 2 }));

            // unique name (global, case-insensitive)
            await Assert.ThrowsAsync<CharacterException>(() =>
                chars.CreateAsync("acc-2", new MyCharacter { Name = "archer" }));

            var list = await chars.ListAsync("acc-1");
            Assert.Equal(2, list.Count);

            // custom field round-trips
            var loaded = await chars.GetAsync(c1.Id);
            Assert.Equal(5, loaded!.ClassId);
            Assert.Equal(vip, loaded.VipUntil);
        }

        [Fact]
        public async Task Softdelete_restore_and_purge()
        {
            var store = new MemoryDocumentStore<MyCharacter>();
            var chars = new CharacterServer<MyCharacter>(store, new CharacterOptions { RestoreWindowSeconds = 3600 });

            var c = await chars.CreateAsync("acc-1", new MyCharacter { Name = "Temp" });

            Assert.True(await chars.SoftDeleteAsync(c.Id));
            Assert.Empty(await chars.ListAsync("acc-1"));                  // hidden from normal lists
            Assert.Single(await chars.ListAsync("acc-1", includeDeleted: true));

            Assert.True(await chars.RestoreAsync(c.Id));                   // within the window
            Assert.Single(await chars.ListAsync("acc-1"));

            Assert.True(await chars.PurgeAsync(c.Id));                     // hard delete
            Assert.Null(await chars.GetAsync(c.Id));
        }
    }
}
