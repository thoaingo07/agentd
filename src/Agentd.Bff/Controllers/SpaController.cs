using Agentd.Bff.Vite;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Agentd.Bff.Controllers;

/// <summary>Serves the SPA shell for "/" and every client-side route.</summary>
public sealed class SpaController(ViteAssetResolver assets) : Controller
{
    [HttpGet]
    public IActionResult Index()
    {
        try
        {
            return View(assets.Resolve());
        }
        catch (ViteManifestException ex)
        {
            return Problem(title: "Web UI is not built", detail: ex.Message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
