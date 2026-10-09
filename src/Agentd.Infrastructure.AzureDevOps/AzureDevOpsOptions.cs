namespace Agentd.Infrastructure.AzureDevOps;

public enum AzureDevOpsAuth
{
    /// <summary>The machine's <c>az login</c> (AzureCliCredential).</summary>
    AzCli,

    /// <summary>A personal access token (secret <c>Agentd:AzureDevOps:Pat</c>).</summary>
    Pat,

    /// <summary>A Microsoft Entra service principal: <c>TenantId</c>, <c>ClientId</c> and the secret <c>Agentd:AzureDevOps:ClientSecret</c>.</summary>
    ServicePrincipal,
}

/// <summary>Configuration section <c>Agentd:AzureDevOps</c>. Work items are polled from <see cref="Organization"/>/<see cref="Project"/>.</summary>
public sealed class AzureDevOpsOptions
{
    public const string Section = "Agentd:AzureDevOps";

    /// <summary>Organization name (e.g. "ermsystem"); API calls go to https://dev.azure.com/{organization}/.</summary>
    public string Organization { get; set; } = "";

    public string Project { get; set; } = "";

    public AzureDevOpsAuth Auth { get; set; } = AzureDevOpsAuth.AzCli;

    /// <summary>PAT when <see cref="Auth"/> is <see cref="AzureDevOpsAuth.Pat"/>. From secrets/env only, never from a committed file.</summary>
    public string? Pat { get; set; }

    /// <summary>The service principal's directory (tenant) id or domain, when <see cref="Auth"/> is <see cref="AzureDevOpsAuth.ServicePrincipal"/>.</summary>
    public string? TenantId { get; set; }

    /// <summary>The service principal's application (client) id.</summary>
    public string? ClientId { get; set; }

    /// <summary>The service principal's client secret. From secrets/env only, never from a committed file.</summary>
    public string? ClientSecret { get; set; }

    public Uri BaseUrl { get; set; } = new("https://dev.azure.com/");
}
