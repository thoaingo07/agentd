using System.Net;
using System.Text.Json;
using Agentd.Application.Events;
using Agentd.Bff.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class TailscaleAuthTests
{
    [TestMethod]
    public async Task Serves_identity_header_signs_in_the_tailnet_user_and_admins_get_admin()
    {
        await using var app = await StartAsync();

        var admin = await UserAsync(app, ("Tailscale-User-Login", "thoaingo07@gmail.com"), ("Tailscale-User-Name", "Thoai Ngo"), ("X-Forwarded-For", "100.126.116.29"));
        var other = await UserAsync(app, ("Tailscale-User-Login", "dev@example.com"), ("X-Forwarded-For", "100.111.15.109"));

        Assert.AreEqual(("Thoai Ngo", "Admin", "tailscale"), admin, "Serve's X-Forwarded-For (the tailnet address) doesn't hide that Serve connected");
        Assert.AreEqual(("dev@example.com", "User", "tailscale"), other);
    }

    [TestMethod]
    public async Task An_ssh_tunnel_is_still_the_local_admin_but_a_proxy_without_an_identity_is_not()
    {
        await using var app = await StartAsync();

        var tunnel = await UserAsync(app);
        using var proxied = await SendAsync(app, ("X-Forwarded-For", "100.64.0.9"));   // a tagged device, Funnel, another proxy

        Assert.AreEqual(("local", "Admin", "tailscale"), tunnel);
        Assert.AreEqual(HttpStatusCode.Unauthorized, proxied.StatusCode);
    }

    [TestMethod]
    public async Task Allowed_logins_keep_other_tailnet_users_out()
    {
        await using var app = await StartAsync(("Agentd:Auth:Tailscale:AllowedLogins:0", "friend@example.com"));

        using var stranger = await SendAsync(app, ("Tailscale-User-Login", "dev@example.com"), ("X-Forwarded-For", "100.111.15.109"));
        var friend = await UserAsync(app, ("Tailscale-User-Login", "friend@example.com"), ("X-Forwarded-For", "100.111.15.110"));

        Assert.AreEqual(HttpStatusCode.Unauthorized, stranger.StatusCode);
        Assert.AreEqual("User", friend.Role);
    }

    [TestMethod]
    public void Tailscale_mode_needs_an_admin_login()
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();

        var result = new BffAuthOptionsValidator(configuration).Validate(null, new BffAuthOptions { Mode = AuthMode.Tailscale });

        Assert.IsFalse(result.Succeeded);
        StringAssert.Contains(result.FailureMessage, "AdminLogins");
    }

    private static async Task<(string Name, string Role, string Provider)> UserAsync(WebApplication app, params (string Name, string Value)[] headers)
    {
        using var response = await SendAsync(app, headers);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var user = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        return (user.GetProperty("name").GetString()!, user.GetProperty("roles")[0].GetString()!, user.GetProperty("provider").GetString()!);
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplication app, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/bff/user", UriKind.Relative));
        foreach (var (name, value) in headers)
        {
            request.Headers.Add(name, value);
        }

        return await app.GetTestClient().SendAsync(request);
    }

    private static async Task<WebApplication> StartAsync(params (string Key, string Value)[] extra)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["Agentd:Auth:Mode"] = "Tailscale";
        builder.Configuration["Agentd:Auth:Tailscale:AdminLogins:0"] = "thoaingo07@gmail.com";
        foreach (var (key, value) in extra)
        {
            builder.Configuration[key] = value;
        }

        builder.Services.AddBff();
        builder.Services.AddSingleton<ILiveEvents>(new EventHub(NullLogger<EventHub>.Instance));
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Loopback;   // Serve connects from this machine
            return next(context);
        });
        app.UseBffForwardedHeaders();   // as the daemon does: X-Forwarded-For becomes RemoteIpAddress
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }
}
