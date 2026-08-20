using System.Text.Json;
using Certiflow.Intelligence.Domain;
using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Scoring;

namespace Certiflow.Intelligence.Infrastructure.Persistence;

/// <summary>
/// How an <see cref="ExtractionJob"/> is stored — a persistence model, distinct from the aggregate.
/// <para>
/// <b>Why not map the aggregate directly.</b> EF cannot: <see cref="ExtractedField"/>'s constructor
/// takes a <see cref="Citation"/> and a list of <see cref="SignalOutcome"/>, and EF refuses to bind
/// navigations to constructor parameters. Every workaround runs the wrong way — a parameterless
/// constructor and settable properties on a value object whose entire purpose is to be immutable
/// and self-validating, or a public constructor on <see cref="Confidence"/> that would let any
/// caller invent a score the scorer never produced.
/// </para>
/// <para>
/// So the domain keeps its guarantees and the database gets a shape it can store. The cost is this
/// class and its mapping; the benefit is that <see cref="ExtractionJob"/> owes nothing to an ORM
/// and its tests still need no database. That trade is the whole argument for a separate
/// persistence model, and it is worth making out loud rather than quietly relaxing a value object.
/// </para>
/// </summary>
public sealed class ExtractionJobRecord
{
    private ExtractionJobRecord()
    {
        DocumentType = null!;
        Status = null!;
        FieldsJson = null!;
    }

    public Guid ExtractionJobId { get; private set; }

    public Guid DocumentId { get; private set; }

    public Guid SupplierId { get; private set; }

    public Guid RequirementId { get; private set; }

    public string DocumentType { get; private set; }

    public string Status { get; private set; }

    public int AttemptCount { get; private set; }

    public string? ModelUsed { get; private set; }

    public string? PromptVersion { get; private set; }

    public int TokensConsumed { get; private set; }

    public decimal OverallConfidence { get; private set; }

    public decimal AutoAcceptThreshold { get; private set; }

    /// <summary>
    /// Stored rather than recomputed on read. It is the answer to "does this need a human?", the
    /// review queue filters on it, and it must reflect the threshold that applied when the job ran
    /// — not whatever the threshold happens to be today.
    /// </summary>
    public bool IsAutoAcceptable { get; private set; }

    public string? TextSource { get; private set; }

    public string? FailureReason { get; private set; }

    public string FieldsJson { get; private set; }

    public DateTimeOffset RecordedAt { get; private set; }

    public static ExtractionJobRecord FromDomain(ExtractionJob job, DateTimeOffset recordedAt)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new ExtractionJobRecord
        {
            ExtractionJobId = job.Id.Value,
            DocumentId = job.DocumentId.Value,
            SupplierId = job.SupplierId.Value,
            RequirementId = job.RequirementId.Value,
            DocumentType = job.DocumentType,
            Status = job.Status.ToString(),
            AttemptCount = job.AttemptCount,
            ModelUsed = job.ModelUsed,
            PromptVersion = job.PromptVersion,
            TokensConsumed = job.TokensConsumed,
            OverallConfidence = job.OverallConfidence.Value,
            AutoAcceptThreshold = job.AutoAcceptThreshold.Value,
            IsAutoAcceptable = job.IsAutoAcceptable,
            TextSource = job.TextSourceUsed?.ToString(),
            FailureReason = job.FailureReason,
            FieldsJson = JsonSerializer.Serialize(job.Fields, DomainJson.Options),
            RecordedAt = recordedAt,
        };
    }

    /// <summary>
    /// The stored fields, rehydrated through the value objects' own factories so a corrupted row
    /// fails loudly rather than producing a confidence of 4.7.
    /// </summary>
    public IReadOnlyList<ExtractedField> Fields() =>
        JsonSerializer.Deserialize<List<ExtractedField>>(FieldsJson, DomainJson.Options) ?? [];
}
