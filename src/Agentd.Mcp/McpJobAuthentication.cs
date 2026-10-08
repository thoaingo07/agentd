using System.Security.Claims;
using System.Text.Encodings.Web;
using Agentd.Application.Ports;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Agentd.Mcp;

/// <summary>
/// Authenticates <c>/mcp</c> requests by bearer token: a job's (the job tools) or a chat's (the read-only chat tools).
/// No cookies; the job or chat id comes only from the token.
/// </summary>
public sealed class McpJobAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IMcpTokenIssuer tokens) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "McpJob";
    public const string JobIdClaim = "agentd:job_id";
    public const string ChatIdClaim = "agentd:chat_id";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = header["Bearer ".Length..].Trim();
        Claim? claim = tokens.Validate(token) is { } jobId ? new Claim(JobIdClaim, jobId.ToString())
            : tokens.ValidateChat(token) is { } chatId ? new Claim(ChatIdClaim, chatId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : null;
        if (claim is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid or expired agent token."));
        }

        var identity = new ClaimsIdentity([claim], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}
