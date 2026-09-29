using Agentd.Domain.Common;

namespace Agentd.Domain.Tests.Jobs;

internal sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = start;

    public static FakeClock Default() => new(new DateTimeOffset(2026, 9, 29, 10, 0, 0, TimeSpan.Zero));
}
