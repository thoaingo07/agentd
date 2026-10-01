using System.Diagnostics.CodeAnalysis;

namespace Agentd.Domain.Common;

/// <summary>Success, or a <see cref="DomainError"/>.</summary>
public readonly record struct Result(DomainError? Error)
{
    public static Result Ok { get; } = new(null);

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static Result Fail(DomainError error) => new(error);

    public static Result<T> Success<T>(T value) => new(value, null);

    public static Result<T> Fail<T>(DomainError error) => new(default, error);

    public static implicit operator Result(DomainError error) => new(error);
}

/// <summary>A value, or a <see cref="DomainError"/>.</summary>
public readonly record struct Result<T>(T? Value, DomainError? Error)
{
    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess => Error is null;

    public static implicit operator Result<T>(T value) => new(value, null);

    public static implicit operator Result<T>(DomainError error) => new(default, error);
}
