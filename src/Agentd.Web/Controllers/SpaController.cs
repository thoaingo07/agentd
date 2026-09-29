using Agentd.Web.Vite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Agentd.Web.Controllers;

/// <summary>
/// Serves the SPA shells. Each app in ClientApps/ gets an action + view; the dashboard app is the
/// default for "/" and every unmatched client route.
/// </summary>
public sealed class SpaController(ViteHelper vite) : Controller
{
    [HttpGet]
    public IActionResult Dashboard() => Shell("dashboard");

    private IActionResult Shell(string app)
    {
        try
        {
            _ = vite.Assets(app);   // fail early with a clear 503 if the build is missing
            return View(app);
        }
        catch (ViteManifestException ex)
        {
            return Problem(title: "Web UI is not built", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
