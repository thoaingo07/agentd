using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Agentd.Bff.Security;

/// <summary><c>Agentd:Auth:CloudflareAccess</c>. None of these are secrets: the AUD tag only names the Access application.</summary>
public sealed class CloudflareAccessOptions
{
    /// <summary>e.g. <c>myteam.cloudflareaccess.com</c>.</summary>
    public string TeamDomain { get; set; } = string.Empty;

    /// <summary>The Access application's AUD tag.</summary>
    public string Audience { get; set; } = string.Empty;

    /// <summary>Optional filter on top of the Access policy; empty = everyone Access lets through.</summary>
    public IReadOnlyList<string> AllowedEmails { get; set; } = [];

    /// <summary>These users get the Admin role; everyone else is a User (until Phase 5 roles).</summary>
    public IReadOnlyList<string> AdminEmails { get; set; } = [];

    public Uri Issuer => new($"https://{TeamDomain.Trim().TrimEnd('/')}");

    public Uri CertsUrl => new(Issuer, "/cdn-cgi/access/certs");
}

/// <summary>
/// Cloudflare Access signs users in at its edge and adds a signed JWT to every request it lets through
/// (<c>Cf-Access-Jwt-Assertion</c>). This handler trusts a request only with a valid token for our team
/// and application: Cloudflare's signing keys, issuer, audience, lifetime. Loopback gives no exemption
/// (cloudflared itself connects from loopback). The user is the token's email.
/// </summary>
public sealed class CloudflareAccessAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<CloudflareAccessOptions> access,
    CloudflareAccessKeys keys) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "CloudflareAccess";
    public const string HeaderName = "Cf-Access-Jwt-Assertion";

    /// <summary>Set by Access in the browser; used only for the hub's WebSocket upgrade.</summary>
    public const string CookieName = "CF_Authorization";

    private static readonly JsonWebTokenHandler s_tokens = new() { MapInboundClaims = false };

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Headers[HeaderName].ToString();
        if (token.Length == 0 && Request.Path.StartsWithSegments("/hubs", StringComparison.Ordinal))
        {
            token = Request.Cookies[CookieName] ?? string.Empty;
        }

        if (token.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        var settings = access.Value;
        var result = await ValidateAsync(token, settings, refreshed: false).ConfigureAwait(false);
        if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // Cloudflare rotated its keys: fetch them once and try again.
            result = await ValidateAsync(token, settings, refreshed: true).ConfigureAwait(false);
        }

        if (!result.IsValid)
        {
            return AuthenticateResult.Fail("Invalid Cloudflare Access token: " + result.Exception?.GetType().Name);
        }

        var email = result.ClaimsIdentity.FindFirst("email")?.Value;
        if (string.IsNullOrWhiteSpace(email))
        {
            return AuthenticateResult.Fail("The Cloudflare Access token has no email.");
        }

        if (settings.AllowedEmails.Count > 0 && !settings.AllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase))
        {
            return AuthenticateResult.Fail($"{email} is not in Agentd:Auth:CloudflareAccess:AllowedEmails.");
        }

        var role = settings.AdminEmails.Contains(email, StringComparer.OrdinalIgnoreCase) ? "Admin" : "User";
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, result.ClaimsIdentity.FindFirst("sub")?.Value ?? email),
                new Claim(ClaimTypes.Name, email),
                new Claim(ClaimTypes.Email, email),
                new Claim(ClaimTypes.Role, role),
            ],
            SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    private async Task<TokenValidationResult> ValidateAsync(string token, CloudflareAccessOptions settings, bool refreshed) =>
        await s_tokens.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = settings.Issuer.ToString().TrimEnd('/'),
            ValidAudience = settings.Audience,
            IssuerSigningKeys = await keys.GetAsync(refreshed, Context.RequestAborted).ConfigureAwait(false),
            ValidateLifetime = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        }).ConfigureAwait(false);
}

/// <summary>Cloudflare's signing keys (JWKS), cached; refreshed when a token names an unknown key (at most once a minute).</summary>
public sealed class CloudflareAccessKeys(IHttpClientFactory http, IOptions<CloudflareAccessOptions> access, TimeProvider time) : IDisposable
{
    public const string HttpClientName = "cloudflare-access";
    private static readonly TimeSpan s_minRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan s_maxAge = TimeSpan.FromHours(12);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<SecurityKey> _keys = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public async Task<IReadOnlyList<SecurityKey>> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var due = _keys.Count == 0 || now - _fetchedAt > s_maxAge || (refresh && now - _fetchedAt > s_minRefreshInterval);
        if (!due)
        {
            return _keys;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_keys.Count == 0 || time.GetUtcNow() - _fetchedAt > s_minRefreshInterval)
            {
                using var client = http.CreateClient(HttpClientName);
                var json = await client.GetStringAsync(access.Value.CertsUrl, cancellationToken).ConfigureAwait(false);
                _keys = new JsonWebKeySet(json).GetSigningKeys().ToList();
                _fetchedAt = time.GetUtcNow();
            }

            return _keys;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
