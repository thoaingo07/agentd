using Agentd.Domain.Common;

namespace Agentd.Application.Abstractions;

/// <summary>A use case that changes state. Expected failures are returned, not thrown.</summary>
public interface ICommandHandler<in TCommand, TResult>
{
    Task<Result<TResult>> Handle(TCommand command, CancellationToken cancellationToken);
}

/// <summary>A read-only use case.</summary>
public interface IQueryHandler<in TQuery, TResult>
{
    Task<TResult> Handle(TQuery query, CancellationToken cancellationToken);
}

/// <summary>Result payload for commands that return nothing.</summary>
public readonly record struct Unit
{
    public static Unit Value { get; }
}
