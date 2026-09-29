using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace Agentd.Host.Options;

/// <summary>Root of the <c>Agentd</c> configuration section. Later phases add subsections.</summary>
public sealed class AgentdOptions
{
    public const string Section = "Agentd";

    [Required]
    [ValidateObjectMembers]
    public WebOptions Web { get; init; } = new();
}

public sealed class WebOptions
{
    /// <summary>Listen address when not launched by Aspire and no ASP.NET Core URL is configured.</summary>
    [Required]
    [Url]
    public string Urls { get; init; } = "http://127.0.0.1:7780";
}
