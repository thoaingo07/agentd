using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Agentd.Application.Reviews;

/// <summary>
/// A guided review (docs/architect/review-sessions.md §2a): the change explained in reading order. Chapters go by importance
/// (the heart of the change, then its consequences, then the glue), each with why it matters and a line per file; notes
/// explain lines (not problems); suggestions are optional improvements (not findings: nothing breaks without them).
/// </summary>
/// <param name="Overview">What the change does and how it fits together.</param>
/// <param name="Chapters">In reading order, by importance.</param>
/// <param name="Notes">Explanations at lines.</param>
/// <param name="Suggestions">Optional improvements.</param>
/// <param name="Before">How it worked before the change, in a sentence or two.</param>
/// <param name="After">How it works after.</param>
/// <param name="Callouts">Heads-ups for the human reviewer (a migration, a new dependency…): facts, not problems.</param>
public sealed record ReviewGuide(
    [property: JsonPropertyName("overview")] string Overview,
    [property: JsonPropertyName("chapters")] IReadOnlyList<GuideChapter> Chapters,
    [property: JsonPropertyName("notes")] IReadOnlyList<GuideNote>? Notes = null,
    [property: JsonPropertyName("suggestions")] IReadOnlyList<GuideSuggestion>? Suggestions = null,
    [property: JsonPropertyName("before")] string? Before = null,
    [property: JsonPropertyName("after")] string? After = null,
    [property: JsonPropertyName("callouts")] IReadOnlyList<GuideCallout>? Callouts = null);

/// <param name="Title">What this part of the change is, in a few words.</param>
/// <param name="Why">What it does and why it matters, in one to three sentences.</param>
/// <param name="Files">The changed files it covers, in reading order.</param>
/// <param name="Attention"><see cref="GuideAttention.Core"/> (read closely) or <see cref="GuideAttention.Skim"/>.</param>
/// <param name="Focus">What to check while reading it, a few short points.</param>
public sealed record GuideChapter(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("why")] string? Why,
    [property: JsonPropertyName("files")] IReadOnlyList<GuideFile> Files,
    [property: JsonPropertyName("attention")] string? Attention = null,
    [property: JsonPropertyName("focus")] IReadOnlyList<string>? Focus = null);

/// <summary>How closely to read a chapter.</summary>
public static class GuideAttention
{
    public const string Core = "core";
    public const string Skim = "skim";
}

/// <summary>
/// Something the human reviewer should know about the change even when nothing is wrong (from pi-review's "human reviewer
/// callouts"): <paramref name="Kind"/> is one of <see cref="GuideCalloutKinds"/>, <paramref name="Detail"/> says what and where.
/// </summary>
public sealed record GuideCallout(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>The heads-ups a guide can carry.</summary>
public static class GuideCalloutKinds
{
    public const string Migration = "migration";
    public const string Dependency = "dependency";
    public const string Auth = "auth";
    public const string Contract = "contract";
    public const string Destructive = "destructive";
    public const string FeatureFlag = "feature-flag";
    public const string Config = "config";

    public static readonly IReadOnlyList<string> All = [Migration, Dependency, Auth, Contract, Destructive, FeatureFlag, Config];
}

/// <param name="Path">Relative to the repository root.</param>
/// <param name="Summary">What changed in it, in one sentence (none for files agentd added to "Everything else").</param>
public sealed record GuideFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("summary")] string? Summary);

/// <summary>What a line does and why, for someone new to the code; <paramref name="Critical"/>: a spot that needs care.</summary>
public sealed record GuideNote(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("critical")] bool Critical = false);

/// <summary>An improvement worth considering: clearer, simpler, safer later. Shown apart from findings.</summary>
public sealed record GuideSuggestion(
    [property: JsonPropertyName("file")] string? File,
    [property: JsonPropertyName("line")] int? Line,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("detail")] string? Detail,
    [property: JsonPropertyName("suggestion")] string? Suggestion);

/// <summary>
/// The reviewer ends its message with a fenced <c>review-guide</c> block next to the findings. This finds and checks it,
/// and fits it to the change's real file list.
/// </summary>
public static partial class ReviewGuides
{
    public const int MaxChapters = 10;
    public const int MaxNotes = 40;
    public const int MaxSuggestions = 10;
    public const int MaxCallouts = 8;

    /// <summary>The chapter agentd adds for changed files the reviewer left out.</summary>
    public const string Rest = "Everything else";

    private static readonly JsonSerializerOptions s_json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The guide in <paramref name="reply"/>, or why it can't be used (null and null: there's none).</summary>
    public static (ReviewGuide? Guide, string? Problem) Extract(string reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        if (Block().Match(reply) is not { Success: true } match)
        {
            return (null, null);
        }

        try
        {
            var guide = JsonSerializer.Deserialize<ReviewGuide>(match.Groups["json"].Value, s_json);
            var problem = guide is null ? "it's empty" : Problem(guide);
            return problem is null ? (guide, null) : (null, problem);
        }
        catch (JsonException ex)
        {
            return (null, "the review-guide block isn't valid JSON: " + ex.Message);
        }
    }

    /// <summary>Why <paramref name="guide"/> can't be used, or null.</summary>
    public static string? Problem(ReviewGuide guide)
    {
        ArgumentNullException.ThrowIfNull(guide);
        var chapters = guide.Chapters ?? [];
        return string.IsNullOrWhiteSpace(guide.Overview) ? "it needs an overview"
            : chapters.Count is 0 or > MaxChapters ? $"it needs 1 to {MaxChapters} chapters"
            : chapters.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Title)) is not null ? "every chapter needs a title"
            : (guide.Notes?.Count ?? 0) > MaxNotes ? $"more than {MaxNotes} notes (keep the ones that help most)"
            : (guide.Suggestions?.Count ?? 0) > MaxSuggestions ? $"more than {MaxSuggestions} suggestions (keep the best)"
            : guide.Notes?.FirstOrDefault(n => string.IsNullOrWhiteSpace(n.Text) || n.Line < 1) is not null ? "every note needs a line (1 or more) and its text"
            : guide.Suggestions?.FirstOrDefault(s => string.IsNullOrWhiteSpace(s.Title)) is not null ? "every suggestion needs a title"
            : null;
    }

    /// <summary>
    /// Fits the guide to the change: each changed file in exactly one chapter (the first that names it), files that weren't
    /// changed dropped, chapters left empty dropped, and the files nobody named in <see cref="Rest"/>. Notes stay only on
    /// changed files (the page shows them in the diff); suggestions keep their place when it's a changed file.
    /// </summary>
    public static ReviewGuide Fit(ReviewGuide guide, IReadOnlyList<string> changedFiles)
    {
        ArgumentNullException.ThrowIfNull(guide);
        ArgumentNullException.ThrowIfNull(changedFiles);
        var changed = changedFiles.ToHashSet(StringComparer.Ordinal);
        var placed = new HashSet<string>(StringComparer.Ordinal);
        var chapters = new List<GuideChapter>();
        foreach (var chapter in guide.Chapters)
        {
            var files = (chapter.Files ?? [])
                .Select(f => f with { Path = Clean(f.Path), Summary = Blank(f.Summary) })
                .Where(f => changed.Contains(f.Path) && placed.Add(f.Path))
                .ToList();
            if (files.Count > 0)
            {
                chapters.Add(new GuideChapter(chapter.Title.Trim(), Blank(chapter.Why), files,
                    string.Equals(chapter.Attention?.Trim(), GuideAttention.Skim, StringComparison.OrdinalIgnoreCase) ? GuideAttention.Skim : GuideAttention.Core,
                    [.. (chapter.Focus ?? []).Select(Blank).OfType<string>().Take(5)]));
            }
        }

        var rest = changedFiles.Where(f => !placed.Contains(f)).Select(f => new GuideFile(f, null)).ToList();
        if (rest.Count > 0)
        {
            chapters.Add(new GuideChapter(Rest, null, rest, GuideAttention.Skim, []));
        }

        return new ReviewGuide(
            guide.Overview.Trim(),
            chapters,
            [.. (guide.Notes ?? []).Select(n => n with { File = Clean(n.File), Text = n.Text.Trim() }).Where(n => changed.Contains(n.File))],
            [.. (guide.Suggestions ?? []).Select(s => s with
            {
                File = s.File is { } f && changed.Contains(Clean(f)) ? Clean(f) : null,
                Line = s.File is { } g && changed.Contains(Clean(g)) ? s.Line : null,
                Title = s.Title.Trim(),
            })],
            Blank(guide.Before),
            Blank(guide.After),
            Callouts(guide.Callouts));
    }

    /// <summary>Known kinds only (lower case), with a detail, one per kind and detail, at most <see cref="MaxCallouts"/>.</summary>
    private static List<GuideCallout> Callouts(IReadOnlyList<GuideCallout>? callouts) =>
        [.. (callouts ?? [])
            .Select(c => new GuideCallout((c.Kind ?? string.Empty).Trim().ToLowerInvariant(), (c.Detail ?? string.Empty).Trim()))
            .Where(c => GuideCalloutKinds.All.Contains(c.Kind) && c.Detail.Length > 0)
            .Distinct()
            .Take(MaxCallouts)];

    private static string Clean(string? path) => (path ?? string.Empty).Trim().TrimStart('/');

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex(@"```review-guide\s*\n(?<json>[\s\S]*?)\n```", RegexOptions.IgnoreCase)]
    private static partial Regex Block();
}
