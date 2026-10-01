using System.Text;
using Agentd.Domain.Common;

namespace Agentd.Domain.Jobs.ValueObjects;

/// <summary>Name of a repository registered in agentd's configuration.</summary>
public readonly record struct RepositoryName
{
    private RepositoryName(string value) => Value = value;

    public string Value { get; }

    public static Result<RepositoryName> Create(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? DomainError.Validation("Repository name is required.")
            : new RepositoryName(value.Trim());

    public static RepositoryName From(string value) =>
        Create(value) is { IsSuccess: true } r ? r.Value : throw new ArgumentException("Repository name is required.", nameof(value));

    public override string ToString() => Value;
}

/// <summary>Git branch for a job: <c>{prefix}{workItemId}-{slug}</c>, e.g. <c>ai/1234-fix-login-redirect</c>.</summary>
public readonly record struct BranchName
{
    public const int MaxSlugLength = 40;

    private BranchName(string value) => Value = value;

    public string Value { get; }

    public static BranchName For(WorkItemId workItem, string? title, string prefix = "ai/")
    {
        var slug = Slugify(title ?? string.Empty);
        return new BranchName(slug.Length == 0 ? $"{prefix}{workItem}" : $"{prefix}{workItem}-{slug}");
    }

    public static BranchName From(string value) =>
        string.IsNullOrWhiteSpace(value) ? throw new ArgumentException("Branch name is required.", nameof(value)) : new BranchName(value);

    public override string ToString() => Value;

    private static string Slugify(string title)
    {
        // Fold common accented Latin letters to ASCII (works under InvariantGlobalization, where
        // Unicode normalization isn't available), keep [a-z0-9], collapse everything else into single dashes.
        var sb = new StringBuilder(title.Length);
        var lastWasDash = true;
        foreach (var c in title)
        {
            var lower = Fold(char.ToLowerInvariant(c));
            if (lower is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(lower);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var slug = sb.ToString().Trim('-');
        if (slug.Length > MaxSlugLength)
        {
            slug = slug[..MaxSlugLength].TrimEnd('-');
        }

        return slug;
    }

    private const string Accented = "àáâãäåçèéêëìíîïñòóôõöùúûüýÿāăąćĉċčďēĕėęěĝğġģĥĩīĭįĵķĺļľńņňōŏőŕŗřśŝşšţťũūŭůűųŵŷźżžơưǎǐǒǔǖǘǚǜǟǡǧǩǫǭǰǵǹǻȁȃȅȇȉȋȍȏȑȓȕȗșțȟȧȩȫȭȯȱȳḁḃḅḇḉḋḍḏḑḓḕḗḙḛḝḟḡḣḥḧḩḫḭḯḱḳḵḷḹḻḽḿṁṃṅṇṉṋṍṏṑṓṕṗṙṛṝṟṡṣṥṧṩṫṭṯṱṳṵṷṹṻṽṿẁẃẅẇẉẋẍẏẑẓẕẖẗẘẙạảấầẩẫậắằẳẵặẹẻẽếềểễệỉịọỏốồổỗộớờởỡợụủứừửữựỳỵỷỹđħıłøŧßæœðþ";
    private const string Folded = "aaaaaaceeeeiiiinooooouuuuyyaaaccccdeeeeegggghiiiijklllnnnooorrrssssttuuuuuuwyzzzouaiouuuuuaagkoojgnaaaeeiioorruusthaeooooyabbbcdddddeeeeefghhhhhiikkkllllmmmnnnnoooopprrrrsssssttttuuuuuvvwwwwwxxyzzzhtwyaaaaaaaaaaaaeeeeeeeeiioooooooooooouuuuuuuyyyydhilotsaodt";

    private static char Fold(char c)
    {
        var i = Accented.IndexOf(c, StringComparison.Ordinal);
        return i >= 0 ? Folded[i] : c;
    }
}

/// <summary>Absolute path of a job's git worktree.</summary>
public readonly record struct WorktreePath(string Value)
{
    public override string ToString() => Value;
}

/// <summary>URL of the pull request created for a job.</summary>
public readonly record struct PullRequestUrl(Uri Value)
{
    public override string ToString() => Value.ToString();
}

/// <summary>What the agent submitted with <c>finish</c>: becomes the pull request.</summary>
public sealed record PullRequestDraft
{
    public const int MaxTitleLength = 200;

    private PullRequestDraft(string title, string description, string summary)
    {
        Title = title;
        Description = description;
        Summary = summary;
    }

    public string Title { get; }

    public string Description { get; }

    public string Summary { get; }

    public static Result<PullRequestDraft> Create(string? title, string? description, string? summary)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return DomainError.Validation("Pull request title is required.");
        }

        var trimmed = title.Trim();
        if (trimmed.Length > MaxTitleLength)
        {
            return DomainError.Validation($"Pull request title must be at most {MaxTitleLength} characters.");
        }

        return new PullRequestDraft(trimmed, description?.Trim() ?? string.Empty, summary?.Trim() ?? string.Empty);
    }
}
