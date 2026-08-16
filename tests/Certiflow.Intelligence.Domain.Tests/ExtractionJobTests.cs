using Certiflow.Intelligence.Domain.Events;
using Certiflow.Intelligence.Domain.Grounding;
using Certiflow.Intelligence.Domain.Schemas;
using Certiflow.Intelligence.Domain.Scoring;
using Certiflow.SharedKernel;
using FluentAssertions;
using Xunit;

namespace Certiflow.Intelligence.Domain.Tests;

public sealed class ExtractionJobTests
{
    private const string Model = "gpt-4o-mini";

    private const string PromptVersion = "extract-iso-v3";

    private static readonly Confidence Threshold = Confidence.FromScore(0.85m);

    private static ExtractionJob NewJob() => ExtractionJob.Create(
        new DocumentId(Guid.CreateVersion7()),
        new SupplierId(Guid.CreateVersion7()),
        new RequirementId(Guid.CreateVersion7()),
        "ISO 9001",
        Threshold);

    /// <summary>Drives a job to the point where it can be completed.</summary>
    private static ExtractionJob RunToGrounding()
    {
        var job = NewJob();
        job.BeginAttempt(Model, PromptVersion);
        job.MarkExtracting(TextSource.EmbeddedTextLayer);
        job.MarkGrounding();
        return job;
    }

    [Fact]
    public void A_new_job_is_pending_with_no_attempts_spent()
    {
        var job = NewJob();

        job.Status.Should().Be(ExtractionStatus.Pending);
        job.AttemptCount.Should().Be(0);
        job.TokensConsumed.Should().Be(0);
        job.IsAutoAcceptable.Should().BeFalse();
    }

    [Fact]
    public void Beginning_an_attempt_records_the_model_and_prompt_version()
    {
        // FR-3.8 — a change in extraction quality has to be traceable to the change that caused it.
        var job = NewJob();

        job.BeginAttempt(Model, PromptVersion);

        job.Status.Should().Be(ExtractionStatus.Parsing);
        job.AttemptCount.Should().Be(1);
        job.ModelUsed.Should().Be(Model);
        job.PromptVersion.Should().Be(PromptVersion);
        job.DomainEvents.OfType<ExtractionStarted>().Should().ContainSingle()
            .Which.AttemptNumber.Should().Be(1);
    }

    [Fact]
    public void An_attempt_cannot_start_while_one_is_already_running()
    {
        var job = NewJob();
        job.BeginAttempt(Model, PromptVersion);

        var act = () => job.BeginAttempt(Model, PromptVersion);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intelligence.job.attempt_already_running");
    }

    [Fact]
    public void The_third_failure_abandons_the_job_rather_than_retrying_forever()
    {
        // Guardrail G6 and SRS §19 Q11 — "what happens when Azure OpenAI is down?". Three backed-off
        // attempts, then a terminal state that raises an event. Never a silent drop.
        var job = NewJob();

        for (var attempt = 1; attempt <= ExtractionJob.MaxAttempts; attempt++)
        {
            job.BeginAttempt(Model, PromptVersion);
            job.FailAttempt("Azure OpenAI returned 503.", tokensConsumed: 120);
        }

        job.Status.Should().Be(ExtractionStatus.Abandoned);
        job.AttemptCount.Should().Be(3);
        job.DomainEvents.OfType<ExtractionFailed>().Last().Abandoned.Should().BeTrue();
    }

    [Fact]
    public void An_abandoned_job_refuses_further_attempts()
    {
        var job = NewJob();

        for (var attempt = 1; attempt <= ExtractionJob.MaxAttempts; attempt++)
        {
            job.BeginAttempt(Model, PromptVersion);
            job.FailAttempt("timeout");
        }

        var act = () => job.BeginAttempt(Model, PromptVersion);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intelligence.job.immutable");
    }

    [Fact]
    public void Tokens_from_failed_attempts_are_still_counted()
    {
        // A failed call to a metered API is not free. Counting only successes is how a cost
        // dashboard starts lying, and guardrail G3's daily ceiling is enforced against this number.
        var job = NewJob();
        job.BeginAttempt(Model, PromptVersion);
        job.FailAttempt("truncated response", tokensConsumed: 1_400);
        job.BeginAttempt(Model, PromptVersion);
        job.MarkExtracting(TextSource.EmbeddedTextLayer);
        job.MarkGrounding();
        job.Complete(CertificateFixture.Schema(), MandatoryFieldsAt(1.00m), tokensConsumed: 2_100);

        job.TokensConsumed.Should().Be(3_500);
    }

    [Fact]
    public void A_job_cannot_complete_while_a_mandatory_field_was_never_attempted()
    {
        // "The model didn't mention it" is a legitimate outcome a reviewer must see. A mandatory
        // field quietly absent from the result set is not — nothing downstream would notice.
        var job = RunToGrounding();
        var missingExpiry = MandatoryFieldsAt(1.00m)
            .Where(f => f.FieldName != CertificateFieldNames.ExpiresOn)
            .ToList();

        var act = () => job.Complete(CertificateFixture.Schema(), missingExpiry, tokensConsumed: 500);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Should().Match<DomainRuleViolationException>(e =>
                e.Rule == "intelligence.job.mandatory_field_not_attempted" &&
                e.Message.Contains(CertificateFieldNames.ExpiresOn, StringComparison.Ordinal));
    }

    [Fact]
    public void Overall_confidence_is_the_worst_mandatory_field_not_the_average()
    {
        // The most dangerous thing this scoring system could do is average. One hallucinated expiry
        // date among six clean fields would score 0.83 on an average and auto-accept at 0.85.
        var job = RunToGrounding();
        var fields = MandatoryFieldsAt(1.00m)
            .Where(f => f.FieldName != CertificateFieldNames.ExpiresOn)
            .Append(Ungrounded(CertificateFieldNames.ExpiresOn))
            .ToList();

        job.Complete(CertificateFixture.Schema(), fields, tokensConsumed: 900);

        job.OverallConfidence.Should().Be(Confidence.Zero);
        job.IsAutoAcceptable.Should().BeFalse();
    }

    [Fact]
    public void An_optional_field_scoring_badly_does_not_hold_up_a_clean_document()
    {
        var job = RunToGrounding();
        var fields = MandatoryFieldsAt(1.00m).Append(Ungrounded(CertificateFieldNames.Scope)).ToList();

        job.Complete(CertificateFixture.Schema(), fields, tokensConsumed: 900);

        job.OverallConfidence.Value.Should().Be(1.00m);
        job.IsAutoAcceptable.Should().BeTrue();
    }

    [Fact]
    public void Completion_raises_grounding_failed_alongside_extraction_completed()
    {
        // Both, not one or the other: the job did finish, and a reviewer needs to see the result in
        // order to reject it (FR-3.4).
        var job = RunToGrounding();
        var fields = MandatoryFieldsAt(1.00m)
            .Where(f => f.FieldName != CertificateFieldNames.IssuerName)
            .Append(Ungrounded(CertificateFieldNames.IssuerName))
            .ToList();

        job.Complete(CertificateFixture.Schema(), fields, tokensConsumed: 900);

        job.DomainEvents.OfType<GroundingFailed>().Should().ContainSingle()
            .Which.UngroundedFieldNames.Should().Equal([CertificateFieldNames.IssuerName]);
        job.DomainEvents.OfType<ExtractionCompleted>().Should().ContainSingle()
            .Which.AutoAcceptable.Should().BeFalse();
    }

    [Fact]
    public void A_clean_extraction_is_auto_acceptable()
    {
        var job = RunToGrounding();

        job.Complete(CertificateFixture.Schema(), MandatoryFieldsAt(1.00m), tokensConsumed: 850);

        job.Status.Should().Be(ExtractionStatus.Completed);
        job.SchemaVersion.Should().Be("2026-08-01");
        job.IsAutoAcceptable.Should().BeTrue();
        job.DomainEvents.OfType<GroundingFailed>().Should().BeEmpty();
    }

    [Fact]
    public void A_field_exactly_on_the_threshold_is_auto_acceptable()
    {
        var job = RunToGrounding();

        job.Complete(CertificateFixture.Schema(), MandatoryFieldsAt(0.85m), tokensConsumed: 850);

        job.IsAutoAcceptable.Should().BeTrue("the threshold is inclusive");
    }

    [Fact]
    public void A_field_one_hundredth_below_the_threshold_is_not()
    {
        var job = RunToGrounding();

        job.Complete(CertificateFixture.Schema(), MandatoryFieldsAt(0.84m), tokensConsumed: 850);

        job.IsAutoAcceptable.Should().BeFalse();
    }

    [Fact]
    public void A_completed_job_is_immutable_and_re_running_means_a_new_job()
    {
        // FR-3.11. The old result may already be cited in an approved verdict and an audit entry.
        var job = RunToGrounding();
        job.Complete(CertificateFixture.Schema(), MandatoryFieldsAt(1.00m), tokensConsumed: 850);

        var act = () => job.BeginAttempt(Model, PromptVersion);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intelligence.job.immutable");
    }

    [Fact]
    public void Stages_cannot_be_skipped()
    {
        var job = NewJob();
        job.BeginAttempt(Model, PromptVersion);

        var act = () => job.MarkGrounding();

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intelligence.job.unexpected_stage");
    }

    [Fact]
    public void Completing_with_a_schema_for_a_different_document_type_is_refused()
    {
        var job = RunToGrounding();
        var wrongSchema = new DocumentTypeSchema(
            "Public Liability Insurance",
            "2026-08-01",
            [new FieldDefinition("insurer", FieldValueType.Text, isMandatory: true)]);

        var act = () => job.Complete(
            wrongSchema,
            [Grounded("insurer", 1.00m, isMandatory: true)],
            tokensConsumed: 100);

        act.Should().Throw<DomainRuleViolationException>()
            .Which.Rule.Should().Be("intelligence.job.schema_document_type_mismatch");
    }

    /// <summary>Every mandatory field of the ISO 9001 schema, all scoring the same.</summary>
    private static List<ExtractedField> MandatoryFieldsAt(decimal confidence) =>
        [.. CertificateFixture.Schema().MandatoryFields.Select(f => Grounded(f.Name, confidence, isMandatory: true))];

    private static ExtractedField Grounded(string fieldName, decimal confidence, bool isMandatory) =>
        new(
            fieldName,
            rawValue: "value",
            typedValue: "value",
            isMandatory,
            Confidence.FromScore(confidence),
            GroundingResult.Verified,
            new Citation(1, "a snippet long enough to be distinctive", 0, 38),
            signals: [SignalOutcome.Pass(ConfidenceSignal.Grounding)]);

    private static ExtractedField Ungrounded(string fieldName) =>
        new(
            fieldName,
            rawValue: "value",
            typedValue: "value",
            isMandatory: fieldName != CertificateFieldNames.Scope,
            Confidence.Zero,
            GroundingResult.NotFoundInSource,
            new Citation(1, "a snippet the model invented"),
            signals: [SignalOutcome.Fail(ConfidenceSignal.Grounding, "not in source")]);
}
