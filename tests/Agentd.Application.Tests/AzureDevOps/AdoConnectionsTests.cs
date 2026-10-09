using System.Security.Cryptography;
using System.Text;
using Agentd.Application.AzureDevOps;
using Agentd.Application.Ports;

namespace Agentd.Application.Tests.AzureDevOps;

[TestClass]
public sealed class AdoConnectionsTests
{
    private static readonly Uri s_callback = new("https://agentd.example/bff/ado/callback");
    private static readonly Guid s_dev = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private readonly FakeDelegation _delegation = new();
    private readonly MemoryConnections _store = new();

    [TestMethod]
    public void A_sign_in_starts_with_a_one_time_state_and_a_pkce_s256_challenge()
    {
        var a = Service().Start(s_callback);
        var b = Service().Start(s_callback);

        Assert.AreNotEqual(a.State, b.State);
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(a.CodeVerifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Assert.AreEqual((a.State, challenge, s_callback), _delegation.Authorized[0]);
        Assert.IsGreaterThanOrEqualTo(43, a.CodeVerifier.Length, "RFC 7636: 43–128 characters");
    }

    [TestMethod]
    public async Task Completing_stores_the_encrypted_token_for_the_person_who_connected()
    {
        var start = Service().Start(s_callback);

        var result = await Service().CompleteAsync("dev@example.com", "the-code", start.State, (start.State, start.CodeVerifier), s_callback, default);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(("the-code", start.CodeVerifier), _delegation.Redeemed.Single());
        var row = _store.Rows.Single();
        Assert.AreEqual((s_dev, "dev.one@example.com", "dev@example.com"), (row.IdentityId, row.UniqueName, row.WebLogin));
        Assert.AreEqual("enc:refresh-SECRET", Encoding.UTF8.GetString(row.RefreshToken), "protected before it's stored");
        Assert.AreEqual("Dev One", (await Service().MineAsync("dev@example.com", default)).Single().DisplayName);
        Assert.IsEmpty(await Service().MineAsync("other@example.com", default), "only your own");
    }

    [TestMethod]
    [DataRow("other-state", "code", "didn't start here")]
    [DataRow(null, "code", "didn't start here")]
    [DataRow("STATE", null, "didn't send a sign-in code")]
    public async Task A_callback_that_isnt_ours_stores_nothing(string? state, string? code, string expected)
    {
        var result = await Service().CompleteAsync("dev@example.com", code, state, ("STATE", "verifier"), s_callback, default);
        var noCookie = await Service().CompleteAsync("dev@example.com", "code", "STATE", null, s_callback, default);

        StringAssert.Contains(result.Error!.Message, expected);
        StringAssert.Contains(noCookie.Error!.Message, "didn't start here");
        Assert.IsEmpty(_store.Rows);
        Assert.IsEmpty(_delegation.Redeemed);
    }

    [TestMethod]
    public async Task Microsofts_refusal_is_shown_and_nothing_is_stored()
    {
        _delegation.Refusal = "AADSTS65001: The user or administrator has not consented to use the application.";

        var result = await Service().CompleteAsync("dev@example.com", "code", "S", ("S", "v"), s_callback, default);

        StringAssert.Contains(result.Error!.Message, "AADSTS65001");
        Assert.IsEmpty(_store.Rows);
    }

    [TestMethod]
    public async Task Disconnect_removes_only_your_own()
    {
        var start = Service().Start(s_callback);
        await Service().CompleteAsync("dev@example.com", "c", start.State, (start.State, start.CodeVerifier), s_callback, default);

        Assert.IsFalse(await Service().DisconnectAsync("admin@example.com", s_dev, default));
        Assert.IsTrue(await Service().DisconnectAsync("dev@example.com", s_dev, default));
        Assert.IsEmpty(_store.Rows);
    }

    private AdoConnections Service() => new(_delegation, _store, new FakeProtector());

    internal sealed class FakeDelegation : IAdoDelegation
    {
        public List<(string State, string Challenge, Uri Redirect)> Authorized { get; } = [];

        public List<(string Code, string Verifier)> Redeemed { get; } = [];

        public string? Refusal { get; set; }

        public bool IsConfigured => true;

        public Uri AuthorizeUrl(string state, string codeChallenge, Uri redirectUri)
        {
            Authorized.Add((state, codeChallenge, redirectUri));
            return new Uri("https://login.example/authorize");
        }

        public Task<DelegatedSignIn> RedeemAsync(string code, string codeVerifier, Uri redirectUri, CancellationToken cancellationToken)
        {
            if (Refusal is not null)
            {
                throw new InvalidOperationException(Refusal);
            }

            Redeemed.Add((code, codeVerifier));
            return Task.FromResult(new DelegatedSignIn(s_dev, "dev.one@example.com", "Dev One", "refresh-SECRET"));
        }

        public List<string> Refreshed { get; } = [];

        /// <summary>Thrown by <see cref="RefreshAsync"/>: InvalidOperationException (refused) or HttpRequestException (unreachable).</summary>
        public Exception? RefreshFailure { get; set; }

        public async Task<DelegatedToken> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            await Task.Yield();
            lock (Refreshed)
            {
                Refreshed.Add(refreshToken);
            }

            return RefreshFailure is { } failure ? throw failure : new DelegatedToken($"access-{Refreshed.Count}", $"rotated-{Refreshed.Count}", TimeSpan.FromHours(1));
        }
    }

    internal sealed class FakeProtector : ITokenProtector
    {
        public byte[] Protect(string token) => Encoding.UTF8.GetBytes("enc:" + token);

        public string? Unprotect(byte[] protectedToken) => Encoding.UTF8.GetString(protectedToken)[4..];
    }

    internal sealed class MemoryConnections : IAdoUserConnections
    {
        public List<AdoUserConnection> Rows { get; } = [];

        public Task UpsertAsync(Guid identityId, string uniqueName, string displayName, string webLogin, byte[] refreshToken, CancellationToken cancellationToken)
        {
            Rows.RemoveAll(r => r.IdentityId == identityId);
            Rows.Add(new AdoUserConnection(identityId, uniqueName, displayName, webLogin, refreshToken, false, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
            return Task.CompletedTask;
        }

        public Task<AdoUserConnection?> FindByIdentityAsync(Guid identityId, CancellationToken cancellationToken) => Task.FromResult(Rows.SingleOrDefault(r => r.IdentityId == identityId));

        public Task<AdoUserConnection?> FindByUniqueNameAsync(string uniqueName, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.FirstOrDefault(r => string.Equals(r.UniqueName, uniqueName, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<AdoUserConnection>> ListByWebLoginAsync(string webLogin, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AdoUserConnection>>([.. Rows.Where(r => r.WebLogin == webLogin)]);

        public Task<bool> StoreTokenAsync(Guid identityId, byte[] refreshToken, CancellationToken cancellationToken)
        {
            var i = Rows.FindIndex(r => r.IdentityId == identityId);
            if (i >= 0)
            {
                Rows[i] = Rows[i] with { RefreshToken = refreshToken, Failed = false, LastError = null };
            }

            return Task.FromResult(i >= 0);
        }

        public Task MarkFailedAsync(Guid identityId, string reason, CancellationToken cancellationToken)
        {
            var i = Rows.FindIndex(r => r.IdentityId == identityId);
            if (i >= 0)
            {
                Rows[i] = Rows[i] with { Failed = true, LastError = reason };
            }

            return Task.CompletedTask;
        }

        public Task<bool> DeleteAsync(Guid identityId, string webLogin, CancellationToken cancellationToken) =>
            Task.FromResult(Rows.RemoveAll(r => r.IdentityId == identityId && r.WebLogin == webLogin) > 0);
    }
}
