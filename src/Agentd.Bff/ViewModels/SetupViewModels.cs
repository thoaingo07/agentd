namespace Agentd.Bff.ViewModels;

/// <summary>The setup session (the one-time link's cookie): when it ends unless used again (it slides).</summary>
public sealed record SetupSessionVm(DateTimeOffset? ExpiresAt);
