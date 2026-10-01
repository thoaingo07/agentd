using Agentd.Domain.Messaging;
using Agentd.Domain.Users;
using Agentd.Infrastructure.Persistence.Users;
using Npgsql;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class UserDirectoryTests
{
    private static readonly ProviderKey s_discord = ProviderKey.From("discord");
    private static int s_next;

    [TestMethod]
    public async Task Sync_inserts_then_is_a_no_op()
    {
        await using var db = await Database.CreateMigratedAsync(NextName());
        var directory = new UserDirectory(db);
        User[] users = [U("tngo", ["Admin"], "789"), U("alice", [], "42")];

        await directory.SyncAsync(users, default);
        await directory.SyncAsync(users, default);

        var tngo = (await directory.FindByIdentityAsync(s_discord, "789", default))!;
        Assert.AreEqual("tngo", tngo.Name);
        CollectionAssert.AreEqual(new[] { "Admin" }, tngo.Roles.ToArray());
        Assert.IsTrue(tngo.IsActive);
        Assert.AreEqual(2L, await CountAsync(db, "SELECT count(*) FROM agentd.users"));
        Assert.AreEqual(2L, await CountAsync(db, "SELECT count(*) FROM agentd.user_identities"));
        Assert.IsNull(await directory.FindByIdentityAsync(s_discord, "nope", default));
    }

    [TestMethod]
    public async Task Users_removed_from_config_are_deactivated_and_lose_their_identities()
    {
        await using var db = await Database.CreateMigratedAsync(NextName());
        var directory = new UserDirectory(db);
        await directory.SyncAsync([U("tngo", [], "789"), U("alice", [], "42")], default);
        Assert.IsNotNull(await directory.FindByIdentityAsync(s_discord, "42", default));

        await directory.SyncAsync([U("tngo", [], "789")], default);

        Assert.IsNull(await directory.FindByIdentityAsync(s_discord, "42", default), "the cache is cleared by a sync");
        Assert.AreEqual(1L, await CountAsync(db, "SELECT count(*) FROM agentd.users WHERE NOT is_active AND name = 'alice'"), "kept, not deleted");
    }

    [TestMethod]
    public async Task An_identity_can_move_to_another_user_and_names_match_case_insensitively()
    {
        await using var db = await Database.CreateMigratedAsync(NextName());
        var directory = new UserDirectory(db);
        await directory.SyncAsync([U("alice", [], "42"), U("bob", [], "7")], default);

        await directory.SyncAsync([U("Alice", ["Operator"], "7"), U("bob", [], "42")], default);

        Assert.AreEqual("bob", (await directory.FindByIdentityAsync(s_discord, "42", default))?.Name);
        var alice = (await directory.FindByIdentityAsync(s_discord, "7", default))!;
        Assert.AreEqual("alice", alice.Name, "the stored name is kept");
        CollectionAssert.AreEqual(new[] { "Operator" }, alice.Roles.ToArray());
        Assert.AreEqual(2L, await CountAsync(db, "SELECT count(*) FROM agentd.users"));
    }

    private static string NextName() => $"users_{Interlocked.Increment(ref s_next)}";

    private static User U(string name, string[] roles, string discordId) =>
        new(default, name, null, roles, true, [new UserIdentity(s_discord, discordId)]);

    private static async Task<long> CountAsync(NpgsqlDataSource db, string sql)
    {
        await using var cmd = db.CreateCommand(sql);
        return (long)(await cmd.ExecuteScalarAsync())!;
    }
}
