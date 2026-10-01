using Agentd.Domain.Common;
using Agentd.Domain.Jobs.ValueObjects;

namespace Agentd.Domain.Messaging;

public sealed record ConversationOpened(JobId JobId, ProviderKey Provider, string ExternalConversationId, Uri? Link, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>The agent asked the developer a question; the job waits for a reply.</summary>
public sealed record DeveloperQuestionAsked(string Question, IReadOnlyList<string> Options, DateTimeOffset OccurredAt) : IDomainEvent;

/// <summary>A developer replied; <see cref="Resumed"/> is false when the reply was queued for the next turn.</summary>
public sealed record DeveloperReplied(string Reply, string From, bool Resumed, DateTimeOffset OccurredAt) : IDomainEvent;
