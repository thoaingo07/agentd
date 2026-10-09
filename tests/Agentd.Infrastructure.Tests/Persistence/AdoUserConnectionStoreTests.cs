using Agentd.Infrastructure.Persistence.Repositories;

namespace Agentd.Infrastructure.Tests.Persistence;

[TestClass]
[TestCategory("Integration")]
public sealed class AdoUserConnectionStoreTests
{
    private static readonly Guid s_dev = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [TestMethod]
    public async Task A_connection_round_trips_and_is_found_by_identity_name_and_web_login()
    {
        await using var db = await Database.CreateMigratedAsync("ado_connections_round_trip");
        var store = new AdoUserConnectionStore(db);

        await store.UpsertAsync(s_dev, "Dev.One@Example.com", "Dev One", "dev.one@example.com", [1, 2, 3], default);

        var byId = (await store.FindByIdentityAsync(s_dev, default))!;
        Assert.AreEqual(("Dev.One@Example.com", "Dev One", "dev.one@example.com", false), (byId.UniqueName, byId.DisplayName, byId.WebLogin, byId.Failed));
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, byId.RefreshToken);
        Assert.AreEqual(s_dev, (await store.FindByUniqueNameAsync("dev.one@EXAMPLE.com", default))?.IdentityId, "case-insensitive");
        Assert.HasCount(1, await store.ListByWebLoginAsync("dev.one@example.com", default));
        Assert.IsNull(await store.FindByIdentityAsync(Guid.NewGuid(), default));
        Assert.IsEmpty(await store.ListByWebLoginAsync("someone@else", default));
    }

    [TestMethod]
    public async Task A_failed_refresh_is_recorded_and_a_rotated_token_or_a_reconnect_clears_it()
    {
        await using var db = await Database.CreateMigratedAsync("ado_connections_failure");
        var store = new AdoUserConnectionStore(db);
        await store.UpsertAsync(s_dev, "dev@example.com", "Dev", "dev@example.com", [1], default);

        await store.MarkFailedAsync(s_dev, "AADSTS700082: the refresh token has expired", default);
        var failed = (await store.FindByIdentityAsync(s_dev, default))!;
        var stored = await store.StoreTokenAsync(s_dev, [9], default);
        var renewed = (await store.FindByIdentityAsync(s_dev, default))!;
        var gone = await store.StoreTokenAsync(Guid.NewGuid(), [9], default);

        Assert.AreEqual((true, "AADSTS700082: the refresh token has expired"), (failed.Failed, failed.LastError));
        Assert.IsTrue(stored);
        Assert.AreEqual((false, (string?)null), (renewed.Failed, renewed.LastError));
        CollectionAssert.AreEqual(new byte[] { 9 }, renewed.RefreshToken);
        Assert.IsFalse(gone, "no connection, nothing stored");
    }

    [TestMethod]
    public async Task Only_the_web_login_that_connected_it_can_remove_it()
    {
        await using var db = await Database.CreateMigratedAsync("ado_connections_delete");
        var store = new AdoUserConnectionStore(db);
        await store.UpsertAsync(s_dev, "dev@example.com", "Dev", "dev@example.com", [1], default);

        var byOther = await store.DeleteAsync(s_dev, "admin@example.com", default);
        var byOwner = await store.DeleteAsync(s_dev, "dev@example.com", default);

        Assert.AreEqual((false, true), (byOther, byOwner));
        Assert.IsNull(await store.FindByIdentityAsync(s_dev, default));
    }

    [TestMethod]
    public async Task Parallel_connects_and_token_rotations_leave_one_valid_row()
    {
        await using var db = await Database.CreateMigratedAsync("ado_connections_parallel");
        var store = new AdoUserConnectionStore(db);

        // Two tabs finishing the sign-in at once, while a job rotates the token: each call has its own connection.
        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() => i % 2 == 0
            ? store.UpsertAsync(s_dev, "dev@example.com", "Dev", "dev@example.com", [(byte)i], default)
            : store.StoreTokenAsync(s_dev, [(byte)i], default))));

        var rows = await store.ListByWebLoginAsync("dev@example.com", default);
        Assert.HasCount(1, rows);
        Assert.HasCount(1, rows[0].RefreshToken, "one whole token from one of the callers, never a mix");
        Assert.IsFalse(rows[0].Failed);
    }
}
