using System.Net;
using System.Text.Json;
using Agentd.Application.Abstractions;
using Agentd.Application.Events;
using Agentd.Application.Jobs;
using Agentd.Bff.Security;
using Agentd.Domain.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Agentd.Bff.Tests;

[TestClass]
public sealed class BffSecurityTests
{
    [TestMethod]
    public async Task Unsafe_requests_need_the_antiforgery_header()
    {
        await using var app = await StartAsync();
        var client = await new AntiforgeryClient(app).InitAsync();

        using var withToken = await client.PostAsync("/api/jobs/7/cancel");
        using var without = await client.PostAsync("/api/jobs/7/cancel", token: string.Empty);

        Assert.AreEqual(HttpStatusCode.NoContent, withToken.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, without.StatusCode);
        Assert.AreEqual("antiforgery_invalid", JsonDocument.Parse(await without.Content.ReadAsStringAsync()).RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task A_token_only_works_with_its_own_cookie()
    {
        await using var app = await StartAsync();
        var a = await new AntiforgeryClient(app).InitAsync();
        var b = await new AntiforgeryClient(app).InitAsync();

        using var mixed = await a.PostAsync("/api/jobs/7/cancel", cookie: b.Cookie);

        Assert.AreEqual(HttpStatusCode.BadRequest, mixed.StatusCode);
    }

    [TestMethod]
    [DataRow(null, "agentd.af=", false)]
    [DataRow("https://127.0.0.1:7781", "__host-agentd.af=", true)]
    public async Task The_antiforgery_cookie_is_never_readable_by_scripts(string? httpsUrl, string name, bool secure)
    {
        await using var app = await StartAsync(httpsUrl);
        using var client = app.GetTestClient();
        if (httpsUrl is not null)
        {
            client.BaseAddress = new Uri("https://localhost/");
        }

        using var response = await client.GetAsync(new Uri("/bff/antiforgery", UriKind.Relative));

        var cookie = response.Headers.GetValues("Set-Cookie").Single().ToLowerInvariant();
        StringAssert.StartsWith(cookie, name);
        foreach (var flag in new[] { "httponly", "samesite=strict", "path=/" })
        {
            StringAssert.Contains(cookie, flag);
        }

        Assert.AreEqual(secure, cookie.Contains("; secure", StringComparison.Ordinal));
        Assert.DoesNotContain("domain=", cookie);
        Assert.AreEqual("no-store", response.Headers.CacheControl?.ToString());
    }

    [TestMethod]
    public async Task The_local_user_is_the_admin_and_gets_work_without_a_token()
    {
        await using var app = await StartAsync();

        var user = JsonDocument.Parse(await app.GetTestClient().GetStringAsync(new Uri("/bff/user", UriKind.Relative))).RootElement;

        Assert.AreEqual("local", user.GetProperty("name").GetString());
        Assert.AreEqual("Admin", user.GetProperty("roles")[0].GetString());
        Assert.AreEqual("local", user.GetProperty("provider").GetString());
    }

    [TestMethod]
    [DataRow("X-Forwarded-For", "203.0.113.9")]
    [DataRow("Cf-Connecting-IP", "203.0.113.9")]
    [DataRow("Forwarded", "for=203.0.113.9")]
    public async Task Requests_through_a_proxy_are_not_the_local_user(string header, string value)
    {
        await using var app = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/bff/user", UriKind.Relative));
        request.Headers.Add(header, value);

        using var response = await app.GetTestClient().SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Cors_is_never_enabled()
    {
        await using var app = await StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri("/api/dashboard", UriKind.Relative));
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using var preflight = await app.GetTestClient().SendAsync(request);

        Assert.IsFalse(preflight.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [TestMethod]
    [DataRow("http://127.0.0.1:7780", true)]
    [DataRow("http://localhost:5000", true)]
    [DataRow("http://[::1]:7780", true)]
    [DataRow("http://0.0.0.0:7780", false)]
    [DataRow("http://*:7780", false)]
    [DataRow("http://+:7780", false)]
    [DataRow("http://192.168.1.10:7780", false)]
    public void Mode_none_only_starts_on_loopback(string url, bool allowed)
    {
        var result = Validate(new() { ["urls"] = $"http://127.0.0.1:1;{url}" }, AuthMode.None);

        Assert.AreEqual(allowed, result.Succeeded, result.FailureMessage);
    }

    [TestMethod]
    public void Ports_that_bind_every_interface_and_unbuilt_modes_are_refused()
    {
        Assert.IsTrue(Validate(new() { ["http_ports"] = "8080" }, AuthMode.None).Failed);
        Assert.IsTrue(Validate(new() { ["Kestrel:Endpoints:Web:Url"] = "http://0.0.0.0:7780" }, AuthMode.None).Failed);
        Assert.IsTrue(Validate([], AuthMode.CloudflareAccess).Failed, "until T3.14");
    }

    [TestMethod]
    public async Task The_host_refuses_to_start_when_mode_none_is_exposed()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["urls"] = "http://0.0.0.0:7780";
        builder.Services.AddBff();
        builder.Services.AddSingleton<ILiveEvents>(new EventHub(NullLogger<EventHub>.Instance));
        await using var app = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => app.StartAsync());

        StringAssert.Contains(error.Message, "only listens on loopback");
    }

    private static ValidateOptionsResult Validate(Dictionary<string, string?> settings, AuthMode mode)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new BffAuthOptionsValidator(config).Validate(null, new BffAuthOptions { Mode = mode });
    }

    private static async Task<WebApplication> StartAsync(string? urls = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration["urls"] = urls;
        builder.Services.AddBff();
        builder.Services.AddSingleton<ILiveEvents>(new EventHub(NullLogger<EventHub>.Instance));
        builder.Services.AddSingleton<ICommandHandler<CancelJob, Unit>>(new Cancel());
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBff();
        await app.StartAsync();
        return app;
    }

    private sealed class Cancel : ICommandHandler<CancelJob, Unit>
    {
        public Task<Result<Unit>> Handle(CancelJob command, CancellationToken cancellationToken) => Task.FromResult<Result<Unit>>(Unit.Value);
    }
}
