namespace Agentd.Infrastructure.AzureDevOps;

/// <summary>An Azure DevOps call failed. <see cref="StatusCode"/> is the HTTP status (0 if none).</summary>
public class AdoException : Exception
{
    public AdoException()
    {
    }

    public AdoException(string message)
        : base(message)
    {
    }

    public AdoException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public AdoException(string message, int statusCode)
        : base(message) => StatusCode = statusCode;

    public int StatusCode { get; }
}
