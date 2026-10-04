using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Agentd.Bff.Security;

/// <summary>
/// CSRF protection for <c>/api</c> and <c>/bff</c>: every unsafe method needs the <c>X-XSRF-TOKEN</c>
/// header matching the HttpOnly antiforgery cookie (<c>__Host-agentd.af</c> over https, <c>agentd.af</c> on http loopback). The token comes from
/// <c>GET /bff/antiforgery</c> and lives only in the SPA's memory. Failure → 400 with
/// <c>code = antiforgery_invalid</c> (the SPA refetches the token once on that code).
/// </summary>
internal sealed class AntiforgeryFilter : IEndpointFilter
{
    public const string HeaderName = "X-XSRF-TOKEN";
    public const string SecureCookieName = "__Host-agentd.af";
    public const string CookieName = "agentd.af";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method))
        {
            return await next(context).ConfigureAwait(false);
        }

        var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
        if (!await antiforgery.IsRequestValidAsync(http).ConfigureAwait(false))
        {
            return TypedResults.Problem(
                "The request needs a valid X-XSRF-TOKEN header; get one from GET /bff/antiforgery.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid antiforgery token",
                extensions: new Dictionary<string, object?> { ["code"] = "antiforgery_invalid" });
        }

        return await next(context).ConfigureAwait(false);
    }
}
