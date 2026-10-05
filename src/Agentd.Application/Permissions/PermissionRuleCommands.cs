using Agentd.Application.Abstractions;
using Agentd.Domain.Common;

namespace Agentd.Application.Permissions;

/// <summary>The remembered approvals ("allow for this job", "always allow in this repo"), newest first.</summary>
public sealed record GetPermissionRules;

/// <summary>Revokes a remembered approval: the agent is asked again next time.</summary>
public sealed record RevokePermissionRule(long RuleId, string By);

public sealed class GetPermissionRulesHandler(IPermissionStore store) : IQueryHandler<GetPermissionRules, IReadOnlyList<PermissionRule>>
{
    public Task<IReadOnlyList<PermissionRule>> Handle(GetPermissionRules query, CancellationToken cancellationToken) =>
        store.ListRulesAsync(cancellationToken);
}

public sealed class RevokePermissionRuleHandler(IPermissionStore store) : ICommandHandler<RevokePermissionRule, Unit>
{
    public async Task<Result<Unit>> Handle(RevokePermissionRule command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await store.DeleteRuleAsync(command.RuleId, command.By, cancellationToken).ConfigureAwait(false)
            ? Unit.Value
            : DomainError.NotFound($"Permission rule {command.RuleId}");
    }
}
