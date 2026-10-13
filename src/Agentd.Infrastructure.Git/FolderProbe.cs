using Agentd.Application.Setup;

namespace Agentd.Infrastructure.Git;

/// <summary>Settings → Folders' Test: creates the folder if needed, then writes and removes a small file there.</summary>
public sealed class FolderProbe : IFolderProbe
{
    public StepCheck Test(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var full = GitOptions.Expand(path);
        try
        {
            Directory.CreateDirectory(full);
            var probe = Path.Combine(full, $".agentd-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            var free = new DriveInfo(full).AvailableFreeSpace / (1024 * 1024 * 1024);
            return new StepCheck(true, $"{full} is writable ({free} GB free).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new StepCheck(false, $"{full} can't be used: {ex.Message}", "Pick a folder agentd's user can create and write to.");
        }
    }
}
