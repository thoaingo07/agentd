namespace Agentd.Infrastructure.AzureDevOps;

public enum AzureDevOpsAuth
{
    /// <summary>The machine's <c>az login</c> (AzureCliCredential).</summary>
    AzCli,

    /// <summary>A personal access token (secret <c>Agentd:AzureDevOps:Pat</c>).</summary>
    Pat,
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

    public Uri BaseUrl { get; set; } = new("https://dev.azure.com/");
}
