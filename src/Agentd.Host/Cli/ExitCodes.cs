using Agentd.Domain.Common;

namespace Agentd.Host.Cli;

/// <summary>Process exit codes of the agentd CLI.</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
    public const int NotFound = 3;
    public const int Conflict = 4;
    public const int DoctorFailed = 5;

    public static int From(DomainError error) => error.Code switch
    {
        "not_found" => NotFound,
        "conflict" => Conflict,
        "validation" => Usage,
        _ => Error,
    };
}
