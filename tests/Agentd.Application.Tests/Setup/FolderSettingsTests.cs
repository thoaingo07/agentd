using Agentd.Application.Setup;

namespace Agentd.Application.Tests.Setup;

[TestClass]
public sealed class FolderSettingsTests
{
    private readonly Config _config = new();
    private readonly Audit _audit = new();
    private readonly Probe _probe = new();

    [TestMethod]
    public async Task Saving_writes_both_folders_to_agentd_json_audited_and_needs_a_restart()
    {
        _config.Values[FolderSettings.WorktreesKey] = "~/.agentd/worktrees";
        _config.Values[FolderSettings.RepositoriesKey] = "~/.agentd/repos";

        var saved = await Settings().SaveAsync(new(" /srv/agentd/worktrees ", "~/.agentd/repos"), "tngo", default);
        var again = await Settings().SaveAsync(new("/srv/agentd/worktrees", "~/.agentd/repos"), "tngo", default);

        Assert.IsTrue(saved.Value!.RestartRequired);
        Assert.IsFalse(again.Value!.RestartRequired, "nothing changed");
        Assert.AreEqual(("/srv/agentd/worktrees", "~/.agentd/repos"), (_config.Values[FolderSettings.WorktreesKey], _config.Values[FolderSettings.RepositoriesKey]));
        Assert.AreEqual("Git:WorktreeRoot set tngo", $"{_audit.Changes.Single().Key} {_audit.Changes.Single().Action} {_audit.Changes.Single().By}");
    }

    [TestMethod]
    public async Task An_empty_folder_goes_back_to_the_default_and_overrides_are_shown()
    {
        _config.Values[FolderSettings.WorktreesKey] = "/srv/agentd/worktrees";
        _config.Overrides[FolderSettings.RepositoriesKey] = "the environment variable Agentd__Git__RepositoriesRoot";

        await Settings().SaveAsync(new("", null), "tngo", default);

        Assert.IsNull(_config.Values[FolderSettings.WorktreesKey]);
        Assert.AreEqual("Git:WorktreeRoot removed", $"{_audit.Changes.Single().Key} {_audit.Changes.Single().Action}");
        Assert.AreEqual("the environment variable Agentd__Git__RepositoriesRoot", Settings().Get().RepositoriesOverriddenBy);
    }

    [TestMethod]
    [DataRow("worktrees")]
    [DataRow("./worktrees")]
    [DataRow("~/agentd/../etc")]
    [DataRow("~worktrees")]
    public async Task Relative_or_climbing_paths_are_refused_and_nothing_is_written(string path)
    {
        var saved = await Settings().SaveAsync(new(path, null), "tngo", default);
        var tested = Settings().Test(new(path, null));

        Assert.AreEqual("validation", saved.Error!.Code);
        Assert.IsFalse(tested.Ok);
        Assert.IsEmpty(_config.Values);
        Assert.IsEmpty(_probe.Tested);
    }

    [TestMethod]
    public void Test_checks_each_folder_given_or_set_and_names_the_one_that_fails()
    {
        _config.Values[FolderSettings.RepositoriesKey] = "/srv/repos";
        _probe.Failing.Add("/srv/repos");

        var both = Settings().Test(new("~/wt", null));
        var fine = Settings().Test(new("~/wt", "~/repos"));

        Assert.AreEqual((false, "Repositories: no"), (both.Ok, both.Message));
        CollectionAssert.AreEqual(new[] { "~/wt", "/srv/repos", "~/wt", "~/repos" }, _probe.Tested);
        Assert.IsTrue(fine.Ok);
    }

    private FolderSettings Settings() => new(_config, _audit, _probe, TimeProvider.System);

    private sealed class Config : IConfigWriter
    {
        public Dictionary<string, string?> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Overrides { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Read(string key) => Values.GetValueOrDefault(key);

        public IReadOnlyList<string> Children(string section) => [];

        public string? OverriddenBy(string key) => Overrides.GetValueOrDefault(key);

        public Task SetAsync(IReadOnlyDictionary<string, string?> values, CancellationToken cancellationToken)
        {
            foreach (var (k, v) in values)
            {
                Values[k] = v;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class Audit : ISettingsAudit
    {
        public List<SettingsChange> Changes { get; } = [];

        public Task RecordAsync(IReadOnlyList<SettingsChange> changes, CancellationToken cancellationToken)
        {
            Changes.AddRange(changes);
            return Task.CompletedTask;
        }
    }

    private sealed class Probe : IFolderProbe
    {
        public List<string> Tested { get; } = [];

        public HashSet<string> Failing { get; } = [];

        public StepCheck Test(string path)
        {
            Tested.Add(path);
            return Failing.Contains(path) ? new StepCheck(false, "no") : new StepCheck(true, "ok");
        }
    }
}
