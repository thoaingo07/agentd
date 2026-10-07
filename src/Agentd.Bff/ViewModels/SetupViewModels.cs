namespace Agentd.Bff.ViewModels;

/// <summary>The setup session (the one-time link's cookie): when it ends unless used again (it slides).</summary>
public sealed record SetupSessionVm(DateTimeOffset? ExpiresAt);

/// <summary>A secret's status. Secrets are write-only: the value never leaves the server.</summary>
public sealed record SecretStatusVm(bool Set, DateTimeOffset? UpdatedAt, string? UpdatedBy)
{
    public static SecretStatusVm From(Application.Setup.SecretStatus s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Set, s.UpdatedAt, s.UpdatedBy);
    }
}

public sealed record DatabaseStepVm(SecretStatusVm ConnectionString);

/// <summary>A step's "Test" result; <see cref="Fix"/> says what to do when it failed.</summary>
public sealed record StepCheckVm(bool Ok, string Message, string? Fix);

/// <summary>Saved. The daemon reads these settings at start, so they apply after a restart.</summary>
public sealed record SaveResultVm(bool RestartRequired);
