using Agentd.Application.Jobs;

namespace Agentd.Application.Tests.Jobs;

[TestClass]
public sealed class ModelsOptionsTests
{
    [TestMethod]
    public void A_complete_profile_and_a_step_using_it_are_valid()
    {
        var (models, jobs) = Config();

        Assert.IsTrue(Validate(models, jobs).Succeeded);
    }

    [TestMethod]
    [DataRow("key", "the ApiKey")]
    [DataRow("url", "BaseUrl")]
    [DataRow("kind", "isn't supported yet")]
    [DataRow("step", "isn't in Agentd:Models:Profiles")]
    public void A_profile_that_cant_work_fails_startup(string defect, string message)
    {
        var (models, jobs) = Config();
        var deepseek = models.Profiles["deepseek"];
        switch (defect)
        {
            case "key": deepseek.ApiKey = null; break;
            case "url": deepseek.BaseUrl = "http://api.deepseek.com/anthropic"; break;
            case "kind": deepseek.Kind = "OpenAiApi"; break;
            case "step": jobs.Steps["fix"] = new StepModel { Profile = "glm" }; break;
        }

        var result = Validate(models, jobs);

        Assert.IsFalse(result.Succeeded);
        Assert.Contains(message.Replace("the ", "", StringComparison.Ordinal), result.FailureMessage ?? string.Empty);
    }

    private static (ModelsOptions, JobOptions) Config()
    {
        var models = new ModelsOptions();
        models.Profiles["DeepSeek"] = new ModelProfile { BaseUrl = "https://api.deepseek.com/anthropic", Model = "deepseek-flash[1m]", ApiKey = "sk-test" };
        var jobs = new JobOptions();
        jobs.Steps["implement"] = new StepModel { Profile = "deepseek" };
        return (models, jobs);
    }

    private static Microsoft.Extensions.Options.ValidateOptionsResult Validate(ModelsOptions models, JobOptions jobs) =>
        new ModelsOptionsValidator(Microsoft.Extensions.Options.Options.Create(jobs)).Validate(null, models);
}
