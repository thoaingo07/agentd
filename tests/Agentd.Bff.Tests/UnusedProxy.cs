using System.Reflection;

namespace Agentd.Bff.Tests;

/// <summary>A dependency a test's requests never reach: any call fails the test.</summary>
#pragma warning disable CA1852 // DispatchProxy needs a non-sealed type
internal class UnusedProxy : DispatchProxy
#pragma warning restore CA1852
{
    public static T Of<T>() where T : class => DispatchProxy.Create<T, UnusedProxy>();

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => throw new NotSupportedException($"{targetMethod?.Name} isn't used here.");
}
