using Certiflow.SharedKernel;
using Certiflow.Verification.Application.Abstractions;
using Certiflow.Verification.Domain;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Certiflow.Verification.Application.Review;

/// <summary>
/// Raises a review task from a completed extraction.
/// <para>
/// Called for <em>every</em> extraction, not only uncertain ones — deciding whether a task is
/// needed is this handler's job, and the reason it was raised is recorded on the task so a
/// reviewer can see why they are being asked (FR-4.1).
/// </para>
/// </summary>
public sealed record RaiseReviewTaskCommand(
    Guid DocumentId,
    Guid ExtractionJobId,
    Guid SupplierId,
    Guid RequirementId,
    string DocumentType,
    string UploadedBy,
    decimal OverallConfidence,
    bool AutoAcceptable,
    bool HadGroundingFailure,
    IReadOnlyList<FieldSuggestionInput> Fields) : IRequest<RaiseReviewTaskResult>;

public sealed record FieldSuggestionInput(
    string FieldName,
    string? SuggestedValue,
    decimal Confidence,
    bool IsMandatory,
    int? CitationPage,
    string? CitationSnippet,
    string? ScoringNote);

public sealed record RaiseReviewTaskResult(Guid? ReviewTaskId, string Outcome);

public sealed class RaiseReviewTaskHandler(
    IReviewTaskRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<RaiseReviewTaskHandler> logger) : IRequestHandler<RaiseReviewTaskCommand, RaiseReviewTaskResult>
{
    public async Task<RaiseReviewTaskResult> Handle(
        RaiseReviewTaskCommand command,
        CancellationToken cancellationToken)
    {
        var documentId = new DocumentId(command.DocumentId);

        // A redelivered ExtractionCompleted must not produce a second task for the same document.
        // The consumer's inbox is the primary guard; this is the cheap one that survives a missed
        // dedupe without leaving a reviewer two identical items in their queue.
        if (await repository.FindOpenForDocumentAsync(documentId, cancellationToken) is { } existing)
        {
            return new RaiseReviewTaskResult(existing.Id.Value, "AlreadyRaised");
        }

        // Auto-acceptable extractions still get a task in this build.
        //
        // The threshold decides whether a human is *required*, not whether the work is visible. An
        // auto-acceptable document that silently became evidence with no record of anyone looking
        // at it is precisely what the audit trail exists to prevent, and what a compliance auditor
        // asks about first. The task is raised as ManualEscalation and can be approved in one
        // click; the alternative - a document becoming evidence with no review row at all - is a
        // hole in the story this product is selling (SRS §9, §11.3).
        var reason = DetermineReason(command);

        var task = ReviewTask.RaiseFor(
            documentId,
            new ExtractionJobId(command.ExtractionJobId),
            new SupplierId(command.SupplierId),
            new RequirementId(command.RequirementId),
            command.DocumentType,
            command.UploadedBy,
            reason,
            command.OverallConfidence,
            [.. command.Fields.Select(ToSuggestion)],
            clock.Today);

        await repository.AddAsync(task, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        ReviewLog.TaskRaised(logger, task.Id.Value, command.DocumentId, reason.ToString(), command.OverallConfidence);

        return new RaiseReviewTaskResult(task.Id.Value, reason.ToString());
    }

    /// <summary>
    /// Why a human is being asked. Ordered by severity: a fabricated citation is a different
    /// problem from a merely uncertain one, and the reviewer should be told which.
    /// </summary>
    private static RaisedReason DetermineReason(RaiseReviewTaskCommand command) =>
        command.HadGroundingFailure ? RaisedReason.GroundingFailure
        : !command.AutoAcceptable ? RaisedReason.LowConfidence
        : RaisedReason.ManualEscalation;

    private static FieldSuggestion ToSuggestion(FieldSuggestionInput field) => new(
        field.FieldName,
        field.SuggestedValue,
        field.Confidence,
        field.IsMandatory,
        field.CitationPage,
        field.CitationSnippet,
        field.ScoringNote);
}

/// <summary>Records a reviewer's decision on one field — accepting or correcting it (FR-4.4).</summary>
public sealed record ResolveFieldCommand(
    Guid ReviewTaskId,
    string FieldName,
    string? AcceptedValue,
    string ReviewerId,
    string? ReviewerNote = null) : IRequest;

public sealed class ResolveFieldValidator : AbstractValidator<ResolveFieldCommand>
{
    public ResolveFieldValidator()
    {
        RuleFor(c => c.ReviewTaskId).NotEmpty();
        RuleFor(c => c.FieldName).NotEmpty().MaximumLength(100);
        RuleFor(c => c.ReviewerId).NotEmpty();
        RuleFor(c => c.ReviewerNote).MaximumLength(1000);
    }
}

public sealed class ResolveFieldHandler(
    IReviewTaskRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock) : IRequestHandler<ResolveFieldCommand>
{
    public async Task Handle(ResolveFieldCommand command, CancellationToken cancellationToken)
    {
        var task = await Load(repository, command.ReviewTaskId, cancellationToken);

        task.ResolveField(command.FieldName, command.AcceptedValue, command.ReviewerId, clock.UtcNow, command.ReviewerNote);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }

    internal static async Task<ReviewTask> Load(
        IReviewTaskRepository repository,
        Guid reviewTaskId,
        CancellationToken cancellationToken) =>
        await repository.FindAsync(new ReviewTaskId(reviewTaskId), cancellationToken)
        ?? throw new ReviewTaskNotFoundException(reviewTaskId);
}

/// <summary>Approves the document. Both gates live in the aggregate, not here (FR-4.5, FR-4.7).</summary>
public sealed record ApproveDocumentCommand(Guid ReviewTaskId, string ReviewerId) : IRequest;

public sealed class ApproveDocumentValidator : AbstractValidator<ApproveDocumentCommand>
{
    public ApproveDocumentValidator()
    {
        RuleFor(c => c.ReviewTaskId).NotEmpty();
        RuleFor(c => c.ReviewerId).NotEmpty();
    }
}

public sealed class ApproveDocumentHandler(
    IReviewTaskRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<ApproveDocumentHandler> logger) : IRequestHandler<ApproveDocumentCommand>
{
    public async Task Handle(ApproveDocumentCommand command, CancellationToken cancellationToken)
    {
        var task = await ResolveFieldHandler.Load(repository, command.ReviewTaskId, cancellationToken);

        // Throws if a mandatory field is unresolved, or if the approver is the uploader. Neither
        // check is repeated here: putting them in the handler as well would mean two places to
        // change and one to forget, and the aggregate is the one an API caller cannot bypass.
        task.Approve(command.ReviewerId, clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        ReviewLog.Approved(logger, task.Id.Value, task.DocumentId.Value, command.ReviewerId);
    }
}

public sealed record RejectDocumentCommand(
    Guid ReviewTaskId,
    string ReviewerId,
    RejectionReason Reason,
    string? ReasonNote) : IRequest;

public sealed class RejectDocumentValidator : AbstractValidator<RejectDocumentCommand>
{
    public RejectDocumentValidator()
    {
        RuleFor(c => c.ReviewTaskId).NotEmpty();
        RuleFor(c => c.ReviewerId).NotEmpty();
        RuleFor(c => c.Reason).IsInEnum();
        RuleFor(c => c.ReasonNote).MaximumLength(1000);
    }
}

public sealed class RejectDocumentHandler(
    IReviewTaskRepository repository,
    IUnitOfWork unitOfWork,
    IClock clock,
    ILogger<RejectDocumentHandler> logger) : IRequestHandler<RejectDocumentCommand>
{
    public async Task Handle(RejectDocumentCommand command, CancellationToken cancellationToken)
    {
        var task = await ResolveFieldHandler.Load(repository, command.ReviewTaskId, cancellationToken);

        task.Reject(command.ReviewerId, command.Reason, command.ReasonNote, clock.UtcNow);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        ReviewLog.Rejected(logger, task.Id.Value, task.DocumentId.Value, command.Reason.ToString());
    }
}

/// <summary>Cancels an open task because its document was superseded (FR-4.9).</summary>
public sealed record CancelReviewTaskCommand(Guid DocumentId, string Reason) : IRequest;

public sealed class CancelReviewTaskHandler(
    IReviewTaskRepository repository,
    IUnitOfWork unitOfWork) : IRequestHandler<CancelReviewTaskCommand>
{
    public async Task Handle(CancelReviewTaskCommand command, CancellationToken cancellationToken)
    {
        // No open task is a perfectly normal outcome: the document may never have needed review,
        // or the task may already be decided. Nothing to do, and nothing wrong.
        if (await repository.FindOpenForDocumentAsync(new DocumentId(command.DocumentId), cancellationToken) is not { } task)
        {
            return;
        }

        task.Cancel(command.Reason);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

public sealed class ReviewTaskNotFoundException(Guid reviewTaskId)
    : Exception($"Review task {reviewTaskId} was not found."), IResourceNotFound
{
    public Guid ReviewTaskId { get; } = reviewTaskId;
}

internal static partial class ReviewLog
{
    [LoggerMessage(EventId = 4401, Level = LogLevel.Information,
        Message = "Raised review task {ReviewTaskId} for document {DocumentId}: {Reason} at confidence {Confidence}")]
    public static partial void TaskRaised(ILogger logger, Guid reviewTaskId, Guid documentId, string reason, decimal confidence);

    [LoggerMessage(EventId = 4402, Level = LogLevel.Information,
        Message = "Review task {ReviewTaskId} approved document {DocumentId} by {ReviewerId}")]
    public static partial void Approved(ILogger logger, Guid reviewTaskId, Guid documentId, string reviewerId);

    [LoggerMessage(EventId = 4403, Level = LogLevel.Information,
        Message = "Review task {ReviewTaskId} rejected document {DocumentId}: {Reason}")]
    public static partial void Rejected(ILogger logger, Guid reviewTaskId, Guid documentId, string reason);
}
