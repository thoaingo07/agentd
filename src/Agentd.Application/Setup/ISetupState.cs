namespace Agentd.Application.Setup;

/// <summary>Whether first-run setup is complete (<c>Agentd:Setup:CompletedAt</c> in <c>agentd.json</c>).</summary>
public interface ISetupState
{
    bool IsComplete { get; }
}
