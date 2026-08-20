using System.Text.Json;
using Certiflow.Intelligence.Domain;
using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Persistence;
using Contracts = Certiflow.Contracts;

namespace Certiflow.Intelligence.Infrastructure.Messaging;

/// <summary>
/// Turns a finished extraction into the integration events other contexts consume.
/// <para>
/// The translation boundary for BC3, mirroring Intake's: the domain speaks in
/// <see cref="ExtractedField"/> and <see cref="Confidence"/>, and only here does that become the
/// Published Language of <c>Certiflow.Contracts</c> (ADR-0004).
/// </para>
/// </summary>
public static class ExtractionEventTranslator
{
    /// <summary>Cached: a new JsonSerializerOptions per call defeats the serialiser's own cache.</summary>
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <see cref="Contracts.ExtractionCompleted"/> for a finished job.
    /// <para>
    /// <c>AutoAcceptable</c> travels as a <em>statement of fact</em> about BC3's own output — every
    /// mandatory field cleared the threshold — not as an instruction to approve. Only a verdict
    /// from BC4 makes evidence count, which is why this event goes to Verification rather than
    /// straight to Compliance (SRS §4.3).
    /// </para>
    /// </summary>
    public static Contracts.ExtractionCompleted ToCompleted(ExtractionJob job, Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new Contracts.ExtractionCompleted(
            job.Id.Value,
            job.DocumentId.Value,
            job.SupplierId.Value,
            job.RequirementId.Value,
            job.DocumentType,
            [.. job.Fields.Select(ToDescriptor)],
            job.OverallConfidence.Value,
            job.IsAutoAcceptable,
            job.ModelUsed ?? "unknown",
            job.PromptVersion ?? "unknown",
            job.TokensConsumed,
            correlationId);
    }

    /// <summary>
    /// Raised when the model produced values whose citations could not be located — it invented
    /// them. Separate from <c>ExtractionFailed</c> because the job technically succeeded; the
    /// output is simply untrustworthy and needs a human (FR-3.4).
    /// </summary>
    public static Contracts.GroundingFailed? ToGroundingFailed(ExtractionJob job, Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(job);

        var ungrounded = job.UngroundedFields.Select(field => field.FieldName).ToList();

        return ungrounded.Count == 0
            ? null
            : new Contracts.GroundingFailed(
                job.Id.Value,
                job.DocumentId.Value,
                job.SupplierId.Value,
                job.RequirementId.Value,
                ungrounded,
                correlationId);
    }

    public static Contracts.ExtractionFailed ToFailed(ExtractionJob job, Guid correlationId)
    {
        ArgumentNullException.ThrowIfNull(job);

        return new Contracts.ExtractionFailed(
            job.Id.Value,
            job.DocumentId.Value,
            job.SupplierId.Value,
            job.RequirementId.Value,
            job.FailureReason ?? "Extraction failed without a recorded reason.",
            job.AttemptCount,
            job.AttemptsExhausted,
            correlationId);
    }

    private static Contracts.ExtractedFieldDescriptor ToDescriptor(ExtractedField field) => new(
        field.FieldName,
        field.RawValue,
        field.TypedValue,
        field.Confidence.Value,
        field.GroundingResult.ToString(),
        field.IsMandatory,
        field.Citation?.PageNumber,
        field.Citation?.Snippet);

    /// <summary>Serialises an integration event into an outbox row.</summary>
    public static OutboxMessage ToOutboxMessage(Contracts.IIntegrationEvent integrationEvent, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);

        return new OutboxMessage(
            integrationEvent.EventId,
            integrationEvent.CorrelationId,
            integrationEvent.GetType().FullName
                ?? throw new InvalidOperationException("Integration events must be named types."),
            JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType(), PayloadJson),
            now);
    }
}
