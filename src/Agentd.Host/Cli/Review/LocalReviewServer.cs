using System.Net;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using Agentd.Application.Reviews;
using Agentd.Bff.Endpoints;
using Agentd.Bff.Security;
using Agentd.Web.Vite;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Agentd.Host.Cli.Review;

/// <summary>
/// The review page on the developer's machine (docs/architect/review-sessions.md §5): the web UI's review app, embedded in
/// the binary, served on 127.0.0.1 and a random port for one <see cref="LocalReviewSession"/>, with the same API shapes as
/// the server's <c>/api/reviews</c>. Only a browser that opened the printed link (one-time token → cookie) gets in, and
/// changes need the antiforgery token.
/// </summary>
internal sealed class LocalReviewServer : IAsyncDisposable
{
    private const string Cookie = "agentd-review";
    private readonly WebApplication _app;
    private readonly TaskCompletionSource<string> _sent = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LocalReviewServer(WebApplication app, Uri url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>The link to open (carries the one-time token).</summary>
    public Uri Url { get; }

    /// <summary>Completes with what was written when the developer clicks Send.</summary>
    public Task<string> Sent => _sent.Task;

    /// <summary>False when the binary was built without the web UI (then <c>agentd review</c> stays in the terminal).</summary>
    public static bool Available => new EmbeddedWebAssets(typeof(EmbeddedWebAssets).Assembly).HasAssets;

    /// <param name="session">The review.</param>
    /// <param name="send">Writes Send's text for the agent; returns where (e.g. <c>.agentd/review.md</c>).</param>
    /// <param name="ct">Cancels the start.</param>
    public static async Task<LocalReviewServer> StartAsync(LocalReviewSession session, Func<string, Task<string>> send, CancellationToken ct)
    {
        var token = Secret();
        var antiforgery = Secret();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseKestrel(o => o.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        builder.Services.AddOptions<ViteOptions>();
        builder.Services.AddSingleton<ViteHelper>();
        var app = builder.Build();
        app.Environment.WebRootFileProvider = new EmbeddedWebAssets(typeof(EmbeddedWebAssets).Assembly);
        LocalReviewServer? server = null;

        app.Use(async (context, next) =>
        {
            context.Response.Headers.ContentSecurityPolicy = SecurityHeaders.Policy(https: false, inlineStyles: false).Replace("; report-uri " + SecurityHeaders.ReportPath + "; report-to csp", string.Empty, StringComparison.Ordinal);
            context.Response.Headers.XContentTypeOptions = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            if (context.Request.Path.StartsWithSegments("/_content", StringComparison.Ordinal))
            {
                await next(context).ConfigureAwait(false);   // the app's own scripts and styles: nothing private
                return;
            }

            if (context.Request.Query["token"] == token)
            {
                context.Response.Cookies.Append(Cookie, token, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/" });
                context.Response.Redirect($"/reviews/{LocalReviewSession.Id}");
                return;
            }

            if (context.Request.Cookies[Cookie] != token)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Open the link `agentd review` printed in your terminal.").ConfigureAwait(false);
                return;
            }

            if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers["X-XSRF-TOKEN"] != antiforgery)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
        app.UseStaticFiles();

        const string api = "/api/reviews/1";
        app.MapGet("/bff/antiforgery", () => Results.Ok(new { token = antiforgery }));
        app.MapGet(api, () => Results.Ok(ReviewSessionDetailVm.From(session.View())));
        app.MapGet(api + "/diff", () => Results.Ok(new ReviewDiffVm(session.BaseCommit, "working tree", session.Files, session.Diff, false)));
        app.MapPut(api + "/findings/{number:int}", (int number, ReviewSessionEndpoints.DecisionRequest? body) =>
            !FindingDecisions.IsValid(body?.Decision) || (body!.Decision == FindingDecisions.Edited && string.IsNullOrWhiteSpace(body.Text))
                ? Results.Problem("A decision is kept, dropped, or edited with your text.", statusCode: StatusCodes.Status400BadRequest)
                : session.Decide(number, body.Decision!, body.Text?.Trim()) ? Results.NoContent() : Results.NotFound());
        app.MapPost(api + "/comments", (ReviewSessionEndpoints.CommentRequest? body) =>
            string.IsNullOrWhiteSpace(body?.Text)
                ? Results.Problem("Write the comment.", statusCode: StatusCodes.Status400BadRequest)
                : session.Comment(body.File, body.Line, body.EndLine, body.Text.Trim()) is { } c ? Results.Created($"{api}/comments/{c.Id}", ReviewCommentVm.From(c)) : Results.Conflict());
        app.MapDelete(api + "/comments/{id:long}", (long id) => session.DeleteComment(id) ? Results.NoContent() : Results.NotFound());
        app.MapPost(api + "/send", async () =>
        {
            var path = await send(session.Send()).ConfigureAwait(false);
            server!._sent.TrySetResult(path);
            return Results.Ok(new { path });
        });
        app.MapGet("/{**rest}", (ViteHelper vite) =>
        {
            using var tags = new StringWriter();
            vite.Tags("review").WriteTo(tags, HtmlEncoder.Default);
            return Results.Content(
                $"<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">" +
                $"<title>agentd review</title><link rel=\"icon\" type=\"image/svg+xml\" href=\"{vite.Url("favicon.svg")}\"><script src=\"{vite.Url("theme-init.js")}\"></script>{tags}</head>" +
                "<body class=\"bg-base-100 text-base-content\"><div id=\"app\"></div></body></html>", "text/html; charset=utf-8");
        });

        await app.StartAsync(ct).ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server = new LocalReviewServer(app, new Uri($"{address.Replace("[::1]", "localhost", StringComparison.Ordinal)}/?token={token}"));
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
