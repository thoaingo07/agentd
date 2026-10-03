using Agentd.Domain.Common;
using Microsoft.AspNetCore.Http;

namespace Agentd.Bff.Http;

/// <summary>Maps <see cref="DomainError"/>s to ProblemDetails, so every endpoint fails the same way.</summary>
public static class ResultExtensions
{
    /// <summary><paramref name="onSuccess"/> for a success, otherwise the error as ProblemDetails.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();
    }

    /// <summary>not_found → 404, invalid_transition / conflict → 409, validation → 400; the code goes in the <c>code</c> extension.</summary>
    public static IResult ToProblem(this DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var (status, title) = error.Code switch
        {
            "not_found" => (StatusCodes.Status404NotFound, "Not found"),
            "invalid_transition" or "conflict" or "not_accepted" => (StatusCodes.Status409Conflict, "Conflict"),
            "validation" => (StatusCodes.Status400BadRequest, "Invalid request"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
        };
        return TypedResults.Problem(error.Message, statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }
}
