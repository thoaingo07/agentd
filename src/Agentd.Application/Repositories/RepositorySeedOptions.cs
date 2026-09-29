namespace Agentd.Application.Repositories;

/// <summary>Declarative repositories from configuration (<c>Agentd:Repositories</c>), upserted on startup.</summary>
public sealed class RepositorySeedOptions
{
    public const string Section = "Agentd:Repositories";

    public IList<RepositorySeed> Items { get; } = [];
}

public sealed class RepositorySeed
{
    public string Url { get; set; } = "";

    public string? Name { get; set; }

    public string? BaseBranch { get; set; }

    public string? MatchTag { get; set; }

    public IList<string> MatchAreaPaths { get; } = [];
}
