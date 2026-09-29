using Agentd.Domain.Common;

namespace Agentd.Host;

/// <summary>The real clock.</summary>
internal sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
