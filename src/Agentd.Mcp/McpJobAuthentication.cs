using System.Security.Claims;
using System.Text.Encodings.Web;
using Agentd.Application.Ports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Mcp;

/// <summary>Authenticates <c>/mcp</c> requests by per-job bearer token. No cookies; the job id comes only from the token.</summary>
public sealed class McpJobAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IMcpTokenIssuer tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "McpJob";
    public const string JobIdClaim = "agentd:job_id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (tokens.Validate(header["Bearer ".Length..].Trim()) is not { } jobId)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired agent token."));
        }

        var identity = new ClaimsIdentity([new Claim(JobIdClaim, jobId.ToString())], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
