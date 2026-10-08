using Microsoft.Extensions.Options;

namespace Agentd.Application.Jobs;

/// <summary>
/// <c>Agentd:Models:Profiles:&lt;name&gt;</c>: other model providers for the agent, ahead of Phase 6. For now one kind,
/// <c>AnthropicCompatible</c>: Claude Code against the provider's Anthropic-compatible endpoint (DeepSeek, GLM, …).
/// </summary>
public sealed class ModelsOptions
{
    public const string Section = "Agentd:Models";

    public IDictionary<string, ModelProfile> Profiles { get; } = new Dictionary<string, ModelProfile>(StringComparer.OrdinalIgnoreCase);
}

public sealed class ModelProfile
{
    public const string AnthropicCompatible = "AnthropicCompatible";

    public string Kind { get; set; } = AnthropicCompatible;

    /// <summary>The provider's Anthropic-compatible endpoint, e.g. <c>https://api.deepseek.com/anthropic</c>.</summary>
    public string? BaseUrl { get; set; }

    /// <summary>The model ID at the provider (its docs name the current ones), e.g. <c>deepseek-flash[1m]</c>.</summary>
    public string? Model { get; set; }

    /// <summary>The cheaper model for background tasks and subagents; default: <see cref="Model"/>.</summary>
    public string? SmallModel { get; set; }

    /// <summary>A secret: <c>agentd secrets set Models:Profiles:&lt;name&gt;:ApiKey</c>. Only this profile's processes get it.</summary>
    public string? ApiKey { get; set; }

    /// <summary>The agent CLI's config and session folder; default <c>~/.agentd/claude/profiles/&lt;name&gt;</c>.</summary>
    public string? ConfigDir { get; set; }

    /// <summary>Extra variables the provider's docs ask for (e.g. <c>CLAUDE_CODE_AUTO_COMPACT_WINDOW</c>).</summary>
    public IDictionary<string, string> Environment { get; } = new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>Fails startup on a profile that can't work, or a step naming a profile that doesn't exist.</summary>
public sealed class ModelsOptionsValidator(IOptions<JobOptions> jobs) : IValidateOptions<ModelsOptions>
{
    public ValidateOptionsResult Validate(string? name, ModelsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var errors = new List<string>();
        foreach (var (profile, p) in options.Profiles)
        {
            if (!string.Equals(p.Kind, ModelProfile.AnthropicCompatible, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add($"Agentd:Models:Profiles:{profile}:Kind '{p.Kind}' isn't supported yet (only {ModelProfile.AnthropicCompatible}; the rest come with Phase 6).");
            }

            if (!Uri.TryCreate(p.BaseUrl, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps && !url.IsLoopback)
            {
                errors.Add($"Agentd:Models:Profiles:{profile}:BaseUrl must be an https URL (or http on this machine, for a gateway).");
            }

            if (string.IsNullOrWhiteSpace(p.ApiKey))
            {
                errors.Add($"Agentd:Models:Profiles:{profile}:ApiKey is missing: agentd secrets set Models:Profiles:{profile}:ApiKey");
            }
        }

        foreach (var (step, model) in jobs.Value.Steps)
        {
            if (model.Profile is { Length: > 0 } profile && !options.Profiles.ContainsKey(profile))
            {
                errors.Add($"Agentd:Jobs:Steps:{step}:Profile '{profile}' isn't in Agentd:Models:Profiles.");
            }
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
