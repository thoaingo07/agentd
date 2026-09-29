namespace Agentd.Domain.Common;

/// <summary>Something that happened in the domain. Persisted to the event log alongside state changes.</summary>
public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}
