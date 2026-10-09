namespace Agentd.Application.AzureDevOps;

/// <summary>
/// "As whom" for the Azure DevOps calls in the current flow (docs/architect/ado-user-delegation.md): inside
/// <see cref="Begin"/>, agentd's Azure DevOps clients use that person's delegated token; outside, agentd's own identity.
/// Call <see cref="Begin"/> from the method that makes the calls (not from inside an awaited helper): the scope flows
/// down the async calls it starts, never back up.
/// </summary>
public sealed class AdoActor
{
    private readonly AsyncLocal<Guid?> _current = new();

    /// <summary>The Azure DevOps identity the calls run as, or null for agentd itself.</summary>
    public Guid? Current => _current.Value;

    /// <summary>Runs the calls until the scope is disposed as <paramref name="identityId"/>; null leaves agentd's identity.</summary>
    public IDisposable Begin(Guid? identityId)
    {
        var previous = _current.Value;
        _current.Value = identityId ?? previous;
        return new Scope(_current, previous);
    }

    private sealed class Scope(AsyncLocal<Guid?> current, Guid? previous) : IDisposable
    {
        public void Dispose() => current.Value = previous;
    }
}
