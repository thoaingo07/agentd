namespace Agentd.Domain.Common;

/// <summary>Current time, injectable for tests. Implemented by the Host.</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
