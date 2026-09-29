namespace Agentd.Domain.Common;

/// <summary>Base for aggregates: identity plus the domain events raised since they were last dequeued.</summary>
public abstract class AggregateRoot<TId>
    where TId : struct
{
    private readonly List<IDomainEvent> _events = [];

    public TId Id { get; protected set; }

    /// <summary>Returns and clears the pending domain events (the repository persists them with the state).</summary>
    public IReadOnlyList<IDomainEvent> DequeueEvents()
    {
        var events = _events.ToList();
        _events.Clear();
        return events;
    }

    protected void Raise(IDomainEvent domainEvent) => _events.Add(domainEvent);
}
